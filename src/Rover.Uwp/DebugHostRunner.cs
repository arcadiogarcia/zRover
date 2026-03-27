using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Logging;
using Rover.Uwp.Capabilities;
using Rover.Uwp.Coordinates;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace Rover.Uwp
{
    internal class DebugHostRunner
    {
        private readonly DebugHostOptions _options;
        private readonly List<IDebugCapability> _capabilities = new List<IDebugCapability>();
        private readonly Func<Task>? _launchFullTrustProcess;

        public DebugHostRunner(DebugHostOptions options, Func<Task>? launchFullTrustProcess = null)
        {
            _options = options;
            _launchFullTrustProcess = launchFullTrustProcess;
        }

        public async Task StartAsync()
        {
            // Configure the log store before anything else so that early log messages
            // (including hooks wired below) land in the right buffer.
            if (_options.EnableLogging && _options.LogBufferCapacity != 2000)
                RoverLog.Store = new InMemoryLogStore(_options.LogBufferCapacity);

            RoverLog.Info("Rover.Host", $"Starting '{_options.AppName}' MCP debug host");

            // Auto-wire UWP crash, lifecycle, and XAML diagnostics once — seamlessly.
            WireUwpDiagnostics();

            var dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
            Func<Func<Task>, Task> runOnUiThread = async (work) =>
            {
                var tcs = new TaskCompletionSource<bool>();
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    work().ContinueWith(workTask =>
                    {
                        if (workTask.IsFaulted)
                            tcs.SetException(workTask.Exception!.GetBaseException());
                        else if (workTask.IsCanceled)
                            tcs.SetCanceled();
                        else
                            tcs.SetResult(true);
                    }, TaskScheduler.Default);
                });
                await tcs.Task;
            };

            var resolver = new UwpCoordinateResolver();
            var artifactDir = _options.ArtifactDirectory
                ?? Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "debug-artifacts");

            var context = new DebugHostContext(_options, resolver, artifactDir, runOnUiThread, RoverLog.Store);

            Directory.CreateDirectory(Path.Combine(artifactDir, "screenshots"));
            Directory.CreateDirectory(Path.Combine(artifactDir, "logs"));

            // Build capability list — logging first so it captures start events of other capabilities
            if (_options.EnableLogging)
                _capabilities.Add(new LoggingCapability());
            if (_options.EnableInputInjection)
                _capabilities.Add(new InputInjectionCapability());
            if (_options.EnableScreenshots)
                _capabilities.Add(new ScreenshotCapability());
            if (_options.ActionableApp != null)
                _capabilities.Add(new AppActionCapability(_options.ActionableApp));
            if (_options.EnableUiTree)
                _capabilities.Add(new UiTreeCapability());
            if (_options.EnableWindowManagement)
                _capabilities.Add(new WindowCapability());
            if (_options.EnableWaitFor)
                _capabilities.Add(new WaitForCapability());

            // Start capabilities
            foreach (var capability in _capabilities)
            {
                await capability.StartAsync(context).ConfigureAwait(false);
                RoverLog.Info("Rover.Host", $"Capability '{capability.Name}' started");
            }

            // Check InputInjection health
            foreach (var capability in _capabilities)
            {
                if (capability is InputInjectionCapability inputCapability && !inputCapability.InjectorAvailable)
                {
                    var error = inputCapability.InjectorError ?? "InputInjector.TryCreate() returned null";
                    RoverLog.Warn("Rover.InputInjection", $"Input injection unavailable — {error}");
                    System.Diagnostics.Debug.WriteLine($"[Rover] WARNING: Input injection unavailable — {error}");
                }
            }

            // Register capabilities as tools with the AppService ToolRegistry
            // The AppService background task will handle tool invocations
            var registry = new SimpleToolRegistry();
            foreach (var capability in _capabilities)
            {
                capability.RegisterTools(registry);
            }

            foreach (var tool in registry.Tools)
            {
                Rover.Uwp.AppService.ToolRegistry.Instance.RegisterTool(
                    tool.Key, tool.Value.Description, tool.Value.InputSchema, tool.Value.Handler);
            }

            RoverLog.Info("Rover.Host", $"Registered {registry.Tools.Count} tools with AppService ToolRegistry");
            System.Diagnostics.Debug.WriteLine($"[Rover] Registered {registry.Tools.Count} tools with AppService ToolRegistry");

            // Launch FullTrust MCP server process (if callback provided)
            if (_launchFullTrustProcess != null)
            {
                await _launchFullTrustProcess().ConfigureAwait(false);
            }

            RoverLog.Info("Rover.Host", $"'{_options.AppName}' MCP debug host started — port {_options.Port}");
            System.Diagnostics.Debug.WriteLine($"[Rover] '{_options.AppName}' MCP debug host started");
        }

        public async Task StopAsync()
        {
            RoverLog.Info("Rover.Host", "Debug host stopping");

            foreach (var capability in _capabilities)
                await capability.StopAsync().ConfigureAwait(false);

            _capabilities.Clear();

            RoverLog.Info("Rover.Host", "Debug host stopped");
            System.Diagnostics.Debug.WriteLine("[Rover] Debug host stopped.");
        }

        // ----------------------------------------------------------------
        // Auto-wiring of UWP crash, lifecycle, and XAML diagnostics
        // ----------------------------------------------------------------

        private static bool _diagnosticsWired;

        private static void WireUwpDiagnostics()
        {
            if (_diagnosticsWired) return;
            _diagnosticsWired = true;

            try
            {
                // ---- Crash surfaces ----
                Windows.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
                {
                    RoverLog.Fatal("App.UnhandledException", e.Message, e.Exception);
                    System.Diagnostics.Debug.WriteLine($"[Rover] FATAL App.UnhandledException: {e.Message}");
                };

                CoreApplication.UnhandledErrorDetected += (s, e) =>
                {
                    // Propagating the error moves it to an observable state; do not call
                    // e.UnhandledError.Propagate() here — just log it.
                    RoverLog.Fatal("CoreApplication.UnhandledErrorDetected", "Unhandled error detected in CoreApplication");
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    RoverLog.Error("TaskScheduler.UnobservedTaskException",
                        e.Exception?.Message ?? "Unobserved task exception", e.Exception);
                    e.SetObserved();
                };

                // ---- Lifecycle breadcrumbs ----
                Windows.UI.Xaml.Application.Current.Suspending += (s, e) =>
                    RoverLog.Info("App.Lifecycle", "Suspending");
                Windows.UI.Xaml.Application.Current.Resuming += (s, e) =>
                    RoverLog.Info("App.Lifecycle", "Resuming");

                CoreApplication.EnteredBackground += (s, e) =>
                    RoverLog.Info("App.Lifecycle", "Entered background");
                CoreApplication.LeavingBackground += (s, e) =>
                    RoverLog.Info("App.Lifecycle", "Leaving background");

                RoverLog.Info("Rover.Host", "UWP diagnostics wired (crash + lifecycle)");

#if DEBUG
                // ---- XAML binding failures (debug builds only — Microsoft guidance) ----
                Windows.UI.Xaml.Application.Current.DebugSettings.IsBindingTracingEnabled = true;
                Windows.UI.Xaml.Application.Current.DebugSettings.BindingFailed += (s, e) =>
                    RoverLog.Warn("XAML.BindingFailed", e.Message);
                RoverLog.Debug("Rover.Host", "XAML binding failure tracing enabled");
#endif
            }
            catch (Exception ex)
            {
                // Non-critical — if diagnostics wiring fails (e.g. no UI thread yet), log and continue
                RoverLog.Warn("Rover.Host", $"Could not wire some UWP diagnostics: {ex.Message}", ex);
                System.Diagnostics.Debug.WriteLine($"[Rover] WARNING: diagnostics wiring failed — {ex.Message}");
            }
        }

    }

    /// <summary>
    /// Simple tool registry that collects tool handlers without MCP SDK dependencies.
    /// </summary>
    internal class SimpleToolRegistry : IMcpToolRegistry
    {
        public Dictionary<string, SimpleToolEntry> Tools { get; } = new Dictionary<string, SimpleToolEntry>();

        public void RegisterTool(string name, string description, string inputSchema, Func<string, Task<string>> handler)
        {
            Tools[name] = new SimpleToolEntry { Description = description, InputSchema = inputSchema, Handler = handler };
        }
    }

    internal class SimpleToolEntry
    {
        public string Description { get; set; } = "";
        public string InputSchema { get; set; } = "{}";
        public Func<string, Task<string>> Handler { get; set; } = _ => Task.FromResult("{}");
    }
}

