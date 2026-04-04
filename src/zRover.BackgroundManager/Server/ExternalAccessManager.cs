using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.BackgroundManager.Packages;
using zRover.Mcp;

namespace zRover.BackgroundManager.Server;

/// <summary>
/// Manages a second HTTP listener bound to <c>0.0.0.0</c> that exposes the same
/// MCP server to external machines. Disabled by default; enabled on-demand via UI
/// toggle or <c>zrover://</c> protocol activation.
///
/// The external listener shares the same <see cref="McpToolRegistryAdapter"/> and
/// session registry as the primary localhost server, so callers see the same tools
/// and sessions regardless of which endpoint they connect to.
///
/// A randomly-generated bearer token is required on every request to the external
/// endpoint. The token is displayed in the UI and included in <c>zrover://connect</c>
/// links for one-click remote manager pairing.
/// </summary>
public sealed class ExternalAccessManager : IDisposable
{
    private readonly IServiceProvider _rootServices;
    private readonly ILogger<ExternalAccessManager> _logger;

    private WebApplication? _externalApp;
    private CancellationTokenSource? _shutdownCts;

    public bool IsEnabled { get; private set; }
    public int Port { get; private set; }
    public string? BearerToken { get; private set; }
    public string? ExternalUrl { get; private set; }

    /// <summary>Fired when <see cref="IsEnabled"/>, <see cref="ExternalUrl"/>, or
    /// <see cref="BearerToken"/> changes.</summary>
    public event EventHandler? StateChanged;

    public ExternalAccessManager(IServiceProvider rootServices, ILogger<ExternalAccessManager> logger)
    {
        _rootServices = rootServices;
        _logger = logger;
    }

    /// <summary>
    /// Starts the external HTTP listener on <c>0.0.0.0:{port}</c> with a fresh bearer
    /// token. If already enabled, stops and restarts on the new port.
    /// </summary>
    public async Task EnableAsync(int port = 5201)
    {
        if (IsEnabled)
            await DisableAsync();

        BearerToken = GenerateToken();
        Port = port;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Share the same MCP tool collection / server config
        builder.Services.AddSingleton(_rootServices.GetRequiredService<McpToolRegistryAdapter>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<Sessions.SessionRegistry>());
        builder.Services.AddSingleton<Core.Sessions.ISessionRegistry>(sp =>
            sp.GetRequiredService<Sessions.SessionRegistry>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<ActiveSessionProxy>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<PackageStagingManager>());

        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "zRover.Manager", Version = "1.0.0" };
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true }
            };
        }).WithHttpTransport();

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        // Share the tool collection from the primary server
        var mcpOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;
        mcpOptions.ToolCollection = _rootServices
            .GetRequiredService<McpToolRegistryAdapter>().Tools;

        var token = BearerToken; // capture for closure

        // Bearer-token auth middleware for all requests.
        // /packages/stage/{token} paths are self-authenticating via their embedded
        // 256-bit single-use token and are therefore exempt from bearer auth.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/packages/stage"))
            {
                await next();
                return;
            }
            var auth = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(auth) || auth != $"Bearer {token}")
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
            await next();
        });

        // Same endpoints as the primary server
        app.MapPost("/sessions/register", async (
            Core.Sessions.SessionRegistrationRequest req,
            Sessions.SessionRegistry registry,
            ActiveSessionProxy proxy,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AppName) || string.IsNullOrWhiteSpace(req.McpUrl))
                return Results.BadRequest(new { error = "appName and mcpUrl are required" });

            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var identity = new Core.Sessions.RoverAppIdentity(req.AppName, req.Version, req.InstanceId);

            log.LogInformation("Session registering (external): {DisplayName} at {McpUrl}",
                identity.DisplayName, req.McpUrl);

            Sessions.McpClientSession session;
            try
            {
                session = await Sessions.McpClientSession.ConnectAsync(sessionId, identity, req.McpUrl, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to connect MCP client to {McpUrl}", req.McpUrl);
                return Results.Problem($"Could not connect to MCP server at {req.McpUrl}: {ex.Message}");
            }

            registry.Add(session);
            session.StartDisconnectMonitoring();
            await proxy.OnSessionRegisteredAsync(session);

            return Results.Ok(new Core.Sessions.SessionRegistrationResponse { SessionId = sessionId });
        });

        app.MapMcp("/mcp");

        // Package staging upload endpoint (shared with primary server)
        var externalStaging = app.Services.GetRequiredService<PackageStagingManager>();
        PackageStagingEndpoint.MapStagingEndpoints(app, externalStaging);

        _shutdownCts = new CancellationTokenSource();
        _externalApp = app;

        await app.StartAsync(_shutdownCts.Token);

        ExternalUrl = $"http://{DetectLanIp()}:{port}/mcp";
        IsEnabled = true;

        _logger.LogInformation("External access enabled on port {Port}. URL: {Url}", port, ExternalUrl);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the external listener and clears the bearer token.</summary>
    public async Task DisableAsync()
    {
        if (!IsEnabled) return;

        try
        {
            _shutdownCts?.Cancel();
            if (_externalApp != null)
                await _externalApp.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error shutting down external listener");
        }
        finally
        {
            (_externalApp as IDisposable)?.Dispose();
            _externalApp = null;
            _shutdownCts?.Dispose();
            _shutdownCts = null;
        }

        IsEnabled = false;
        BearerToken = null;
        ExternalUrl = null;
        Port = 0;

        _logger.LogInformation("External access disabled");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Generates a <c>zrover://connect</c> URI that a remote machine can open
    /// to pair with this manager.</summary>
    public string? GetConnectionLink()
    {
        if (!IsEnabled || ExternalUrl == null || BearerToken == null) return null;
        return $"zrover://connect?url={Uri.EscapeDataString(ExternalUrl)}&token={BearerToken}";
    }

    public void Dispose()
    {
        DisableAsync().GetAwaiter().GetResult();
    }

    /// <summary>Generates a cryptographically random 32-character hex token.</summary>
    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>Returns the best non-loopback IPv4 address for LAN access.</summary>
    internal static string DetectLanIp()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { /* fallback below */ }

        return Dns.GetHostName();
    }
}
