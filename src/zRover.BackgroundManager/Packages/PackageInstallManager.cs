using Microsoft.Extensions.Logging;

namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Controls whether MCP clients are allowed to install or uninstall MSIX packages
/// on this machine. Disabled by default; must be explicitly enabled via the UI
/// toggle or a <c>zrover://enable-package-install</c> protocol activation.
///
/// This gate covers <c>install_package</c>, <c>uninstall_package</c>, and
/// <c>request_package_upload</c> — the destructive/write package operations.
/// Read-only operations (<c>list_installed_packages</c>, <c>get_package_info</c>,
/// <c>launch_app</c>, <c>stop_app</c>) are not gated.
///
/// The first time the gate is enabled, <see cref="DevCertManager.EnsureReadyAsync"/>
/// is called to create the signing cert and show the one-time UAC trust prompt.
/// Subsequent enable/disable cycles are instant.
/// </summary>
public sealed class PackageInstallManager
{
    private readonly IDevCertManager _devCerts;
    private readonly ILogger<PackageInstallManager> _logger;
    private bool _certInitialised;

    public PackageInstallManager(IDevCertManager devCerts, ILogger<PackageInstallManager> logger)
    {
        _devCerts = devCerts;
        _logger   = logger;
    }

    /// <summary>Whether package install/uninstall operations are currently permitted.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Raised when <see cref="IsEnabled"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Allows MCP clients to install and uninstall packages.
    /// On the first call, initialises the dev cert (may show a one-time UAC prompt to trust it).
    /// </summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        if (IsEnabled) return;

        if (!_certInitialised)
        {
            try
            {
                await _devCerts.EnsureReadyAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dev cert initialisation failed — unsigned package signing may be unavailable");
            }
            _certInitialised = true;
        }

        IsEnabled = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Prevents MCP clients from installing or uninstalling packages.</summary>
    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
