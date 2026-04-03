using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace zRover.Core.Sessions
{
    /// <summary>
    /// Represents one connected app instance managed by the BackgroundManager.
    /// Each session wraps an MCP client connection to a per-app zRover MCP server.
    /// </summary>
    public interface IRoverSession
    {
        /// <summary>
        /// Opaque identifier assigned by the BackgroundManager when the session registers.
        /// Stable for the lifetime of this connection; a new SessionId is issued on reconnect.
        /// </summary>
        string SessionId { get; }

        /// <summary>Identity (AppName / Version / InstanceId) as reported by the app.</summary>
        RoverAppIdentity Identity { get; }

        /// <summary>Base URL of the per-app MCP server (e.g. "http://localhost:5100/mcp").</summary>
        string McpUrl { get; }

        /// <summary>True while the MCP connection is healthy.</summary>
        bool IsConnected { get; }

        /// <summary>Fired when the session loses its MCP connection.</summary>
        event EventHandler? Disconnected;

        /// <summary>
        /// Returns the tools this session exposes. Used once on registration to
        /// populate the proxy tool skeleton in the master MCP server.
        /// </summary>
        Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Forwards a tool invocation to the app. The provided
        /// <paramref name="cancellationToken"/> should be linked to the active-session
        /// lifetime so calls are cancelled when the session is deactivated.
        /// Returns a <see cref="RoverToolResult"/> that may include an inline image.
        /// </summary>
        Task<RoverToolResult> InvokeToolAsync(string toolName, string argsJson, CancellationToken cancellationToken = default);
    }
}
