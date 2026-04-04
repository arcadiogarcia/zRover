using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Manages a single machine-specific self-signed code-signing certificate used to
/// sign MSIX packages before local installation.
///
/// A single cert with subject <c>CN=zRover Dev Signing</c> is created on first use
/// and reused for all subsequent packages, regardless of their declared publisher.
/// When the package publisher does not match the cert subject, the package is
/// repacked via <c>makeappx.exe</c> with the publisher patched to match before signing.
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
                     "zRover.BackgroundManager");

    private readonly ILogger<DevCertManager> _logger;
    private X509Certificate2? _cert;
    private string? _signtool;
    private string? _makeappx;

    public DevCertManager(ILogger<DevCertManager> logger)
    {
        _logger = logger;
    }

    /// <summary>True once <see cref="EnsureReadyAsync"/> has completed successfully.</summary>
    public bool IsReady => _cert is not null && _signtool is not null;

    /// <summary>
    /// Creates (or loads) the machine dev cert, trusts it, and locates signtool/makeappx.
    /// Safe to call multiple times.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (IsReady) return;

        _signtool = FindTool("signtool.exe");
        _makeappx = FindTool("makeappx.exe");

        if (_signtool is null)
            _logger.LogWarning("signtool.exe not found — package signing will be unavailable");
        if (_makeappx is null)
            _logger.LogWarning("makeappx.exe not found — publisher patching will be unavailable");

        _cert = LoadPersistedCert() ?? CreateAndPersistCert();
        await EnsureTrustedAsync(ct);

        _logger.LogInformation("DevCertManager ready (thumb: {Thumb})", _cert.Thumbprint);
    }

    /// <summary>
    /// Ensures the MSIX at <paramref name="msixPath"/> has publisher
    /// <see cref="CertSubject"/> (repacking via makeappx if needed),
    /// then signs it with the dev cert.
    /// </summary>
    public async Task SignPackageAsync(string msixPath, CancellationToken ct = default)
    {
        if (_cert is null || _signtool is null)
            throw new InvalidOperationException("DevCertManager is not ready. Call EnsureReadyAsync first.");

        var publisher = ReadPublisherFromMsix(msixPath);

        if (!string.Equals(publisher, CertSubject, StringComparison.OrdinalIgnoreCase))
        {
            if (_makeappx is null)
                throw new InvalidOperationException(
                    $"Package publisher '{publisher}' does not match the dev cert subject '{CertSubject}', " +
                    "and makeappx.exe was not found to repack it. " +
                    "Install the Windows SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/).");

            _logger.LogInformation("Patching publisher '{Old}' \u2192 '{New}' in {Path}",
                publisher, CertSubject, msixPath);
            await RepackWithPatchedPublisherAsync(msixPath, ct);
        }

        _logger.LogInformation("Signing {Path} (thumb: {Thumb})", msixPath, _cert.Thumbprint);
        await RunAsync(_signtool,
            $"sign /fd SHA256 /sha1 {_cert.Thumbprint} \"{msixPath}\"", ct);
        _logger.LogInformation("Signed OK: {Path}", msixPath);
    }

    // ── Publisher patching ─────────────────────────────────────────────────────

    private async Task RepackWithPatchedPublisherAsync(string msixPath, CancellationToken ct)
    {
        var tempExtract = Path.Combine(Path.GetTempPath(), $"zrover-extract-{Guid.NewGuid():N}");
        var tempPacked  = Path.Combine(Path.GetTempPath(), $"zrover-repacked-{Guid.NewGuid():N}.msix");

        try
        {
            ZipFile.ExtractToDirectory(msixPath, tempExtract);

            var manifestPath = Path.Combine(tempExtract, "AppxManifest.xml");
            PatchManifestPublisher(manifestPath, CertSubject);

            // Remove old signature — signtool will add a fresh one after repacking
            var sigPath = Path.Combine(tempExtract, "AppxSignature.p7x");
            if (File.Exists(sigPath)) File.Delete(sigPath);

            // makeappx recalculates AppxBlockMap.xml automatically
            await RunAsync(_makeappx!,
                $"pack /d \"{tempExtract}\" /p \"{tempPacked}\" /o", ct);

            File.Move(tempPacked, msixPath, overwrite: true);
        }
        finally
        {
            if (Directory.Exists(tempExtract))
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            if (File.Exists(tempPacked))
                try { File.Delete(tempPacked); } catch { }
        }
    }

    private static void PatchManifestPublisher(string manifestPath, string newPublisher)
    {
        var doc = XDocument.Load(manifestPath);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var attr = doc.Root
            ?.Element(ns + "Identity")
            ?.Attribute("Publisher")
            ?? throw new InvalidOperationException(
                "Could not find Identity/@Publisher in AppxManifest.xml.");
        attr.SetValue(newPublisher);
        doc.Save(manifestPath);
    }

    // ── MSIX manifest reading ──────────────────────────────────────────────────

    private static string ReadPublisherFromMsix(string msixPath)
    {
        using var zip = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry("AppxManifest.xml")
            ?? throw new InvalidOperationException("AppxManifest.xml not found in package.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        return doc.Root
            ?.Element(ns + "Identity")
            ?.Attribute("Publisher")
            ?.Value
            ?? throw new InvalidOperationException(
                "Could not read Publisher from AppxManifest.xml.");
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
        if (IsCertTrusted(_cert!)) return;

        _logger.LogInformation("Dev cert not yet trusted in LocalMachine\\TrustedPeople — importing");

        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(_cert!);
            _logger.LogInformation("Dev cert trusted in LocalMachine\\TrustedPeople");
            return;
        }
        catch (UnauthorizedAccessException) { }
        catch (CryptographicException ex) when ((uint)ex.HResult == 0x80070005) { }

        // Export to %TEMP% so the elevated process can always read the file —
        // the packaged app's LocalAppData has restricted ACLs that block other users/processes.
        var cerPath = Path.Combine(Path.GetTempPath(), $"zrover-dev-cert-{_cert!.Thumbprint[..8]}.cer");
        File.WriteAllBytes(cerPath, _cert!.Export(X509ContentType.Cert));

        var escapedPath = cerPath.Replace("'", "''");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Import-Certificate -FilePath '{escapedPath}' " +
            "-CertStoreLocation Cert:\\\\LocalMachine\\\\TrustedPeople | Out-Null\"")
        {
            Verb = "runas", UseShellExecute = true, CreateNoWindow = false,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && IsCertTrusted(_cert!))
                {
                    _logger.LogInformation("Dev cert trusted via UAC elevation");
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

    // ── Process helpers ────────────────────────────────────────────────────────

    private static async Task RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited {proc.ExitCode}. " +
                $"stdout: {await stdout} stderr: {await stderr}");
    }

    // ── Tool discovery ─────────────────────────────────────────────────────────

    private string? FindTool(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(full)) return full;
        }

        string? kitsRoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            kitsRoot = key?.GetValue("KitsRoot10") as string;
        }
        catch { }

        if (kitsRoot is not null)
        {
            var binDir = Path.Combine(kitsRoot, "bin");
            var archs  = Environment.Is64BitOperatingSystem
                ? new[] { "x64", "arm64", "x86" }
                : new[] { "x86" };

            foreach (var ver in Directory.GetDirectories(binDir, "10.*")
                         .OrderByDescending(d => d))
                foreach (var arch in archs)
                {
                    var path = Path.Combine(ver, arch, exeName);
                    if (File.Exists(path)) return path;
                }

            foreach (var arch in archs)
            {
                var path = Path.Combine(binDir, arch, exeName);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }
}
