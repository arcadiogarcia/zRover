namespace zRover.Core.Sessions
{
    /// <summary>
    /// HTTP body sent by a per-app zRover MCP server to register itself with
    /// the Retriever at startup.
    /// POST {managerUrl}/sessions/register
    /// </summary>
    public sealed class SessionRegistrationRequest
    {
        /// <summary>Stable logical name of the application (e.g. "MyGame").</summary>
        public string AppName { get; set; } = "";

        /// <summary>Build or release version string (e.g. "1.0.3").</summary>
        public string Version { get; set; } = "";

        /// <summary>
        /// Optional disambiguator for multiple instances of the same app+version.
        /// Null or omitted for single-instance scenarios.
        /// </summary>
        public string? InstanceId { get; set; }

        /// <summary>
        /// Full URL of the per-app MCP server endpoint the Retriever
        /// should connect its MCP client to (e.g. "http://hostname:5100/mcp").
        /// Must be reachable from the Retriever process (supports cross-machine).
        /// </summary>
        public string McpUrl { get; set; } = "";
    }

    /// <summary>Response body returned to the registering per-app server.</summary>
    public sealed class SessionRegistrationResponse
    {
        /// <summary>
        /// Opaque session ID assigned by the Retriever.
        /// The per-app server may log this for diagnostics.
        /// </summary>
        public string SessionId { get; set; } = "";
    }
}
