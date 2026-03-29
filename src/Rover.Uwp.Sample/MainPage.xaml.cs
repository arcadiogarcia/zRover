using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rover.Core;
using Windows.UI;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Rover.Uwp.Sample
{
    public sealed partial class MainPage : Page, IActionableApp
    {
        public MainPage()
        {
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                var path = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "mainpage-crash.log");
                System.IO.File.WriteAllText(path,
                    $"{DateTimeOffset.Now:o} InitializeComponent FAILED:\r\n{ex}\r\n");
                throw;
            }

            UpdateColorPreview();

            // Subscribe after InitializeComponent so events don't fire before
            // all named elements are available.
            RedSlider.ValueChanged += Slider_ValueChanged;
            GreenSlider.ValueChanged += Slider_ValueChanged;
            BlueSlider.ValueChanged += Slider_ValueChanged;

            Rover.Uwp.RoverMcp.Log("MainPage", "MainPage initialized");

            // Input diagnostics via Rover logging — visible in get_logs, helps diagnose
            // injection misclicks without requiring further app changes.
            var coreWindow = Windows.UI.Core.CoreWindow.GetForCurrentThread();
            coreWindow.PointerPressed += (s, e) =>
            {
                var pt = e.CurrentPoint;
                Rover.Uwp.RoverMcp.Log("MainPage.Input",
                    $"PointerPressed type={pt.PointerDevice.PointerDeviceType} " +
                    $"id={pt.PointerId} pos=({pt.Position.X:F1},{pt.Position.Y:F1}) " +
                    $"primary={pt.Properties.IsPrimary}");
            };
            coreWindow.PointerReleased += (s, e) =>
            {
                var pt = e.CurrentPoint;
                if (pt.PointerDevice.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Mouse)
                    Rover.Uwp.RoverMcp.Log("MainPage.Input",
                        $"PointerReleased type={pt.PointerDevice.PointerDeviceType} " +
                        $"id={pt.PointerId} pos=({pt.Position.X:F1},{pt.Position.Y:F1})");
            };

            // Log window content size on load and resize — needed to interpret normalized coordinates.
            this.Loaded += (s, e) =>
                Rover.Uwp.RoverMcp.Log("MainPage.Layout",
                    $"Content size: {ActualWidth:F0}x{ActualHeight:F0}");
            this.SizeChanged += (s, e) =>
                Rover.Uwp.RoverMcp.Log("MainPage.Layout",
                    $"Size changed: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");

            // Track text changes for char count
            TestTextBox.TextChanged += TextBox_TextChanged;
            MultiLineTextBox.TextChanged += TextBox_TextChanged;

            // Configure InkCanvas to accept all input types (pen, mouse, touch)
            TestInkCanvas.InkPresenter.InputDeviceTypes =
                Windows.UI.Core.CoreInputDeviceTypes.Pen |
                Windows.UI.Core.CoreInputDeviceTypes.Mouse |
                Windows.UI.Core.CoreInputDeviceTypes.Touch;

            // Default ink attributes — use a bright color visible on both dark and light themes
            var drawingAttrs = new InkDrawingAttributes
            {
                Color = Colors.DeepSkyBlue,
                Size = new Windows.Foundation.Size(4, 4),
                IgnorePressure = false,
                FitToCurve = true
            };
            TestInkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(drawingAttrs);

            // Track stroke changes
            TestInkCanvas.InkPresenter.StrokesCollected += InkPresenter_StrokesCollected;
            TestInkCanvas.InkPresenter.StrokesErased += InkPresenter_StrokesErased;
        }

        #region Color Picker

        private void PresetColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string hex)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);

                Rover.Uwp.RoverMcp.Log("MainPage.ColorPicker", $"Preset color clicked: #{hex} (R={r}, G={g}, B={b})");
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
            }
        }

        private void Slider_ValueChanged(object sender,
            Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateColorPreview();
        }

        private void UpdateColorPreview()
        {
            if (PreviewBrush == null) return;

            byte r = (byte)RedSlider.Value;
            byte g = (byte)GreenSlider.Value;
            byte b = (byte)BlueSlider.Value;

            PreviewBrush.Color = Color.FromArgb(255, r, g, b);
            HexLabel.Text = $"#{r:X2}{g:X2}{b:X2}";
            RedValue.Text = r.ToString();
            GreenValue.Text = g.ToString();
            BlueValue.Text = b.ToString();

            Rover.Uwp.RoverMcp.Log("MainPage.ColorPicker", $"Color updated: #{r:X2}{g:X2}{b:X2} (R={r}, G={g}, B={b})");
        }

        #endregion

        #region Text Input

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            int total = (TestTextBox.Text?.Length ?? 0) + (MultiLineTextBox.Text?.Length ?? 0);
            CharCountLabel.Text = $"Chars: {total}";
        }

        private void ClearText_Click(object sender, RoutedEventArgs e)
        {
            Rover.Uwp.RoverMcp.Log("MainPage.TextInput", "Text cleared by user");
            TestTextBox.Text = "";
            MultiLineTextBox.Text = "";
        }

        #endregion

        #region Ink Canvas

        private void ClearInk_Click(object sender, RoutedEventArgs e)
        {
            int before = TestInkCanvas.InkPresenter.StrokeContainer.GetStrokes().Count;
            Rover.Uwp.RoverMcp.Log("MainPage.InkCanvas", $"Ink cleared ({before} strokes removed)");
            TestInkCanvas.InkPresenter.StrokeContainer.Clear();
            StrokeCountLabel.Text = "Strokes: 0";
        }

        private void InkMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string mode)
            {
                var attrs = TestInkCanvas.InkPresenter.CopyDefaultDrawingAttributes();
                Rover.Uwp.RoverMcp.Log("MainPage.InkCanvas", $"Ink mode changed to: {mode}");
                switch (mode)
                {
                    case "pen":
                        attrs.Size = new Windows.Foundation.Size(4, 4);
                        attrs.Color = Colors.Black;
                        attrs.DrawAsHighlighter = false;
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                        break;
                    case "eraser":
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Erasing;
                        return;
                    case "highlighter":
                        attrs.Size = new Windows.Foundation.Size(16, 8);
                        attrs.Color = Colors.Yellow;
                        attrs.DrawAsHighlighter = true;
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                        break;
                }
                TestInkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(attrs);
            }
        }

        private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            UpdateStrokeCount();
            Rover.Uwp.RoverMcp.Log("MainPage.InkCanvas",
                $"{args.Strokes.Count} stroke(s) collected — total: {TestInkCanvas.InkPresenter.StrokeContainer.GetStrokes().Count}");
        }

        private void InkPresenter_StrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
        {
            UpdateStrokeCount();
            Rover.Uwp.RoverMcp.Log("MainPage.InkCanvas",
                $"{args.Strokes.Count} stroke(s) erased — total remaining: {TestInkCanvas.InkPresenter.StrokeContainer.GetStrokes().Count}");
        }

        private void UpdateStrokeCount()
        {
            int count = TestInkCanvas.InkPresenter.StrokeContainer.GetStrokes().Count;
            StrokeCountLabel.Text = $"Strokes: {count}";
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        //  IActionableApp — App Action API
        // ═══════════════════════════════════════════════════════════════

        private static readonly IReadOnlyList<ActionDescriptor> _actions = new[]
        {
            new ActionDescriptor(
                name: "SetPresetColor",
                description: "Sets the color picker to one of the five preset colors by name, " +
                             "updating the R, G, and B sliders and the preview rectangle.",
                parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""color""],
  ""properties"": {
    ""color"": {
      ""type"": ""string"",
      ""description"": ""The preset color name to apply."",
      ""enum"": [""Red"", ""Green"", ""Blue"", ""Yellow"", ""White""]
    }
  }
}"),
            new ActionDescriptor(
                name: "SwitchTab",
                description: "Switches the visible tab in the main Pivot. " +
                             "Use this to navigate between the Color Picker, Text Input, Ink Canvas, and Scroll Test tabs.",
                parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""tab""],
  ""properties"": {
    ""tab"": {
      ""type"": ""string"",
      ""description"": ""The tab to switch to."",
      ""enum"": [""Color Picker"", ""Text Input"", ""Ink Canvas"", ""Scroll Test""]
    }
  }
}"),
            new ActionDescriptor(
                name: "SetColorChannel",
                description: "Sets a single RGB color channel to the given value (0–255), " +
                             "updating the corresponding slider and the preview rectangle.",
                parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""channel"", ""value""],
  ""properties"": {
    ""channel"": {
      ""type"": ""string"",
      ""description"": ""Which color channel to set: R (red), G (green), or B (blue)."",
      ""enum"": [""R"", ""G"", ""B""]
    },
    ""value"": {
      ""type"": ""integer"",
      ""description"": ""The channel intensity, from 0 (off) to 255 (full)."",
      ""minimum"": 0,
      ""maximum"": 255
    }
  }
}"),
        };

        public IReadOnlyList<ActionDescriptor> GetAvailableActions() => _actions;

        public async Task<ActionResult> DispatchAsync(string actionName, string parametersJson)
        {
            Rover.Uwp.RoverMcp.Log("MainPage.Actions", $"Dispatching action: {actionName} params={parametersJson}");
            try
            {
                ActionResult result;
                switch (actionName)
                {
                    case "SetPresetColor":
                        result = await DispatchSetPresetColorAsync(parametersJson); break;
                    case "SetColorChannel":
                        result = await DispatchSetColorChannelAsync(parametersJson); break;
                    case "SwitchTab":
                        result = await DispatchSwitchTabAsync(parametersJson); break;
                    default:
                        result = ActionResult.Fail("unknown_action", $"No action named '{actionName}' is registered."); break;
                }
                if (result.Success)
                    Rover.Uwp.RoverMcp.Log("MainPage.Actions", $"Action '{actionName}' succeeded");
                else
                    Rover.Uwp.RoverMcp.LogWarn("MainPage.Actions", $"Action '{actionName}' failed: {result.ErrorMessage}");
                return result;
            }
            catch (Exception ex)
            {
                Rover.Uwp.RoverMcp.LogError("MainPage.Actions", $"Action '{actionName}' threw an exception", ex);
                return ActionResult.Fail("execution_error", ex.Message);
            }
        }

        private Task<ActionResult> DispatchSetPresetColorAsync(string parametersJson)
        {
            JObject p;
            try { p = JObject.Parse(parametersJson); }
            catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

            var colorName = p["color"]?.Value<string>();
            byte r, g, b;
            switch (colorName)
            {
                case "Red": r = 255; g = 0; b = 0; break;
                case "Green": r = 0; g = 255; b = 0; break;
                case "Blue": r = 0; g = 0; b = 255; break;
                case "Yellow": r = 255; g = 255; b = 0; break;
                case "White": r = 255; g = 255; b = 255; break;
                default:
                    return Task.FromResult(ActionResult.Fail("validation_error",
                        $"params.color: '{colorName}' is not in the valid set [Red, Green, Blue, Yellow, White]"));
            }

            var tcs = new TaskCompletionSource<ActionResult>();
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    RedSlider.Value = r;
                    GreenSlider.Value = g;
                    BlueSlider.Value = b;
                    tcs.TrySetResult(ActionResult.Ok(new[] { "UpdateColorPreview" }));
                }
                catch (Exception ex) { tcs.TrySetResult(ActionResult.Fail("execution_error", ex.Message)); }
            });
            return tcs.Task;
        }

        private Task<ActionResult> DispatchSetColorChannelAsync(string parametersJson)
        {
            JObject p;
            try { p = JObject.Parse(parametersJson); }
            catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

            var channel = p["channel"]?.Value<string>();
            var valueToken = p["value"];
            if (valueToken == null)
                return Task.FromResult(ActionResult.Fail("validation_error", "params.value is required."));

            int value = valueToken.Value<int>();
            if (value < 0 || value > 255)
                return Task.FromResult(ActionResult.Fail("validation_error",
                    $"params.value: {value} is out of range [0, 255]"));

            if (channel != "R" && channel != "G" && channel != "B")
                return Task.FromResult(ActionResult.Fail("validation_error",
                    $"params.channel: '{channel}' is not in the valid set [R, G, B]"));

            var tcs = new TaskCompletionSource<ActionResult>();
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    switch (channel)
                    {
                        case "R": RedSlider.Value = value; break;
                        case "G": GreenSlider.Value = value; break;
                        case "B": BlueSlider.Value = value; break;
                    }
                    tcs.TrySetResult(ActionResult.Ok(new[] { "UpdateColorPreview" }));
                }
                catch (Exception ex) { tcs.TrySetResult(ActionResult.Fail("execution_error", ex.Message)); }
            });
            return tcs.Task;
        }

        private Task<ActionResult> DispatchSwitchTabAsync(string parametersJson)
        {
            JObject p;
            try { p = JObject.Parse(parametersJson); }
            catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

            var tab = p["tab"]?.Value<string>();
            int index;
            switch (tab)
            {
                case "Color Picker": index = 0; break;
                case "Text Input": index = 1; break;
                case "Ink Canvas": index = 2; break;
                case "Scroll Test": index = 3; break;
                default:
                    return Task.FromResult(ActionResult.Fail("validation_error",
                        $"params.tab: '{tab}' is not in the valid set [Color Picker, Text Input, Ink Canvas, Scroll Test]"));
            }

            var tcs = new TaskCompletionSource<ActionResult>();
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    ((Windows.UI.Xaml.Controls.Pivot)Content).SelectedIndex = index;
                    tcs.TrySetResult(ActionResult.Ok(new[] { "TabChanged" }));
                }
                catch (Exception ex) { tcs.TrySetResult(ActionResult.Fail("execution_error", ex.Message)); }
            });
            return tcs.Task;
        }
    }
}
