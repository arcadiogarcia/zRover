using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Lifecycle status of a staging entry.
/// </summary>
public enum StagingStatus
{
    /// <summary>Token created; no bytes received yet.</summary>
    PendingUpload,
    /// <summary>Upload is actively being received.</summary>
    Uploading,
    /// <summary>All bytes received; SHA-256 check in progress.</summary>
    Verifying,
    /// <summary>Verified locally; now being forwarded to the next hop.</summary>
    Forwarding,
    /// <summary>File is on disk and ready to pass to <c>install_package</c>.</summary>
    Ready,
    /// <summary>Upload failed or integrity check failed.</summary>
    Failed,
    /// <summary>Token expired before upload completed.</summary>
    Expired,
}

/// <summary>
/// Base record for all staging entries.  Created by <see cref="PackageStagingManager"/>.
/// </summary>
public abstract record StagingEntry(
    string StagingId,
    string UploadToken,
    string Filename,
    string ExpectedSha256,
    long ExpectedBytes,
    DateTimeOffset ExpiresAt)
{
    public StagingStatus Status { get; set; } = StagingStatus.PendingUpload;
    public string? FailureReason { get; set; }
}

/// <summary>
/// A file that is stored on this machine.
/// Used in the final hop of a chain (including single-machine scenarios).
/// </summary>
public sealed record LocalStagingEntry(
    string StagingId,
    string UploadToken,
    string Filename,
    string ExpectedSha256,
    long ExpectedBytes,
    DateTimeOffset ExpiresAt,
    string LocalPath)
    : StagingEntry(StagingId, UploadToken, Filename, ExpectedSha256, ExpectedBytes, ExpiresAt);

/// <summary>
/// A file that is received here, verified, then forwarded to the next hop.
/// Used on intermediate managers in a multi-hop chain.
/// </summary>
public sealed record ForwardingStagingEntry(
    string StagingId,
    string UploadToken,
    string Filename,
    string ExpectedSha256,
    long ExpectedBytes,
    DateTimeOffset ExpiresAt,
    string LocalPath,
    string DownstreamUploadUrl,
    string DownstreamStagingId,
    string? DownstreamDeviceId)
    : StagingEntry(StagingId, UploadToken, Filename, ExpectedSha256, ExpectedBytes, ExpiresAt);

// ══════════════════════════════════════════════════════════════════════════════
//  Result types
// ══════════════════════════════════════════════════════════════════════════════

public sealed class UploadResult
{
    public bool Success { get; init; }
    public string? StagingId { get; init; }
    public long? SizeBytes { get; init; }
    public string? Error { get; init; }
    public string? ErrorMessage { get; init; }
    public string? HopAlias { get; init; }  // populated when forwarding fails

    public static UploadResult Ok(string stagingId, long sizeBytes) => new()
        { Success = true, StagingId = stagingId, SizeBytes = sizeBytes };

    public static UploadResult Fail(string error, string message, string? hop = null) => new()
        { Success = false, Error = error, ErrorMessage = message, HopAlias = hop };
}

public sealed class StagingTicket
{
    public required string StagingId { get; init; }
    public required string UploadToken { get; init; }
    public required string UploadPath { get; init; }   // relative: /packages/stage/{token}
    public required DateTimeOffset ExpiresAt { get; init; }
    public required int Hops { get; init; }
}

// ══════════════════════════════════════════════════════════════════════════════
//  PackageStagingManager
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Manages the lifecycle of staged MSIX packages:
/// <list type="bullet">
///   <item>Creates pre-signed single-use upload tokens.</item>
///   <item>Receives raw bytes, verifies SHA-256, writes to a temp directory.</item>
///   <item>For forwarding entries: re-uploads to the downstream manager after local verification.</item>
///   <item>Purges expired entries (called by the background <c>Worker</c>).</item>
/// </list>
///
/// <b>Security model</b>:
/// Each upload token is 256-bit random (64 hex chars) and single-use.
/// Tokens expire 30 minutes after creation if unused, and staged files are
/// auto-deleted 24 hours after a successful upload completes.
/// </summary>
public sealed class PackageStagingManager : IDisposable
{
    internal const int TokenUploadExpiryMinutes = 30;
    internal const int FileRetentionHours = 24;
    internal const long MaxFileSizeBytes = 4L * 1024 * 1024 * 1024; // 4 GiB

    private readonly string _stagingRoot;
    private readonly ILogger<PackageStagingManager> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StagingEntry>
        _byToken = new(StringComparer.OrdinalIgnoreCase);     // token → entry
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StagingEntry>
        _byId = new(StringComparer.OrdinalIgnoreCase);         // stagingId → entry

    public PackageStagingManager(ILogger<PackageStagingManager> logger)
    {
        _logger = logger;
        _stagingRoot = Path.Combine(Path.GetTempPath(), "zRover", "packages");
        Directory.CreateDirectory(_stagingRoot);
    }

    // ── Creation ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a local staging entry: the upload will be stored on this machine.
    /// </summary>
    public StagingTicket CreateLocalStage(
        string filename, string sha256Hex, long sizeBytes)
    {
        var (stagingId, token) = GenerateIds();
        var dir = Path.Combine(_stagingRoot, stagingId);
        Directory.CreateDirectory(dir);
        var localPath = Path.Combine(dir, SanitiseFilename(filename));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(TokenUploadExpiryMinutes);

        var entry = new LocalStagingEntry(
            stagingId, token, filename, sha256Hex, sizeBytes, expiry, localPath);

        Register(token, stagingId, entry);

        _logger.LogInformation("Created local staging entry {StagingId} for {Filename} ({Bytes} bytes)",
            stagingId, filename, sizeBytes);

        return new StagingTicket
        {
            StagingId  = stagingId,
            UploadToken = token,
            UploadPath = $"/packages/stage/{token}",
            ExpiresAt  = expiry,
            Hops       = 0,
        };
    }

    /// <summary>
    /// Creates a forwarding staging entry: after local verification the file is
    /// re-uploaded to <paramref name="downstreamUploadUrl"/> on the next hop.
    /// </summary>
    public StagingTicket CreateForwardingStage(
        string filename, string sha256Hex, long sizeBytes,
        string downstreamUploadUrl, string downstreamStagingId,
        string? downstreamDeviceId, int hopCount)
    {
        var (stagingId, token) = GenerateIds();
        var dir = Path.Combine(_stagingRoot, stagingId);
        Directory.CreateDirectory(dir);
        var localPath = Path.Combine(dir, SanitiseFilename(filename));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(TokenUploadExpiryMinutes);

        var entry = new ForwardingStagingEntry(
            stagingId, token, filename, sha256Hex, sizeBytes, expiry,
            localPath, downstreamUploadUrl, downstreamStagingId, downstreamDeviceId);

        Register(token, stagingId, entry);

        _logger.LogInformation(
            "Created forwarding staging entry {StagingId} → {DownstreamUrl} (downstream id: {DownstreamId})",
            stagingId, downstreamUploadUrl, downstreamStagingId);

        return new StagingTicket
        {
            StagingId   = stagingId,
            UploadToken = token,
            UploadPath  = $"/packages/stage/{token}",
            ExpiresAt   = expiry,
            Hops        = hopCount,
        };
    }

    // ── Upload handling ────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a raw upload stream identified by its single-use token.
    /// Writes to disk, verifies SHA-256, and for forwarding entries triggers
    /// the downstream upload before returning.
    /// </summary>
    public async Task<UploadResult> AcceptUploadAsync(
        string uploadToken,
        Stream body,
        long? contentLength,
        CancellationToken ct = default)
    {
        if (!_byToken.TryGetValue(uploadToken, out var entry))
            return UploadResult.Fail("TOKEN_NOT_FOUND",
                "Upload token is unknown or has expired. Request a new token via request_package_upload.");

        if (entry.Status == StagingStatus.Expired)
            return UploadResult.Fail("TOKEN_EXPIRED",
                "The upload token has expired. Request a new token via request_package_upload.");

        if (entry.Status != StagingStatus.PendingUpload)
            return UploadResult.Fail("ALREADY_UPLOADED",
                $"This staging entry has already been used (status: {entry.Status}). " +
                "Discard it with discard_package_stage and start a new upload.");

        if (contentLength.HasValue)
        {
            if (contentLength.Value > MaxFileSizeBytes)
                return UploadResult.Fail("FILE_TOO_LARGE",
                    $"Content-Length ({contentLength.Value:N0} bytes) exceeds the maximum allowed size of {MaxFileSizeBytes:N0} bytes.");
            if (contentLength.Value != entry.ExpectedBytes)
                return UploadResult.Fail("WRONG_SIZE",
                    $"Content-Length ({contentLength.Value:N0}) does not match the declared sizeBytes ({entry.ExpectedBytes:N0}). " +
                    "Re-request an upload token with the correct file size.");
        }

        entry.Status = StagingStatus.Uploading;

        string localPath;
        if (entry is LocalStagingEntry local) localPath = local.LocalPath;
        else if (entry is ForwardingStagingEntry fwd) localPath = fwd.LocalPath;
        else return UploadResult.Fail("INTERNAL_ERROR", "Unknown entry type.");

        // Write to temporary file first; move to final path only after verification
        var tmpPath = localPath + ".tmp";
        long bytesWritten;

        try
        {
            using var fileStream = File.Create(tmpPath);
            await body.CopyToAsync(fileStream, ct);
            bytesWritten = fileStream.Length;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write upload for {StagingId}", entry.StagingId);
            entry.Status = StagingStatus.Failed;
            entry.FailureReason = ex.Message;
            TryDeleteFile(tmpPath);
            return UploadResult.Fail("WRITE_FAILED", $"Failed to write package to disk: {ex.Message}");
        }

        if (bytesWritten != entry.ExpectedBytes)
        {
            entry.Status = StagingStatus.Failed;
            entry.FailureReason = $"Size mismatch: expected {entry.ExpectedBytes}, got {bytesWritten}";
            TryDeleteFile(tmpPath);
            return UploadResult.Fail("WRONG_SIZE",
                $"Received {bytesWritten:N0} bytes but sizeBytes declared {entry.ExpectedBytes:N0}.");
        }

        // Verify SHA-256
        entry.Status = StagingStatus.Verifying;
        string actualHash;
        try
        {
            actualHash = await ComputeSha256Async(tmpPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            entry.Status = StagingStatus.Failed;
            TryDeleteFile(tmpPath);
            return UploadResult.Fail("VERIFY_FAILED", $"SHA-256 computation failed: {ex.Message}");
        }

        if (!actualHash.Equals(entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            entry.Status = StagingStatus.Failed;
            entry.FailureReason = $"SHA-256 mismatch: expected {entry.ExpectedSha256}, got {actualHash}";
            TryDeleteFile(tmpPath);
            _logger.LogWarning("SHA-256 mismatch for {StagingId}: expected {Expected}, got {Actual}",
                entry.StagingId, entry.ExpectedSha256, actualHash);
            return UploadResult.Fail("SHA256_MISMATCH",
                $"Package integrity check failed. Expected SHA-256: {entry.ExpectedSha256}. " +
                $"Actual: {actualHash}. The file may be corrupt or incomplete.");
        }

        File.Move(tmpPath, localPath, overwrite: true);
        _logger.LogInformation("Upload verified for {StagingId} ({Bytes} bytes)", entry.StagingId, bytesWritten);

        // For forwarding entries, push the file to the downstream manager
        if (entry is ForwardingStagingEntry forwardEntry)
        {
            entry.Status = StagingStatus.Forwarding;
            var forwardResult = await ForwardToDownstreamAsync(forwardEntry, localPath, ct);
            if (!forwardResult.Success)
            {
                entry.Status = StagingStatus.Failed;
                entry.FailureReason = forwardResult.ErrorMessage;
                return forwardResult;
            }
        }

        entry.Status = StagingStatus.Ready;

        // Invalidate the one-time token — remove from token map but keep id map
        _byToken.TryRemove(uploadToken, out _);

        return UploadResult.Ok(entry.StagingId, bytesWritten);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a local (non-forwarding) staging entry by its stagingId.
    /// Returns <c>null</c> if not found or not a local entry.
    /// </summary>
    public LocalStagingEntry? ResolveLocal(string stagingId)
    {
        if (_byId.TryGetValue(stagingId, out var entry) && entry is LocalStagingEntry local)
            return local;
        return null;
    }

    /// <summary>
    /// Looks up any staging entry (local or forwarding) by its stagingId.
    /// </summary>
    public StagingEntry? Resolve(string stagingId)
    {
        _byId.TryGetValue(stagingId, out var entry);
        return entry;
    }

    // ── Discard ───────────────────────────────────────────────────────────────

    public bool Discard(string stagingId)
    {
        if (!_byId.TryRemove(stagingId, out var entry)) return false;
        _byToken.TryRemove(entry.UploadToken, out _);

        string? localPath = entry is LocalStagingEntry l ? l.LocalPath
            : entry is ForwardingStagingEntry f ? f.LocalPath
            : null;

        if (localPath is not null)
            TryDeleteDirectory(Path.GetDirectoryName(localPath)!);

        _logger.LogInformation("Discarded staging entry {StagingId}", stagingId);
        return true;
    }

    // ── Expiry purge (called by Worker) ──────────────────────────────────────

    public void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, entry) in _byId.ToArray())
        {
            bool shouldPurge = entry.Status switch
            {
                StagingStatus.PendingUpload => now > entry.ExpiresAt,
                StagingStatus.Ready         => now > entry.ExpiresAt.AddHours(FileRetentionHours),
                StagingStatus.Failed        => now > entry.ExpiresAt,
                StagingStatus.Expired       => true,
                _                           => false,   // Uploading/Verifying/Forwarding: active, skip
            };

            if (!shouldPurge) continue;

            entry.Status = StagingStatus.Expired;
            _byId.TryRemove(id, out _);
            _byToken.TryRemove(entry.UploadToken, out _);

            string? dir = entry is LocalStagingEntry l ? Path.GetDirectoryName(l.LocalPath)
                : entry is ForwardingStagingEntry f ? Path.GetDirectoryName(f.LocalPath)
                : null;

            if (dir != null) TryDeleteDirectory(dir);

            _logger.LogDebug("Purged expired staging entry {StagingId} (status was {Status})", id, entry.Status);
        }

        // Also clean up any orphaned temp dirs not tracked in memory
        try
        {
            if (!Directory.Exists(_stagingRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_stagingRoot))
            {
                var info = new DirectoryInfo(dir);
                if ((now - info.CreationTimeUtc).TotalHours > FileRetentionHours + 1)
                    TryDeleteDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning staging root for orphaned directories");
        }
    }

    // ── Forwarding ────────────────────────────────────────────────────────────

    private async Task<UploadResult> ForwardToDownstreamAsync(
        ForwardingStagingEntry entry, string localPath, CancellationToken ct)
    {
        _logger.LogInformation(
            "Forwarding {StagingId} to downstream {Url} (stagingId={DownstreamId})",
            entry.StagingId, entry.DownstreamUploadUrl, entry.DownstreamStagingId);

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
            await using var fileStream = File.OpenRead(localPath);

            var content = new StreamContent(fileStream);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = new FileInfo(localPath).Length;

            var response = await httpClient.PostAsync(entry.DownstreamUploadUrl, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Forwarded {StagingId} to downstream successfully", entry.StagingId);
                return UploadResult.Ok(entry.DownstreamStagingId, entry.ExpectedBytes);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Downstream rejected forwarded upload for {StagingId}: {Status} {Body}",
                entry.StagingId, response.StatusCode, body);

            return UploadResult.Fail("FORWARD_FAILED",
                $"Downstream manager rejected the upload (HTTP {(int)response.StatusCode}): {body}",
                hop: ExtractHostname(entry.DownstreamUploadUrl));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach downstream for {StagingId}", entry.StagingId);
            return UploadResult.Fail("DOWNSTREAM_UNREACHABLE",
                $"Could not connect to downstream manager: {ex.Message}",
                hop: ExtractHostname(entry.DownstreamUploadUrl));
        }
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static (string stagingId, string token) GenerateIds()
    {
        var stagingId = "sa-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var token     = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (stagingId, token);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.Create().ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitiseFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(filename.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "package.msix" : safe;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static string ExtractHostname(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    private void Register(string token, string stagingId, StagingEntry entry)
    {
        _byToken[token]     = entry;
        _byId[stagingId]    = entry;
    }

    public void Dispose()
    {
        // Orphaned files are cleaned by PurgeExpired; nothing to dispose here.
    }
}
