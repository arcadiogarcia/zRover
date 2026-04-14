using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace zRover.Mcp.IntegrationTests;

/// <summary>
/// Integration tests for the new semantic-targeting tools:
/// tap_element, find_element, hittest, and activate_element.
///
/// These tests require the zRover.WinUI.Sample app to be running.
/// </summary>
[Collection("E2E")]
public class NewToolsE2ETests : IAsyncLifetime
{
    private static readonly Uri McpEndpoint = new(
        Environment.GetEnvironmentVariable("ZROVER_MCP_ENDPOINT")
        ?? "http://localhost:5100/mcp");

    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = McpEndpoint
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task SwitchToTabAsync(string tabName)
    {
        var result = await _client.CallToolAsync("dispatch_action",
            new Dictionary<string, object?>
            {
                { "action", "SwitchTab" },
                { "params", new Dictionary<string, object?> { { "tab", tabName } } }
            });
        result.IsError.Should().NotBe(true, $"SwitchTab '{tabName}' failed");
        await Task.Delay(400);
    }

    private async Task SetColorAsync(string colorName)
    {
        var result = await _client.CallToolAsync("dispatch_action",
            new Dictionary<string, object?>
            {
                { "action", "SetPresetColor" },
                { "params", new Dictionary<string, object?> { { "color", colorName } } }
            });
        result.IsError.Should().NotBe(true, $"SetPresetColor {colorName} failed");
    }

    private async Task WaitForStableAsync()
    {
        await _client.CallToolAsync("wait_for",
            new Dictionary<string, object?> { { "condition", "visual_stable" }, { "timeoutMs", 5000 }, { "stabilityMs", 400 } });
    }

    private static string GetText(CallToolResult result)
    {
        var tb = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return tb?.Text ?? "";
    }

    private static JsonElement GetJson(CallToolResult result)
    {
        var text = GetText(result);
        return JsonDocument.Parse(text).RootElement;
    }

    // ═══════════════════════════════════════════════════════════════
    //  find_element tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindElement_ByName_ReturnsSlider()
    {
        await SwitchToTabAsync("Color Picker");

        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "name", "RedSlider" } });

        result.IsError.Should().NotBe(true, "find_element should not error");
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeTrue();
        json.GetProperty("type").GetString().Should().Be("Slider");
        json.GetProperty("centerX").GetDouble().Should().BeInRange(0.01, 0.99);
        json.GetProperty("centerY").GetDouble().Should().BeInRange(0.01, 0.99);
    }

    [Fact]
    public async Task FindElement_ByType_ReturnsButtons()
    {
        await SwitchToTabAsync("Color Picker");

        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "type", "Button" }, { "all", true } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeTrue();
        json.GetProperty("matchCount").GetInt32().Should().BeGreaterOrEqualTo(5,
            "Color Picker tab has at least 5 buttons (Red, Green, Blue, Yellow, White)");
    }

    [Fact]
    public async Task FindElement_ByText_ReturnsButtonMatch()
    {
        // WinUI Buttons expose their Content string via text search, not AutomationProperties.Name
        await SwitchToTabAsync("Color Picker");

        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "text", "Red" }, { "type", "Button" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeTrue();
        json.GetProperty("type").GetString().Should().Be("Button");
    }

    [Fact]
    public async Task FindElement_ByText_FindsHexInput()
    {
        await SwitchToTabAsync("Color Picker");
        await SetColorAsync("Red");
        await Task.Delay(200);

        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "name", "HexInput" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeTrue();
        json.GetProperty("type").GetString().Should().Be("TextBox");
    }

    [Fact]
    public async Task FindElement_NotFound_ReturnsFalse()
    {
        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "name", "NonexistentElement12345" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task FindElement_NoSearchCriteria_ReturnsError()
    {
        var result = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?>());

        result.IsError.Should().NotBe(true); // MCP won't error, but the result should indicate failure
        var json = GetJson(result);
        json.GetProperty("found").GetBoolean().Should().BeFalse();
        json.TryGetProperty("error", out _).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  hittest tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task HitTest_AtCenter_ReturnsElement()
    {
        await SwitchToTabAsync("Color Picker");

        // (0.5, 0.5) should hit the ColorPreview rectangle area
        var result = await _client.CallToolAsync("hittest",
            new Dictionary<string, object?> { { "x", 0.5 }, { "y", 0.5 } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("centerX").GetDouble().Should().BeInRange(0.0, 1.0);
        json.GetProperty("centerY").GetDouble().Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public async Task HitTest_AtTopLeft_ReturnsElement()
    {
        var result = await _client.CallToolAsync("hittest",
            new Dictionary<string, object?> { { "x", 0.1 }, { "y", 0.05 } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  tap_element tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TapElement_ByText_ClicksRedButton()
    {
        await SetColorAsync("Blue");               // reset to known state
        await SwitchToTabAsync("Color Picker");

        // Use find_element to locate Red button by text, then tap with inject_tap
        var findResult = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "text", "Red" }, { "type", "Button" } });
        var findJson = GetJson(findResult);
        findJson.GetProperty("found").GetBoolean().Should().BeTrue("Red button should be found by text");

        double cx = findJson.GetProperty("centerX").GetDouble();
        double cy = findJson.GetProperty("centerY").GetDouble();

        var tapResult = await _client.CallToolAsync("inject_tap",
            new Dictionary<string, object?> { { "x", cx }, { "y", cy }, { "coordinateSpace", "normalized" } });
        tapResult.IsError.Should().NotBe(true, "inject_tap should not error");

        // Verify the color actually changed
        await WaitForStableAsync();
        var treeResult = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "name", "HexInput" } });
        var treeJson = GetJson(treeResult);
        treeJson.GetProperty("found").GetBoolean().Should().BeTrue();
        treeJson.GetProperty("text").GetString().Should().Be("FF0000");
    }

    [Fact]
    public async Task TapElement_ByName_ClicksClearTextButton()
    {
        await SwitchToTabAsync("Text Input");
        await Task.Delay(200);

        // First type some text
        await _client.CallToolAsync("inject_text",
            new Dictionary<string, object?> { { "text", "hello" } });
        await Task.Delay(200);

        // tap_element to click "Clear All" button by name
        var result = await _client.CallToolAsync("tap_element",
            new Dictionary<string, object?> { { "name", "ClearTextBtn" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TapElement_DryRun_DoesNotInject()
    {
        await SwitchToTabAsync("Text Input");
        await Task.Delay(200);

        // dryRun should show where the tap would land without actually clicking
        var result = await _client.CallToolAsync("tap_element",
            new Dictionary<string, object?> { { "name", "ClearTextBtn" }, { "dryRun", true } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TapElement_NotFound_ReturnsError()
    {
        var result = await _client.CallToolAsync("tap_element",
            new Dictionary<string, object?> { { "name", "DoesNotExist999" } });

        result.IsError.Should().NotBe(true); // MCP doesn't error, but result.success = false
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.TryGetProperty("error", out _).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  activate_element tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ActivateElement_InvokeButton_ClicksClearAll()
    {
        await SwitchToTabAsync("Text Input");
        await Task.Delay(200);

        // Type some text first
        await _client.CallToolAsync("inject_text",
            new Dictionary<string, object?> { { "text", "hello" } });
        await Task.Delay(200);

        // activate_element by x:Name to invoke the Clear All button
        var result = await _client.CallToolAsync("activate_element",
            new Dictionary<string, object?> { { "name", "ClearTextBtn" }, { "action", "invoke" } });

        result.IsError.Should().NotBe(true, "activate_element should not error");
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("method").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ActivateElement_Focus_FocusesTextBox()
    {
        await SwitchToTabAsync("Text Input");
        await Task.Delay(200);

        var result = await _client.CallToolAsync("activate_element",
            new Dictionary<string, object?> { { "name", "TestTextBox" }, { "action", "focus" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("method").GetString().Should().Be("Control.Focus");
    }

    [Fact]
    public async Task ActivateElement_NotFound_ReturnsError()
    {
        var result = await _client.CallToolAsync("activate_element",
            new Dictionary<string, object?> { { "name", "NonexistentElement" }, { "action", "invoke" } });

        result.IsError.Should().NotBe(true);
        var json = GetJson(result);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cross-tool workflow: find_element → inject_tap
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindThenTap_UsesExactCoordinates()
    {
        await SetColorAsync("Red");
        await SwitchToTabAsync("Color Picker");

        // Step 1: find_element returns centerX/centerY
        var findResult = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "text", "Blue" }, { "type", "Button" } });
        var findJson = GetJson(findResult);
        findJson.GetProperty("found").GetBoolean().Should().BeTrue();

        double cx = findJson.GetProperty("centerX").GetDouble();
        double cy = findJson.GetProperty("centerY").GetDouble();
        cx.Should().BeInRange(0.01, 0.99);
        cy.Should().BeInRange(0.01, 0.99);

        // Step 2: inject_tap with those exact coordinates
        var tapResult = await _client.CallToolAsync("inject_tap",
            new Dictionary<string, object?> { { "x", cx }, { "y", cy }, { "coordinateSpace", "normalized" } });
        tapResult.IsError.Should().NotBe(true);

        // Step 3: verify the color changed to Blue
        await WaitForStableAsync();
        var hexResult = await _client.CallToolAsync("find_element",
            new Dictionary<string, object?> { { "name", "HexInput" } });
        var hexJson = GetJson(hexResult);
        hexJson.GetProperty("text").GetString().Should().Be("0000FF");
    }
}
