using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using zRover.BackgroundManager.Packages;

namespace zRover.BackgroundManager.Tests;

/// <summary>
/// Unit tests for <see cref="PackageInstallManager"/>.
/// All tests use a <see cref="FakeDevCertManager"/> — no Windows cert store or tools required.
/// </summary>
public sealed class PackageInstallManagerTests
{
    // ─── Enable ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnableAsync_WhenDisabled_SetsIsEnabledTrue()
    {
        var (sut, _) = Build();
        await sut.EnableAsync();
        sut.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnableAsync_FirstCall_CallsEnsureReadyOnce()
    {
        var (sut, cert) = Build();
        await sut.EnableAsync();
        cert.EnsureReadyCallCount.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_SecondCall_DoesNotCallEnsureReadyAgain()
    {
        var (sut, cert) = Build();
        await sut.EnableAsync(); // enable
        sut.Disable();           // disable again
        await sut.EnableAsync(); // re-enable
        cert.EnsureReadyCallCount.Should().Be(1, "cert should only be initialised once");
    }

    [Fact]
    public async Task EnableAsync_WhenAlreadyEnabled_IsNoOp()
    {
        var (sut, cert) = Build();
        await sut.EnableAsync();
        await sut.EnableAsync(); // second call
        cert.EnsureReadyCallCount.Should().Be(1);
        sut.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnableAsync_FiresStateChanged()
    {
        var (sut, _) = Build();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        await sut.EnableAsync();
        fired.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_WhenAlreadyEnabled_DoesNotFireStateChanged()
    {
        var (sut, _) = Build();
        await sut.EnableAsync();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        await sut.EnableAsync(); // already enabled
        fired.Should().Be(0);
    }

    [Fact]
    public async Task EnableAsync_WhenCertInitFails_StillSetsIsEnabled()
    {
        var (sut, cert) = Build(certThrows: true);
        await sut.EnableAsync();
        sut.IsEnabled.Should().BeTrue("gate should open even if cert init failed");
    }

    [Fact]
    public async Task EnableAsync_WhenCertInitFails_DoesNotRetryOnNextEnable()
    {
        var (sut, cert) = Build(certThrows: true);
        await sut.EnableAsync();
        sut.Disable();
        await sut.EnableAsync(); // second enable
        cert.EnsureReadyCallCount.Should().Be(1, "failed init should not be retried");
    }

    // ─── Disable ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disable_WhenEnabled_SetsIsEnabledFalse()
    {
        var (sut, _) = Build();
        await sut.EnableAsync();
        sut.Disable();
        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Disable_WhenEnabled_FiresStateChanged()
    {
        var (sut, _) = Build();
        await sut.EnableAsync();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        sut.Disable();
        fired.Should().Be(1);
    }

    [Fact]
    public void Disable_WhenAlreadyDisabled_IsNoOp()
    {
        var (sut, _) = Build();
        int fired = 0;
        sut.StateChanged += (_, _) => fired++;
        sut.Disable(); // already disabled
        sut.IsEnabled.Should().BeFalse();
        fired.Should().Be(0);
    }

    // ─── Default state ────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_DefaultsToFalse()
    {
        var (sut, _) = Build();
        sut.IsEnabled.Should().BeFalse();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (PackageInstallManager sut, FakeDevCertManager cert) Build(bool certThrows = false)
    {
        var cert = new FakeDevCertManager(certThrows);
        var sut = new PackageInstallManager(cert, NullLogger<PackageInstallManager>.Instance);
        return (sut, cert);
    }
}

/// <summary>Controllable fake for <see cref="IDevCertManager"/>.</summary>
internal sealed class FakeDevCertManager : IDevCertManager
{
    private readonly bool _throws;
    public int EnsureReadyCallCount { get; private set; }
    public bool IsReady => EnsureReadyCallCount > 0 && !_throws;

    public FakeDevCertManager(bool throws = false) => _throws = throws;

    public Task EnsureReadyAsync(CancellationToken ct = default)
    {
        EnsureReadyCallCount++;
        if (_throws) throw new InvalidOperationException("Simulated cert init failure");
        return Task.CompletedTask;
    }

    public Task SignPackageAsync(string msixPath, CancellationToken ct = default) => Task.CompletedTask;
}
