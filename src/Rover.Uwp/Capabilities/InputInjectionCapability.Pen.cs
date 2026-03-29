using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Coordinates;
using Rover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace Rover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private const string PenTapSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate of the pen tap."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate of the pen tap."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""default"": 0.5, ""description"": ""Pen pressure 0.0 to 1.0."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Pen X tilt in degrees (-90 to 90)."" },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Pen Y tilt in degrees (-90 to 90)."" },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""Pen rotation in degrees (0.0 to 359.0)."" },
    ""barrel"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Whether the barrel button is pressed."" },
    ""eraser"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Whether the eraser end is active."" },
    ""hover"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, pen hovers (InRange) without touching (no InContact)."" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""x"", ""y""]
}";

        private const string PenStrokeSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""x"": { ""type"": ""number"" },
          ""y"": { ""type"": ""number"" },
          ""pressure"": { ""type"": ""number"", ""description"": ""Per-point pressure override."" },
          ""tiltX"": { ""type"": ""integer"", ""description"": ""Per-point tiltX override."" },
          ""tiltY"": { ""type"": ""integer"", ""description"": ""Per-point tiltY override."" },
          ""rotation"": { ""type"": ""number"", ""description"": ""Per-point rotation override."" }
        },
        ""required"": [""x"", ""y""]
      },
      ""minItems"": 2,
      ""description"": ""Ordered points for the pen stroke.""
    },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""default"": 0.5, ""description"": ""Default pressure for all points."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0 },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0 },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0 },
    ""barrel"": { ""type"": ""boolean"", ""default"": false },
    ""eraser"": { ""type"": ""boolean"", ""default"": false },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""points""]
}";

        private void RegisterPenTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_pen_tap",
                "Injects a pen tap at the specified coordinates. " +
                "Supports pressure, tilt, rotation, barrel button, and eraser mode. " +
                "Set hover=true to hover without touching the surface.",
                PenTapSchema,
                InjectPenTapAsync);

            registry.RegisterTool(
                "inject_pen_stroke",
                "Injects a pen stroke along a path of points. " +
                "Each point can optionally override pressure, tilt, and rotation for realistic ink strokes. " +
                "Falls back to stroke-level defaults for any unspecified per-point values.",
                PenStrokeSchema,
                InjectPenStrokeAsync);
        }

        private InjectedInputPenButtons BuildPenButtons(bool barrel, bool eraser)
        {
            var buttons = (InjectedInputPenButtons)0;
            if (barrel) buttons |= InjectedInputPenButtons.Barrel;
            if (eraser) buttons |= InjectedInputPenButtons.Inverted | InjectedInputPenButtons.Eraser;
            return buttons;
        }

        private async Task<string> InjectPenTapAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectPenTapRequest>(argsJson)
                      ?? new InjectPenTapRequest();

            LogToFile($"InjectPenTapAsync: ({req.X},{req.Y}) pressure={req.Pressure} hover={req.Hover} dryRun={req.DryRun}");

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
                    LogToFile($"PenTap preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectPenTapResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    Pressure = req.Pressure,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectPenTapResponse
                {
                    Success = false,
                    Pressure = req.Pressure,
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
                    var (rawX, rawY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);

                    var penButtons = BuildPenButtons(req.Barrel, req.Eraser);
                    var pointerDown = req.Hover
                        ? InjectedInputPointerOptions.InRange | InjectedInputPointerOptions.New
                        : InjectedInputPointerOptions.PointerDown | InjectedInputPointerOptions.InRange
                          | InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.New
                          | InjectedInputPointerOptions.Primary;

                    var penParams = InjectedInputPenParameters.Pressure
                                  | InjectedInputPenParameters.Rotation
                                  | InjectedInputPenParameters.TiltX
                                  | InjectedInputPenParameters.TiltY;

                    // Pen down (or hover in)
                    injector.InjectPenInput(new InjectedInputPenInfo
                    {
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = pointerDown,
                            PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                        },
                        Pressure = req.Pressure,
                        TiltX = req.TiltX,
                        TiltY = req.TiltY,
                        Rotation = req.Rotation,
                        PenButtons = penButtons,
                        PenParameters = penParams
                    });

                    System.Threading.Thread.Sleep(50);

                    // Pen up (or hover out)
                    var pointerUp = req.Hover
                        ? InjectedInputPointerOptions.PointerUp
                        : InjectedInputPointerOptions.PointerUp | InjectedInputPointerOptions.InRange;

                    injector.InjectPenInput(new InjectedInputPenInfo
                    {
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = pointerUp,
                            PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                        },
                        Pressure = 0.0,
                        PenParameters = InjectedInputPenParameters.Pressure
                    });
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"PenTap FAILED: {error.Message}");
            else
                LogToFile("PenTap succeeded");

            return JsonConvert.SerializeObject(new InjectPenTapResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                Pressure = req.Pressure,
                PreviewScreenshotPath = previewPath
            });
        }

        private async Task<string> InjectPenStrokeAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectPenStrokeRequest>(argsJson)
                      ?? new InjectPenStrokeRequest();

            LogToFile($"InjectPenStrokeAsync: {req.Points.Count} points, duration={req.DurationMs}ms dryRun={req.DryRun}");

            // Capture preview using the drag path annotation (pen stroke points → CoordinatePoints)
            string? previewPath = null;
            if (_runOnUiThread != null && req.Points.Count >= 2)
            {
                try
                {
                    previewPath = await CaptureAnnotatedDragPreview(new InjectDragPathRequest
                    {
                        Points = req.Points.Select(p => new CoordinatePoint(p.X, p.Y)).ToList(),
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                    LogToFile($"PenStroke preview: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"PenStroke preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectPenStrokeResponse
                {
                    Success = true,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || req.Points.Count < 2)
            {
                return InjectorUnavailableResponse(new InjectPenStrokeResponse
                {
                    Success = false,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    PreviewScreenshotPath = previewPath
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    InjectPenStrokeGesture(injector, req);
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"PenStroke FAILED: {error.Message}");
            else
                LogToFile("PenStroke succeeded");

            var result = JsonConvert.SerializeObject(new InjectPenStrokeResponse
            {
                Success = error == null,
                PointCount = req.Points.Count,
                DurationMs = req.DurationMs,
                PreviewScreenshotPath = previewPath
            });
            return result;
        }

        private void InjectPenStrokeGesture(InputInjector injector, InjectPenStrokeRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
            double dpiScale = dispInfo.RawPixelsPerViewPixel;

            var penButtons = BuildPenButtons(req.Barrel, req.Eraser);
            var penParams = InjectedInputPenParameters.Pressure
                          | InjectedInputPenParameters.Rotation
                          | InjectedInputPenParameters.TiltX
                          | InjectedInputPenParameters.TiltY;

            // Resolve first point
            var firstPt = req.Points[0];
            var firstResolved = _resolver!.Resolve(new CoordinatePoint(firstPt.X, firstPt.Y), space);
            var (firstRawX, firstRawY) = ToTouchInjectionPoint(firstResolved.X, firstResolved.Y, dpiScale);

            // Pen down
            injector.InjectPenInput(new InjectedInputPenInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = 1,
                    PointerOptions = InjectedInputPointerOptions.PointerDown
                                   | InjectedInputPointerOptions.InRange
                                   | InjectedInputPointerOptions.InContact
                                   | InjectedInputPointerOptions.New
                                   | InjectedInputPointerOptions.Primary,
                    PixelLocation = new InjectedInputPoint { PositionX = firstRawX, PositionY = firstRawY }
                },
                Pressure = firstPt.Pressure ?? req.Pressure,
                TiltX = firstPt.TiltX ?? req.TiltX,
                TiltY = firstPt.TiltY ?? req.TiltY,
                Rotation = firstPt.Rotation ?? req.Rotation,
                PenButtons = penButtons,
                PenParameters = penParams
            });

            int delayPerPoint = Math.Max(1, req.DurationMs / Math.Max(1, req.Points.Count - 1));

            // Move frames
            for (int i = 1; i < req.Points.Count - 1; i++)
            {
                System.Threading.Thread.Sleep(delayPerPoint);

                var pt = req.Points[i];
                var resolved = _resolver!.Resolve(new CoordinatePoint(pt.X, pt.Y), space);
                var (rawX, rawY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);

                injector.InjectPenInput(new InjectedInputPenInfo
                {
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 1,
                        PointerOptions = InjectedInputPointerOptions.InRange
                                       | InjectedInputPointerOptions.InContact
                                       | InjectedInputPointerOptions.Update,
                        PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                    },
                    Pressure = pt.Pressure ?? req.Pressure,
                    TiltX = pt.TiltX ?? req.TiltX,
                    TiltY = pt.TiltY ?? req.TiltY,
                    Rotation = pt.Rotation ?? req.Rotation,
                    PenButtons = penButtons,
                    PenParameters = penParams
                });
            }

            // Pen up at last point
            var lastPt = req.Points[req.Points.Count - 1];
            var lastResolved = _resolver!.Resolve(new CoordinatePoint(lastPt.X, lastPt.Y), space);
            var (lastRawX, lastRawY) = ToTouchInjectionPoint(lastResolved.X, lastResolved.Y, dpiScale);

            System.Threading.Thread.Sleep(delayPerPoint);
            injector.InjectPenInput(new InjectedInputPenInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = 1,
                    PointerOptions = InjectedInputPointerOptions.PointerUp | InjectedInputPointerOptions.InRange,
                    PixelLocation = new InjectedInputPoint { PositionX = lastRawX, PositionY = lastRawY }
                },
                Pressure = 0.0,
                PenParameters = InjectedInputPenParameters.Pressure
            });
        }
    }
}
