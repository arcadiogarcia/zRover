using System;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Coordinates;
using Rover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace Rover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private const string MouseScrollSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate where the scroll occurs."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate where the scroll occurs."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""deltaY"": { ""type"": ""integer"", ""default"": -120, ""description"": ""Vertical scroll amount. Negative scrolls down, positive scrolls up. 120 = one notch."" },
    ""deltaX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Horizontal scroll amount. Negative scrolls left, positive scrolls right. 120 = one notch."" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, previews the scroll location without injecting."" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string MouseMoveSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Target X coordinate."" },
    ""y"": { ""type"": ""number"", ""description"": ""Target Y coordinate."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, previews the move target without injecting."" }
  },
  ""required"": [""x"", ""y""]
}";

        private void RegisterMouseTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_mouse_scroll",
                "Injects a mouse scroll event at the specified coordinates. " +
                "Moves the mouse to the target position first, then scrolls. " +
                "deltaY: negative = scroll down, positive = scroll up. " +
                "deltaX: negative = scroll left, positive = scroll right. " +
                "One wheel notch = 120 units.",
                MouseScrollSchema,
                InjectMouseScrollAsync);

            registry.RegisterTool(
                "inject_mouse_move",
                "Moves the mouse cursor to the specified coordinates without clicking. " +
                "Useful for hover effects, tooltips, or positioning before other actions.",
                MouseMoveSchema,
                InjectMouseMoveAsync);
        }

        // Win32 P/Invoke for virtual desktop metrics (needed for multi-monitor coordinate mapping).
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_XVIRTUALSCREEN  = 76;
        private const int SM_YVIRTUALSCREEN  = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        /// <summary>
        /// Converts a DIP screen-space point to the injection coordinate expected by
        /// <see cref="InjectedInputPoint.PositionX"/>/<see cref="InjectedInputPoint.PositionY"/>
        /// for touch and pen injection (physical pixels offset by virtual desktop origin).
        /// </summary>
        private (int x, int y) ToTouchInjectionPoint(double dipX, double dipY, double dpiScale)
        {
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop  = GetSystemMetrics(SM_YVIRTUALSCREEN);
            return ((int)(dipX * dpiScale - vLeft), (int)(dipY * dpiScale - vTop));
        }

        /// <summary>
        /// Converts a DIP screen-space point to the 0–65535 range expected by
        /// <see cref="InjectedInputMouseOptions.Absolute"/> combined with
        /// <see cref="InjectedInputMouseOptions.VirtualDesk"/>, so the result is
        /// correct on any monitor in a multi-monitor setup.
        /// </summary>
        private (int normX, int normY) ToMouseNormalized(CoordinatePoint dipPoint, double dpiScale)
        {
            // Convert DIP → raw physical pixels (DIP * dpiScale of the current display).
            double rawX = dipPoint.X * dpiScale;
            double rawY = dipPoint.Y * dpiScale;

            // Map into 0..65535 relative to the full virtual desktop so that
            // InjectedInputMouseOptions.VirtualDesk places the cursor on the correct monitor.
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop  = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vW    = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH    = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            int normX = vW > 0 ? (int)((rawX - vLeft) / vW * 65535) : 0;
            int normY = vH > 0 ? (int)((rawY - vTop)  / vH * 65535) : 0;
            return (normX, normY);
        }

        private async Task<string> InjectMouseScrollAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectMouseScrollRequest>(argsJson)
                      ?? new InjectMouseScrollRequest();

            LogToFile($"InjectMouseScrollAsync: ({req.X},{req.Y}) deltaY={req.DeltaY} deltaX={req.DeltaX} dryRun={req.DryRun}");

            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(new InjectTapRequest
                    {
                        X = req.X,
                        Y = req.Y,
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToFile($"Scroll preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectMouseScrollResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    DeltaY = req.DeltaY,
                    DeltaX = req.DeltaX,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectMouseScrollResponse
                {
                    Success = false,
                    DeltaY = req.DeltaY,
                    DeltaX = req.DeltaX,
                    PreviewScreenshotPath = previewPath
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double dpiScale = dispInfo.RawPixelsPerViewPixel;
                    var (normX, normY) = ToMouseNormalized(resolved, dpiScale);

                    // Move mouse to position (VirtualDesk ensures correct placement on any monitor)
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX,
                        DeltaY = normY
                    }});

                    // Vertical scroll
                    if (req.DeltaY != 0)
                    {
                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.Wheel,
                            MouseData = (uint)req.DeltaY
                        }});
                    }

                    // Horizontal scroll
                    if (req.DeltaX != 0)
                    {
                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.HWheel,
                            MouseData = (uint)req.DeltaX
                        }});
                    }
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"MouseScroll FAILED: {error.Message}");
            else
                LogToFile("MouseScroll succeeded");

            return JsonConvert.SerializeObject(new InjectMouseScrollResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                DeltaY = req.DeltaY,
                DeltaX = req.DeltaX,
                PreviewScreenshotPath = previewPath
            });
        }

        private async Task<string> InjectMouseMoveAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectMouseMoveRequest>(argsJson)
                      ?? new InjectMouseMoveRequest();

            LogToFile($"InjectMouseMoveAsync: ({req.X},{req.Y}) dryRun={req.DryRun}");

            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(new InjectTapRequest
                    {
                        X = req.X,
                        Y = req.Y,
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToFile($"MouseMove preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectMouseMoveResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectMouseMoveResponse
                {
                    Success = false,
                    PreviewScreenshotPath = previewPath
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double dpiScale = dispInfo.RawPixelsPerViewPixel;
                    var (normX, normY) = ToMouseNormalized(resolved, dpiScale);

                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX,
                        DeltaY = normY
                    }});
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"MouseMove FAILED: {error.Message}");
            else
                LogToFile("MouseMove succeeded");

            return JsonConvert.SerializeObject(new InjectMouseMoveResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                PreviewScreenshotPath = previewPath
            });
        }
    }
}
