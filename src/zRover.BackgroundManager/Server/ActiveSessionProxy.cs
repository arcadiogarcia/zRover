using System.Text.Json;
using zRover.Core;
using zRover.Core.Sessions;
using zRover.BackgroundManager.Sessions;
using zRover.Mcp;

namespace zRover.BackgroundManager.Server;

/// <summary>
/// Owns the dynamic proxy layer between the master MCP server and the active session.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>
///     On first session connection, fetches the tool skeleton from that session and
///     registers forwarding proxies in the <see cref="McpToolRegistryAdapter"/>.
///     All sessions are expected to expose identical tool sets (enforced by sharing
///     the same zRover.Uwp SDK version).
///   </item>
///   <item>
///     Every proxy tool delegates to <see cref="ISessionRegistry.ActiveSession"/> at
///     call time — the tool registration itself never changes after initialisation.
///   </item>
///   <item>
///     Maintains a <see cref="CancellationTokenSource"/> scoped to the active session.
///     When the active session changes or disconnects, that CTS is cancelled, which
///     interrupts all in-flight calls and returns an "interrupted" error to the caller.
///   </item>
/// </list>
/// </summary>
public sealed class ActiveSessionProxy
{
    private readonly ISessionRegistry _sessions;
    private readonly McpToolRegistryAdapter _adapter;
    private readonly ILogger<ActiveSessionProxy> _logger;

    // Replaced atomically whenever the active session changes.
    // All in-flight proxy calls hold a reference to their token at dispatch time.
    private volatile CancellationTokenSource _activeCts = new();

    // True once proxy tools have been registered from the first session.
    private bool _toolsInitialised;
    private readonly object _initLock = new();

    public ActiveSessionProxy(
        ISessionRegistry sessions,
        McpToolRegistryAdapter adapter,
        ILogger<ActiveSessionProxy> logger)
    {
        _sessions = sessions;
        _adapter = adapter;
        _logger = logger;

        _sessions.ActiveSessionChanged += OnActiveSessionChanged;
    }

    /// <summary>
    /// Called when a new session registers. If proxy tools haven't been registered yet,
    /// fetches the tool list from this session and initialises the proxy skeleton.
    /// Tool schemas are fixed after first initialisation; subsequent sessions are assumed
    /// to expose the same set (same SDK version).
    /// </summary>
    public async Task OnSessionRegisteredAsync(IRoverSession session)
    {
        bool shouldInit;
        lock (_initLock)
            shouldInit = !_toolsInitialised;

        if (!shouldInit) return;

        IReadOnlyList<DiscoveredTool> tools;
        try
        {
            tools = await session.ListToolsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list tools from first registered session {SessionId}", session.SessionId);
            return;
        }

        lock (_initLock)
        {
            if (_toolsInitialised) return; // another thread beat us here

            _logger.LogInformation("Initialising proxy tool skeleton from session {SessionId} ({DisplayName}): {Count} tools",
                session.SessionId, session.Identity.DisplayName, tools.Count);

            foreach (var tool in tools)
            {
                var capturedName = tool.Name;
                _adapter.RegisterTool(tool.Name, tool.Description, tool.InputSchema,
                    (Func<string, Task<RoverToolResult>>)(argsJson =>
                        ProxyInvokeAsync(capturedName, argsJson)));
            }

            _toolsInitialised = true;
        }
    }

    private async Task<RoverToolResult> ProxyInvokeAsync(string toolName, string argsJson)
    {
        var activeSession = _sessions.ActiveSession;
        if (activeSession == null || !activeSession.IsConnected)
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "no_active_session",
                message = "No active app session is set. Use set_active_app to choose one, then retry."
            }));

        // Capture the CTS for the current active session so that if the session changes
        // mid-call we cancel this invocation, not a future one.
        var sessionCts = _activeCts;

        try
        {
            var raw = await activeSession.InvokeToolAsync(toolName, argsJson, sessionCts.Token);
            var augmentedText = AugmentResult(raw.Text, activeSession);
            return raw.HasImage
                ? RoverToolResult.WithImage(augmentedText, raw.ImageBytes!, raw.ImageMimeType ?? "image/png")
                : RoverToolResult.FromText(augmentedText);
        }
        catch (OperationCanceledException)
        {
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "interrupted",
                message = "Tool call was interrupted because the active session changed or disconnected. Use set_active_app to set a new active session and retry."
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed on session {SessionId}", toolName, activeSession.SessionId);
            return RoverToolResult.FromText(JsonSerializer.Serialize(new { error = "invocation_failed", message = ex.Message }));
        }
    }

    /// <summary>
    /// Injects a <c>_rover_session</c> key into the result so the MCP client always
    /// knows which app instance handled the call, even if it lost track of the active session.
    /// <list type="bullet">
    ///   <item>If the result is a JSON object, the key is added at the top level.</item>
    ///   <item>Otherwise the original result is preserved under <c>_result</c>.</item>
    /// </list>
    /// </summary>
    private static string AugmentResult(string raw, IRoverSession session)
    {
        object? originNode = null;
        if (session is PropagatedSession ps)
        {
            originNode = new
            {
                type = ps.Origin.Type,
                managerId = ps.Origin.ManagerId,
                managerAlias = ps.Origin.ManagerAlias,
                hops = ps.Origin.Hops
            };
        }

        var sessionNode = new
        {
            sessionId   = session.SessionId,
            appName     = session.Identity.AppName,
            version     = session.Identity.Version,
            instanceId  = session.Identity.InstanceId,
            displayName = session.Identity.DisplayName,
            origin      = originNode
        };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Merge _rover_session into the existing object
                using var ms = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WritePropertyName("_rover_session");
                JsonSerializer.Serialize(writer, sessionNode);
                writer.WriteEndObject();
                writer.Flush();
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch { /* unparseable — fall through to wrapper */ }

        // Non-object result (array, scalar, raw text): wrap it
        return JsonSerializer.Serialize(new { _result = raw, _rover_session = sessionNode });
    }

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e)
    {
        // Cancel all in-flight calls from the previous session
        var oldCts = Interlocked.Exchange(ref _activeCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();

        if (e.Current != null)
            _logger.LogInformation("Active session → {DisplayName} (session {SessionId})",
                e.Current.Identity.DisplayName, e.Current.SessionId);
        else if (e.Previous != null)
            _logger.LogWarning("Active session {DisplayName} disconnected — no active session",
                e.Previous.Identity.DisplayName);
    }
}
