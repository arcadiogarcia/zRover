using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.Core;
using zRover.Core.Sessions;
using zRover.Mcp;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace zRover.FullTrust.McpServer;

class Program
{
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    // P/Invoke for bringing the app's ApplicationFrameWindow to Z-order top before
    // input injection, so that inputInjectionBrokered touch events hit the correct window.
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;

    /// <summary>
    /// Finds the ApplicationFrameWindow that hosts the CoreWindow owned by
    /// <paramref name="processName"/> and moves it to Z-order top — no focus
    /// change, no show/hide — so that inputInjectionBrokered events land on it.
    /// </summary>
    static void BringWindowToZTop(string processName)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(processName);
            if (procs.Length == 0) return;
            var pids = new HashSet<uint>(procs.Select(p => (uint)p.Id));

            IntPtr target = IntPtr.Zero;
            // For packaged UWP apps the visible frame is an "ApplicationFrameWindow" owned
            // by ApplicationFrameHost.exe, which CONTAINS a "Windows.UI.Core.CoreWindow"
            // child that belongs to the UWP process.  We locate the frame by inspecting
            // the CoreWindow child's owning process — never touching the CoreWindow itself.
            EnumWindowsProc callback = (hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var cls = new System.Text.StringBuilder(128);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "ApplicationFrameWindow")
                {
                    var child = FindWindowEx(hWnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                    if (child != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(child, out uint childPid);
                        if (pids.Contains(childPid)) { target = hWnd; return false; }
                    }
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            // ARM64 / Windows 11 fallback: CoreWindow is not found as a Win32 child of AFW
            // via FindWindowEx on some hardware.  Match by window title keyword instead.
            if (target == IntPtr.Zero)
            {
                // "zRover.Uwp.Sample" → keyword "zRover"; matches title "zRover UWP Sample".
                var keyword = processName.Contains('.') ? processName.Split('.')[0] : processName;
                EnumWindowsProc titleCallback = (hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    var cls2 = new System.Text.StringBuilder(128);
                    GetClassName(hWnd, cls2, cls2.Capacity);
                    if (cls2.ToString() == "ApplicationFrameWindow")
                    {
                        var ttl = new System.Text.StringBuilder(256);
                        GetWindowText(hWnd, ttl, ttl.Capacity);
                        if (ttl.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        { target = hWnd; return false; }
                    }
                    return true;
                };
                EnumWindows(titleCallback, IntPtr.Zero);
                GC.KeepAlive(titleCallback);
            }

            if (target != IntPtr.Zero)
                // Z-order change only — no focus steal, no SWP_SHOWWINDOW.
                SetWindowPos(target, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }
        catch { /* best-effort */ }
    }

    static async Task Main(string[] args)
    {
        // Hide the console window immediately so it doesn't steal focus from the UWP app.
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd != IntPtr.Zero)
            ShowWindow(consoleHwnd, SW_HIDE);

        Console.Error.WriteLine("[McpServer] Starting zRover FullTrust MCP Server...");

        var useStdio   = args.Contains("--stdio");
        string? instanceId = ArgValue(args, "--instance-id");
        string  appVersion = ArgValue(args, "--app-version") ?? "1.0";

        // Connect to the UWP AppService first — all other config comes from there.
        var backend = new AppServiceToolBackend();
        await backend.ConnectAsync();
        Console.Error.WriteLine("[McpServer] Connected to UWP AppService");

        // Ask the UWP host for port + manager URL + app name via the AppService channel.
        // CLI args act as explicit overrides (useful for dev/standalone testing).
        var (configPort, configAppName, configManagerUrl) = await backend.GetConfigAsync();
        var port       = ArgValue(args, "--port") is { } portStr && int.TryParse(portStr, out var argPort)
                             ? argPort : configPort;
        var appName    = ArgValue(args, "--app-name") ?? configAppName;
        var managerUrl = ArgValue(args, "--manager-url") ?? configManagerUrl;

        Console.Error.WriteLine($"[McpServer] Config — port={port}, app={appName}, manager={managerUrl ?? "(none)"}");

        // Discover tools from the UWP app upfront
        var adapter = new McpToolRegistryAdapter();
        var tools = await backend.ListToolsAsync();
        Console.Error.WriteLine($"[McpServer] Discovered {tools.Count} tools");

        foreach (var tool in tools)
        {
            Console.Error.WriteLine($"[McpServer] Registering proxy tool: {tool.Name}");
            var capturedName = tool.Name;

            Func<string, Task<RoverToolResult>> handler = argsJson =>
                backend.InvokeToolAsync(capturedName, argsJson);

            // For any input-injection tool, bring the UWP window to the Z-order top first
            // so that inputInjectionBrokered events land on the correct window even when
            // another application (e.g. VS Code or a terminal) is in the foreground.
            if (capturedName.StartsWith("inject_", StringComparison.OrdinalIgnoreCase))
            {
                var inner = handler;
                handler = async argsJson =>
                {
                    BringWindowToZTop(appName);
                    await Task.Delay(80); // allow Z-order change to take effect
                    return await inner(argsJson);
                };
            }

            adapter.RegisterTool(tool.Name, tool.Description, tool.InputSchema, handler);
        }

        // Register with the Retriever if a manager URL was provided.
        // Do this after the MCP server is about to start so the manager can connect back immediately.
        if (managerUrl != null)
        {
            var mcpUrl  = $"http://localhost:{port}/mcp";
            var request = new SessionRegistrationRequest
            {
                AppName    = appName,
                Version    = appVersion,
                InstanceId = instanceId,
                McpUrl     = mcpUrl
            };

            _ = Task.Run(() => RegisterWithManagerAsync(managerUrl, request, backend.ShutdownToken));
        }

        if (useStdio)
        {
            Console.Error.WriteLine("[McpServer] Running in stdio mode");
            await McpServerRunner.RunStdioAsync(adapter, Console.OpenStandardInput(), Console.OpenStandardOutput(), backend.ShutdownToken);
        }
        else
        {
            Console.Error.WriteLine($"[McpServer] Running HTTP server on port {port}");
            await McpServerRunner.RunHttpAsync(adapter, port, backend.ShutdownToken, backend.LogPath);
        }

        Console.Error.WriteLine("[McpServer] MCP server shutting down");
        backend.Dispose();
    }

    /// <summary>
    /// Reads the value following a named flag from the args array.
    /// Returns null if the flag is absent or has no value.
    /// </summary>
    private static string? ArgValue(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>
    /// POSTs registration to the Retriever with exponential backoff.
    /// Runs fire-and-forget so it doesn't block the MCP server startup.
    /// </summary>
    private static async Task RegisterWithManagerAsync(
        string managerUrl,
        SessionRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url  = managerUrl.TrimEnd('/') + "/sessions/register";
        var body = JsonSerializer.Serialize(request);

        const int maxAttempts = 8;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var content  = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await http.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine($"[McpServer] Registered with Retriever. Response: {responseBody}");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpServer] Manager registration attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 30)), cancellationToken);
            }
        }

        Console.Error.WriteLine("[McpServer] Could not reach Retriever after all attempts — running standalone.");
    }
}

/// <summary>
/// Reusable server setup: runs the MCP server over stdio or HTTP.
/// Tools are pre-registered in the <see cref="McpToolRegistryAdapter"/> by the caller.
/// </summary>
internal static class McpServerRunner
{
    /// <summary>Runs the MCP server over stdin/stdout (classic FullTrustProcess mode).</summary>
    public static async Task RunStdioAsync(
        McpToolRegistryAdapter adapter,
        System.IO.Stream input,
        System.IO.Stream output,
        CancellationToken cancellationToken = default)
    {
        var options = CreateOptions(adapter);

        await using var transport = new StreamServerTransport(input, output, "zRover");
        var server = ModelContextProtocol.Server.McpServer.Create(transport, options);

        Console.Error.WriteLine("[McpServer] MCP server running (stdio)");
        try
        {
            await server.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("[McpServer] Stdio server stopped (host app closed)");
        }
    }

    /// <summary>Runs the MCP server as an HTTP endpoint using ASP.NET Core + MapMcp().</summary>
    public static async Task RunHttpAsync(
        McpToolRegistryAdapter adapter,
        int port,
        CancellationToken cancellationToken = default,
        string logPath = "")
    {
        var builder = WebApplication.CreateBuilder(new string[] { "--urls", $"http://localhost:{port}" });

        // Register pre-discovered tools as McpServerTool singletons so the HTTP
        // transport can resolve them from DI via IEnumerable<McpServerTool>.
        foreach (var tool in adapter.Tools)
        {
            builder.Services.AddSingleton<ModelContextProtocol.Server.McpServerTool>(tool);
        }

        builder.Services.AddMcpServer(options => ApplyOptions(options, adapter)).WithHttpTransport();

        var app = builder.Build();

        // Capture any unhandled exceptions and write them to the log for diagnostics.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (Exception ex)
            {
                var msg = $"Unhandled HTTP pipeline exception: {ex}";
                Console.Error.WriteLine($"[McpServer] {msg}");
                if (!string.IsNullOrEmpty(logPath))
                    try { System.IO.File.AppendAllText(logPath, $"{DateTimeOffset.Now:o} {msg}\r\n"); } catch { }
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    var body = System.Text.Json.JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message, detail = ex.ToString() });
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                }
            }
        });

        app.MapMcp("/mcp");

        // Stop the HTTP server when the UWP host app closes
        cancellationToken.Register(() => app.Lifetime.StopApplication());

        Console.Error.WriteLine($"[McpServer] MCP server running on http://localhost:{port}/mcp");
        await app.RunAsync();
    }

    private static McpServerOptions CreateOptions(McpToolRegistryAdapter adapter)
    {
        var options = new McpServerOptions();
        ApplyOptions(options, adapter);
        return options;
    }

    /// <summary>
    /// Single source of truth for <see cref="McpServerOptions"/> so that the
    /// stdio and HTTP transports advertise identical capabilities and the same
    /// tool collection. Both transports declare <c>tools.listChanged = true</c>;
    /// the SDK will silently downgrade that to <c>null</c> if the underlying
    /// transport is the stateless Streamable HTTP variant (which cannot send
    /// unsolicited notifications), so the value is always safe to set.
    /// </summary>
    private static void ApplyOptions(McpServerOptions options, McpToolRegistryAdapter adapter)
    {
        options.ServerInfo = new Implementation { Name = "zRover", Version = "1.0.0" };
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = true }
        };
        options.ToolCollection = adapter.Tools;
    }
}

/// <summary>
/// Real <see cref="IToolBackend"/> that communicates with the UWP app via
/// <see cref="AppServiceConnection"/> (same-package IPC).
/// </summary>
internal sealed class AppServiceToolBackend : IToolBackend, IDisposable
{
    private AppServiceConnection? _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _logPath = System.IO.Path.Combine(
        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
        "fulltrust-server.log");

    /// <summary>Exposes the log path so HTTP middleware can write to the same log file.</summary>
    public string LogPath => _logPath;

    /// <summary>
    /// Token that is cancelled when the AppService connection to the UWP host is lost
    /// (e.g. the UWP app is closed). Use this to shut down the MCP server.
    /// </summary>
    public CancellationToken ShutdownToken => _cts.Token;

    private void Log(string msg)
    {
        var line = $"{DateTimeOffset.Now:o} {msg}";
        Console.Error.WriteLine($"[McpServer] {msg}");
        try { System.IO.File.AppendAllText(_logPath, line + "\r\n"); } catch { }
    }

    public async Task ConnectAsync()
    {
        var familyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
        Log($"PackageFamilyName: {familyName}");

        // The UWP app may not have its AppService ready immediately when we start.
        // Retry with backoff to handle the race condition.
        const int maxRetries = 15;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _connection = new AppServiceConnection
            {
                AppServiceName = "com.zrover.toolinvocation",
                PackageFamilyName = familyName
            };

            _connection.ServiceClosed += (sender, closeArgs) =>
            {
                Log($"AppService connection closed: {closeArgs.Status} — shutting down");
                if (!_cts.IsCancellationRequested)
                    _cts.Cancel();
            };

            var status = await _connection.OpenAsync();
            if (status == AppServiceConnectionStatus.Success)
            {
                Log($"AppService connected on attempt {attempt}");
                StartHeartbeat();
                return;
            }

            Log($"AppService connect attempt {attempt}/{maxRetries}: {status}");
            _connection.Dispose();
            _connection = null;

            if (attempt < maxRetries)
                await Task.Delay(2000); // 2s between retries
        }

        Log("Failed to connect to AppService after all retries");
        throw new Exception($"Failed to connect to AppService after {maxRetries} attempts");
    }

    public async Task<(int Port, string AppName, string? ManagerUrl)> GetConfigAsync()
    {
        var request = new ValueSet { { "command", "get_config" } };
        var response = await _connection!.SendMessageAsync(request);

        if (response.Status != AppServiceResponseStatus.Success)
            throw new Exception($"get_config failed: {response.Status}");

        var portStr    = response.Message.TryGetValue("port",       out var p) ? p as string : null;
        var appName    = response.Message.TryGetValue("appName",    out var a) ? a as string : null;
        var managerUrl = response.Message.TryGetValue("managerUrl", out var m) ? m as string : null;

        int port = int.TryParse(portStr, out var parsed) ? parsed : RoverPorts.App;
        return (port, appName ?? "UnknownApp", string.IsNullOrEmpty(managerUrl) ? null : managerUrl);
    }

    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync()
    {
        var request = new ValueSet { { "command", "list_tools" } };
        var response = await _connection!.SendMessageAsync(request);

        if (response.Status != AppServiceResponseStatus.Success)
            throw new Exception($"list_tools failed: {response.Status}");

        var toolsJson = response.Message["tools"] as string ?? "[]";
        var tools = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DiscoveredTool>>(toolsJson)
                    ?? new List<DiscoveredTool>();
        return tools;
    }

    public async Task<RoverToolResult> InvokeToolAsync(string toolName, string argumentsJson)
    {
        var request = new ValueSet
        {
            { "command", "invoke_tool" },
            { "tool", toolName },
            { "arguments", argumentsJson }
        };

        var response = await _connection!.SendMessageAsync(request);

        if (response.Status != AppServiceResponseStatus.Success)
            throw new Exception($"AppService call failed: {response.Status}");

        var status = response.Message["status"] as string;
        if (status == "success")
        {
            var text = response.Message["result"] as string ?? "{}";
            var imageBytes = response.Message.TryGetValue("resultImageBytes", out var ib)
                ? ib as byte[] : null;
            var mimeType = response.Message.TryGetValue("resultImageMimeType", out var mt)
                ? mt as string : null;
            return imageBytes != null
                ? RoverToolResult.WithImage(text, imageBytes, mimeType ?? "image/png")
                : RoverToolResult.FromText(text);
        }

        var error = response.Message.ContainsKey("error")
            ? response.Message["error"] as string
            : response.Message.ContainsKey("message")
                ? response.Message["message"] as string
                : "Unknown error";
        throw new Exception($"Tool invocation failed: {error}");
    }

    /// <summary>
    /// Periodically pings the UWP AppService. If the ping fails (host app closed/suspended),
    /// the shutdown token is cancelled so the MCP server exits.
    /// </summary>
    private void StartHeartbeat()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Log("Heartbeat monitor started");
                const int intervalMs = 3000;
                const int maxFailures = 2;
                int failures = 0;

                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs);
                    if (_cts.IsCancellationRequested) break;

                    try
                    {
                        var ping = new ValueSet { { "command", "ping" } };
                        var resp = await _connection!.SendMessageAsync(ping);
                        if (resp.Status == AppServiceResponseStatus.Success)
                        {
                            bool windowClosed = resp.Message.ContainsKey("windowClosed")
                                                && resp.Message["windowClosed"] is bool wc && wc;
                            if (windowClosed)
                            {
                                Log("Host app window closed — triggering shutdown");
                                if (!_cts.IsCancellationRequested)
                                    _cts.Cancel();
                                break;
                            }
                            failures = 0;
                            continue;
                        }
                        failures++;
                        Log($"Heartbeat ping failed (status={resp.Status}), failure {failures}/{maxFailures}");
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Log($"Heartbeat ping exception: {ex.Message}, failure {failures}/{maxFailures}");
                    }

                    if (failures >= maxFailures)
                    {
                        Log("Host app unreachable — triggering shutdown");
                        if (!_cts.IsCancellationRequested)
                            _cts.Cancel();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Heartbeat task crashed: {ex}");
            }
        });
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _cts.Dispose();
        _connection?.Dispose();
        _connection = null;
    }
}
