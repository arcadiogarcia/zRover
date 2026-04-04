using System.IO.Pipes;
using Microsoft.UI.Dispatching;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.BackgroundManager;
using zRover.BackgroundManager.Packages;
using zRover.BackgroundManager.Server;
using zRover.BackgroundManager.Sessions;
using zRover.Core.Sessions;
using zRover.Mcp;

namespace zRover.BackgroundManager;

public class Program
{
    private const string MutexName = "zRover.BackgroundManager.SingleInstance";
    private const string PipeName  = "zRover.BackgroundManager.Activate";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Another instance is running — forward our args and exit.
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };

                // Check if launched with a zrover:// URI argument
                var uriArg = args.FirstOrDefault(a => a.StartsWith("zrover://", StringComparison.OrdinalIgnoreCase));
                if (uriArg != null)
                {
                    // Parse and convert the URI into a pipe command
                    var uri = new Uri(uriArg);
                    var host = uri.Host.ToLowerInvariant();
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

                    switch (host)
                    {
                        case "enable-external":
                            var portStr = query["port"];
                            writer.WriteLine(portStr != null ? $"enable-external:{portStr}" : "enable-external");
                            break;
                        case "disable-external":
                            writer.WriteLine("disable-external");
                            break;
                        case "enable-package-install":
                            writer.WriteLine("enable-package-install");
                            break;
                        case "disable-package-install":
                            writer.WriteLine("disable-package-install");
                            break;
                        case "connect":
                            var url = query["url"] ?? "";
                            var token = query["token"];
                            writer.WriteLine(token != null ? $"connect:{url}|{token}" : $"connect:{url}");
                            break;
                        default:
                            writer.WriteLine("activate");
                            break;
                    }
                }
                else
                {
                    writer.WriteLine("activate");
                }
                pipe.WaitForPipeDrain();
            }
            catch { /* Best effort — the other instance will surface eventually. */ }
            return;
        }

        var builder = WebApplication.CreateBuilder(args);

        // ── Core services ──────────────────────────────────────────────────────────
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
        builder.Services.AddSingleton<McpToolRegistryAdapter>();
        builder.Services.AddSingleton<ActiveSessionProxy>();
        builder.Services.AddSingleton<ExternalAccessManager>();
        builder.Services.AddSingleton<RemoteManagerRegistry>();
        builder.Services.AddSingleton<PackageStagingManager>();
        builder.Services.AddSingleton<DevCertManager>();
        builder.Services.AddSingleton<IDevCertManager>(sp => sp.GetRequiredService<DevCertManager>());
        builder.Services.AddSingleton<PackageInstallManager>();
        builder.Services.AddSingleton<IDevicePackageManager, LocalDevicePackageManager>();
        builder.Services.AddHostedService<Worker>();

        // ── Master MCP server ──────────────────────────────────────────────────────
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "zRover.Manager", Version = "1.0.0" };
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true }
            };
        }).WithHttpTransport();

        var webApp = builder.Build();

        // ── Initialise management tools in the adapter ─────────────────────────────
        var adapter        = webApp.Services.GetRequiredService<McpToolRegistryAdapter>();
        var sessions       = webApp.Services.GetRequiredService<ISessionRegistry>();
        var stagingManager = webApp.Services.GetRequiredService<PackageStagingManager>();
        var localPkgMgr    = webApp.Services.GetRequiredService<IDevicePackageManager>();
        var remoteMgrs     = webApp.Services.GetRequiredService<RemoteManagerRegistry>();
        var extAccess      = webApp.Services.GetRequiredService<ExternalAccessManager>();
        var pkgLogger      = webApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Packages");

        SessionManagementTools.Register(adapter, sessions);
        DevicePackageManagementTools.Register(
            adapter, localPkgMgr, stagingManager, remoteMgrs, extAccess,
            webApp.Services.GetRequiredService<PackageInstallManager>(),
            pkgLogger);

        var mcpOptions = webApp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;
        mcpOptions.ToolCollection = adapter.Tools;

        // ── Session registration endpoint ─────────────────────────────────────────
        webApp.MapPost("/sessions/register", async (
            SessionRegistrationRequest req,
            SessionRegistry registry,
            ActiveSessionProxy proxy,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AppName) || string.IsNullOrWhiteSpace(req.McpUrl))
                return Results.BadRequest(new { error = "appName and mcpUrl are required" });

            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var identity  = new RoverAppIdentity(req.AppName, req.Version, req.InstanceId);

            log.LogInformation("Session registering: {DisplayName} at {McpUrl}", identity.DisplayName, req.McpUrl);

            McpClientSession session;
            try
            {
                session = await McpClientSession.ConnectAsync(sessionId, identity, req.McpUrl, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to connect MCP client to {McpUrl}", req.McpUrl);
                return Results.Problem($"Could not connect to MCP server at {req.McpUrl}: {ex.Message}");
            }

            registry.Add(session);
            session.StartDisconnectMonitoring();
            log.LogInformation("Session registered: {SessionId} {DisplayName}", sessionId, identity.DisplayName);

            await proxy.OnSessionRegisteredAsync(session);

            return Results.Ok(new SessionRegistrationResponse { SessionId = sessionId });
        });

        webApp.MapMcp("/mcp");

        // ── Package staging upload endpoint ───────────────────────────────────────
        PackageStagingEndpoint.MapStagingEndpoints(webApp, stagingManager);

        // ── Fire tools/list_changed when sessions change (enables real-time sync) ──
        var sessionRegistry = webApp.Services.GetRequiredService<SessionRegistry>();
        var toolAdapter = webApp.Services.GetRequiredService<McpToolRegistryAdapter>();
        sessionRegistry.SessionsChanged += (_, _) => toolAdapter.NotifyToolsChanged();

        // ── Launch web host on background thread, WinUI on main (STA) thread ────
        _ = Task.Run(async () =>
        {
            await webApp.StartAsync();
        });
        Thread.Sleep(500);

        App.Services = webApp.Services;

        // Listen for activation requests from subsequent launches
        _ = Task.Run(() => ListenForActivationAsync());

        // If this instance was launched via protocol activation (zrover:// URI),
        // handle it now. This covers the case where the app isn't running yet and
        // the user clicks a zrover:// link for the first time.
        var protocolArg = args.FirstOrDefault(a => a.StartsWith("zrover://", StringComparison.OrdinalIgnoreCase));
        if (protocolArg != null && Uri.TryCreate(protocolArg, UriKind.Absolute, out var activationUri))
        {
            App.HandleProtocolActivation(activationUri);
        }

        // Bootstrap the WindowsAppRuntime only when running unpackaged (VS F5).
        // When launched as a registered package (Start Menu), the framework
        // dependency in AppxManifest handles resolution automatically.
        bool isPackaged = false;
        try { _ = Windows.ApplicationModel.Package.Current; isPackaged = true; }
        catch { }

        if (!isPackaged)
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00010008);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        if (!isPackaged)
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Shutdown();
        }

        // UI closed — keep the background service alive
        webApp.WaitForShutdownAsync().GetAwaiter().GetResult();
    }

    private static async Task ListenForActivationAsync()
    {
        while (true)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            try
            {
                using var reader = new StreamReader(server);
                var msg = await reader.ReadLineAsync();
                if (msg == "activate")
                    App.ActivateFromExternal();
                else if (msg != null)
                    App.HandlePipeCommand(msg);
            }
            catch { /* client disconnected */ }
        }
    }
}

