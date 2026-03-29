using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace zRover.Mcp.IntegrationTests;

/// <summary>
/// E2E tests that exercise MCP input injection against the Color Picker test UI.
///
/// Approach:
///   1. Use dispatch_action SetPresetColor to reset to a known different color — no
///      coordinate guessing needed for setup, and no assumption about prior app state.
///   2. Look up the target button's actual center via get_ui_tree so inject_tap
///      works regardless of window size or Pivot header offset.
///   3. Tap the button via inject_tap.
///   4. Call wait_for visual_stable — blocks until the UI stops animating.
///   5. Read HexLabel.text via get_ui_tree — no screenshot pixel-sampling fragility.
///
/// Round-trip exercised:
///   Test → MCP HTTP → FullTrust Server → AppService IPC →
///   UWP InputInjector → XAML Button.Click → HexLabel update → get_ui_tree read
/// </summary>
[Collection("E2E")]
public class ColorPickerE2ETests : IAsyncLifetime
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

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Uses dispatch_action to programmatically set a preset color — reliable setup
    /// that does not depend on knowing exact UI coordinates.
    /// </summary>
    private async Task SetColorAsync(string colorName)
    {
        var result = await _client.CallToolAsync("dispatch_action",
            new Dictionary<string, object?>
            {
                { "action", "SetPresetColor" },
                { "params", new Dictionary<string, object?> { { "color", colorName } } }
            });
        result.IsError.Should().NotBe(true, $"dispatch_action SetPresetColor {colorName} failed");
    }

    /// <summary>
    /// Finds the center of a Button whose text content matches <paramref name="buttonText"/>
    /// by calling get_ui_tree. This adapts to any window size or Pivot header offset.
    /// </summary>
    private async Task<(double x, double y)> FindButtonCenterAsync(string buttonText)
    {
        var result = await _client.CallToolAsync("get_ui_tree", new Dictionary<string, object?>());
        result.IsError.Should().NotBe(true, "get_ui_tree (for button lookup) failed");

        var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        textBlock.Should().NotBeNull("get_ui_tree result should contain text");

        using var doc = JsonDocument.Parse(textBlock!.Text);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue("get_ui_tree should succeed");

        var bounds = FindButtonBounds(root.GetProperty("root"), buttonText)
            ?? throw new InvalidOperationException($"Button '{buttonText}' not found in UI tree");

        return (bounds.x + bounds.w / 2, bounds.y + bounds.h / 2);
    }

    private async Task TapAtAsync(double x, double y)
    {
        var result = await _client.CallToolAsync("inject_tap",
            new Dictionary<string, object?> { { "x", x }, { "y", y }, { "coordinateSpace", "normalized" } });
        result.IsError.Should().NotBe(true, $"inject_tap({x:F2},{y:F2}) failed");
    }

    private async Task WaitForStableAsync()
    {
        var result = await _client.CallToolAsync("wait_for",
            new Dictionary<string, object?> { { "condition", "visual_stable" }, { "timeoutMs", 8000 }, { "stabilityMs", 400 } });
        result.IsError.Should().NotBe(true, "wait_for visual_stable failed");
    }

    /// <summary>
    /// Reads the HexLabel TextBlock text from the UI tree (e.g. "#FF0000").
    /// </summary>
    private async Task<string> ReadHexLabelAsync()
    {
        var result = await _client.CallToolAsync("get_ui_tree", new Dictionary<string, object?>());
        result.IsError.Should().NotBe(true, "get_ui_tree failed");

        var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        textBlock.Should().NotBeNull("get_ui_tree result should contain text");

        using var doc = JsonDocument.Parse(textBlock!.Text);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue("get_ui_tree should succeed");

        return FindNodeText(root.GetProperty("root"), "HexLabel")
            ?? throw new InvalidOperationException("HexLabel node not found in UI tree");
    }

    private static string? FindNodeText(JsonElement node, string targetName)
    {
        if (node.TryGetProperty("name", out var nameEl) && nameEl.GetString() == targetName)
            return node.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var found = FindNodeText(child, targetName);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static (double x, double y, double w, double h)? FindButtonBounds(JsonElement node, string buttonText)
    {
        if (node.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "Button")
        {
            if (node.TryGetProperty("text", out var textEl) && textEl.GetString() == buttonText)
            {
                var b = node.GetProperty("bounds");
                return (b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(),
                        b.GetProperty("width").GetDouble(), b.GetProperty("height").GetDouble());
            }
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var found = FindButtonBounds(child, buttonText);
                if (found.HasValue) return found;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Button tap → wait_for stable → read HexLabel from UI tree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TapRedButton_HexLabelBecomesFF0000()
    {
        await SetColorAsync("Blue");           // reset to a known different state
        var (x, y) = await FindButtonCenterAsync("Red");
        await TapAtAsync(x, y);
        await WaitForStableAsync();

        var hex = await ReadHexLabelAsync();
        hex.Should().Be("#FF0000", "Red button sets color to #FF0000");
    }

    [Fact]
    public async Task TapGreenButton_HexLabelBecomesGreen()
    {
        await SetColorAsync("Red");            // reset
        var (x, y) = await FindButtonCenterAsync("Green");
        await TapAtAsync(x, y);
        await WaitForStableAsync();

        var hex = await ReadHexLabelAsync();
        hex.Should().Be("#00FF00", "Green button sets color to #00FF00");
    }

    [Fact]
    public async Task TapBlueButton_HexLabelBecomesBlue()
    {
        await SetColorAsync("Red");            // reset
        var (x, y) = await FindButtonCenterAsync("Blue");
        await TapAtAsync(x, y);
        await WaitForStableAsync();

        var hex = await ReadHexLabelAsync();
        hex.Should().Be("#0000FF", "Blue button sets color to #0000FF");
    }

    [Fact]
    public async Task TapYellowButton_HexLabelBecomesYellow()
    {
        await SetColorAsync("Blue");            // reset
        var (x, y) = await FindButtonCenterAsync("Yellow");
        await TapAtAsync(x, y);
        await WaitForStableAsync();

        var hex = await ReadHexLabelAsync();
        hex.Should().Be("#FFFF00", "Yellow button sets color to #FFFF00");
    }

    [Fact]
    public async Task TapWhiteButton_HexLabelBecomesWhite()
    {
        await SetColorAsync("Blue");            // reset
        var (x, y) = await FindButtonCenterAsync("White");
        await TapAtAsync(x, y);
        await WaitForStableAsync();

        var hex = await ReadHexLabelAsync();
        hex.Should().Be("#FFFFFF", "White button sets color to #FFFFFF");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Screenshot sanity — verifies capture_current_view works
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Screenshot_HasReasonableDimensions()
    {
        var result = await _client.CallToolAsync("capture_current_view",
            new Dictionary<string, object?> { { "format", "png" } });
        result.IsError.Should().NotBe(true, "capture_current_view failed");

        var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        textBlock.Should().NotBeNull();

        using var doc = JsonDocument.Parse(textBlock!.Text);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var width  = root.GetProperty("width").GetInt32();
        var height = root.GetProperty("height").GetInt32();
        var path   = root.GetProperty("filePath").GetString()!;

        width.Should().BeGreaterThan(100, "screenshot should have reasonable width");
        height.Should().BeGreaterThan(100, "screenshot should have reasonable height");
        File.Exists(path).Should().BeTrue("screenshot PNG file should exist at returned path");
    }
}
