using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace zRover.Retriever.Packages;

/// <summary>
/// Manages a single machine-specific self-signed code-signing certificate used to
/// sign MSIX packages before local installation.
///
/// A single cert with subject <c>CN=zRover Dev Signing</c> is created on first use
/// and reused for all subsequent packages, regardless of their declared publisher.
/// When the package publisher does not match the cert subject, the package is
/// repacked with the publisher patched (via <see cref="MsixPacker"/>) before being
/// signed (via <see cref="MsixSigner"/>). No external SDK tools are required.
///
/// This means only one UAC prompt is ever needed (to trust the cert in
/// <c>LocalMachine\TrustedPeople</c>) — subsequent package installs are fully
/// unattended.
/// </summary>
public sealed class DevCertManager : IDevCertManager
{
    /// <summary>The fixed cert subject used for all packages on this machine.</summary>
    public const string CertSubject = "CN=zRover Dev Signing";

    private const string FriendlyName  = "zRover Dev Signing";
    private const string StateFileName = "dev-cert.json";
    private static readonly string StateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "zRover.Retriever");

    private readonly ILogger<DevCertManager> _logger;
    private X509Certificate2? _cert;

    public DevCertManager(ILogger<DevCertManager> logger)
    {
        _logger = logger;
    }

    /// <summary>True once <see cref="EnsureReadyAsync"/> has completed successfully.</summary>
    public bool IsReady => _cert is not null;

    /// <summary>
    /// Creates (or loads) the machine dev cert and trusts it in
    /// <c>LocalMachine\TrustedPeople</c> (one-time UAC prompt).
    /// Safe to call multiple times — subsequent calls return immediately if already ready.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (IsReady) return;

        _cert = LoadPersistedCert() ?? CreateAndPersistCert();
        await EnsureTrustedAsync(ct);

        _logger.LogInformation("DevCertManager ready (thumb: {Thumb})", _cert.Thumbprint);
    }

    /// <summary>
    /// Ensures the MSIX at <paramref name="msixPath"/> has publisher
    /// <see cref="CertSubject"/> (repacking via <see cref="MsixPacker"/> if needed),
    /// then signs it with the dev cert via <see cref="MsixSigner"/>.
    /// </summary>
    public async Task SignPackageAsync(string msixPath, CancellationToken ct = default)
    {
        if (_cert is null)
            throw new InvalidOperationException("DevCertManager is not ready. Call EnsureReadyAsync first.");

        var publisher = MsixPacker.ReadPublisher(msixPath);

        if (!string.Equals(publisher, CertSubject, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Patching publisher '{Old}' \u2192 '{New}' in {Path}",
                publisher, CertSubject, msixPath);
            await MsixPacker.RepackWithPatchedPublisherAsync(msixPath, CertSubject, ct);
        }

        _logger.LogInformation("Signing {Path} (thumb: {Thumb})", msixPath, _cert.Thumbprint);
        await MsixSigner.SignAsync(msixPath, _cert, ct);
        _logger.LogInformation("Signed OK: {Path}", msixPath);
    }

    // ── Cert lifecycle ─────────────────────────────────────────────────────────

    private X509Certificate2? LoadPersistedCert()
    {
        var stateFile = Path.Combine(StateDir, StateFileName);
        if (!File.Exists(stateFile)) return null;

        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(stateFile));
            if (state is null || !state.TryGetValue("thumbprint", out var thumb)) return null;

            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint, thumb, validOnly: false);

            if (matches.Count == 0 || !matches[0].HasPrivateKey)
            {
                _logger.LogWarning("Persisted cert {Thumb} not found or lacks private key — recreating", thumb);
                return null;
            }

            _logger.LogInformation("Loaded existing dev cert (thumb: {Thumb})", thumb);
            return matches[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted cert state — will recreate");
            return null;
        }
    }

    private X509Certificate2 CreateAndPersistCert()
    {
        _logger.LogInformation("Creating dev signing cert (subject: {Subject})", CertSubject);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            CertSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        var raw = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));

        var cert = X509CertificateLoader.LoadPkcs12(
            raw.Export(X509ContentType.Pfx),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        cert.FriendlyName = FriendlyName;

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);

        Directory.CreateDirectory(StateDir);
        File.WriteAllText(
            Path.Combine(StateDir, StateFileName),
            JsonSerializer.Serialize(new Dictionary<string, string>
                { ["thumbprint"] = cert.Thumbprint, ["subject"] = CertSubject }));

        _logger.LogInformation("Dev cert created (thumb: {Thumb})", cert.Thumbprint);
        return cert;
    }

    // ── Trust management ────────────────────────────────────────────────────────

    private async Task EnsureTrustedAsync(CancellationToken ct)
    {
        var needsTrustedPeople = !IsCertTrusted(_cert!);
        var needsRoot          = !IsCertInRootStore(_cert!);
        if (!needsTrustedPeople && !needsRoot) return;

        if (needsTrustedPeople)
            _logger.LogInformation("Dev cert not yet trusted in LocalMachine\\TrustedPeople — importing");
        if (needsRoot)
            _logger.LogInformation("Dev cert not yet in LocalMachine\\Root — importing (required for SignerSignEx2)");

        // Try without elevation first (only works for TrustedPeople on some configurations).
        if (needsTrustedPeople)
        {
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(_cert!);
                _logger.LogInformation("Dev cert trusted in LocalMachine\\TrustedPeople");
                needsTrustedPeople = false;
            }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException ex) when ((uint)ex.HResult == 0x80070005) { }
        }

        if (!needsTrustedPeople && !needsRoot) return;

        // Export to %TEMP% so the elevated process can always read the file —
        // the packaged app's LocalAppData has restricted ACLs that block other users/processes.
        var cerPath = Path.Combine(Path.GetTempPath(), $"zrover-dev-cert-{_cert!.Thumbprint[..8]}.cer");
        File.WriteAllBytes(cerPath, _cert!.Export(X509ContentType.Cert));

        var escapedPath = cerPath.Replace("'", "''");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Import-Certificate -FilePath '{escapedPath}' " +
            "-CertStoreLocation Cert:\\\\LocalMachine\\\\TrustedPeople | Out-Null; " +
            "Import-Certificate -FilePath '{escapedPath}' " +
            "-CertStoreLocation Cert:\\\\LocalMachine\\\\Root | Out-Null\"")
        {
            Verb = "runas", UseShellExecute = true, CreateNoWindow = false,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && IsCertTrusted(_cert!) && IsCertInRootStore(_cert!))
                {
                    _logger.LogInformation("Dev cert trusted via UAC elevation (TrustedPeople + Root)");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elevated cert trust import failed");
        }

        _logger.LogWarning(
            "Could not trust dev cert in LocalMachine\\TrustedPeople — " +
            "package installs may fail with CERT_NOT_TRUSTED.");
    }

    private static bool IsCertTrusted(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false)
                .Count > 0;
        }
        catch { return false; }
    }

    private static bool IsCertInRootStore(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false)
                .Count > 0;
        }
        catch { return false; }
    }

}
