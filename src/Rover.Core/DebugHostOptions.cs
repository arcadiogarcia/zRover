namespace Rover.Core
{
    public class DebugHostOptions
    {
        public string AppName { get; set; } = "App";
        public int Port { get; set; } = 7331;
        public bool EnableInputInjection { get; set; } = true;
        public bool EnableScreenshots { get; set; } = true;
        public bool RequireAuthToken { get; set; } = true;
        public string? AuthToken { get; set; }
        public string? ArtifactDirectory { get; set; }
        public bool SkipUwpCheck { get; set; } = false;
        public bool TestAppService { get; set; } = false;

        /// <summary>
        /// Optional application to expose via the App Action API (<c>list_actions</c> /
        /// <c>dispatch_action</c> MCP tools). Set this to enable AI agents to programmatically
        /// observe and drive the host application through a stable, self-describing interface.
        /// </summary>
        public IActionableApp? ActionableApp { get; set; }

        /// <summary>
        /// Enables the in-memory log store and the <c>get_logs</c> MCP tool.
        /// When active, UWP lifecycle events, unhandled exceptions, and XAML binding
        /// failures are automatically captured without any extra code in the host app.
        /// Defaults to <c>true</c>.
        /// </summary>
        public bool EnableLogging { get; set; } = true;

        /// <summary>
        /// Maximum number of log entries kept in memory. Older entries are overwritten
        /// once the buffer is full. Defaults to 2000.
        /// </summary>
        public int LogBufferCapacity { get; set; } = 2000;
    }
}
