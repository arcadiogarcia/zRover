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
        private const string MultiTouchSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointers"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""id"": { ""type"": ""integer"", ""description"": ""Unique pointer ID (1-based)."" },
          ""path"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""} }, ""required"": [""x"",""y""] }, ""minItems"": 1 },
          ""pressure"": { ""type"": ""number"", ""default"": 1.0 },
          ""orientation"": { ""type"": ""integer"", ""default"": 0, ""description"": ""Contact orientation 0-359 degrees."" },
          ""contactWidth"": { ""type"": ""integer"", ""default"": 4 },
          ""contactHeight"": { ""type"": ""integer"", ""default"": 4 }
        },
        ""required"": [""id"", ""path""]
      },
      ""description"": ""Array of pointer paths to inject simultaneously.""
    },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400, ""description"": ""Total gesture duration in ms."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""pointers""]
}";

        private const string PinchSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""centerX"": { ""type"": ""number"", ""description"": ""Center X of the pinch gesture."" },
    ""centerY"": { ""type"": ""number"", ""description"": ""Center Y of the pinch gesture."" },
    ""startDistance"": { ""type"": ""number"", ""default"": 0.3, ""description"": ""Starting distance between fingers (normalized)."" },
    ""endDistance"": { ""type"": ""number"", ""default"": 0.1, ""description"": ""Ending distance between fingers (normalized). Less than startDistance = pinch in, greater = pinch out."" },
    ""angle"": { ""type"": ""number"", ""default"": 0, ""description"": ""Angle of the pinch axis in degrees."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""centerX"", ""centerY""]
}";

        private const string RotateSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""centerX"": { ""type"": ""number"", ""description"": ""Center X of the rotation."" },
    ""centerY"": { ""type"": ""number"", ""description"": ""Center Y of the rotation."" },
    ""distance"": { ""type"": ""number"", ""default"": 0.2, ""description"": ""Distance of each finger from center (normalized)."" },
    ""startAngle"": { ""type"": ""number"", ""default"": 0, ""description"": ""Starting angle in degrees."" },
    ""endAngle"": { ""type"": ""number"", ""default"": 90, ""description"": ""Ending angle in degrees. Positive = clockwise."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 400 },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false }
  },
  ""required"": [""centerX"", ""centerY""]
}";

        private void RegisterTouchTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_multi_touch",
                "Injects a multi-touch gesture with multiple simultaneous pointers. " +
                "Each pointer follows its own path. All pointers are injected simultaneously at each frame. " +
                "Use for custom multi-finger gestures. For common gestures, prefer inject_pinch or inject_rotate.",
                MultiTouchSchema,
                InjectMultiTouchAsync);

            registry.RegisterTool(
                "inject_pinch",
                "Injects a pinch gesture (two fingers moving toward or away from each other). " +
                "Set endDistance < startDistance to pinch in (zoom out), or endDistance > startDistance to pinch out (zoom in).",
                PinchSchema,
                InjectPinchAsync);

            registry.RegisterTool(
                "inject_rotate",
                "Injects a two-finger rotation gesture around a center point. " +
                "Positive endAngle = clockwise rotation, negative = counter-clockwise.",
                RotateSchema,
                InjectRotateAsync);
        }

        private async Task<string> InjectMultiTouchAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectMultiTouchRequest>(argsJson)
                      ?? new InjectMultiTouchRequest();

            LogToFile($"InjectMultiTouchAsync: {req.Pointers.Count} pointers, duration={req.DurationMs}ms dryRun={req.DryRun}");

            string? previewPath = null;
            if (_runOnUiThread != null && req.Pointers.Count > 0)
            {
                try
                {
                    var paths = req.Pointers.Select(p => p.Path).ToList();
                    previewPath = await CaptureAnnotatedMultiPathPreview(
                        paths, req.CoordinateSpace, "multitouch_preview").ConfigureAwait(false);
                    LogToFile($"MultiTouch preview: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"MultiTouch preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectMultiTouchResponse
                {
                    Success = true,
                    PointerCount = req.Pointers.Count,
                    DurationMs = req.DurationMs,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || req.Pointers.Count == 0)
            {
                return InjectorUnavailableResponse(new InjectMultiTouchResponse
                {
                    Success = false,
                    PointerCount = req.Pointers.Count,
                    DurationMs = req.DurationMs
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    InjectMultiTouchGesture(injector, req);
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"MultiTouch FAILED: {error.Message}");
            else
                LogToFile("MultiTouch succeeded");

            return JsonConvert.SerializeObject(new InjectMultiTouchResponse
            {
                Success = error == null,
                PointerCount = req.Pointers.Count,
                DurationMs = req.DurationMs,
                PreviewScreenshotPath = previewPath
            });
        }

        private void InjectMultiTouchGesture(InputInjector injector, InjectMultiTouchRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);

            // InjectedInputPoint uses physical pixels offset by virtual desktop origin.
            var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
            double dpiScale = dispInfo.RawPixelsPerViewPixel;
            var resolvedPaths = new List<List<(int x, int y)>>();
            foreach (var pointer in req.Pointers)
            {
                var rawPath = new List<(int x, int y)>();
                foreach (var pt in pointer.Path)
                {
                    var resolved = _resolver!.Resolve(pt, space);
                    rawPath.Add(ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale));
                }
                resolvedPaths.Add(rawPath);
            }

            // Normalize all paths to the same number of steps
            int maxPoints = resolvedPaths.Max(p => p.Count);
            int steps = Math.Max(maxPoints, req.DurationMs / 16);

            injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);

            // Down frame - all pointers
            var downInfos = new List<InjectedInputTouchInfo>();
            for (int p = 0; p < req.Pointers.Count; p++)
            {
                var ptr = req.Pointers[p];
                var path = resolvedPaths[p];
                var start = path[0];
                int hw = ptr.ContactWidth / 2;
                int hh = ptr.ContactHeight / 2;

                downInfos.Add(new InjectedInputTouchInfo
                {
                    Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = (uint)ptr.Id,
                        PointerOptions = InjectedInputPointerOptions.PointerDown
                                       | InjectedInputPointerOptions.InContact
                                       | InjectedInputPointerOptions.New
                                       | (p == 0 ? InjectedInputPointerOptions.Primary : InjectedInputPointerOptions.None),
                        PixelLocation = new InjectedInputPoint { PositionX = start.x, PositionY = start.y }
                    },
                    Pressure = ptr.Pressure,
                    Orientation = ptr.Orientation,
                    TouchParameters = InjectedInputTouchParameters.Pressure
                                    | InjectedInputTouchParameters.Contact
                                    | InjectedInputTouchParameters.Orientation
                });
            }
            injector.InjectTouchInput(downInfos);

            int delayPerStep = Math.Max(1, req.DurationMs / steps);

            // Move frames
            for (int s = 1; s < steps; s++)
            {
                System.Threading.Thread.Sleep(delayPerStep);

                var moveInfos = new List<InjectedInputTouchInfo>();
                for (int p = 0; p < req.Pointers.Count; p++)
                {
                    var ptr = req.Pointers[p];
                    var path = resolvedPaths[p];
                    double t = (double)s / (steps - 1);
                    var pos = InterpolatePath(path, t);
                    int hw = ptr.ContactWidth / 2;
                    int hh = ptr.ContactHeight / 2;

                    moveInfos.Add(new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = (uint)ptr.Id,
                            PointerOptions = InjectedInputPointerOptions.InContact
                                           | InjectedInputPointerOptions.Update,
                            PixelLocation = new InjectedInputPoint { PositionX = pos.x, PositionY = pos.y }
                        },
                        Pressure = ptr.Pressure,
                        Orientation = ptr.Orientation,
                        TouchParameters = InjectedInputTouchParameters.Pressure
                                        | InjectedInputTouchParameters.Contact
                                        | InjectedInputTouchParameters.Orientation
                    });
                }
                injector.InjectTouchInput(moveInfos);
            }

            // Up frame - all pointers
            var upInfos = new List<InjectedInputTouchInfo>();
            for (int p = 0; p < req.Pointers.Count; p++)
            {
                var ptr = req.Pointers[p];
                var path = resolvedPaths[p];
                var end = path[path.Count - 1];
                int hw = ptr.ContactWidth / 2;
                int hh = ptr.ContactHeight / 2;

                upInfos.Add(new InjectedInputTouchInfo
                {
                    Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = (uint)ptr.Id,
                        PointerOptions = InjectedInputPointerOptions.PointerUp,
                        PixelLocation = new InjectedInputPoint { PositionX = end.x, PositionY = end.y }
                    },
                    Pressure = 0.0,
                    TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                });
            }
            injector.InjectTouchInput(upInfos);
            injector.UninitializeTouchInjection();
        }

        private static (int x, int y) InterpolatePath(List<(int x, int y)> path, double t)
        {
            if (path.Count == 1) return path[0];
            t = Math.Max(0, Math.Min(1, t));

            double totalSegments = path.Count - 1;
            double pos = t * totalSegments;
            int seg = (int)Math.Floor(pos);
            if (seg >= path.Count - 1) return path[path.Count - 1];

            double segT = pos - seg;
            return (
                (int)(path[seg].x + segT * (path[seg + 1].x - path[seg].x)),
                (int)(path[seg].y + segT * (path[seg + 1].y - path[seg].y))
            );
        }

        private async Task<string> InjectPinchAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectPinchRequest>(argsJson)
                      ?? new InjectPinchRequest();

            LogToFile($"InjectPinchAsync: center=({req.CenterX},{req.CenterY}) distance {req.StartDistance}->{req.EndDistance} dryRun={req.DryRun}");

            // Convert pinch to multi-touch: two fingers along an axis
            double angleRad = req.Angle * Math.PI / 180.0;
            double cosA = Math.Cos(angleRad);
            double sinA = Math.Sin(angleRad);

            double halfStart = req.StartDistance / 2.0;
            double halfEnd = req.EndDistance / 2.0;

            var p1Start = new CoordinatePoint(req.CenterX + cosA * halfStart, req.CenterY + sinA * halfStart);
            var p1End = new CoordinatePoint(req.CenterX + cosA * halfEnd, req.CenterY + sinA * halfEnd);
            var p2Start = new CoordinatePoint(req.CenterX - cosA * halfStart, req.CenterY - sinA * halfStart);
            var p2End = new CoordinatePoint(req.CenterX - cosA * halfEnd, req.CenterY - sinA * halfEnd);

            // Capture preview showing the two pointer paths
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    var paths = new List<List<CoordinatePoint>>
                    {
                        new List<CoordinatePoint> { p1Start, p1End },
                        new List<CoordinatePoint> { p2Start, p2End }
                    };
                    previewPath = await CaptureAnnotatedMultiPathPreview(
                        paths, req.CoordinateSpace, "pinch_preview").ConfigureAwait(false);
                    LogToFile($"Pinch preview: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Pinch preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectPinchResponse
                {
                    Success = true,
                    Pointer1Start = p1Start,
                    Pointer1End = p1End,
                    Pointer2Start = p2Start,
                    Pointer2End = p2End,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            // Build multi-touch request
            var multiReq = new InjectMultiTouchRequest
            {
                CoordinateSpace = req.CoordinateSpace,
                DurationMs = req.DurationMs,
                Pointers = new List<TouchPointerPath>
                {
                    new TouchPointerPath
                    {
                        Id = 1,
                        Path = new List<CoordinatePoint> { p1Start, p1End }
                    },
                    new TouchPointerPath
                    {
                        Id = 2,
                        Path = new List<CoordinatePoint> { p2Start, p2End }
                    }
                }
            };

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectPinchResponse
                {
                    Success = false,
                    Pointer1Start = p1Start,
                    Pointer1End = p1End,
                    Pointer2Start = p2Start,
                    Pointer2End = p2End,
                    PreviewScreenshotPath = previewPath
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    InjectMultiTouchGesture(injector, multiReq);
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"Pinch FAILED: {error.Message}");
            else
                LogToFile("Pinch succeeded");

            return JsonConvert.SerializeObject(new InjectPinchResponse
            {
                Success = error == null,
                Pointer1Start = p1Start,
                Pointer1End = p1End,
                Pointer2Start = p2Start,
                Pointer2End = p2End,
                PreviewScreenshotPath = previewPath
            });
        }

        private async Task<string> InjectRotateAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectRotateRequest>(argsJson)
                      ?? new InjectRotateRequest();

            LogToFile($"InjectRotateAsync: center=({req.CenterX},{req.CenterY}) angles {req.StartAngle}->{req.EndAngle} dryRun={req.DryRun}");

            double startRad = req.StartAngle * Math.PI / 180.0;
            double endRad = req.EndAngle * Math.PI / 180.0;

            var p1Start = new CoordinatePoint(
                req.CenterX + Math.Cos(startRad) * req.Distance,
                req.CenterY + Math.Sin(startRad) * req.Distance);
            var p1End = new CoordinatePoint(
                req.CenterX + Math.Cos(endRad) * req.Distance,
                req.CenterY + Math.Sin(endRad) * req.Distance);
            var p2Start = new CoordinatePoint(
                req.CenterX - Math.Cos(startRad) * req.Distance,
                req.CenterY - Math.Sin(startRad) * req.Distance);
            var p2End = new CoordinatePoint(
                req.CenterX - Math.Cos(endRad) * req.Distance,
                req.CenterY - Math.Sin(endRad) * req.Distance);

            // Generate arc path with multiple intermediate points for preview + smooth rotation
            int arcSteps = Math.Max(10, req.DurationMs / 16);
            var path1 = new List<CoordinatePoint>();
            var path2 = new List<CoordinatePoint>();

            for (int i = 0; i <= arcSteps; i++)
            {
                double t = (double)i / arcSteps;
                double angle = startRad + t * (endRad - startRad);
                path1.Add(new CoordinatePoint(
                    req.CenterX + Math.Cos(angle) * req.Distance,
                    req.CenterY + Math.Sin(angle) * req.Distance));
                path2.Add(new CoordinatePoint(
                    req.CenterX - Math.Cos(angle) * req.Distance,
                    req.CenterY - Math.Sin(angle) * req.Distance));
            }

            // Capture preview showing the two arc paths
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedMultiPathPreview(
                        new List<List<CoordinatePoint>> { path1, path2 },
                        req.CoordinateSpace, "rotate_preview").ConfigureAwait(false);
                    LogToFile($"Rotate preview: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Rotate preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectRotateResponse
                {
                    Success = true,
                    Pointer1Start = p1Start,
                    Pointer1End = p1End,
                    Pointer2Start = p2Start,
                    Pointer2End = p2End,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var multiReq = new InjectMultiTouchRequest
            {
                CoordinateSpace = req.CoordinateSpace,
                DurationMs = req.DurationMs,
                Pointers = new List<TouchPointerPath>
                {
                    new TouchPointerPath { Id = 1, Path = path1 },
                    new TouchPointerPath { Id = 2, Path = path2 }
                }
            };

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectRotateResponse
                {
                    Success = false,
                    Pointer1Start = p1Start,
                    Pointer1End = p1End,
                    Pointer2Start = p2Start,
                    Pointer2End = p2End,
                    PreviewScreenshotPath = previewPath
                });
            }

            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    InjectMultiTouchGesture(injector, multiReq);
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"Rotate FAILED: {error.Message}");
            else
                LogToFile("Rotate succeeded");

            return JsonConvert.SerializeObject(new InjectRotateResponse
            {
                Success = error == null,
                Pointer1Start = p1Start,
                Pointer1End = p1End,
                Pointer2Start = p2Start,
                Pointer2End = p2End,
                PreviewScreenshotPath = previewPath
            });
        }
    }
}
