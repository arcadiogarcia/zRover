using System.IO.Pipes;
using Microsoft.UI.Dispatching;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.Retriever;
using zRover.Retriever.Packages;
using zRover.Retriever.Server;
using zRover.Retriever.Sessions;
using zRover.Core.Sessions;
using zRover.Mcp;

namespace zRover.Retriever;

public class Program
{
    private const string MutexName = "zRover.Retriever.SingleInstance";
    private const string PipeName  = "zRover.Retriever.Activate";

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

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // ── Core services ──────────────────────────────────────────────────────────
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
        builder.Services.AddSingleton<McpToolRegistryAdapter>();
        builder.Services.AddSingleton<ActiveSessionProxy>();
        builder.Services.AddSingleton<ExternalAccessManager>();
        builder.Services.AddSingleton<RetrieverSettingsStore>();
        builder.Services.AddSingleton<RemoteManagerRegistry>();
        builder.Services.AddSingleton<PackageStagingManager>();
        builder.Services.AddSingleton<DevCertManager>();
        builder.Services.AddSingleton<IDevCertManager>(sp => sp.GetRequiredService<DevCertManager>());
        builder.Services.AddSingleton<PackageInstallManager>();
        builder.Services.AddSingleton<IDevicePackageManager, LocalDevicePackageManager>();
        builder.Services.AddSingleton<ControllerRegistry>();
        builder.Services.AddHostedService<Worker>();

        // ── Master MCP server ──────────────────────────────────────────────────────
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "zRover.Manager", Version = "1.0.0" };
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Resources = new ResourcesCapability { }
            };
        })
        .WithHttpTransport()
        .WithResources<zRover.Retriever.Server.IntegrationGuideResource>();

        var webApp = builder.Build();

        // ── Initialise management tools in the adapter ─────────────────────────────
        var adapter        = webApp.Services.GetRequiredService<McpToolRegistryAdapter>();
        var sessions       = webApp.Services.GetRequiredService<ISessionRegistry>();
        var stagingManager = webApp.Services.GetRequiredService<PackageStagingManager>();
        var localPkgMgr    = webApp.Services.GetRequiredService<IDevicePackageManager>();
        var remoteMgrs     = webApp.Services.GetRequiredService<RemoteManagerRegistry>();
        var extAccess      = webApp.Services.GetRequiredService<ExternalAccessManager>();
        var pkgInstall     = webApp.Services.GetRequiredService<PackageInstallManager>();
        var pkgLogger      = webApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Packages");

        SessionManagementTools.Register(adapter, sessions);
        DevicePackageManagementTools.Register(
            adapter, localPkgMgr, stagingManager, remoteMgrs, extAccess,
            pkgInstall,
            pkgLogger);

        var selfUpdateLogger = webApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SelfUpdate");
        SelfUpdateTools.Register(adapter, remoteMgrs, selfUpdateLogger);

        var mcpOptions = webApp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;
        mcpOptions.ToolCollection = adapter.Tools;

        // ── Restore persisted settings ────────────────────────────────────────────
        var settingsStore  = webApp.Services.GetRequiredService<RetrieverSettingsStore>();
        // lastSaved is mutated on every save so the ExternalBearerToken is not
        // accidentally overwritten by old startup values when external is toggled off.
        var lastSaved = settingsStore.Load();

        if (lastSaved.ExternalEnabled)
        {
            _ = Task.Run(async () =>
            {
                try { await extAccess.EnableAsync(lastSaved.ExternalPort, lastSaved.ExternalBearerToken); }
                catch { /* logged inside EnableAsync */ }
            });
        }

        if (lastSaved.PackageInstallEnabled)
        {
            _ = Task.Run(async () =>
            {
                try { await pkgInstall.EnableAsync(); }
                catch { /* logged inside EnableAsync */ }
            });
        }

        // Attempt to reconnect all previously-known remote retrievers in the background.
        foreach (var saved in lastSaved.SavedRemoteManagers)
        {
            var url   = saved.McpUrl;
            var token = saved.BearerToken;
            var alias = saved.Alias;
            _ = Task.Run(async () =>
            {
                try { await remoteMgrs.ConnectAsync(url, token, alias); }
                catch { /* silently ignored — will appear in past-managers list */ }
            });
        }

        // Save whenever external access, package install, or remote managers change state.
        void SaveCurrentSettings()
        {
            // Merge currently-connected managers into the persisted list so that
            // managers that disconnect are kept for future one-click reconnect.
            var currentConnections = remoteMgrs.GetSaveableManagers();
            var mergedManagers = new List<SavedRemoteManager>(lastSaved.SavedRemoteManagers);
            foreach (var (url, btoken, alias) in currentConnections)
            {
                var idx = mergedManagers.FindIndex(s => s.McpUrl == url);
                if (idx < 0)
                    mergedManagers.Add(new SavedRemoteManager { McpUrl = url, BearerToken = btoken, Alias = alias });
                else
                    mergedManagers[idx] = new SavedRemoteManager { McpUrl = url, BearerToken = btoken, Alias = alias };
            }

            var s = new RetrieverSettings
            {
                ExternalEnabled       = extAccess.IsEnabled,
                ExternalPort          = extAccess.IsEnabled ? extAccess.Port : lastSaved.ExternalPort,
                ExternalBearerToken   = extAccess.BearerToken ?? lastSaved.ExternalBearerToken,
                PackageInstallEnabled = pkgInstall.IsEnabled,
                SavedRemoteManagers   = mergedManagers,
            };
            settingsStore.Save(s);
            lastSaved = s;   // keep in sync so the next save doesn't regress the token
        }

        extAccess.StateChanged    += (_, _) => SaveCurrentSettings();
        pkgInstall.StateChanged   += (_, _) => SaveCurrentSettings();
        remoteMgrs.ManagersChanged += (_, _) => SaveCurrentSettings();

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

        // ── Track inbound MCP controllers (remote retrievers controlling this instance) ──
        var controllerRegistry = webApp.Services.GetRequiredService<ControllerRegistry>();
        webApp.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }

            var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = context.Request.Headers.UserAgent.ToString();
            var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
            var key = controllerRegistry.Track(remoteIp,
                string.IsNullOrEmpty(userAgent) ? null : userAgent,
                string.IsNullOrEmpty(sessionId) ? null : sessionId);
            context.RequestAborted.Register(() => controllerRegistry.Untrack(key));
            try
            {
                await next();
            }
            finally
            {
                controllerRegistry.Untrack(key);
            }
        });

        webApp.MapMcp("/mcp");

        // ── Package staging upload endpoint ───────────────────────────────────────
        PackageStagingEndpoint.MapStagingEndpoints(webApp, stagingManager);

        // ── Fire tools/list_changed when sessions change ──────────────────────────
        // ActiveSessionProxy already emits notifications when tools are added /
        // removed (per-session ref-counted) and when the active session
        // rotates. The SessionsChanged event also fires for in-flight session
        // additions where ActiveSessionProxy hasn't run yet (e.g. before the
        // session has published its tool list); we re-emit a notification then
        // so clients that surface a session list see it refresh promptly.
        var sessionRegistry = webApp.Services.GetRequiredService<SessionRegistry>();
        var toolAdapter = webApp.Services.GetRequiredService<McpToolRegistryAdapter>();
        var activeProxy = webApp.Services.GetRequiredService<ActiveSessionProxy>();
        sessionRegistry.SessionsChanged += (_, _) =>
        {
            if (activeProxy.IsInitialized)
                toolAdapter.NotifyToolsChanged();
        };

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

