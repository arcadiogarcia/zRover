using System;
using System.Threading.Tasks;
using zRover.Core.Logging;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;

namespace zRover.Uwp
{
    /// <summary>
    /// Single entry point for integrating zRover MCP into a UWP app.
    /// Handles tool registration, AppService routing, and lifecycle management
    /// behind a simple static API.
    /// <para>
    /// Minimal integration (3 touch-points in App.xaml.cs):
    /// <code>
    /// // 1. In OnLaunched, after Window.Current.Activate():
    /// await RoverMcp.StartAsync("MyApp", port: 5100,
    ///     () => FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("McpServer").AsTask());
    ///
    /// // 2. Override OnBackgroundActivated:
    /// protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
    /// {
    ///     if (RoverMcp.HandleBackgroundActivation(args)) return;
    ///     base.OnBackgroundActivated(args);
    /// }
    ///
    /// // 3. In OnSuspending:
    /// RoverMcp.Stop();
    /// </code>
    /// </para>
    /// </summary>
    public static class RoverMcp
    {
        /// <summary>
        /// Starts zRover: registers capabilities and tools, launches the FullTrust
        /// MCP companion server, and hooks the window-closed signal.
        /// Call once from <c>OnLaunched</c> after <c>Window.Current.Activate()</c>.
        /// </summary>
        /// <param name="appName">Display name for the MCP server.</param>
        /// <param name="port">TCP port the MCP server listens on (default 5100).</param>
        /// <param name="launchFullTrust">
        /// Callback that launches the FullTrust companion process.
        /// Typically: <c>() => FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("McpServer").AsTask()</c>
        /// </param>
        public static async Task StartAsync(
            string appName,
            int port = 5100,
            Func<Task>? launchFullTrust = null,
            zRover.Core.IActionableApp? actionableApp = null)
        {
            // Wire window-close signal so FullTrust process shuts down
            if (Window.Current != null)
                Window.Current.Closed += (s, a) => RoverAppService.WindowClosed = true;

            // Register capabilities + tools
            await DebugHost.StartAsync(appName, port: port, actionableApp: actionableApp);

            // Launch the FullTrust MCP companion process
            if (launchFullTrust != null)
                await launchFullTrust();
        }

        /// <summary>
        /// Routes AppService background activations to zRover's tool-invocation handler.
        /// Returns true if handled; false if the activation is not a zRover request.
        /// Call from <c>OnBackgroundActivated</c>.
        /// </summary>
        public static bool HandleBackgroundActivation(BackgroundActivatedEventArgs args)
        {
            return RoverAppService.TryHandle(args);
        }

        /// <summary>
        /// Stops zRover and releases resources. Call from <c>OnSuspending</c>.
        /// </summary>
        public static void Stop()
        {
            DebugHost.StopAsync().GetAwaiter().GetResult();
        }

        // ---------------------------------------------------------------
        // Logging shorthands — host app code can call these directly
        // without importing zRover.Core.Logging.
        // ---------------------------------------------------------------

        /// <summary>Writes a diagnostic info message to the zRover log (visible via <c>get_logs</c>).</summary>
        public static void Log(string category, string message)
            => RoverLog.Info(category, message);

        /// <summary>Writes a warning to the zRover log.</summary>
        public static void LogWarn(string category, string message, Exception? exception = null)
            => RoverLog.Warn(category, message, exception);

        /// <summary>Writes an error to the zRover log.</summary>
        public static void LogError(string category, string message, Exception? exception = null)
            => RoverLog.Error(category, message, exception);
    }
}
