using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace zRover.FullTrust.McpServer
{
    /// <summary>
    /// AppServiceConnection-based client for communicating with the UWP app.
    /// Replaces the HTTP-based approach which had network isolation issues.
    /// </summary>
    public sealed class AppServiceToolInvoker : IDisposable
    {
        private AppServiceConnection? _connection;
        private readonly string _packageFamilyName;
        private readonly string _serviceName;
        private bool _disposed;

        public AppServiceToolInvoker(string packageFamilyName, string serviceName = "com.zrover.toolinvocation")
        {
            _packageFamilyName = packageFamilyName;
            _serviceName = serviceName;
        }

        public async Task<bool> ConnectAsync()
        {
            if (_connection != null)
                return true;

            _connection = new AppServiceConnection
            {
                AppServiceName = _serviceName,
                PackageFamilyName = _packageFamilyName
            };

            _connection.ServiceClosed += OnServiceClosed;

            Console.WriteLine($"[AppServiceToolInvoker] Connecting to '{_serviceName}' in package '{_packageFamilyName}'...");
            
            var status = await _connection.OpenAsync();
            
            if (status == AppServiceConnectionStatus.Success)
            {
                Console.WriteLine("[AppServiceToolInvoker] ✅ Connected to UWP AppService");
                return true;
            }
            else
            {
                Console.WriteLine($"[AppServiceToolInvoker] ❌ Connection failed: {status}");
                _connection.Dispose();
                _connection = null;
                return false;
            }
        }

        public async Task<bool> PingAsync()
        {
            if (_connection == null)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

            var message = new ValueSet();
            message.Add("command", "ping");

            var response = await _connection.SendMessageAsync(message);
            
            if (response.Status == AppServiceResponseStatus.Success &&
                response.Message.ContainsKey("status") &&
                response.Message["status"] as string == "success")
            {
                return true;
            }

            return false;
        }

        public async Task<string> InvokeToolAsync(string toolName, string argumentsJson)
        {
            if (_connection == null)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

            Console.WriteLine($"[AppServiceToolInvoker] Invoking tool '{toolName}'...");

            var message = new ValueSet();
            message.Add("command", "invoke_tool");
            message.Add("tool", toolName);
            message.Add("arguments", argumentsJson);

            var response = await _connection.SendMessageAsync(message);

            if (response.Status != AppServiceResponseStatus.Success)
            {
                throw new Exception($"AppService request failed: {response.Status}");
            }

            if (!response.Message.ContainsKey("status"))
            {
                throw new Exception("AppService response missing 'status' field");
            }

            var status = response.Message["status"] as string;
            
            if (status == "success")
            {
                if (response.Message.ContainsKey("result"))
                {
                    var result = response.Message["result"] as string;
                    Console.WriteLine($"[AppServiceToolInvoker] ✅ Tool invocation succeeded");
                    return result ?? "{}";
                }
                else
                {
                    return "{}";
                }
            }
            else
            {
                var errorMessage = response.Message.ContainsKey("message") 
                    ? response.Message["message"] as string 
                    : "Unknown error";
                
                Console.WriteLine($"[AppServiceToolInvoker] ❌ Tool invocation failed: {errorMessage}");
                throw new Exception($"Tool invocation failed: {errorMessage}");
            }
        }

        private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            Console.WriteLine($"[AppServiceToolInvoker] Service closed: {args.Status}");
            _connection?.Dispose();
            _connection = null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Dispose();
                _connection = null;
                _disposed = true;
            }
        }
    }
}
