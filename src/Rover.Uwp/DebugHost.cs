using System;
using System.Threading.Tasks;
using Rover.Core;

namespace Rover.Uwp
{
    /// <summary>
    /// Entry point for the Rover in-app debug host. Active in DEBUG builds only.
    /// </summary>
    public static class DebugHost
    {
        private static DebugHostRunner? _runner;
        private static Func<Task>? _fullTrustLauncher;

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
        /// Does not require a direct reference to <c>Rover.Core</c> from caller code.
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
            Rover.Core.IActionableApp? actionableApp = null)
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
