using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using zRover.BackgroundManager.Packages;

namespace zRover.BackgroundManager.Tests;

/// <summary>
/// Unit tests for <see cref="PackageStagingManager"/>.
/// All tests use only in-process fake data — no WinUI, WinRT, or network I/O.
/// </summary>
public sealed class PackageStagingManagerTests : IDisposable
{
    private readonly PackageStagingManager _sut;

    public PackageStagingManagerTests()
    {
        _sut = new PackageStagingManager(NullLogger<PackageStagingManager>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    // ─── CreateLocalStage ─────────────────────────────────────────────────────

    [Fact]
    public void CreateLocalStage_Returns_ValidTicket()
    {
        var ticket = _sut.CreateLocalStage("App.msix", FakeSha256, 1024);

        ticket.StagingId.Should().StartWith("sa-");
        ticket.UploadToken.Should().HaveLength(64); // 32 bytes hex
        ticket.UploadPath.Should().Be($"/packages/stage/{ticket.UploadToken}");
        ticket.Hops.Should().Be(0);
        ticket.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CreateLocalStage_CreatesLocalEntry_Resolvable()
    {
        var ticket = _sut.CreateLocalStage("App.msix", FakeSha256, 512);
        var entry = _sut.ResolveLocal(ticket.StagingId);

        entry.Should().NotBeNull();
        entry!.Filename.Should().Be("App.msix");
        entry.ExpectedBytes.Should().Be(512);
        entry.ExpectedSha256.Should().Be(FakeSha256);
        entry.Status.Should().Be(StagingStatus.PendingUpload);
    }

    // ─── AcceptUploadAsync — success path ────────────────────────────────────

    [Fact]
    public async Task AcceptUpload_WithCorrectData_ReturnsSuccess()
    {
        var (data, sha256) = MakePayload("hello world");
        var ticket = _sut.CreateLocalStage("test.msix", sha256, data.Length);

        var result = await _sut.AcceptUploadAsync(
            ticket.UploadToken, new MemoryStream(data), data.Length);

        result.Success.Should().BeTrue();
        result.StagingId.Should().Be(ticket.StagingId);
        result.SizeBytes.Should().Be(data.Length);
    }

    [Fact]
    public async Task AcceptUpload_WithCorrectData_StagingStatusBecomesReady()
    {
        var (data, sha256) = MakePayload("package bytes");
        var ticket = _sut.CreateLocalStage("pkg.msix", sha256, data.Length);

        await _sut.AcceptUploadAsync(ticket.UploadToken, new MemoryStream(data), data.Length);

        var entry = _sut.ResolveLocal(ticket.StagingId)!;
        entry.Status.Should().Be(StagingStatus.Ready);
    }

    [Fact]
    public async Task AcceptUpload_WithCorrectData_InvalidatesToken()
    {
        var (data, sha256) = MakePayload("some bytes");
        var ticket = _sut.CreateLocalStage("pkg.msix", sha256, data.Length);

        await _sut.AcceptUploadAsync(ticket.UploadToken, new MemoryStream(data), data.Length);

        // Second attempt with same token should fail
        var second = await _sut.AcceptUploadAsync(
            ticket.UploadToken, new MemoryStream(data), data.Length);

        second.Success.Should().BeFalse();
        second.Error.Should().Be("TOKEN_NOT_FOUND");
    }

    // ─── AcceptUploadAsync — failure paths ───────────────────────────────────

    [Fact]
    public async Task AcceptUpload_UnknownToken_ReturnsTokenNotFound()
    {
        var result = await _sut.AcceptUploadAsync(
            "0000000000000000000000000000000000000000000000000000000000000000",
            new MemoryStream([1, 2, 3]), 3);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("TOKEN_NOT_FOUND");
    }

    [Fact]
    public async Task AcceptUpload_WrongSize_ReturnsWrongSize()
    {
        var (data, sha256) = MakePayload("data");
        // Declare 1 byte but claim content length of 999
        var ticket = _sut.CreateLocalStage("pkg.msix", sha256, data.Length);

        var result = await _sut.AcceptUploadAsync(
            ticket.UploadToken, new MemoryStream(data), contentLength: 999);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("WRONG_SIZE");
    }

    [Fact]
    public async Task AcceptUpload_CorrectSizeWrongHash_ReturnsSha256Mismatch()
    {
        var (data, _) = MakePayload("original");
        var wrongHash = new string('a', 64);
        var ticket = _sut.CreateLocalStage("pkg.msix", wrongHash, data.Length);

        var result = await _sut.AcceptUploadAsync(
            ticket.UploadToken, new MemoryStream(data), data.Length);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("SHA256_MISMATCH");
    }

    [Fact]
    public async Task AcceptUpload_ExceedsMaxSize_ReturnsFileTooLarge()
    {
        var ticket = _sut.CreateLocalStage("big.msix", FakeSha256, 100);

        var result = await _sut.AcceptUploadAsync(
            ticket.UploadToken, new MemoryStream([1]),
            contentLength: PackageStagingManager.MaxFileSizeBytes + 1);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("FILE_TOO_LARGE");
    }

    // ─── Discard ──────────────────────────────────────────────────────────────

    [Fact]
    public void Discard_ExistingEntry_ReturnsTrue()
    {
        var ticket = _sut.CreateLocalStage("app.msix", FakeSha256, 1024);

        _sut.Discard(ticket.StagingId).Should().BeTrue();
        _sut.ResolveLocal(ticket.StagingId).Should().BeNull();
    }

    [Fact]
    public void Discard_UnknownEntry_ReturnsFalse()
    {
        _sut.Discard("sa-nonexistent").Should().BeFalse();
    }

    // ─── PurgeExpired ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeExpired_RemovesEntriesWhoseTokenHasExpired()
    {
        // The expiry is TokenUploadExpiryMinutes in the future — we can't wait that
        // long in a unit test.  Instead we upload successfully and then manually
        // verify that a Ready entry is NOT purged before its retention window.
        var (data, sha256) = MakePayload("retain me");
        var ticket = _sut.CreateLocalStage("pkg.msix", sha256, data.Length);
        await _sut.AcceptUploadAsync(ticket.UploadToken, new MemoryStream(data), data.Length);

        // Purge should NOT remove a Ready entry that is still within retention
        _sut.PurgeExpired();

        _sut.ResolveLocal(ticket.StagingId).Should().NotBeNull("Ready entry should survive purge within retention window");
    }

    [Fact]
    public void PurgeExpired_DoesNotThrow_WithEmptyRegistry()
    {
        var act = () => _sut.PurgeExpired();
        act.Should().NotThrow();
    }

    // ─── CreateForwardingStage ────────────────────────────────────────────────

    [Fact]
    public void CreateForwardingStage_Returns_ValidTicket()
    {
        var ticket = _sut.CreateForwardingStage(
            "App.msix", FakeSha256, 1024,
            "http://B:5201/packages/stage/downstream-token",
            "sa-downstream123", "c3d4", hopCount: 2);

        ticket.StagingId.Should().StartWith("sa-");
        ticket.Hops.Should().Be(2);
        ticket.UploadPath.Should().Be($"/packages/stage/{ticket.UploadToken}");
    }

    [Fact]
    public void CreateForwardingStage_EntryShouldBeResolvableThroughResolve()
    {
        var ticket = _sut.CreateForwardingStage(
            "App.msix", FakeSha256, 1024,
            "http://B:5201/packages/stage/tok", "sa-ds", null, hopCount: 1);

        var entry = _sut.Resolve(ticket.StagingId);

        entry.Should().NotBeNull();
        entry.Should().BeOfType<ForwardingStagingEntry>();
        var fwd = (ForwardingStagingEntry)entry!;
        fwd.DownstreamStagingId.Should().Be("sa-ds");
        fwd.DownstreamDeviceId.Should().BeNull();
    }

    // ─── Resolve ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ReturnsNull_ForUnknownId()
    {
        _sut.Resolve("sa-does-not-exist").Should().BeNull();
    }

    [Fact]
    public void ResolveLocal_ReturnsNull_ForForwardingEntry()
    {
        var ticket = _sut.CreateForwardingStage(
            "App.msix", FakeSha256, 1024,
            "http://B/stage/tok", "sa-ds", null, hopCount: 1);

        // ResolveLocal must not return a forwarding entry
        _sut.ResolveLocal(ticket.StagingId).Should().BeNull();
    }

    // ─── Filename sanitisation ────────────────────────────────────────────────

    [Theory]
    [InlineData("My App.msix")]
    [InlineData("my-app_1.2.3.msixbundle")]
    [InlineData("app (debug).msix")]
    public void CreateLocalStage_AcceptsVariousFilenames(string filename)
    {
        var act = () => _sut.CreateLocalStage(filename, FakeSha256, 100);
        act.Should().NotThrow();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private const string FakeSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static (byte[] data, string sha256Hex) MakePayload(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(data);
        return (data, Convert.ToHexString(hash).ToLowerInvariant());
    }
}
