using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using zRover.Core;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace zRover.Uwp.Transport
{
    /// <summary>
    /// Lightweight HTTP listener that handles tool invocation requests from the FullTrust MCP server.
    /// Listens on a different port than the external MCP endpoint (e.g., 7332 for IPC, 7331 for MCP).
    /// </summary>
    internal sealed class UwpToolInvocationListener : IDisposable
    {
        private readonly int _port;
        private readonly Dictionary<string, Func<string, Task<string>>> _tools;
        private StreamSocketListener? _listener;
        private CancellationTokenSource? _cts;

        public UwpToolInvocationListener(int port)
        {
            _port = port;
            _tools = new Dictionary<string, Func<string, Task<string>>>();
        }

        public void RegisterTool(string name, Func<string, Task<string>> handler)
        {
            _tools[name] = handler;
        }

        public async Task StartAsync()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] UwpToolInvocationListener.StartAsync() called for port {_port}");
            
            try
            {
                _cts = new CancellationTokenSource();
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] CancellationTokenSource created");
                
                _listener = new StreamSocketListener();
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] StreamSocketListener created");
                
                // Set control options for loopback
                _listener.Control.QualityOfService = Windows.Networking.Sockets.SocketQualityOfService.Normal;
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] QualityOfService set");
                
                _listener.ConnectionReceived += OnConnectionReceived;
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] ConnectionReceived event handler attached");
                
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] About to call BindServiceNameAsync for port {_port} on all interfaces...");
                // Bind to all interfaces (both IPv4 and IPv6)
                await _listener.BindServiceNameAsync(_port.ToString());
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] BindServiceNameAsync() completed successfully!");
                
                // Verify listener is still valid
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Listener object: {(_listener != null ? "Valid" : "NULL")}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Listener port: {_listener.Information.LocalPort}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Keeping strong reference to listener");
                
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] ✅ Tool invocation listener STARTED on port {_port}");
                System.Diagnostics.Debug.WriteLine($"[zRover.Uwp] ✅ Tool invocation listener STARTED on port {_port}");
                
                // Write success log
                await WriteLogFileAsync("listener-success.log", log.ToString()).ConfigureAwait(false);
                
                // SELF-TEST: Try to connect from within the UWP app itself
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000); // Give listener time to fully start
                    await SelfTestConnection();
                });
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] ❌ ERROR: {ex.GetType().FullName}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Message: {ex.Message}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] HRESULT: 0x{ex.HResult:X8}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Stack: {ex.StackTrace}");
                
                System.Diagnostics.Debug.WriteLine($"[zRover.Uwp] ❌ ERROR starting listener: {ex.Message}");
                
                // Write error log
                await WriteLogFileAsync("listener-error.log", log.ToString()).ConfigureAwait(false);
                throw;
            }
        }
        
        private async Task SelfTestConnection()
        {
            var testLog = new StringBuilder();
            testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Starting connection test to port {_port}");
            
            try
            {
                using (var testSocket = new Windows.Networking.Sockets.StreamSocket())
                {
                    testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Creating socket...");
                    
                    var hostName = new Windows.Networking.HostName("localhost");
                    testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Connecting to localhost:{_port}...");
                    
                    await testSocket.ConnectAsync(hostName, _port.ToString());
                    testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: ✅ Connected successfully!");
                    
                    // Send a simple HTTP GET request
                    using (var writer = new System.IO.StreamWriter(testSocket.OutputStream.AsStreamForWrite()))
                    {
                        testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Sending GET /ping request...");
                        await writer.WriteAsync("GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n");
                        await writer.FlushAsync();
                        testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Request sent");
                    }
                    
                    // Read response
                    using (var reader = new System.IO.StreamReader(testSocket.InputStream.AsStreamForRead()))
                    {
                        testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Reading response...");
                        var response = await reader.ReadToEndAsync();
                        testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Response received: {response.Substring(0, Math.Min(100, response.Length))}");
                    }
                    
                    testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: ✅✅✅ SUCCESS! Listener is working!");
                }
            }
            catch (Exception ex)
            {
                testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: ❌ FAILED: {ex.GetType().FullName}");
                testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: Message: {ex.Message}");
                testLog.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] SELF-TEST: HR ESULT: 0x{ex.HResult:X8}");
            }
            finally
            {
                await WriteLogFileAsync("self-test.log", testLog.ToString()).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(testLog.ToString());
            }
        }
        
        private static async Task WriteLogFileAsync(string filename, string content)
        {
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    filename,
                    Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, content);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Dispose();
            _listener = null;
            return Task.CompletedTask;
        }

        public void Dispose() => StopAsync().GetAwaiter().GetResult();

        private async void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            // IMMEDIATELY write a trigger file to prove this method was called
            try
            {
                var triggerFile = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    $"connection-trigger-{DateTimeOffset.Now:HHmmss-fff}.txt",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.WriteTextAsync(triggerFile, $"OnConnectionReceived called at {DateTimeOffset.Now:O}");
            }
            catch { }
            
            var connectionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var log = new System.Text.StringBuilder();
            log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] OnConnectionReceived triggered! ConnectionId={connectionId}");
            log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Remote: {args.Socket.Information.RemoteAddress}:{args.Socket.Information.RemotePort}");
            
            try
            {
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Getting streams...");
                using (args.Socket)
                {
                    var inputStream = args.Socket.InputStream.AsStreamForRead();
                    var outputStream = args.Socket.OutputStream.AsStreamForWrite();
                    log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Calling HandleConnectionAsync...");
                    await HandleConnectionAsync(inputStream, outputStream).ConfigureAwait(false);
                    log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] HandleConnectionAsync completed");
                }
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] ✅ Connection handled successfully");
                
                await WriteLogFileAsync($"connection-{connectionId}.log", log.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] ❌ ERROR: {ex.GetType().FullName}");
                log.AppendLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Message: {ex.Message}");
                
                System.Diagnostics.Debug.WriteLine($"[zRover.Uwp] IPC connection error: {ex.Message}");
                
                await WriteLogFileAsync($"connection-{connectionId}-error.log", log.ToString()).ConfigureAwait(false);
            }
        }

        private async Task HandleConnectionAsync(Stream inputStream, Stream outputStream)
        {
            var (method, path, headers, bodyBytes) = await ReadHttpRequestAsync(inputStream).ConfigureAwait(false);

            if (method == "GET" && path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            {
                // Health check endpoint
                await WriteHttpResponseAsync(outputStream, 200, "text/plain", Encoding.UTF8.GetBytes("pong")).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path.Equals("/invoke-tool", StringComparison.OrdinalIgnoreCase))
            {
                await HandleToolInvocationAsync(outputStream, bodyBytes).ConfigureAwait(false);
                return;
            }

            await WriteHttpResponseAsync(outputStream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")).ConfigureAwait(false);
        }

        private async Task HandleToolInvocationAsync(Stream outputStream, byte[] bodyBytes)
        {
            try
            {
                var bodyJson = Encoding.UTF8.GetString(bodyBytes);
                var request = JsonConvert.DeserializeObject<ToolInvocationRequest>(bodyJson);

                if (request == null || string.IsNullOrEmpty(request.Tool))
                {
                    await WriteHttpResponseAsync(outputStream, 400, "text/plain", Encoding.UTF8.GetBytes("Invalid request")).ConfigureAwait(false);
                    return;
                }

                if (!_tools.TryGetValue(request.Tool, out var handler))
                {
                    await WriteHttpResponseAsync(outputStream, 404, "text/plain", Encoding.UTF8.GetBytes($"Tool '{request.Tool}' not found")).ConfigureAwait(false);
                    return;
                }

                // Invoke the tool with arguments
                var argsJson = request.Arguments != null
                    ? JsonConvert.SerializeObject(request.Arguments)
                    : "{}";

                var result = await handler(argsJson).ConfigureAwait(false);

                // Return result
                await WriteHttpResponseAsync(outputStream, 200, "application/json", Encoding.UTF8.GetBytes(result)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[zRover.Uwp] Tool invocation error: {ex}");
                var error = new { success = false, error = ex.Message };
                var errorJson = JsonConvert.SerializeObject(error);
                await WriteHttpResponseAsync(outputStream, 500, "application/json", Encoding.UTF8.GetBytes(errorJson)).ConfigureAwait(false);
            }
        }

        private class ToolInvocationRequest
        {
            public string Tool { get; set; } = "";
            public object? Arguments { get; set; }
        }

        // Minimal HTTP parser (reuse logic from HttpMcpListener)
        private static async Task<(string method, string path, Dictionary<string, string> headers, byte[] body)>
            ReadHttpRequestAsync(Stream stream)
        {
            var reader = new BufferedHttpReader(stream);

            var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var rawPath = parts.Length > 1 ? parts[1] : "/";
            var path = rawPath.Split('?')[0];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) break;
                var idx = line.IndexOf(':');
                if (idx > 0)
                    headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }

            byte[] body = Array.Empty<byte>();
            if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) && len > 0)
            {
                body = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = await stream.ReadAsync(body, read, len - read).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }

            return (method, path, headers, body);
        }

        private static async Task WriteHttpResponseAsync(Stream stream, int statusCode, string contentType, byte[] body)
        {
            var statusText = statusCode switch
            {
                200 => "OK",
                202 => "Accepted",
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                500 => "Internal Server Error",
                _ => "Unknown"
            };

            var header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);

            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            if (body.Length > 0)
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    // Reuse BufferedHttpReader from HttpMcpListener
    internal class BufferedHttpReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _bufferPos;
        private int _bufferLen;

        public BufferedHttpReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<string> ReadLineAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_bufferPos >= _bufferLen)
                {
                    _bufferPos = 0;
                    _bufferLen = await _stream.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
                    if (_bufferLen == 0) break;
                }

                byte b = _buffer[_bufferPos++];
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                        sb.Length--;
                    break;
                }
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
