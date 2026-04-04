namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Returned by every <see cref="IDevicePackageManager"/> mutating operation.
/// On success the operation-specific fields are populated; on failure only
/// <see cref="Error"/>, <see cref="ErrorMessage"/>, and <see cref="HResult"/> are set.
/// </summary>
public sealed class PackageOperationResult
{
    public bool Success { get; init; }

    // ── Success fields ─────────────────────────────────────────────

    /// <summary>e.g. <c>ContosoApp_8wekyb3d8bbwe</c>. Populated after install.</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>e.g. <c>ContosoApp_1.2.3.0_x64__8wekyb3d8bbwe</c>. Populated after install.</summary>
    public string? PackageFullName { get; init; }

    /// <summary>Installed version string. Populated after install.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Whether an existing package was updated rather than freshly installed.</summary>
    public bool WasUpdate { get; init; }

    /// <summary>
    /// Whether the install was deferred: the package is staged but not yet active.
    /// The app will be registered the next time it is launched.
    /// </summary>
    public bool IsRegistrationDeferred { get; init; }

    /// <summary>OS process ID of the launched app. Populated after launch.</summary>
    public int? ProcessId { get; init; }

    /// <summary>AUMID that was activated. Populated after launch.</summary>
    public string? Aumid { get; init; }

    /// <summary>Number of processes that were terminated. Populated after stop.</summary>
    public int? StoppedProcessCount { get; init; }

    // ── Failure fields ─────────────────────────────────────────────

    /// <summary>
    /// Short constant identifying the error category (UPPER_SNAKE_CASE),
    /// e.g. <c>CERT_NOT_TRUSTED</c>, <c>PACKAGE_NOT_FOUND</c>.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Human-readable explanation of the failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>HRESULT as a hex string, e.g. <c>0x800B0101</c>. Null for non-HRESULT errors.</summary>
    public string? HResult { get; init; }

    // ── Factories ──────────────────────────────────────────────────

    public static PackageOperationResult Ok() => new() { Success = true };

    public static PackageOperationResult InstallOk(
        string packageFamilyName, string packageFullName, string version,
        bool wasUpdate, bool isDeferred) => new()
        {
            Success = true,
            PackageFamilyName = packageFamilyName,
            PackageFullName = packageFullName,
            InstalledVersion = version,
            WasUpdate = wasUpdate,
            IsRegistrationDeferred = isDeferred,
        };

    public static PackageOperationResult LaunchOk(int pid, string aumid) => new()
    {
        Success = true,
        ProcessId = pid,
        Aumid = aumid,
    };

    public static PackageOperationResult StopOk(int stoppedCount) => new()
    {
        Success = true,
        StoppedProcessCount = stoppedCount,
    };

    public static PackageOperationResult Fail(string error, string message, int? hresult = null) => new()
    {
        Success = false,
        Error = error,
        ErrorMessage = message,
        HResult = hresult.HasValue ? $"0x{hresult.Value:X8}" : null,
    };
}
