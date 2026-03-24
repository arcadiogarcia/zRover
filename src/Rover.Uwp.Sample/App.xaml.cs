using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Newtonsoft.Json;

namespace Rover.Uwp.Sample
{
    sealed partial class App : Application
    {
        private static bool _windowClosed;

        public App()
        {
            this.UnhandledException += (s, args) =>
            {
                args.Handled = true;
                try
                {
                    var path = System.IO.Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                        "crash.log");
                    System.IO.File.AppendAllText(path,
                        $"{DateTimeOffset.Now:o} UNHANDLED: {args.Exception}\r\n");
                }
                catch { }
            };
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                var crashLog = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "crash.log");
                System.IO.File.AppendAllText(crashLog, $"{DateTimeOffset.Now:o} OnLaunched START\r\n");
            }
            catch { }

            Frame? rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                Window.Current.Activate();

                // Track when the user closes the window so the FullTrust process can shut down
                Window.Current.Closed += (s, a) => _windowClosed = true;
            }

#if DEBUG
            try
            {
                // Register tools with the ToolRegistry (used by AppService background task)
                await Rover.Uwp.DebugHost.StartAsync("Rover.Uwp.Sample", port: 7331);
                System.Diagnostics.Debug.WriteLine("[Rover.Sample] Debug host started, tools registered");

                // Launch the FullTrust MCP server (packaged inside this app).
                // This must happen AFTER Window.Current.Activate() so the in-process
                // AppService infrastructure is ready to accept connections.
                try
                {
                    await Windows.ApplicationModel.FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("McpServer");
                    System.Diagnostics.Debug.WriteLine("[Rover.Sample] FullTrust MCP server launched");
                    await LogToFileAsync("FullTrust MCP server launched successfully");
                }
                catch (Exception ftEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rover.Sample] FullTrust launch failed: {ftEx}");
                    await LogToFileAsync($"FullTrust launch FAILED: {ftEx.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Rover.Sample] Startup error: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// In-process AppService handler. The FullTrust MCP server connects here
        /// to discover and invoke tools registered with ToolRegistry.Instance.
        /// </summary>
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            _ = LogToFileAsync("OnBackgroundActivated fired");

            var taskInstance = args.TaskInstance;
            taskInstance.Canceled += (s, r) =>
                System.Diagnostics.Debug.WriteLine($"[AppService] Canceled: {r}");

            if (taskInstance.TriggerDetails is AppServiceTriggerDetails details)
            {
                var deferral = taskInstance.GetDeferral();
                var connection = details.AppServiceConnection;

                connection.RequestReceived += async (sender, reqArgs) =>
                {
                    var msgDeferral = reqArgs.GetDeferral();
                    try
                    {
                        var response = await HandleAppServiceRequest(reqArgs.Request.Message);
                        await reqArgs.Request.SendResponseAsync(response);
                    }
                    catch (Exception ex)
                    {
                        var err = new ValueSet { { "status", "error" }, { "message", ex.Message } };
                        await reqArgs.Request.SendResponseAsync(err);
                    }
                    finally
                    {
                        msgDeferral.Complete();
                    }
                };

                connection.ServiceClosed += (sender, closeArgs) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[AppService] Closed: {closeArgs.Status}");
                    deferral.Complete();
                };

                System.Diagnostics.Debug.WriteLine("[AppService] In-process AppService ready");
            }
        }

        private async Task<ValueSet> HandleAppServiceRequest(ValueSet request)
        {
            var response = new ValueSet();
            var command = request.ContainsKey("command") ? request["command"] as string : null;

            System.Diagnostics.Debug.WriteLine($"[AppService] Command: {command}");

            switch (command)
            {
                case "ping":
                    response["status"] = "success";
                    response["message"] = "pong";
                    response["windowClosed"] = _windowClosed;
                    break;

                case "list_tools":
                    var registry = Rover.Uwp.AppService.ToolRegistry.Instance;
                    var allTools = registry.GetAllTools();
                    var toolList = new List<Dictionary<string, string>>();
                    foreach (var tool in allTools)
                    {
                        toolList.Add(new Dictionary<string, string>
                        {
                            { "name", tool.Name },
                            { "description", tool.Description },
                            { "inputSchema", tool.InputSchema }
                        });
                    }
                    response["status"] = "success";
                    response["tools"] = JsonConvert.SerializeObject(toolList);
                    System.Diagnostics.Debug.WriteLine($"[AppService] Listed {allTools.Count} tools");
                    break;

                case "invoke_tool":
                    var toolName = request.ContainsKey("tool") ? request["tool"] as string : null;
                    var argsJson = request.ContainsKey("arguments") ? request["arguments"] as string : "{}";

                    if (string.IsNullOrEmpty(toolName))
                    {
                        response["status"] = "error";
                        response["message"] = "No tool specified";
                        break;
                    }

                    if (!Rover.Uwp.AppService.ToolRegistry.Instance.TryGetTool(toolName, out var handler))
                    {
                        response["status"] = "error";
                        response["message"] = $"Tool '{toolName}' not found";
                        break;
                    }

                    var result = await handler(argsJson ?? "{}");
                    response["status"] = "success";
                    response["result"] = result;
                    System.Diagnostics.Debug.WriteLine($"[AppService] Tool '{toolName}' invoked successfully");
                    break;

                default:
                    response["status"] = "error";
                    response["message"] = $"Unknown command: {command}";
                    break;
            }

            return response;
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
#if DEBUG
            Rover.Uwp.DebugHost.StopAsync().GetAwaiter().GetResult();
#endif
            deferral.Complete();
        }

        private static async Task LogToFileAsync(string message)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("mcp-server.log",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.AppendTextAsync(file,
                    $"{DateTimeOffset.Now:o} {message}\r\n");
            }
            catch { /* best effort logging */ }
        }
    }
}
