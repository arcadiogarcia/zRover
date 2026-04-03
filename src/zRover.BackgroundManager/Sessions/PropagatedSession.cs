using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zRover.Core;
using zRover.Core.Sessions;

namespace zRover.BackgroundManager.Sessions;

/// <summary>
/// An <see cref="IRoverSession"/> that represents a remote app reachable through
/// another Background Manager. Tool invocations are forwarded by first calling
/// <c>set_active_app</c> on the remote manager, then invoking the tool.
///
/// The <see cref="McpUrl"/> points to the remote manager (not the app directly),
/// because all traffic flows through the MCP client connection to that manager.
/// </summary>
public sealed class PropagatedSession : IRoverSession
{
    private readonly McpClient _managerClient;
    private readonly string _originalSessionId;
    private readonly object _lock = new();
    private volatile bool _connected = true;
    private IReadOnlyList<DiscoveredTool>? _cachedTools;

    /// <summary>Namespaced session ID: <c>{managerId}:{originalSessionId}</c>.</summary>
    public string SessionId { get; }

    public RoverAppIdentity Identity { get; }

    /// <summary>URL of the remote Background Manager's MCP endpoint.</summary>
    public string McpUrl { get; }

    public bool IsConnected
    {
        get => _connected;
        internal set
        {
            if (_connected == value) return;
            _connected = value;
            if (!value) Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Disconnected;

    /// <summary>The manager ID prefix used in session-ID namespacing.</summary>
    public string ManagerId { get; }

    /// <summary>The session ID as known on the remote manager.</summary>
    public string OriginalSessionId => _originalSessionId;

    /// <summary>Metadata about where this session originates.</summary>
    public SessionOrigin Origin { get; }

    public PropagatedSession(
        string managerId,
        string originalSessionId,
        RoverAppIdentity identity,
        string managerMcpUrl,
        McpClient managerClient,
        SessionOrigin origin)
    {
        ManagerId = managerId;
        _originalSessionId = originalSessionId;
        SessionId = $"{managerId}:{originalSessionId}";
        Identity = identity;
        McpUrl = managerMcpUrl;
        _managerClient = managerClient;
        Origin = origin;
    }

    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTools != null) return _cachedTools;

        // Fetch tools from the remote manager (they're the same proxy tools it exposes)
        var tools = await _managerClient.ListToolsAsync(cancellationToken: cancellationToken);
        var result = tools
            .Where(t => t.Name != "list_apps" && t.Name != "set_active_app")
            .Select(t => new DiscoveredTool
            {
                Name = t.Name,
                Description = t.Description ?? "",
                InputSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                    ? t.JsonSchema.ToString()
                    : "{}"
            }).ToList();

        _cachedTools = result;
        return result;
    }

    public async Task<RoverToolResult> InvokeToolAsync(string toolName, string argsJson, CancellationToken cancellationToken = default)
    {
        // Step 1: Ensure this session is active on the remote manager
        await SetRemoteActiveAsync(cancellationToken);

        // Step 2: Invoke the tool on the remote manager (which forwards to the app)
        Dictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(argsJson) && argsJson != "{}")
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);

        try
        {
            var result = await _managerClient.CallToolAsync(toolName, arguments,
                cancellationToken: cancellationToken);
            var text     = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
            var imgBlock = result.Content.OfType<ImageContentBlock>().FirstOrDefault();
            if (imgBlock != null)
                return RoverToolResult.WithImage(text,
                    imgBlock.DecodedData.ToArray(), imgBlock.MimeType ?? "image/png");
            return RoverToolResult.FromText(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            IsConnected = false;
            throw;
        }
    }

    internal void MarkDisconnected()
    {
        IsConnected = false;
    }

    /// <summary>
    /// Caches the last-set active session on the remote manager to avoid redundant
    /// <c>set_active_app</c> round-trips when making multiple calls to the same app.
    /// </summary>
    private string? _lastSetActiveId;

    private async Task SetRemoteActiveAsync(CancellationToken cancellationToken)
    {
        if (_lastSetActiveId == _originalSessionId) return;

        var args = new Dictionary<string, object?> { ["sessionId"] = _originalSessionId };
        await _managerClient.CallToolAsync("set_active_app", args, cancellationToken: cancellationToken);
        _lastSetActiveId = _originalSessionId;
    }
}

/// <summary>Describes the origin of a session for federation-aware <c>list_apps</c>.</summary>
public sealed class SessionOrigin
{
    public string Type { get; init; } = "local";
    public string? ManagerId { get; init; }
    public string? ManagerAlias { get; init; }
    public string? ManagerUrl { get; init; }
    public int Hops { get; init; }
}
