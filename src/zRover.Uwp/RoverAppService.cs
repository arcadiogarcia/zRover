using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using zRover.Core.Logging;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Newtonsoft.Json;

namespace zRover.Uwp
{
    /// <summary>
    /// Drop-in handler for the zRover AppService background task.
    /// Extracts the ~80 lines of boilerplate from App.xaml.cs into a one-liner.
    /// <para>
    /// Usage in App.xaml.cs:
    /// <code>
    /// protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
    /// {
    ///     if (zRover.Uwp.RoverAppService.TryHandle(args)) return;
    ///     base.OnBackgroundActivated(args);
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public static class RoverAppService
    {
        /// <summary>
        /// Set to true when the host window closes, so the FullTrust process can detect
        /// shutdown via the heartbeat ping response.
        /// </summary>
        public static bool WindowClosed { get; set; }

        /// <summary>
        /// Attempts to handle the background activation as a zRover AppService request.
        /// Returns true if the activation was handled (caller should return immediately);
        /// false if it's not a zRover request and should be handled by other code.
        /// </summary>
        public static bool TryHandle(BackgroundActivatedEventArgs args)
        {
            var taskInstance = args.TaskInstance;
            if (!(taskInstance.TriggerDetails is AppServiceTriggerDetails details))
                return false;

            // Only handle our specific AppService
            if (details.AppServiceConnection.AppServiceName != "com.zrover.toolinvocation")
                return false;

            var deferral = taskInstance.GetDeferral();
            var connection = details.AppServiceConnection;

            taskInstance.Canceled += (s, r) =>
            {
                RoverLog.Warn("zRover.AppService", $"AppService task canceled: {r}");
                System.Diagnostics.Debug.WriteLine($"[zRover.AppService] Canceled: {r}");
            };

            connection.RequestReceived += async (sender, reqArgs) =>
            {
                var msgDeferral = reqArgs.GetDeferral();
                try
                {
                    var response = await HandleRequest(reqArgs.Request.Message).ConfigureAwait(false);
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
                RoverLog.Info("zRover.AppService", $"AppService connection closed: {closeArgs.Status}");
                System.Diagnostics.Debug.WriteLine($"[zRover.AppService] Closed: {closeArgs.Status}");
                deferral.Complete();
            };

            RoverLog.Info("zRover.AppService", "AppService connection ready");
            System.Diagnostics.Debug.WriteLine("[zRover.AppService] Ready");
            return true;
        }

        private static async Task<ValueSet> HandleRequest(ValueSet request)
        {
            var response = new ValueSet();
            var command = request.ContainsKey("command") ? request["command"] as string : null;

            RoverLog.Trace("zRover.AppService", $"Command received: {command}");
            System.Diagnostics.Debug.WriteLine($"[zRover.AppService] Command: {command}");

            switch (command)
            {
                case "ping":
                    response["status"] = "success";
                    response["message"] = "pong";
                    response["windowClosed"] = WindowClosed;
                    break;

                case "get_config":
                    response["status"]     = "success";
                    response["port"]       = DebugHost.CurrentPort.ToString();
                    response["appName"]    = DebugHost.CurrentAppName;
                    response["managerUrl"] = DebugHost.CurrentManagerUrl ?? "";
                    break;

                case "list_tools":
                    var registry = AppService.ToolRegistry.Instance;
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
                    RoverLog.Trace("zRover.AppService", $"list_tools: {allTools.Count} tools returned");
                    System.Diagnostics.Debug.WriteLine($"[zRover.AppService] Listed {allTools.Count} tools");
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

                    if (!AppService.ToolRegistry.Instance.TryGetTool(toolName!, out var handler))
                    {
                        response["status"] = "error";
                        response["message"] = $"Tool '{toolName}' not found";
                        break;
                    }

                    var toolResult = await handler(argsJson ?? "{}").ConfigureAwait(false);
                    response["status"] = "success";
                    response["result"] = toolResult.Text;
                    if (toolResult.HasImage)
                    {
                        response["resultImageBytes"]    = toolResult.ImageBytes;
                        response["resultImageMimeType"] = toolResult.ImageMimeType;
                    }
                    RoverLog.Trace("zRover.AppService", $"invoke_tool '{toolName}' succeeded");
                    System.Diagnostics.Debug.WriteLine($"[zRover.AppService] Tool '{toolName}' invoked");
                    break;

                default:
                    response["status"] = "error";
                    response["message"] = $"Unknown command: {command}";
                    break;
            }

            return response;
        }
    }
}
