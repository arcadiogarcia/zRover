namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Abstraction over the local device's MSIX package management capabilities.
/// Implemented by <see cref="LocalDevicePackageManager"/>; testable via mocking.
/// All operations target the device on which the Background Manager is running.
/// Cross-device routing is handled by <see cref="../Server/DevicePackageManagementTools"/>.
/// </summary>
public interface IDevicePackageManager
{
    /// <summary>
    /// Enumerates installed MSIX packages for the current user (and optionally all users).
    /// </summary>
    /// <param name="nameFilter">
    /// Optional case-insensitive substring filter over display name and package family name.
    /// Pass <c>null</c> to return all packages.
    /// </param>
    /// <param name="includeFrameworks">Include framework/runtime packages. Default false.</param>
    /// <param name="includeSystemPackages">Include system-provisioned packages. Default false.</param>
    Task<IReadOnlyList<PackageInfo>> ListInstalledPackagesAsync(
        string? nameFilter,
        bool includeFrameworks,
        bool includeSystemPackages,
        CancellationToken ct = default);

    /// <summary>
    /// Installs or updates an MSIX package from the given URI.
    /// Supports <c>https://</c>, <c>file://</c>, <c>ms-appinstaller://</c>,
    /// and <c>staged://{stagingId}</c> for locally staged packages.
    /// </summary>
    Task<PackageOperationResult> InstallPackageAsync(
        string packageUri,
        InstallOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Removes an installed package by its package family name.
    /// Resolves the full name from the family name internally.
    /// </summary>
    Task<PackageOperationResult> UninstallPackageAsync(
        string packageFamilyName,
        bool removeForAllUsers,
        bool preserveAppData,
        CancellationToken ct = default);

    /// <summary>
    /// Activates the default (or specified) app entry within a package via AUMID.
    /// Returns the OS process ID of the launched process.
    /// </summary>
    Task<PackageOperationResult> LaunchAppAsync(
        string packageFamilyName,
        string? appId,
        string? arguments,
        CancellationToken ct = default);

    /// <summary>
    /// Terminates all running processes belonging to a package.
    /// When <paramref name="force"/> is false, sends a graceful close request first
    /// and waits up to three seconds before killing.
    /// </summary>
    Task<PackageOperationResult> StopAppAsync(
        string packageFamilyName,
        bool force,
        CancellationToken ct = default);

    /// <summary>
    /// Returns full package metadata for a single package, including dependencies,
    /// declared capabilities, and detailed status flags.
    /// Returns <c>null</c> if the package is not installed.
    /// </summary>
    Task<PackageInfo?> GetPackageInfoAsync(
        string packageFamilyName,
        CancellationToken ct = default);
}
