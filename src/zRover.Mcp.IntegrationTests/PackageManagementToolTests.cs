using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace zRover.Mcp.IntegrationTests;

/// <summary>
/// Integration tests for the device package management MCP tools exposed by the
/// Background Manager (<c>http://localhost:5200/mcp</c>).
///
/// Prerequisites:
///   1. zRover.BackgroundManager is running (port 5200).
///   2. Run: dotnet test --filter "PackageManagementToolTests"
///
/// These tests verify the tool schemas are present and that the tools return
/// well-formed JSON for both the local device and remote routing.
/// Tests that modify system state (install/uninstall) are skipped by default;
/// set the environment variable ZROVER_RUN_DESTRUCTIVE_TESTS=1 to enable them.
/// </summary>
[Collection("E2E")]
public class PackageManagementToolTests : IAsyncLifetime
{
    private static readonly Uri ManagerEndpoint = new(
        Environment.GetEnvironmentVariable("ZROVER_MANAGER_ENDPOINT")
        ?? "http://localhost:5200/mcp");

    private static readonly bool RunDestructive =
        string.Equals(Environment.GetEnvironmentVariable("ZROVER_RUN_DESTRUCTIVE_TESTS"), "1",
            StringComparison.Ordinal);

    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = ManagerEndpoint
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
    }

    // ─── Tool discovery ───────────────────────────────────────────────────────

    [Fact]
    public async Task Manager_Exposes_AllPackageManagementTools()
    {
        var tools = await _client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        names.Should().Contain("list_devices");
        names.Should().Contain("list_installed_packages");
        names.Should().Contain("install_package");
        names.Should().Contain("uninstall_package");
        names.Should().Contain("launch_app");
        names.Should().Contain("stop_app");
        names.Should().Contain("get_package_info");
        names.Should().Contain("request_package_upload");
        names.Should().Contain("get_package_stage_status");
        names.Should().Contain("discard_package_stage");
    }

    // ─── list_devices ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListDevices_AlwaysIncludes_LocalDevice()
    {
        var result = await _client.CallToolAsync("list_devices");
        var json   = GetText(result);

        using var doc = JsonDocument.Parse(json);
        var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();

        devices.Should().NotBeEmpty();

        var local = devices.FirstOrDefault(d =>
            d.TryGetProperty("deviceId", out var id) &&
            id.GetString() == "local");

        local.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "list_devices must always include a 'local' entry");

        local.GetProperty("isConnected").GetBoolean().Should().BeTrue();
        local.GetProperty("hops").GetInt32().Should().Be(0);
    }

    // ─── list_installed_packages ──────────────────────────────────────────────

    [Fact]
    public async Task ListInstalledPackages_ReturnsWellFormedResponse()
    {
        var result = await _client.CallToolAsync("list_installed_packages", new Dictionary<string, object?>
        {
            ["includeFrameworks"]     = false,
            ["includeSystemPackages"] = false,
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("packages", out var packages).Should().BeTrue();
        packages.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListInstalledPackages_WithNameFilter_ReturnsSubset()
    {
        // Filter by "Microsoft" — at minimum the Windows App Runtime is present on any dev machine
        var result = await _client.CallToolAsync("list_installed_packages", new Dictionary<string, object?>
        {
            ["nameFilter"] = "Microsoft",
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);
        var packages = doc.RootElement.GetProperty("packages").EnumerateArray().ToList();

        packages.Should().NotBeEmpty("at least one Microsoft package should be installed");

        foreach (var pkg in packages)
        {
            var familyName  = pkg.GetProperty("packageFamilyName").GetString()!;
            var displayName = pkg.GetProperty("displayName").GetString()!;
            (familyName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
             displayName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue($"filtered package '{familyName}' doesn't match the filter");
        }
    }

    [Fact]
    public async Task ListInstalledPackages_PackageShape_ContainsRequiredFields()
    {
        var result = await _client.CallToolAsync("list_installed_packages", new Dictionary<string, object?>
        {
            ["nameFilter"] = "Microsoft",
        });

        using var doc = JsonDocument.Parse(GetText(result));
        var first = doc.RootElement.GetProperty("packages").EnumerateArray().FirstOrDefault();

        first.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        first.TryGetProperty("packageFamilyName", out _).Should().BeTrue();
        first.TryGetProperty("packageFullName",   out _).Should().BeTrue();
        first.TryGetProperty("displayName",       out _).Should().BeTrue();
        first.TryGetProperty("version",           out _).Should().BeTrue();
        first.TryGetProperty("architecture",      out _).Should().BeTrue();
        first.TryGetProperty("isRunning",         out _).Should().BeTrue();
        first.TryGetProperty("apps",              out _).Should().BeTrue();
    }

    // ─── get_package_info ────────────────────────────────────────────────────

    [Fact]
    public async Task GetPackageInfo_UnknownPackage_ReturnsNotFound()
    {
        var result = await _client.CallToolAsync("get_package_info", new Dictionary<string, object?>
        {
            ["packageFamilyName"] = "zRover.DoesNotExist_8wekyb3d8bbwe"
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Be("PACKAGE_NOT_FOUND");
    }

    // ─── stop_app — NOT_RUNNING ───────────────────────────────────────────────

    [Fact]
    public async Task StopApp_NotRunningPackage_ReturnsNotRunning()
    {
        var result = await _client.CallToolAsync("stop_app", new Dictionary<string, object?>
        {
            ["packageFamilyName"] = "zRover.DoesNotExist_8wekyb3d8bbwe",
            ["force"] = false,
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString()
            .Should().BeOneOf("NOT_RUNNING", "PACKAGE_NOT_FOUND");
    }

    // ─── request_package_upload ───────────────────────────────────────────────

    [Fact]
    public async Task RequestPackageUpload_Returns_ValidTicket()
    {
        var result = await _client.CallToolAsync("request_package_upload", new Dictionary<string, object?>
        {
            ["filename"]  = "TestApp.msix",
            ["sha256"]    = new string('a', 64),
            ["sizeBytes"] = 1_000_000,
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("stagingId").GetString().Should().StartWith("sa-");
        doc.RootElement.GetProperty("uploadPath").GetString().Should().StartWith("/packages/stage/");
        doc.RootElement.GetProperty("localUploadUrl").GetString().Should().StartWith("http://127.0.0.1");
        doc.RootElement.GetProperty("hops").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task RequestPackageUpload_MissingSha256_ReturnsError()
    {
        var result = await _client.CallToolAsync("request_package_upload", new Dictionary<string, object?>
        {
            ["filename"]  = "TestApp.msix",
            ["sha256"]    = "",
            ["sizeBytes"] = 1_000_000,
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Be("MISSING_SHA256");
    }

    // ─── get_package_stage_status ─────────────────────────────────────────────

    [Fact]
    public async Task GetPackageStageStatus_AfterRequestUpload_ReturnsPendingUpload()
    {
        var uploadResult = await _client.CallToolAsync("request_package_upload", new Dictionary<string, object?>
        {
            ["filename"]  = "StatusTest.msix",
            ["sha256"]    = new string('b', 64),
            ["sizeBytes"] = 500_000,
        });

        using var uploadDoc = JsonDocument.Parse(GetText(uploadResult));
        var stagingId = uploadDoc.RootElement.GetProperty("stagingId").GetString()!;

        var statusResult = await _client.CallToolAsync("get_package_stage_status", new Dictionary<string, object?>
        {
            ["stagingId"] = stagingId,
        });

        using var statusDoc = JsonDocument.Parse(GetText(statusResult));
        statusDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        statusDoc.RootElement.GetProperty("status").GetString().Should().Be("pendingupload");
        statusDoc.RootElement.GetProperty("filename").GetString().Should().Be("StatusTest.msix");
    }

    // ─── discard_package_stage ────────────────────────────────────────────────

    [Fact]
    public async Task DiscardPackageStage_ExistingEntry_ReturnsSuccess()
    {
        var uploadResult = await _client.CallToolAsync("request_package_upload", new Dictionary<string, object?>
        {
            ["filename"]  = "ToDiscard.msix",
            ["sha256"]    = new string('c', 64),
            ["sizeBytes"] = 100_000,
        });

        using var uploadDoc = JsonDocument.Parse(GetText(uploadResult));
        var stagingId = uploadDoc.RootElement.GetProperty("stagingId").GetString()!;

        var discardResult = await _client.CallToolAsync("discard_package_stage", new Dictionary<string, object?>
        {
            ["stagingId"] = stagingId,
        });

        using var discardDoc = JsonDocument.Parse(GetText(discardResult));
        discardDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DiscardPackageStage_NonExistentEntry_ReturnsNotFound()
    {
        var result = await _client.CallToolAsync("discard_package_stage", new Dictionary<string, object?>
        {
            ["stagingId"] = "sa-doesnotexist",
        });

        using var doc = JsonDocument.Parse(GetText(result));
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Be("STAGING_NOT_FOUND");
    }

    // ─── HTTP upload round-trip (local, no install) ───────────────────────────

    [Fact]
    public async Task FullUploadRoundTrip_LocalStage_Succeeds()
    {
        // 1. Request an upload ticket via MCP
        var payload = "fake msix bytes for integration test"u8.ToArray();
        var sha256  = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
            .ToLowerInvariant();

        var ticketResult = await _client.CallToolAsync("request_package_upload", new Dictionary<string, object?>
        {
            ["filename"]  = "IntegTest.msix",
            ["sha256"]    = sha256,
            ["sizeBytes"] = payload.Length,
        });

        using var ticketDoc = JsonDocument.Parse(GetText(ticketResult));
        ticketDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var stagingId    = ticketDoc.RootElement.GetProperty("stagingId").GetString()!;
        var localUpload  = ticketDoc.RootElement.GetProperty("localUploadUrl").GetString()!;

        // 2. POST the package bytes to the upload URL
        using var http   = new System.Net.Http.HttpClient();
        var content      = new System.Net.Http.ByteArrayContent(payload);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = payload.Length;

        var uploadResponse = await http.PostAsync(localUpload, content);
        uploadResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            $"Upload failed: {await uploadResponse.Content.ReadAsStringAsync()}");

        // 3. Verify stage status is Ready
        var statusResult = await _client.CallToolAsync("get_package_stage_status", new Dictionary<string, object?>
        {
            ["stagingId"] = stagingId,
        });

        using var statusDoc = JsonDocument.Parse(GetText(statusResult));
        statusDoc.RootElement.GetProperty("status").GetString().Should().Be("ready");

        // 4. Clean up
        await _client.CallToolAsync("discard_package_stage", new Dictionary<string, object?>
        {
            ["stagingId"] = stagingId,
        });
    }

    // ─── Destructive tests (opt-in) ───────────────────────────────────────────

    [SkippableFact]
    public async Task InstallPackage_InvalidUri_ReturnsError()
    {
        Skip.IfNot(RunDestructive, "Set ZROVER_RUN_DESTRUCTIVE_TESTS=1 to run destructive tests");

        var result = await _client.CallToolAsync("install_package", new Dictionary<string, object?>
        {
            ["packageUri"] = "https://example.com/nonexistent.msix"
        });

        var json = GetText(result);
        using var doc = JsonDocument.Parse(json);

        // The install should fail gracefully — we just care it does not throw
        doc.RootElement.TryGetProperty("success", out var success).Should().BeTrue();
        // success may be false (expected) or the URI may resolve — just verify structure
        success.ValueKind.Should().Be(JsonValueKind.False);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetText(ModelContextProtocol.Client.CallToolResult result)
        => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
}

// SkippableFact attribute for conditional tests
[AttributeUsage(AttributeTargets.Method)]
file sealed class SkippableFactAttribute : FactAttribute
{
    public override string? Skip { get; set; }
}

file static class SkipExtension
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition) throw new Xunit.SkipException(reason);
    }
}
