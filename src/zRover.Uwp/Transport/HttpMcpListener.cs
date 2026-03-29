using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace zRover.Uwp.Transport
{
    /// <summary>
    /// A lightweight HTTP/1.1 server built on <see cref="StreamSocketListener"/> that
    /// routes MCP Streamable HTTP (POST /mcp) and optional SSE GET requests to the
    /// official SDK's <see cref="StreamableHttpServerTransport"/>.
    ///
    /// Auth token, when provided, is checked against the
    /// <c>Authorization: Bearer &lt;token&gt;</c> header on every request.
    /// </summary>
    internal sealed class HttpMcpListener : IDisposable
    {
        private const string McpPath = "/mcp";

        private readonly int _port;
        private readonly string? _authToken;
        private readonly StreamableHttpServerTransport _transport;
        private StreamSocketListener? _listener;
        private CancellationTokenSource? _cts;

        public HttpMcpListener(int port, string? authToken, StreamableHttpServerTransport transport)
        {
            _port = port;
            _authToken = authToken;
            _transport = transport;
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            await _listener.BindServiceNameAsync(_port.ToString());
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
            try
            {
                using (args.Socket)
                {
                    var inputStream = args.Socket.InputStream.AsStreamForRead();
                    var outputStream = args.Socket.OutputStream.AsStreamForWrite();
                    await HandleConnectionAsync(inputStream, outputStream).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[zRover] Connection error: {ex.Message}");
            }
        }

        private async Task HandleConnectionAsync(Stream inputStream, Stream outputStream)
        {
            var (method, path, headers, bodyBytes) = await ReadHttpRequestAsync(inputStream).ConfigureAwait(false);

            // Auth check
            if (_authToken != null)
            {
                if (!headers.TryGetValue("authorization", out var auth)
                    || auth != $"Bearer {_authToken}")
                {
                    await WriteHttpResponseAsync(outputStream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized")).ConfigureAwait(false);
                    return;
                }
            }

            if (method == "POST" && path.StartsWith(McpPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandlePostAsync(outputStream, bodyBytes).ConfigureAwait(false);
            }
            else if (method == "GET" && path.StartsWith(McpPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleGetAsync(outputStream).ConfigureAwait(false);
            }
            else
            {
                await WriteHttpResponseAsync(outputStream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")).ConfigureAwait(false);
            }
        }

        private async Task HandlePostAsync(Stream outputStream, byte[] bodyBytes)
        {
            JsonRpcMessage? message;
            try
            {
                var bodyJson = Encoding.UTF8.GetString(bodyBytes);
                message = System.Text.Json.JsonSerializer.Deserialize<JsonRpcMessage>(
                    bodyJson, McpJsonUtilities.DefaultOptions);
            }
            catch
            {
                await WriteHttpResponseAsync(outputStream, 400, "text/plain", Encoding.UTF8.GetBytes("Bad Request")).ConfigureAwait(false);
                return;
            }

            if (message == null)
            {
                await WriteHttpResponseAsync(outputStream, 400, "text/plain", Encoding.UTF8.GetBytes("Invalid JSON-RPC")).ConfigureAwait(false);
                return;
            }

            using var responseBodyStream = new MemoryStream();
            bool wroteData = await _transport.HandlePostRequestAsync(message, responseBodyStream).ConfigureAwait(false);

            if (wroteData)
            {
                var body = responseBodyStream.ToArray();
                await WriteHttpResponseAsync(outputStream, 200, "application/json", body).ConfigureAwait(false);
            }
            else
            {
                await WriteHttpResponseAsync(outputStream, 202, "text/plain", Encoding.UTF8.GetBytes("Accepted")).ConfigureAwait(false);
            }
        }

        private async Task HandleGetAsync(Stream outputStream)
        {
            // Write SSE headers — keep this connection open while the transport pumps events.
            var header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await outputStream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            await outputStream.FlushAsync().ConfigureAwait(false);

            await _transport.HandleGetRequestAsync(outputStream, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }

        // -------------------------------------------------------
        // Minimal HTTP/1.1 parser — sufficient for MCP clients.
        // -------------------------------------------------------

        private static async Task<(string method, string path, Dictionary<string, string> headers, byte[] body)>
            ReadHttpRequestAsync(Stream stream)
        {
            var reader = new BufferedHttpReader(stream);

            // Request line
            var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var rawPath = parts.Length > 1 ? parts[1] : "/";
            var path = rawPath.Split('?')[0]; // strip query string

            // Headers
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) break;
                var idx = line.IndexOf(':');
                if (idx > 0)
                    headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }

            // Body
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
                _ => "Error"
            };

            var header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Connection: close\r\n\r\n";

            var headerBytes = Encoding.UTF8.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            if (body.Length > 0)
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        // Reads CRLF-terminated lines from a network stream without over-reading.
        private sealed class BufferedHttpReader
        {
            private readonly Stream _stream;
            private readonly byte[] _buf = new byte[1];

            public BufferedHttpReader(Stream stream) => _stream = stream;

            public async Task<string> ReadLineAsync()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    int n = await _stream.ReadAsync(_buf, 0, 1).ConfigureAwait(false);
                    if (n == 0) break;
                    char c = (char)_buf[0];
                    if (c == '\n') break;
                    if (c != '\r') sb.Append(c);
                }
                return sb.ToString();
            }
        }
    }
}
