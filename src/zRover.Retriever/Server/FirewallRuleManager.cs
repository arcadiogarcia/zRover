using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace zRover.Retriever.Server;

/// <summary>
/// Ensures a Windows Firewall inbound rule exists for the external MCP listener
/// port so users do not see the "Allow access" dialog every time the app's
/// install path changes (i.e. after every MSIX update).
///
/// First call requires admin (one-time UAC prompt). The rule is port-based and
/// program-independent, so it survives all subsequent updates without further
/// prompts.
/// </summary>
internal static class FirewallRuleManager
{
    private const string RuleName = "zRover.Retriever.External";

    // Once we observe (or create) the rule in this process, skip the PowerShell
    // probe on subsequent toggles. The rule is created by us and only changes
    // out-of-band if the user/admin manually deletes it, which is rare enough
    // that requiring a process restart to redetect is acceptable.
    private static int s_ruleConfirmed;

    /// <summary>
    /// Adds an inbound TCP allow rule for <paramref name="port"/> if one with the
    /// expected name does not already exist. Silently succeeds if the rule is
    /// already present (no UAC).
    /// </summary>
    public static Task EnsureInboundRuleAsync(int port, ILogger logger, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref s_ruleConfirmed, 0, 0) == 1)
        {
            logger.LogDebug("Firewall rule '{Rule}' already confirmed this session \u2014 skipping probe", RuleName);
            return Task.CompletedTask;
        }

        // Run the entire check+create flow on a background thread. The rule
        // existence probe launches PowerShell which can take a second or two
        // to spin up .NET + the NetSecurity module; doing that on the UI
        // thread freezes the window while the external-access toggle is on.
        return Task.Run(async () =>
        {
            if (RuleExists())
            {
                logger.LogDebug("Firewall rule '{Rule}' already exists \u2014 skipping creation", RuleName);
                Interlocked.Exchange(ref s_ruleConfirmed, 1);
                return;
            }

            logger.LogInformation(
                "Creating Windows Firewall inbound rule '{Rule}' for TCP port {Port} (one-time UAC prompt)",
                RuleName, port);

            var script =
                $"if (-not (Get-NetFirewallRule -Name '{RuleName}' -ErrorAction SilentlyContinue)) {{ " +
                $"New-NetFirewallRule -Name '{RuleName}' " +
                $"-DisplayName 'zRover Retriever (External MCP)' " +
                $"-Description 'Allows inbound MCP federation connections to the zRover Retriever.' " +
                $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -Profile Any | Out-Null }}";

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (proc.ExitCode == 0)
                    {
                        logger.LogInformation("Firewall rule '{Rule}' created successfully", RuleName);
                        Interlocked.Exchange(ref s_ruleConfirmed, 1);
                        return;
                    }
                    logger.LogWarning(
                        "Firewall rule creation exited with code {Code} \u2014 users may see the Windows Firewall prompt on first external connection",
                        proc.ExitCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not create firewall rule (likely UAC declined). Users may see the Windows Firewall prompt on first external connection.");
            }
        }, ct);
    }

    private static bool RuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"if (Get-NetFirewallRule -Name '{RuleName}' -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
