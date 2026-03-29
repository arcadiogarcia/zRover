using System;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Logging;

namespace zRover.Uwp
{
    /// <summary>
    /// Entry point for the zRover in-app debug host. Active in DEBUG builds only.
    /// </summary>
    public static class DebugHost
    {
        private static DebugHostRunner? _runner;
        private static Func<Task>? _fullTrustLauncher;

        /// <summary>
        /// The in-memory log store for the running zRover instance.
        /// Backed by <see cref="RoverLog.Store"/>.
        /// MCP clients access this via the <c>get_logs</c> tool;
        /// host app code can write to it through <see cref="RoverLog"/>.
        /// </summary>
        public static IInMemoryLogStore LogStore => RoverLog.Store;
        /// <summary>
        /// Sets the callback to launch the FullTrust MCP server process.
        /// Call this before StartAsync to enable out-of-process MCP server.
        /// </summary>
        public static void SetFullTrustLauncher(Func<Task> launcher)
        {
            _fullTrustLauncher = launcher;
        }

        /// <summary>
        /// Starts the MCP debug host. Call from App.OnLaunched.
        /// </summary>
        public static async Task StartAsync(DebugHostOptions options)
        {
            // Allow idempotent calls - if already running, do nothing
            if (_runner != null)
                return;

            _runner = new DebugHostRunner(options, _fullTrustLauncher);
            await _runner.StartAsync();
        }

        /// <summary>
        /// Convenience overload that starts the MCP debug host with common settings.
        /// Does not require a direct reference to <c>zRover.Core</c> from caller code.
        /// </summary>
        /// <param name="appName">Display name used in the MCP server info.</param>
        /// <param name="port">TCP port to listen on. Default is 7331.</param>
        /// <param name="requireAuthToken">When true, callers must supply an Authorization header.</param>
        /// <param name="authToken">Bearer token to require (only used when <paramref name="requireAuthToken"/> is true).</param>
        public static Task StartAsync(
            string appName,
            int port = 7331,
            bool requireAuthToken = false,
            string? authToken = null,
            zRover.Core.IActionableApp? actionableApp = null)
        {
            return StartAsync(new DebugHostOptions
            {
                AppName = appName,
                Port = port,
                EnableInputInjection = true,
                EnableScreenshots = true,
                RequireAuthToken = requireAuthToken,
                AuthToken = authToken,
                ActionableApp = actionableApp
            });
        }

        /// <summary>Stops the MCP debug host and releases all capabilities.</summary>
        public static async Task StopAsync()
        {
            if (_runner == null) return;
            await _runner.StopAsync();
            _runner = null;
        }
    }
}
