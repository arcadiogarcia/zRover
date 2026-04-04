namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Complete metadata for an installed MSIX package, as returned by
/// <see cref="IDevicePackageManager.ListInstalledPackagesAsync"/> and
/// <see cref="IDevicePackageManager.GetPackageInfoAsync"/>.
/// </summary>
public sealed class PackageInfo
{
    /// <summary>e.g. <c>ContosoApp_8wekyb3d8bbwe</c></summary>
    public required string PackageFamilyName { get; init; }

    /// <summary>e.g. <c>ContosoApp_1.2.3.0_x64__8wekyb3d8bbwe</c></summary>
    public required string PackageFullName { get; init; }

    /// <summary>Localised display name shown in Start/Settings.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Dotted version string, e.g. <c>1.2.3.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Raw publisher subject DN, e.g. <c>CN=Contoso, O=Contoso, C=US</c>.</summary>
    public required string Publisher { get; init; }

    /// <summary>Friendly publisher display name if available, otherwise same as <see cref="Publisher"/>.</summary>
    public required string PublisherDisplayName { get; init; }

    /// <summary>UTC install timestamp.</summary>
    public required DateTimeOffset InstallDate { get; init; }

    /// <summary>Absolute path to the installed package directory on disk.</summary>
    public required string InstalledLocation { get; init; }

    /// <summary>Whether this entry is an MSIX bundle (contains per-arch sub-packages).</summary>
    public required bool IsBundle { get; init; }

    /// <summary>
    /// Classification of the package:
    /// <c>Main</c>, <c>Framework</c>, <c>Resource</c>, <c>Optional</c>, or <c>Bundle</c>.
    /// </summary>
    public required string PackageType { get; init; }

    /// <summary>Processor architecture string, e.g. <c>X64</c>, <c>Arm64</c>, <c>Neutral</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary>
    /// Signature trust/validity:
    /// <c>Valid</c>, <c>Invalid</c>, <c>NotSigned</c>, or <c>Unknown</c>.
    /// </summary>
    public required string SigningStatus { get; init; }

    /// <summary>Whether at least one process from this package is currently running.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>All launchable app entries inside this package.</summary>
    public required IReadOnlyList<AppEntryInfo> Apps { get; init; }

    // ── Extended fields (populated by GetPackageInfoAsync only) ────────────────

    /// <summary>
    /// Package family names of all declared dependencies (framework, runtime, etc.).
    /// <c>null</c> when not fetched (list_installed_packages does not populate this).
    /// </summary>
    public IReadOnlyList<string>? Dependencies { get; init; }

    /// <summary>
    /// Raw capability strings declared in the manifest (e.g. <c>internetClient</c>).
    /// <c>null</c> when not fetched.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>
    /// Detailed health/status flags for this package.
    /// <c>null</c> when not fetched.
    /// </summary>
    public PackageStatusInfo? Status { get; init; }
}

/// <summary>Health flags from <c>Package.Status</c>.</summary>
public sealed class PackageStatusInfo
{
    public bool DataOffline { get; init; }
    public bool DependencyIssue { get; init; }
    public bool Modified { get; init; }
    public bool NeedsRemediation { get; init; }
    public bool NotAvailable { get; init; }
    public bool PackageOffline { get; init; }
    public bool Servicing { get; init; }
    public bool Tampered { get; init; }
    public bool Disabled { get; init; }
}
