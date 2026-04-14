using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Newtonsoft.Json.Linq;
using Windows.UI;
using zRover.Core;

namespace zRover.WinUI.Sample
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

            RedSlider.ValueChanged += Slider_ValueChanged;
            GreenSlider.ValueChanged += Slider_ValueChanged;
            BlueSlider.ValueChanged += Slider_ValueChanged;
            HexInput.KeyDown += HexInput_KeyDown;
            HexInput.LostFocus += HexInput_LostFocus;

            zRover.WinUI.RoverMcp.Log("MainPage", "MainPage initialized");

            // Input diagnostics via zRover logging
            this.PointerPressed += (s, e) =>
            {
                var pt = e.GetCurrentPoint(this);
                zRover.WinUI.RoverMcp.Log("MainPage.Input",
                    $"PointerPressed type={e.Pointer.PointerDeviceType} " +
                    $"id={pt.PointerId} pos=({pt.Position.X:F1},{pt.Position.Y:F1}) " +
                    $"primary={pt.Properties.IsPrimary}");
            };
            this.PointerReleased += (s, e) =>
            {
                var pt = e.GetCurrentPoint(this);
                if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
                    zRover.WinUI.RoverMcp.Log("MainPage.Input",
                        $"PointerReleased type={e.Pointer.PointerDeviceType} " +
                        $"id={pt.PointerId} pos=({pt.Position.X:F1},{pt.Position.Y:F1})");
            };
            this.PointerCanceled += (s, e) =>
            {
                var pt = e.GetCurrentPoint(this);
                zRover.WinUI.RoverMcp.Log("MainPage.Input",
                    $"PointerCanceled type={e.Pointer.PointerDeviceType} " +
                    $"id={pt.PointerId} pos=({pt.Position.X:F1},{pt.Position.Y:F1})");
            };

            this.Loaded += (s, e) =>
                zRover.WinUI.RoverMcp.Log("MainPage.Layout",
                    $"Content size: {ActualWidth:F0}x{ActualHeight:F0}");
            this.SizeChanged += (s, e) =>
                zRover.WinUI.RoverMcp.Log("MainPage.Layout",
                    $"Size changed: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");

            TestTextBox.TextChanged += TextBox_TextChanged;
            MultiLineTextBox.TextChanged += TextBox_TextChanged;
        }

        #region Color Picker

        private void PresetColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string hex)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);

                zRover.WinUI.RoverMcp.Log("MainPage.ColorPicker", $"Preset color clicked: #{hex} (R={r}, G={g}, B={b})");
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
            }
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
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
            if (HexInput != null) HexInput.Text = $"{r:X2}{g:X2}{b:X2}";
            RedValue.Text = r.ToString();
            GreenValue.Text = g.ToString();
            BlueValue.Text = b.ToString();

            zRover.WinUI.RoverMcp.Log("MainPage.ColorPicker", $"Color updated: #{r:X2}{g:X2}{b:X2} (R={r}, G={g}, B={b})");
        }

        private void HexInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                ApplyHexInput();
        }

        private void HexInput_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyHexInput();
        }

        private void ApplyHexInput()
        {
            var hex = HexInput.Text.Trim().TrimStart('#');
            if (hex.Length != 6) return;
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
                zRover.WinUI.RoverMcp.Log("MainPage.ColorPicker", $"Hex input applied: #{hex.ToUpper()}");
            }
            catch { /* ignore bad input */ }
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
            zRover.WinUI.RoverMcp.Log("MainPage.TextInput", "Text cleared by user");
            TestTextBox.Text = "";
            MultiLineTextBox.Text = "";
        }

        #endregion

        #region Canvas Drawing (Ink Canvas replacement)

        private Polyline? _currentStroke;
        private int _strokeCount = 0;

        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
            TestInkCanvas.CapturePointer(e.Pointer);
            var pt = e.GetCurrentPoint(TestInkCanvas);
            var pointerType = e.Pointer.PointerDeviceType;
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"PointerPressed type={pointerType} pos=({pt.Position.X:F0},{pt.Position.Y:F0}) isPrimary={pt.Properties.IsPrimary} isLeft={pt.Properties.IsLeftButtonPressed}");
            var stroke = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 191, 255)),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            stroke.Points.Add(pt.Position);
            TestInkCanvas.Children.Add(stroke);
            _currentStroke = stroke;
        }

        private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_currentStroke == null) return;
            e.Handled = true;
            var pt = e.GetCurrentPoint(TestInkCanvas);
            if (pt.Properties.IsLeftButtonPressed || pt.Properties.IsPrimary
                || e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                _currentStroke.Points.Add(pt.Position);
        }

        private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TestInkCanvas);
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"PointerReleased type={e.Pointer.PointerDeviceType} pos=({pt.Position.X:F0},{pt.Position.Y:F0}) hasStroke={_currentStroke != null}");
            if (_currentStroke == null) return;
            e.Handled = true;
            // Add the final position so every stroke has at least a start→end line
            _currentStroke.Points.Add(pt.Position);
            TestInkCanvas.ReleasePointerCapture(e.Pointer);
            _currentStroke = null;
            _strokeCount++;
            StrokeCountLabel.Text = $"Strokes: {_strokeCount}";
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"Stroke complete — total: {_strokeCount}");
        }

        private void InkCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TestInkCanvas);
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"PointerCanceled type={e.Pointer.PointerDeviceType} pos=({pt.Position.X:F0},{pt.Position.Y:F0}) hasStroke={_currentStroke != null}");
            // Complete the stroke even on cancel so injected-touch drags are counted
            if (_currentStroke == null) return;
            TestInkCanvas.ReleasePointerCapture(e.Pointer);
            _currentStroke = null;
            _strokeCount++;
            StrokeCountLabel.Text = $"Strokes: {_strokeCount}";
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"Stroke saved on cancel — total: {_strokeCount}");
        }

        private void InkCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TestInkCanvas);
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"PointerCaptureLost type={e.Pointer.PointerDeviceType} pos=({pt.Position.X:F0},{pt.Position.Y:F0}) hasStroke={_currentStroke != null}");
            if (_currentStroke == null) return;
            _currentStroke = null;
            _strokeCount++;
            StrokeCountLabel.Text = $"Strokes: {_strokeCount}";
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"Stroke saved on capture-lost — total: {_strokeCount}");
        }

        private void ClearInk_Click(object sender, RoutedEventArgs e)
        {
            zRover.WinUI.RoverMcp.Log("MainPage.InkCanvas", $"Canvas cleared ({_strokeCount} strokes removed)");
            TestInkCanvas.Children.Clear();
            _strokeCount = 0;
            _currentStroke = null;
            StrokeCountLabel.Text = "Strokes: 0";
        }

        #endregion

        //═══════════════════════════════════════════════════════════════════
        //  IActionableApp — App Action API
        //═══════════════════════════════════════════════════════════════════

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
            zRover.WinUI.RoverMcp.Log("MainPage.Actions", $"Dispatching action: {actionName} params={parametersJson}");
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
                    zRover.WinUI.RoverMcp.Log("MainPage.Actions", $"Action '{actionName}' succeeded");
                else
                    zRover.WinUI.RoverMcp.LogWarn("MainPage.Actions", $"Action '{actionName}' failed: {result.ErrorMessage}");
                return result;
            }
            catch (Exception ex)
            {
                zRover.WinUI.RoverMcp.LogError("MainPage.Actions", $"Action '{actionName}' threw an exception", ex);
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
                case "Red":    r = 255; g = 0;   b = 0;   break;
                case "Green":  r = 0;   g = 255; b = 0;   break;
                case "Blue":   r = 0;   g = 0;   b = 255; break;
                case "Yellow": r = 255; g = 255; b = 0;   break;
                case "White":  r = 255; g = 255; b = 255; break;
                default:
                    return Task.FromResult(ActionResult.Fail("validation_error",
                        $"params.color: '{colorName}' is not in the valid set [Red, Green, Blue, Yellow, White]"));
            }

            var tcs = new TaskCompletionSource<ActionResult>();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
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
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
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
                case "Text Input":   index = 1; break;
                case "Ink Canvas":   index = 2; break;
                case "Scroll Test":  index = 3; break;
                default:
                    return Task.FromResult(ActionResult.Fail("validation_error",
                        $"params.tab: '{tab}' is not in the valid set [Color Picker, Text Input, Ink Canvas, Scroll Test]"));
            }

            var tcs = new TaskCompletionSource<ActionResult>();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    // The root content of this page is a Pivot — find it via VisualTreeHelper
                    if (Content is Pivot pivot)
                    {
                        pivot.SelectedIndex = index;
                    }
                    tcs.TrySetResult(ActionResult.Ok(new[] { "TabChanged" }));
                }
                catch (Exception ex) { tcs.TrySetResult(ActionResult.Fail("execution_error", ex.Message)); }
            });
            return tcs.Task;
        }
    }
}
