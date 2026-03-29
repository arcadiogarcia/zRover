using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace zRover.Uwp.AppService
{
    /// <summary>
    /// AppService background task for handling tool invocation requests from the FullTrust MCP server.
    /// This replaces the StreamSocketListener approach which had network isolation issues.
    /// </summary>
    public sealed class RoverToolInvocationService : IBackgroundTask
    {
        private BackgroundTaskDeferral? _deferral;
        private AppServiceConnection? _connection;
        private readonly Dictionary<string, Func<string, Task<string>>> _tools = new();

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            System.Diagnostics.Debug.WriteLine("[RoverToolInvocationService] Background task starting");
            
            // Get a deferral so the service isn't terminated
            _deferral = taskInstance.GetDeferral();

            // Handle cancellation
            taskInstance.Canceled += OnTaskCanceled;

            // Get the app service connection
            var details = taskInstance.TriggerDetails as AppServiceTriggerDetails;
            _connection = details?.AppServiceConnection;
            
            if (_connection != null)
            {
                _connection.RequestReceived += OnRequestReceived;
                _connection.ServiceClosed += OnServiceClosed;
                System.Diagnostics.Debug.WriteLine("[RoverToolInvocationService] Service connected and ready");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[RoverToolInvocationService] ERROR: No AppServiceConnection");
                _deferral?.Complete();
            }
        }

        private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine($"[RoverToolInvocation Service] Service closed: {args.Status}");
            _deferral?.Complete();
        }

        private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var messageDeferral = args.GetDeferral();
            
            try
            {
                ValueSet request = args.Request.Message;
                ValueSet response = new ValueSet();

                System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Request received with {request.Count} keys");

                // Handle different request types
                if (request.ContainsKey("command"))
                {
                    var command = request["command"] as string;
                    System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Command: {command}");

                    switch (command)
                    {
                        case "ping":
                            response.Add("status", "success");
                            response.Add("message", "pong");
                            break;

                        case "list_tools":
                            HandleListTools(response);
                            break;

                        case "register_tool":
                            // Tool registration handled by UWP host
                            response.Add("status", "success");
                            break;

                        case "invoke_tool":
                            await HandleToolInvocationAsync(request, response);
                            break;

                        default:
                            response.Add("status", "error");
                            response.Add("message", $"Unknown command: {command}");
                            break;
                    }
                }
                else
                {
                    response.Add("status", "error");
                    response.Add("message", "No command specified");
                }

                await args.Request.SendResponseAsync(response);
                System.Diagnostics.Debug.WriteLine("[RoverToolInvocationService] Response sent");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] ERROR: {ex}");
                
                try
                {
                    var errorResponse = new ValueSet();
                    errorResponse.Add("status", "error");
                    errorResponse.Add("message", ex.Message);
                    errorResponse.Add("type", ex.GetType().Name);
                    await args.Request.SendResponseAsync(errorResponse);
                }
                catch { }
            }
            finally
            {
                messageDeferral.Complete();
            }
        }

        private async Task HandleToolInvocationAsync(ValueSet request, ValueSet response)
        {
            try
            {
                if (!request.ContainsKey("tool"))
                {
                    response.Add("status", "error");
                    response.Add("message", "No tool specified");
                    return;
                }

                var toolName = request["tool"] as string;
                var argumentsJson = request.ContainsKey("arguments") ? request["arguments"] as string : "{}";

                System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Invoking tool: {toolName}");

                // Get tool handler from singleton registry
                var registry = ToolRegistry.Instance;
                if (!registry.TryGetTool(toolName, out var handler))
                {
                    response.Add("status", "error");
                    response.Add("message", $"Tool '{toolName}' not found");
                    return;
                }

                // Invoke the tool
                var resultJson = await handler(argumentsJson ?? "{}");

                response.Add("status", "success");
                response.Add("result", resultJson);
                
                System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Tool invocation succeeded");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Tool invocation error: {ex}");
                response.Add("status", "error");
                response.Add("message", ex.Message);
                response.Add("type", ex.GetType().Name);
            }
        }

        private void HandleListTools(ValueSet response)
        {
            var registry = ToolRegistry.Instance;
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
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(toolList);
            response.Add("status", "success");
            response.Add("tools", json);
            System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Listed {allTools.Count} tools");
        }

        private void OnTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            System.Diagnostics.Debug.WriteLine($"[RoverToolInvocationService] Task canceled: {reason}");
            _deferral?.Complete();
        }
    }
}
