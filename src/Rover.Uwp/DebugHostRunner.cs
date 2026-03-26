using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Rover.Core;
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

            var context = new DebugHostContext(_options, resolver, artifactDir, runOnUiThread);

            Directory.CreateDirectory(Path.Combine(artifactDir, "screenshots"));
            Directory.CreateDirectory(Path.Combine(artifactDir, "logs"));

            // Build capability list
            if (_options.EnableInputInjection)
                _capabilities.Add(new InputInjectionCapability());
            if (_options.EnableScreenshots)
                _capabilities.Add(new ScreenshotCapability());
            if (_options.ActionableApp != null)
                _capabilities.Add(new AppActionCapability(_options.ActionableApp));

            // Start capabilities
            foreach (var capability in _capabilities)
            {
                await capability.StartAsync(context).ConfigureAwait(false);
            }

            // Check InputInjection health
            foreach (var capability in _capabilities)
            {
                if (capability is InputInjectionCapability inputCapability && !inputCapability.InjectorAvailable)
                {
                    var error = inputCapability.InjectorError ?? "InputInjector.TryCreate() returned null";
                    System.Diagnostics.Debug.WriteLine($"[Rover] WARNING: Input injection unavailable — {error}");
                }
            }

            // Start IPC listener for tool invocation from FullTrust process
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

            System.Diagnostics.Debug.WriteLine($"[Rover] Registered {registry.Tools.Count} tools with AppService ToolRegistry");

            // Launch FullTrust MCP server process (if callback provided)
            if (_launchFullTrustProcess != null)
            {
                await _launchFullTrustProcess().ConfigureAwait(false);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Rover] '{_options.AppName}' MCP debug host started");
        }

        public async Task StopAsync()
        {
            foreach (var capability in _capabilities)
                await capability.StopAsync().ConfigureAwait(false);

            _capabilities.Clear();

            System.Diagnostics.Debug.WriteLine("[Rover] Debug host stopped.");
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

