using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using zRover.BackgroundManager.Packages;
using zRover.BackgroundManager.Sessions;

namespace zRover.BackgroundManager;

/// <summary>
/// Lifecycle logger and periodic health-check for the BackgroundManager.
/// Sweeps local sessions every 10 s and removes any whose MCP endpoint is unreachable.
/// Also verifies remote manager connectivity and re-syncs stale connections.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly SessionRegistry _registry;
    private readonly RemoteManagerRegistry _managers;
    private readonly PackageStagingManager _staging;

    public Worker(
        ILogger<Worker> logger,
        SessionRegistry registry,
        RemoteManagerRegistry managers,
        PackageStagingManager staging)
    {
        _logger  = logger;
        _registry = registry;
        _managers = managers;
        _staging  = staging;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("zRover.BackgroundManager running");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }

            // Health-check local sessions (skip PropagatedSessions — they're managed by RemoteManagerRegistry)
            foreach (var session in _registry.Sessions)
            {
                if (session is PropagatedSession) continue;

                if (!session.IsConnected)
                {
                    _registry.Remove(session.SessionId);
                    continue;
                }

                if (!await IsReachableAsync(session.McpUrl, stoppingToken))
                {
                    _logger.LogInformation("Session {SessionId} unreachable, removing", session.SessionId);
                    _registry.Remove(session.SessionId);
                }
            }

            // Health-check remote managers
            foreach (var manager in _managers.Managers)
            {
                if (!manager.IsConnected) continue;

                if (!await _managers.TestConnectivityAsync(manager.ManagerId, stoppingToken))
                {
                    _logger.LogWarning("Remote manager {Alias} ({ManagerId}) failed health check",
                        manager.Alias, manager.ManagerId);
                }
            }

            // Purge expired staging entries and orphaned temp files
            _staging.PurgeExpired();
        }

        _logger.LogInformation("zRover.BackgroundManager stopping");
    }

    private static async Task<bool> IsReachableAsync(string mcpUrl, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(mcpUrl);
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
