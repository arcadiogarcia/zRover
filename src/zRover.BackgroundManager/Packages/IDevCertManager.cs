namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Manages the machine-scoped signing certificate used to sign MSIX packages
/// before local installation.
/// </summary>
public interface IDevCertManager
{
    /// <summary>True once <see cref="EnsureReadyAsync"/> has completed successfully.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Creates (or loads) the machine dev cert, trusts it, and locates signtool/makeappx.
    /// Safe to call multiple times — subsequent calls return immediately if already ready.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// Ensures the MSIX at <paramref name="msixPath"/> is signed with the dev cert,
    /// patching the publisher in the package manifest if necessary.
    /// </summary>
    Task SignPackageAsync(string msixPath, CancellationToken ct = default);
}
