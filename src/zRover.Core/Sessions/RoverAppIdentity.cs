namespace zRover.Core.Sessions
{
    /// <summary>
    /// Immutable identity for a Rover-instrumented app instance.
    /// Provided by the app when it registers with the Retriever.
    /// </summary>
    public sealed class RoverAppIdentity
    {
        /// <summary>
        /// Stable logical name for the application (e.g. "MyGame", "Calculator").
        /// Should not change across versions or restarts.
        /// </summary>
        public string AppName { get; }

        /// <summary>
        /// Version string identifying the build or release (e.g. "1.0.3", "2024-12-01").
        /// May differ across parallel instances running different builds.
        /// </summary>
        public string Version { get; }

        /// <summary>
        /// Optional free-form string to distinguish multiple simultaneous instances
        /// of the same app+version (e.g. "left-monitor", "test-profile-A").
        /// Null when there is only one instance.
        /// </summary>
        public string? InstanceId { get; }

        public RoverAppIdentity(string appName, string version, string? instanceId = null)
        {
            AppName = appName;
            Version = version;
            InstanceId = instanceId;
        }

        /// <summary>Human-readable label, e.g. "MyGame v1.0 [left-monitor]".</summary>
        public string DisplayName =>
            InstanceId != null
                ? $"{AppName} v{Version} [{InstanceId}]"
                : $"{AppName} v{Version}";

        public override string ToString() => DisplayName;
    }
}
