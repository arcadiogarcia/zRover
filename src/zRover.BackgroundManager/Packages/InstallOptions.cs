namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Options controlling how an MSIX package is installed.
/// Passed to <see cref="IDevicePackageManager.InstallPackageAsync"/>.
/// </summary>
public sealed class InstallOptions
{
    /// <summary>
    /// Explicit URI strings for dependency packages (frameworks, VCLibs, etc.).
    /// Usually empty — Windows resolves these automatically when online.
    /// </summary>
    public IReadOnlyList<string> DependencyUris { get; init; } = [];

    /// <summary>
    /// Force-close any running instances of the package before installing/updating.
    /// Default: false.
    /// </summary>
    public bool ForceAppShutdown { get; init; }

    /// <summary>
    /// Allow packages not signed by a trusted certificate.
    /// Requires Developer Mode on the target device.
    /// Default: false.
    /// </summary>
    public bool AllowUnsigned { get; init; }

    /// <summary>
    /// Install for all users, not just the current user.
    /// Requires the <c>packageManagement</c> restricted capability.
    /// Default: false.
    /// </summary>
    public bool InstallForAllUsers { get; init; }

    /// <summary>
    /// Stage the package but defer registration to the next app launch.
    /// Useful for background updates that should not interrupt a running session.
    /// Default: false.
    /// </summary>
    public bool DeferRegistration { get; init; }
}
