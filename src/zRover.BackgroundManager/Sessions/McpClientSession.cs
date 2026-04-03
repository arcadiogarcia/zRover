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
/// Implements <see cref="IRoverSession"/> by wrapping an MCP client connection
/// to a per-app <c>zRover.FullTrust.McpServer</c>. Supports same-device and
/// cross-machine scenarios via HTTP.
/// </summary>
public sealed class McpClientSession : IRoverSession, IAsyncDisposable
{
    private readonly McpClient _client;
    private volatile bool _connected = true;

    public string SessionId { get; }
    public RoverAppIdentity Identity { get; }
    public string McpUrl { get; }
    public bool IsConnected => _connected;

    public event EventHandler? Disconnected;

    private McpClientSession(string sessionId, RoverAppIdentity identity, string mcpUrl, McpClient client)
    {
        SessionId = sessionId;
        Identity = identity;
        McpUrl = mcpUrl;
        _client = client;
    }

    /// <summary>
    /// Creates and initialises an MCP client session connected to the given URL.
    /// Performs the MCP initialize handshake before returning.
    /// </summary>
    public static async Task<McpClientSession> ConnectAsync(
        string sessionId,
        RoverAppIdentity identity,
        string mcpUrl,
        CancellationToken cancellationToken = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(mcpUrl),
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var session = new McpClientSession(sessionId, identity, mcpUrl, client);
        return session;
    }

    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools.Select(t => new DiscoveredTool
            {
                Name = t.Name,
                Description = t.Description ?? "",
                InputSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                    ? t.JsonSchema.ToString()
                    : "{}"
            }).ToList();
        }
        catch
        {
            MarkDisconnected();
            throw;
        }
    }

    public async Task<RoverToolResult> InvokeToolAsync(string toolName, string argsJson, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(argsJson) && argsJson != "{}")
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);

        try
        {
            var result = await _client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
            var text     = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
            var imgBlock = result.Content.OfType<ImageContentBlock>().FirstOrDefault();
            if (imgBlock != null)
                return RoverToolResult.WithImage(text,
                    imgBlock.DecodedData.ToArray(), imgBlock.MimeType ?? "image/png");
            return RoverToolResult.FromText(text);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate — caller handles the interruption message
            throw;
        }
        catch
        {
            MarkDisconnected();
            throw;
        }
    }

    /// <summary>
    /// Begin watching the MCP transport for completion. Call this AFTER
    /// the <see cref="Disconnected"/> event handler has been attached so
    /// the notification is never lost.
    /// </summary>
    internal void StartDisconnectMonitoring()
    {
        _ = _client.Completion.ContinueWith(_ => MarkDisconnected(), TaskScheduler.Default);
    }

    internal void MarkDisconnected()
    {
        if (_connected)
        {
            _connected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisconnected();
        if (_client is IAsyncDisposable d)
            await d.DisposeAsync();
    }
}
