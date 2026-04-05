using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.System;

namespace zRover.Retriever.Packages;

/// <summary>
/// Implements <see cref="IDevicePackageManager"/> using the WinRT
/// <see cref="Windows.Management.Deployment.PackageManager"/> API for install/uninstall/list,
/// the <c>IApplicationActivationManager</c> COM interface for launch, and
/// <see cref="AppDiagnosticInfo"/> (with a Win32 fallback) for stop.
///
/// All operations target the local device only. Cross-device routing is handled by
/// <c>DevicePackageManagementTools</c> before this class is ever invoked.
/// </summary>
public sealed class LocalDevicePackageManager : IDevicePackageManager
{
    private const long MaxPackageSizeBytes = 4L * 1024 * 1024 * 1024; // 4 GiB

    private readonly PackageManager _pm = new();
    private readonly ILogger<LocalDevicePackageManager> _logger;
    private readonly PackageStagingManager _staging;
    private readonly IDevCertManager _devCerts;

    // ── HRESULT → error code map ───────────────────────────────────────────────
    private static readonly Dictionary<uint, string> HResultErrors = new()
    {
        [0x80073CF1] = "PACKAGE_ALREADY_REGISTERED",
        [0x80073CF2] = "PACKAGE_NOT_FOUND",
        [0x80073CF3] = "PACKAGE_STAGING_FAILED",
        [0x80073CF9] = "DEPLOYMENT_CANCELED",
        [0x80073D06] = "HIGHER_VERSION_INSTALLED",
        [0x80073CFB] = "INVALID_PACKAGE",
        [0x80070070] = "INSUFFICIENT_DISK_SPACE",
        [0x800B0101] = "CERT_NOT_TRUSTED",
        [0x800B0109] = "CERT_CHAIN_UNTRUSTED",
        [0x80070005] = "ACCESS_DENIED",
        [0x80073D02] = "PACKAGE_IN_USE",
        [0x80070490] = "DEPENDENCY_NOT_FOUND",
    };

    public LocalDevicePackageManager(
        PackageStagingManager staging,
        IDevCertManager devCerts,
        ILogger<LocalDevicePackageManager> logger)
    {
        _staging  = staging;
        _devCerts = devCerts;
        _logger   = logger;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ListInstalledPackagesAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<PackageInfo>> ListInstalledPackagesAsync(
        string? nameFilter,
        bool includeFrameworks,
        bool includeSystemPackages,
        CancellationToken ct = default)
    {
        // Build the package type filter
        var types = PackageTypes.Main | PackageTypes.Optional | PackageTypes.Bundle;
        if (includeFrameworks) types |= PackageTypes.Framework;
        if (includeSystemPackages) types |= PackageTypes.Resource;

        // FindPackagesForUserWithPackageTypes with empty string = current user
        IEnumerable<Package> packages = _pm.FindPackagesForUserWithPackageTypes("", types);

        var runningFamilies = GetRunningPackageFamilyNames();
        var results = new List<PackageInfo>();

        foreach (var pkg in packages)
        {
            ct.ThrowIfCancellationRequested();

            var familyName = pkg.Id.FamilyName;
            var displayName = TryGetDisplayName(pkg);

            if (nameFilter != null &&
                !familyName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) &&
                !displayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var apps = await GetAppEntriesAsync(pkg, ct);

            results.Add(new PackageInfo
            {
                PackageFamilyName  = familyName,
                PackageFullName    = pkg.Id.FullName,
                DisplayName        = displayName,
                Version            = FormatVersion(pkg.Id.Version),
                Publisher          = pkg.Id.Publisher,
                PublisherDisplayName = TryGetPublisherDisplayName(pkg),
                InstallDate        = pkg.InstalledDate,
                InstalledLocation  = TryGetInstalledPath(pkg),
                IsBundle           = pkg.IsBundle,
                PackageType        = ClassifyPackageType(pkg),
                Architecture       = pkg.Id.Architecture.ToString(),
                SigningStatus      = GetSigningStatus(pkg),
                IsRunning          = runningFamilies.Contains(familyName),
                Apps               = apps,
            });
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  InstallPackageAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<PackageOperationResult> InstallPackageAsync(
        string packageUri,
        InstallOptions options,
        CancellationToken ct = default)
    {
        // Resolve staged:// URIs to a local file:// URI
        if (packageUri.StartsWith("staged://", StringComparison.OrdinalIgnoreCase))
        {
            var stagingId = packageUri["staged://".Length..];
            var entry = _staging.ResolveLocal(stagingId);
            if (entry is null)
                return PackageOperationResult.Fail("STAGING_NOT_FOUND",
                    $"No staged package found with ID '{stagingId}'. It may have expired or never been uploaded.");
            if (entry.Status != StagingStatus.Ready)
                return PackageOperationResult.Fail("STAGING_NOT_READY",
                    $"Staged package '{stagingId}' has status '{entry.Status}'. It must be in 'Ready' state before installation.");
            packageUri = new Uri(entry.LocalPath).AbsoluteUri;
        }

        var uri = new Uri(packageUri);
        _logger.LogInformation("Installing package from {Uri}", packageUri);

        // Auto-sign local MSIX/APPX files using the dev cert managed by DevCertManager.
        // This covers both staged uploads and direct file:// installs.
        if (uri.IsFile)
        {
            var localPath = uri.LocalPath;
            var ext = Path.GetExtension(localPath);
            if (ext.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
            {
                if (!_devCerts.IsReady)
                {
                    _logger.LogWarning("DevCertManager is not ready; skipping auto-sign. Package may fail with CERT_NOT_TRUSTED.");
                }
                else
                {
                    try
                    {
                        await _devCerts.SignPackageAsync(localPath, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-sign failed for {Path}", localPath);
                        return PackageOperationResult.Fail("SIGN_FAILED",
                            $"Failed to sign package before installation: {ex.Message}");
                    }
                }
            }
        }

        try
        {
            DeploymentResult result;

            if (uri.Scheme.Equals("ms-appinstaller", StringComparison.OrdinalIgnoreCase) ||
                packageUri.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase))
            {
                result = await InstallViaAppInstallerAsync(uri, options, ct);
            }
            else
            {
                result = await InstallViaPackageManagerAsync(uri, options, ct);
            }

            // Find the newly installed package to return its info
            var pkgFullName = result.ActivityId.ToString(); // fallback
            var installedPkg = FindPackageByUri(uri);

            if (installedPkg is not null)
            {
                var version = FormatVersion(installedPkg.Id.Version);
                _logger.LogInformation("Package installed: {FullName} v{Version}",
                    installedPkg.Id.FullName, version);

                return PackageOperationResult.InstallOk(
                    installedPkg.Id.FamilyName,
                    installedPkg.Id.FullName,
                    version,
                    wasUpdate: false,
                    isDeferred: result.IsRegistered);
            }

            return PackageOperationResult.InstallOk(
                pkgFullName, pkgFullName, "unknown",
                wasUpdate: false,
                isDeferred: result.IsRegistered);
        }
        catch (COMException comEx)
        {
            return MapDeploymentError(comEx.HResult, comEx.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Package installation failed for {Uri}", packageUri);
            return PackageOperationResult.Fail("INSTALL_FAILED", ex.Message);
        }
    }

    private async Task<DeploymentResult> InstallViaPackageManagerAsync(
        Uri uri, InstallOptions options, CancellationToken ct)
    {
        var addOptions = new AddPackageOptions
        {
            ForceAppShutdown = options.ForceAppShutdown,
            DeferRegistrationWhenPackagesAreInUse = options.DeferRegistration,
        };

        if (options.AllowUnsigned)
            addOptions.AllowUnsigned = true;

        if (options.InstallForAllUsers)
            addOptions.StubPackageOption = StubPackageOption.InstallFull;

        foreach (var dep in options.DependencyUris)
            addOptions.DependencyPackageUris.Add(new Uri(dep));

        var progress = new Progress<DeploymentProgress>(p =>
            _logger.LogDebug("Install progress: {State} {Percent}%",
                p.state, p.percentage));

        return await _pm.AddPackageByUriAsync(uri, addOptions)
            .AsTask(ct, progress);
    }

    private async Task<DeploymentResult> InstallViaAppInstallerAsync(
        Uri uri, InstallOptions options, CancellationToken ct)
    {
        var progress = new Progress<DeploymentProgress>(p =>
            _logger.LogDebug("AppInstaller progress: {State} {Percent}%",
                p.state, p.percentage));

        return await _pm.AddPackageByAppInstallerFileAsync(
                uri,
                AddPackageByAppInstallerOptions.None,
                _pm.GetDefaultPackageVolume())
            .AsTask(ct, progress);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  UninstallPackageAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<PackageOperationResult> UninstallPackageAsync(
        string packageFamilyName,
        bool removeForAllUsers,
        bool preserveAppData,
        CancellationToken ct = default)
    {
        var pkg = _pm.FindPackagesForUser("", packageFamilyName).FirstOrDefault();
        if (pkg is null)
            return PackageOperationResult.Fail("PACKAGE_NOT_FOUND",
                $"No installed package found for family name '{packageFamilyName}'.");

        var fullName = pkg.Id.FullName;
        _logger.LogInformation("Uninstalling package {FullName}", fullName);

        try
        {
            var removal = removeForAllUsers
                ? RemovalOptions.RemoveForAllUsers
                : RemovalOptions.None;

            if (preserveAppData)
                removal |= RemovalOptions.PreserveApplicationData;

            var progress = new Progress<DeploymentProgress>(p =>
                _logger.LogDebug("Uninstall progress: {State} {Percent}%",
                    p.state, p.percentage));

            await _pm.RemovePackageAsync(fullName, removal)
                .AsTask(ct, progress);

            _logger.LogInformation("Package uninstalled: {FullName}", fullName);
            return PackageOperationResult.Ok();
        }
        catch (COMException comEx)
        {
            return MapDeploymentError(comEx.HResult, comEx.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Uninstall failed for {PackageFamilyName}", packageFamilyName);
            return PackageOperationResult.Fail("UNINSTALL_FAILED", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LaunchAppAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<PackageOperationResult> LaunchAppAsync(
        string packageFamilyName,
        string? appId,
        string? arguments,
        CancellationToken ct = default)
    {
        // Find the package to enumerate its app entries
        var pkg = _pm.FindPackagesForUser("", packageFamilyName).FirstOrDefault();
        if (pkg is null)
            return PackageOperationResult.Fail("PACKAGE_NOT_FOUND",
                $"No installed package found for family name '{packageFamilyName}'.");

        IReadOnlyList<AppEntryInfo> apps;
        try
        {
            apps = await GetAppEntriesAsync(pkg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate app entries for {PackageFamilyName}", packageFamilyName);
            return PackageOperationResult.Fail("APP_ENTRY_ENUMERATION_FAILED", ex.Message);
        }

        if (apps.Count == 0)
            return PackageOperationResult.Fail("NO_APP_ENTRIES",
                $"Package '{packageFamilyName}' has no launchable app entries.");

        // Select the target entry: by appId if specified, otherwise the first one
        AppEntryInfo? target = appId is null
            ? apps[0]
            : apps.FirstOrDefault(a => a.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return PackageOperationResult.Fail("APP_NOT_FOUND",
                $"No app entry with ID '{appId}' found in package '{packageFamilyName}'. " +
                $"Available: {string.Join(", ", apps.Select(a => a.AppId))}");

        _logger.LogInformation("Launching {Aumid} with args: {Args}", target.Aumid, arguments ?? "(none)");

        try
        {
            int pid = ActivateApplication(target.Aumid, arguments ?? "");
            _logger.LogInformation("Launched {Aumid} — PID {Pid}", target.Aumid, pid);
            return PackageOperationResult.LaunchOk(pid, target.Aumid);
        }
        catch (COMException comEx)
        {
            _logger.LogError(comEx, "COM activation failed for {Aumid}", target.Aumid);
            return PackageOperationResult.Fail("LAUNCH_FAILED", comEx.Message, comEx.HResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Launch failed for {PackageFamilyName}", packageFamilyName);
            return PackageOperationResult.Fail("LAUNCH_FAILED", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  StopAppAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<PackageOperationResult> StopAppAsync(
        string packageFamilyName,
        bool force,
        CancellationToken ct = default)
    {
        int stopped = 0;

        // Primary path: AppDiagnosticInfo (needs appDiagnostics capability)
        try
        {
            var diagnosticInfos = await AppDiagnosticInfo.RequestInfoForPackageAsync(packageFamilyName);
            if (diagnosticInfos.Count > 0)
            {
                foreach (var info in diagnosticInfos)
                {
                    foreach (var group in info.GetResourceGroups())
                    {
                        ct.ThrowIfCancellationRequested();
                        await group.StartTerminateAsync();
                        stopped++;
                    }
                }

                _logger.LogInformation("Stopped {Count} resource group(s) for {PackageFamilyName}",
                    stopped, packageFamilyName);

                if (stopped == 0)
                    return PackageOperationResult.Fail("NOT_RUNNING",
                        $"No running instances of '{packageFamilyName}' were found.");

                return PackageOperationResult.StopOk(stopped);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // appDiagnostics capability not granted; fall through to Win32 path
            _logger.LogDebug("AppDiagnosticInfo access denied for {PackageFamilyName}, using Win32 fallback",
                packageFamilyName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AppDiagnosticInfo path failed for {PackageFamilyName}, using Win32 fallback",
                packageFamilyName);
        }

        // Fallback: enumerate processes via Win32 and match by package family name
        var matchingProcessIds = FindProcessesByPackageFamilyName(packageFamilyName);
        if (matchingProcessIds.Count == 0)
            return PackageOperationResult.Fail("NOT_RUNNING",
                $"No running processes found for package family '{packageFamilyName}'.");

        foreach (var pid in matchingProcessIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);

                if (!force)
                {
                    // Graceful: post WM_CLOSE to all windows of the process
                    proc.CloseMainWindow();
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (!proc.HasExited && DateTime.UtcNow < deadline)
                        await Task.Delay(100, ct);
                }

                if (!proc.HasExited)
                    proc.Kill();

                stopped++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not terminate process {Pid}", pid);
            }
        }

        _logger.LogInformation("Stopped {Count} process(es) for {PackageFamilyName}", stopped, packageFamilyName);

        if (stopped == 0)
            return PackageOperationResult.Fail("STOP_FAILED",
                "Located running processes but failed to terminate them. Check permissions.");

        return PackageOperationResult.StopOk(stopped);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GetPackageInfoAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<PackageInfo?> GetPackageInfoAsync(
        string packageFamilyName,
        CancellationToken ct = default)
    {
        var pkg = _pm.FindPackagesForUser("", packageFamilyName).FirstOrDefault();
        if (pkg is null) return null;

        var runningFamilies = GetRunningPackageFamilyNames();
        var apps = await GetAppEntriesAsync(pkg, ct);
        var deps = GetDependencies(pkg);
        var caps = GetCapabilities(pkg);
        var status = GetStatusInfo(pkg);

        return new PackageInfo
        {
            PackageFamilyName    = pkg.Id.FamilyName,
            PackageFullName      = pkg.Id.FullName,
            DisplayName          = TryGetDisplayName(pkg),
            Version              = FormatVersion(pkg.Id.Version),
            Publisher            = pkg.Id.Publisher,
            PublisherDisplayName = TryGetPublisherDisplayName(pkg),
            InstallDate          = pkg.InstalledDate,
            InstalledLocation    = TryGetInstalledPath(pkg),
            IsBundle             = pkg.IsBundle,
            PackageType          = ClassifyPackageType(pkg),
            Architecture         = pkg.Id.Architecture.ToString(),
            SigningStatus        = GetSigningStatus(pkg),
            IsRunning            = runningFamilies.Contains(pkg.Id.FamilyName),
            Apps                 = apps,
            Dependencies         = deps,
            Capabilities         = caps,
            Status               = status,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ══════════════════════════════════════════════════════════════════════════

    private static async Task<IReadOnlyList<AppEntryInfo>> GetAppEntriesAsync(
        Package pkg, CancellationToken ct)
    {
        try
        {
            var entries = await pkg.GetAppListEntriesAsync().AsTask(ct);
            return entries.Select(e =>
            {
                // AppUserModelId format is "{PackageFamilyName}!{AppId}"
                var aumid = e.AppUserModelId;
                var bangIdx = aumid.IndexOf('!');
                var appId = bangIdx >= 0 ? aumid[(bangIdx + 1)..] : aumid;
                return new AppEntryInfo
                {
                    AppId       = appId,
                    Aumid       = aumid,
                    DisplayName = e.DisplayInfo.DisplayName,
                };
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> GetDependencies(Package pkg)
    {
        try
        {
            return pkg.Dependencies
                .Select(d => d.Id.FamilyName)
                .Distinct()
                .ToList();
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> GetCapabilities(Package pkg)
    {
        // Package.Capabilities is not exposed via a direct WinRT API.
        // We read them from the manifest XML instead.
        try
        {
            var manifestPath = Path.Combine(pkg.InstalledPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) return [];

            var doc = System.Xml.Linq.XDocument.Load(manifestPath);
            var ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
            var uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

            return doc.Descendants()
                .Where(e =>
                    e.Name.LocalName == "Capability" &&
                    (e.Name.NamespaceName == ns ||
                     e.Name.NamespaceName == rescap ||
                     e.Name.NamespaceName == uap))
                .Select(e => e.Attribute("Name")?.Value ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch { return []; }
    }

    private static PackageStatusInfo GetStatusInfo(Package pkg)
    {
        try
        {
            var s = pkg.Status;
            return new PackageStatusInfo
            {
                DataOffline         = s.DataOffline,
                DependencyIssue     = s.DependencyIssue,
                Modified            = s.Modified,
                NeedsRemediation    = s.NeedsRemediation,
                NotAvailable        = s.NotAvailable,
                PackageOffline      = s.PackageOffline,
                Servicing           = s.Servicing,
                Tampered            = s.Tampered,
                Disabled            = !s.VerifyIsOK(),
            };
        }
        catch
        {
            return new PackageStatusInfo();
        }
    }

    private static string TryGetDisplayName(Package pkg)
    {
        try { return pkg.DisplayName; }
        catch { return pkg.Id.Name; }
    }

    private static string TryGetPublisherDisplayName(Package pkg)
    {
        try { return pkg.PublisherDisplayName; }
        catch { return pkg.Id.Publisher; }
    }

    private static string TryGetInstalledPath(Package pkg)
    {
        try { return pkg.InstalledPath; }
        catch { return ""; }
    }

    private static string FormatVersion(PackageVersion v)
        => $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

    private static string ClassifyPackageType(Package pkg)
    {
        if (pkg.IsFramework) return "Framework";
        if (pkg.IsResourcePackage) return "Resource";
        if (pkg.IsOptional) return "Optional";
        if (pkg.IsBundle) return "Bundle";
        return "Main";
    }

    private static string GetSigningStatus(Package pkg)
    {
        try
        {
            return pkg.Status.VerifyIsOK() ? "Valid" : "Invalid";
        }
        catch { return "Unknown"; }
    }

    private Package? FindPackageByUri(Uri uri)
    {
        // After installation the package is typically the most recently installed one
        // matching the extracted family name. We can't know it in advance, so we
        // attempt to extract the family name from the full name in the URI path.
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(uri.LocalPath);
            return _pm.FindPackagesForUser("")
                .FirstOrDefault(p =>
                    p.Id.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.FamilyName.StartsWith(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    // ── Process enumeration (Win32 fallback for StopApp) ──────────────────────

    private static HashSet<string> GetRunningPackageFamilyNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var family = GetPackageFamilyNameFromProcess(proc);
                if (!string.IsNullOrEmpty(family))
                    result.Add(family);
            }
            catch { /* skip protected/system processes */ }
            finally { proc.Dispose(); }
        }
        return result;
    }

    private static List<int> FindProcessesByPackageFamilyName(string packageFamilyName)
    {
        var pids = new List<int>();
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var family = GetPackageFamilyNameFromProcess(proc);
                if (string.Equals(family, packageFamilyName, StringComparison.OrdinalIgnoreCase))
                    pids.Add(proc.Id);
            }
            catch { /* skip */ }
            finally { proc.Dispose(); }
        }
        return pids;
    }

    private static string? GetPackageFamilyNameFromProcess(System.Diagnostics.Process proc)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)proc.Id);
        if (handle == IntPtr.Zero) return null;

        try
        {
            uint length = 256;
            var sb = new System.Text.StringBuilder((int)length);
            int hr = GetPackageFamilyName(handle, ref length, sb);
            return hr == 0 ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── IApplicationActivationManager COM activation ──────────────────────────

    private static int ActivateApplication(string aumid, string arguments)
    {
        var activator = (IApplicationActivationManager)
            Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;

        activator.ActivateApplication(aumid, arguments, ActivateOptions.None, out uint pid);
        return (int)pid;
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFamilyName(
        IntPtr hProcess,
        ref uint packageFamilyNameLength,
        System.Text.StringBuilder packageFamilyName);

    [DllImport("ole32.dll")]
    private static extern int CoGetClassObject(
        ref Guid rclsid,
        uint dwClsContext,
        IntPtr pvReserved,
        ref Guid riid,
        out IntPtr ppv);

    // ── COM interfaces for app activation ─────────────────────────────────────

    private enum ActivateOptions
    {
        None = 0,
        DesignMode = 0x1,
        NoErrorUI = 0x2,
        NoSplashScreen = 0x4,
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    private static PackageOperationResult MapDeploymentError(int hresult, string message)
    {
        var uhr = (uint)hresult;
        var code = HResultErrors.TryGetValue(uhr, out var known) ? known : "DEPLOYMENT_FAILED";
        return PackageOperationResult.Fail(code, message, hresult);
    }
}
