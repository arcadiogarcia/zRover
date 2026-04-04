using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zRover.BackgroundManager.Server;
using zRover.Core.Sessions;

namespace zRover.BackgroundManager.Sessions;

/// <summary>
/// Manages MCP client connections to remote Background Managers and propagates
/// their sessions into the local <see cref="SessionRegistry"/>.
///
/// When connected to a remote manager, this class:
/// <list type="bullet">
///   <item>Calls <c>list_apps</c> to discover remote sessions</item>
///   <item>Creates <see cref="PropagatedSession"/> for each remote app</item>
///   <item>Listens for <c>tools/list_changed</c> notifications to detect session changes</item>
///   <item>Periodically verifies connectivity and syncs the session list</item>
/// </list>
/// </summary>
public sealed class RemoteManagerRegistry : IDisposable
{
    private readonly SessionRegistry _sessionRegistry;
    private readonly ActiveSessionProxy _activeSessionProxy;
    private readonly ILogger<RemoteManagerRegistry> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteManagerConnection> _managers = new();

    public IReadOnlyList<RemoteManagerInfo> Managers
    {
        get
        {
            lock (_lock)
                return _managers.Values.Select(m => m.Info).ToList();
        }
    }

    public event EventHandler? ManagersChanged;

    public RemoteManagerRegistry(SessionRegistry sessionRegistry, ActiveSessionProxy activeSessionProxy, ILogger<RemoteManagerRegistry> logger)
    {
        _sessionRegistry = sessionRegistry;
        _activeSessionProxy = activeSessionProxy;
        _logger = logger;
    }

    /// <summary>
    /// Connects to a remote Background Manager via MCP, discovers its sessions, and
    /// propagates them into the local registry.
    /// </summary>
    /// <returns>The assigned manager ID.</returns>
    public async Task<string> ConnectAsync(string mcpUrl, string? bearerToken, string? alias = null,
        CancellationToken cancellationToken = default)
    {
        var managerId = GenerateManagerId();

        _logger.LogInformation("Connecting to remote manager at {McpUrl} (alias={Alias}, id={ManagerId})",
            mcpUrl, alias ?? "(none)", managerId);

        // Create MCP client to the remote manager with optional auth
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(mcpUrl),
        };

        if (!string.IsNullOrEmpty(bearerToken))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {bearerToken}"
            };
        }

        var transport = new HttpClientTransport(transportOptions);

        // Register a notification handler for tools/list_changed so we can
        // re-sync sessions in real-time when the remote manager's session list changes.
        var connectionRef = new StrongBox<RemoteManagerConnection?>(null);
        var clientOptions = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification,
                        async (notification, ct) =>
                        {
                            var conn = connectionRef.Value;
                            if (conn is null) return;
                            _logger.LogInformation(
                                "Remote manager {ManagerId} signaled tools/list_changed — re-syncing",
                                conn.ManagerId);
                            try
                            {
                                await SyncRemoteSessionsAsync(conn, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Re-sync failed for remote manager {ManagerId}",
                                    conn.ManagerId);
                            }
                        })
                ]
            }
        };

        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to remote manager at {McpUrl}", mcpUrl);
            throw;
        }

        var connection = new RemoteManagerConnection
        {
            ManagerId = managerId,
            McpUrl = mcpUrl,
            BearerToken = bearerToken,
            Alias = alias ?? ExtractHostname(mcpUrl),
            Client = client,
            PropagatedSessionIds = new List<string>(),
            Info = new RemoteManagerInfo
            {
                ManagerId = managerId,
                Alias = alias ?? ExtractHostname(mcpUrl),
                McpUrl = mcpUrl,
                IsConnected = true,
                AppCount = 0
            }
        };

        // Wire up the connection reference so the notification handler can access it
        connectionRef.Value = connection;

        // Discover remote sessions
        await SyncRemoteSessionsAsync(connection, cancellationToken);

        // Start disconnect monitoring
        _ = client.Completion.ContinueWith(_ => OnManagerDisconnected(managerId), TaskScheduler.Default);

        lock (_lock)
            _managers[managerId] = connection;

        _logger.LogInformation("Connected to remote manager {Alias} ({ManagerId}) — {Count} apps",
            connection.Alias, managerId, connection.PropagatedSessionIds.Count);

        ManagersChanged?.Invoke(this, EventArgs.Empty);
        return managerId;
    }

    /// <summary>Disconnects from a remote manager and removes all its propagated sessions.</summary>
    public async Task DisconnectAsync(string managerId)
    {
        RemoteManagerConnection? connection;
        lock (_lock)
        {
            if (!_managers.Remove(managerId, out connection)) return;
        }

        _logger.LogInformation("Disconnecting from remote manager {Alias} ({ManagerId})",
            connection.Alias, managerId);

        await CleanupConnectionAsync(connection);
        ManagersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tests whether a remote manager is reachable by calling <c>list_apps</c>.</summary>
    public async Task<bool> TestConnectivityAsync(string managerId, CancellationToken ct = default)
    {
        RemoteManagerConnection? connection;
        lock (_lock)
        {
            if (!_managers.TryGetValue(managerId, out connection)) return false;
        }

        try
        {
            await connection.Client.CallToolAsync("list_apps", cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Routes a device-level tool call to the identified remote manager.
    /// Strips the leading manager-ID segment from <paramref name="argsJson"/>'s
    /// <c>deviceId</c> field automatically so each hop only sees a relative ID.
    /// </summary>
    /// <remarks>
    /// Used by <c>DevicePackageManagementTools</c> to forward install/uninstall/launch/stop
    /// and staging calls across federation hops without coupling the tool layer to
    /// the MCP client internals.
    /// </remarks>
    public async Task<string> RouteDeviceToolAsync(
        string deviceId,
        string toolName,
        string argsJson,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        // deviceId may be "a1b2" (direct) or "a1b2:c3d4:..." (deeper chain).
        // We peel off the first segment to find our immediate connection;
        // the residual becomes the downstream deviceId.
        var colon = deviceId.IndexOf(':');
        string managerId  = colon < 0 ? deviceId       : deviceId[..colon];
        string residualId = colon < 0 ? ""              : deviceId[(colon + 1)..];

        RemoteManagerConnection? connection;
        lock (_lock)
        {
            if (!_managers.TryGetValue(managerId, out connection))
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "MANAGER_NOT_FOUND",
                    errorMessage = $"No remote manager with ID '{managerId}' is connected."
                });
        }

        if (!connection.Info.IsConnected)
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                success = false,
                error = "MANAGER_DISCONNECTED",
                errorMessage = $"Remote manager '{connection.Alias}' ({managerId}) is currently disconnected."
            });

        // Rewrite argsJson: replace or remove the deviceId field
        string rewrittenArgs;
        try
        {
            rewrittenArgs = RewriteDeviceId(argsJson, string.IsNullOrEmpty(residualId) ? null : residualId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to rewrite args for RouteDeviceToolAsync");
            rewrittenArgs = argsJson;
        }

        // Deserialise to the arg dict the MCP SDK expects
        Dictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(rewrittenArgs) && rewrittenArgs != "{}")
            arguments = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(rewrittenArgs);

        try
        {
            logger?.LogDebug("Routing {Tool} to remote manager {Alias} ({ManagerId})",
                toolName, connection.Alias, managerId);

            var result = await connection.Client.CallToolAsync(toolName, arguments, cancellationToken: ct);
            return result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                       .FirstOrDefault()?.Text ?? "{}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogError(ex, "RouteDeviceToolAsync failed: {Tool} on manager {ManagerId}", toolName, managerId);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                success = false,
                error = "REMOTE_CALL_FAILED",
                errorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// Returns the MCP endpoint URL for a given manager ID, or <c>null</c> if not found.
    /// Used by <c>DevicePackageManagementTools</c> to build downstream upload URLs.
    /// </summary>
    public string? GetManagerMcpUrl(string managerId)
    {
        lock (_lock)
        {
            return _managers.TryGetValue(managerId, out var conn) ? conn.McpUrl : null;
        }
    }

    // ── Arg rewriting ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a copy of <paramref name="argsJson"/> with the <c>deviceId</c> field
    /// replaced by <paramref name="newDeviceId"/>, or removed when it is <c>null</c>.
    /// </summary>
    private static string RewriteDeviceId(string argsJson, string? newDeviceId)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
        var dict = new Dictionary<string, object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase))
                continue;

            dict[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? prop.Value.GetString()
                : (object?)prop.Value;
        }

        if (!string.IsNullOrEmpty(newDeviceId))
            dict["deviceId"] = newDeviceId;

        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    /// <summary>Re-syncs the session list from a remote manager.</summary>
    public async Task ResyncAsync(string managerId, CancellationToken ct = default)
    {
        RemoteManagerConnection? connection;
        lock (_lock)
        {
            if (!_managers.TryGetValue(managerId, out connection)) return;
        }

        await SyncRemoteSessionsAsync(connection, ct);
    }

    public void Dispose()
    {
        List<RemoteManagerConnection> connections;
        lock (_lock)
        {
            connections = _managers.Values.ToList();
            _managers.Clear();
        }

        foreach (var c in connections)
            CleanupConnectionAsync(c).GetAwaiter().GetResult();
    }

    // ── Internal sync logic ────────────────────────────────────────────────

    private async Task SyncRemoteSessionsAsync(RemoteManagerConnection connection, CancellationToken ct = default)
    {
        List<RemoteAppInfo> remoteApps;
        try
        {
            remoteApps = await FetchRemoteAppsAsync(connection.Client, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch apps from remote manager {ManagerId}", connection.ManagerId);
            return;
        }

        var newIds = new HashSet<string>();

        foreach (var app in remoteApps)
        {
            var propagatedId = $"{connection.ManagerId}:{app.SessionId}";
            newIds.Add(propagatedId);

            // Check if we already have this session
            var existing = _sessionRegistry.Sessions.FirstOrDefault(s => s.SessionId == propagatedId);
            if (existing != null)
            {
                // Update connected state if changed
                if (existing is PropagatedSession ps)
                    ps.IsConnected = app.IsConnected;
                continue;
            }

            // Create propagated session
            var identity = new RoverAppIdentity(app.AppName, app.Version, app.InstanceId);
            var origin = new SessionOrigin
            {
                Type = "remote",
                ManagerId = connection.ManagerId,
                ManagerAlias = connection.Alias,
                ManagerUrl = connection.McpUrl,
                Hops = app.RemoteHops + 1
            };

            var propagated = new PropagatedSession(
                connection.ManagerId,
                app.SessionId,
                identity,
                connection.McpUrl,
                connection.Client,
                origin);

            _sessionRegistry.Add(propagated);
            connection.PropagatedSessionIds.Add(propagatedId);
            await _activeSessionProxy.OnSessionRegisteredAsync(propagated);

            _logger.LogInformation("Propagated session: {DisplayName} as {PropagatedId}",
                identity.DisplayName, propagatedId);
        }

        // Remove propagated sessions that no longer exist on the remote manager
        var stale = connection.PropagatedSessionIds
            .Where(id => !newIds.Contains(id))
            .ToList();

        foreach (var id in stale)
        {
            _sessionRegistry.Remove(id);
            connection.PropagatedSessionIds.Remove(id);
            _logger.LogInformation("Removed stale propagated session {SessionId}", id);
        }

        connection.Info = connection.Info with { AppCount = newIds.Count, IsConnected = true };
        ManagersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<List<RemoteAppInfo>> FetchRemoteAppsAsync(McpClient client, CancellationToken ct)
    {
        var result = await client.CallToolAsync("list_apps", cancellationToken: ct);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";

        using var doc = JsonDocument.Parse(text);
        var apps = new List<RemoteAppInfo>();

        if (doc.RootElement.TryGetProperty("apps", out var appsArray))
        {
            foreach (var app in appsArray.EnumerateArray())
            {
                // Extract hops from the origin object if present (for multi-hop chains)
                int remoteHops = 0;
                if (app.TryGetProperty("origin", out var originEl)
                    && originEl.TryGetProperty("hops", out var hopsEl)
                    && hopsEl.ValueKind == JsonValueKind.Number)
                {
                    remoteHops = hopsEl.GetInt32();
                }

                apps.Add(new RemoteAppInfo
                {
                    SessionId = app.GetProperty("sessionId").GetString() ?? "",
                    AppName = app.GetProperty("appName").GetString() ?? "",
                    Version = app.GetProperty("version").GetString() ?? "",
                    InstanceId = app.TryGetProperty("instanceId", out var iid) ? iid.GetString() : null,
                    McpUrl = app.TryGetProperty("mcpUrl", out var url) ? url.GetString() ?? "" : "",
                    IsConnected = app.TryGetProperty("isConnected", out var ic) && ic.GetBoolean(),
                    RemoteHops = remoteHops,
                });
            }
        }

        return apps;
    }


    private void OnManagerDisconnected(string managerId)
    {
        RemoteManagerConnection? connection;
        lock (_lock)
        {
            if (!_managers.TryGetValue(managerId, out connection)) return;
        }

        _logger.LogWarning("Remote manager {Alias} ({ManagerId}) disconnected",
            connection.Alias, managerId);

        // Mark all propagated sessions as disconnected
        foreach (var id in connection.PropagatedSessionIds.ToList())
        {
            var session = _sessionRegistry.Sessions.FirstOrDefault(s => s.SessionId == id);
            if (session is PropagatedSession ps)
                ps.MarkDisconnected();
        }

        connection.Info = connection.Info with { IsConnected = false };
        ManagersChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CleanupConnectionAsync(RemoteManagerConnection connection)
    {
        // Remove all propagated sessions from the registry
        foreach (var id in connection.PropagatedSessionIds.ToList())
            _sessionRegistry.Remove(id);

        connection.PropagatedSessionIds.Clear();

        // Dispose the MCP client
        try
        {
            if (connection.Client is IAsyncDisposable ad)
                await ad.DisposeAsync();
        }
        catch { /* best effort */ }
    }

    private static string GenerateManagerId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    private static string ExtractHostname(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    // ── Internal Types ─────────────────────────────────────────────────────

    private class RemoteManagerConnection
    {
        public required string ManagerId;
        public required string McpUrl;
        public required string? BearerToken;
        public required string Alias;
        public required McpClient Client;
        public required List<string> PropagatedSessionIds;
        public required RemoteManagerInfo Info;
    }

    private record RemoteAppInfo
    {
        public string SessionId { get; init; } = "";
        public string AppName { get; init; } = "";
        public string Version { get; init; } = "";
        public string? InstanceId { get; init; }
        public string McpUrl { get; init; } = "";
        public bool IsConnected { get; init; }
        public int RemoteHops { get; init; }
    }
}

/// <summary>Public read-only snapshot of a remote manager connection.</summary>
public record RemoteManagerInfo
{
    public string ManagerId { get; init; } = "";
    public string Alias { get; init; } = "";
    public string McpUrl { get; init; } = "";
    public bool IsConnected { get; init; }
    public int AppCount { get; init; }
}
