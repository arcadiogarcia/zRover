using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Coordinates;
using zRover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace zRover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private readonly PointerSessionState _pointerSession = new PointerSessionState();

        private static PointerSessionState.DeviceKind ParsePointerDevice(string? device) =>
            string.Equals(device, "pen", StringComparison.OrdinalIgnoreCase)
                ? PointerSessionState.DeviceKind.Pen
                : PointerSessionState.DeviceKind.Touch;

        private const string PointerDownSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Unique pointer ID (1-based). Use different IDs for multi-finger touch gestures. Pen supports only one active pointer."" },
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In the default normalized space this is 0.0 (left) to 1.0 (right)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In the default normalized space this is 0.0 (top) to 1.0 (bottom)."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""pen""], ""default"": ""touch"", ""description"": ""Input device type. 'touch' supports multiple simultaneous pointers; 'pen' supports one pointer with tilt, rotation, barrel, and eraser."" },
    ""pressure"": { ""type"": ""number"", ""default"": 1.0, ""description"": ""Contact pressure 0.0-1.0 (default 1.0 for touch, 0.5 for pen)."" },
    ""orientation"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(touch only) Contact orientation 0-359 degrees."" },
    ""contactWidth"": { ""type"": ""integer"", ""default"": 4, ""description"": ""(touch only) Contact patch width."" },
    ""contactHeight"": { ""type"": ""integer"", ""default"": 4, ""description"": ""(touch only) Contact patch height."" },
    ""tiltX"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(pen only) X tilt in degrees (-90 to 90)."" },
    ""tiltY"": { ""type"": ""integer"", ""default"": 0, ""description"": ""(pen only) Y tilt in degrees (-90 to 90)."" },
    ""rotation"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""(pen only) Pen rotation in degrees (0.0-359.0)."" },
    ""barrel"": { ""type"": ""boolean"", ""default"": false, ""description"": ""(pen only) Whether the barrel button is pressed."" },
    ""eraser"": { ""type"": ""boolean"", ""default"": false, ""description"": ""(pen only) Whether the eraser end is active."" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string PointerMoveSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Pointer ID of an active (held-down) pointer."" },
    ""x"": { ""type"": ""number"", ""description"": ""New X coordinate."" },
    ""y"": { ""type"": ""number"", ""description"": ""New Y coordinate."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""normalized"", ""pixels""], ""default"": ""normalized"" },
    ""pressure"": { ""type"": ""number"", ""description"": ""Updated pressure (optional, keeps previous value if omitted)."" },
    ""tiltX"": { ""type"": ""integer"", ""description"": ""(pen only) Updated X tilt (optional)."" },
    ""tiltY"": { ""type"": ""integer"", ""description"": ""(pen only) Updated Y tilt (optional)."" },
    ""rotation"": { ""type"": ""number"", ""description"": ""(pen only) Updated rotation (optional)."" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string PointerUpSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pointerId"": { ""type"": ""integer"", ""default"": 1, ""description"": ""Pointer ID of the active pointer to release. When the last touch pointer is released the touch injection session is torn down."" }
  }
}";

        private void RegisterPointerTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "pointer_down",
                "Creates a new touch or pen contact and holds it down at the specified position. " +
                "The pointer stays active until explicitly released with pointer_up. " +
                "Set device='touch' (default) for finger input — use different pointerId values (1, 2, 3, ...) " +
                "to hold multiple fingers simultaneously. " +
                "Set device='pen' for stylus input with tilt, rotation, barrel button, and eraser support " +
                "(only one pen pointer can be active at a time). " +
                "Combine with pointer_move and pointer_up for complex multi-step gestures " +
                "where the full path is not known in advance.",
                PointerDownSchema,
                PointerDownAsync);

            registry.RegisterTool(
                "pointer_move",
                "Moves an active (held-down) pointer to a new position without releasing it. " +
                "Works for both touch and pen pointers. " +
                "The pointer must have been created with pointer_down first. " +
                "For pen pointers, tiltX, tiltY, and rotation can be updated per move. " +
                "Can be called repeatedly to trace an incremental path.",
                PointerMoveSchema,
                PointerMoveAsync);

            registry.RegisterTool(
                "pointer_up",
                "Releases an active pointer (touch or pen). " +
                "When the last active touch pointer is released, the touch injection session is automatically torn down. " +
                "Always release all pointers when the gesture is complete to avoid stuck inputs.",
                PointerUpSchema,
                PointerUpAsync);
        }

        private async Task<string> PointerDownAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<PointerDownRequest>(argsJson)
                      ?? new PointerDownRequest();
            var deviceKind = ParsePointerDevice(req.Device);

            LogToFile($"PointerDownAsync: id={req.PointerId} ({req.X},{req.Y}) device={deviceKind} space={req.CoordinateSpace}");

            var validationError = _pointerSession.ValidateDown(req.PointerId, deviceKind);
            if (validationError != null)
            {
                return JsonConvert.SerializeObject(new PointerDownResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count,
                    Error = validationError
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new PointerDownResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            int touchX = 0, touchY = 0;
            Exception? error = null;

            await _runOnUiThread(() =>
            {
                try
                {
                    try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { }
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double dpiScale = dispInfo.RawPixelsPerViewPixel;
                    (touchX, touchY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);

                    LogToFile($"PointerDown id={req.PointerId} device={deviceKind} DIP=({resolved.X:F1},{resolved.Y:F1}) inject=({touchX},{touchY})");

                    if (deviceKind == PointerSessionState.DeviceKind.Pen)
                    {
                        var penButtons = BuildPenButtons(req.Barrel, req.Eraser);
                        var penParams = InjectedInputPenParameters.Pressure
                                      | InjectedInputPenParameters.Rotation
                                      | InjectedInputPenParameters.TiltX
                                      | InjectedInputPenParameters.TiltY;

                        injector.InjectPenInput(new InjectedInputPenInfo
                        {
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = (uint)req.PointerId,
                                PointerOptions = InjectedInputPointerOptions.PointerDown
                                               | InjectedInputPointerOptions.InRange
                                               | InjectedInputPointerOptions.InContact
                                               | InjectedInputPointerOptions.New
                                               | InjectedInputPointerOptions.Primary,
                                PixelLocation = new InjectedInputPoint { PositionX = touchX, PositionY = touchY }
                            },
                            Pressure = req.Pressure,
                            TiltX = req.TiltX,
                            TiltY = req.TiltY,
                            Rotation = req.Rotation,
                            PenButtons = penButtons,
                            PenParameters = penParams
                        });
                    }
                    else
                    {
                        // Initialize the touch injection session on first touch pointer
                        if (!_pointerSession.TouchSessionActive)
                        {
                            injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
                            LogToFile("Touch injection session initialized for pointer tools");
                        }

                        int hw = req.ContactWidth / 2;
                        int hh = req.ContactHeight / 2;
                        bool isPrimary = !_pointerSession.PointerMap.Values.Any(
                            p => p.Device == PointerSessionState.DeviceKind.Touch);

                        // Build the down frame. Existing touch pointers must be included as
                        // Update frames in the same batch (Windows requires all active contacts
                        // in every InjectTouchInput call).
                        var infos = new List<InjectedInputTouchInfo>();

                        foreach (var kvp in _pointerSession.PointerMap)
                        {
                            if (kvp.Value.Device != PointerSessionState.DeviceKind.Touch) continue;
                            var st = kvp.Value;
                            int ehw = st.ContactWidth / 2;
                            int ehh = st.ContactHeight / 2;
                            infos.Add(new InjectedInputTouchInfo
                            {
                                Contact = new InjectedInputRectangle { Top = -ehh, Bottom = ehh, Left = -ehw, Right = ehw },
                                PointerInfo = new InjectedInputPointerInfo
                                {
                                    PointerId = (uint)st.PointerId,
                                    PointerOptions = InjectedInputPointerOptions.InContact
                                                   | InjectedInputPointerOptions.Update,
                                    PixelLocation = new InjectedInputPoint { PositionX = st.LastX, PositionY = st.LastY }
                                },
                                Pressure = st.Pressure,
                                Orientation = st.Orientation,
                                TouchParameters = InjectedInputTouchParameters.Pressure
                                                | InjectedInputTouchParameters.Contact
                                                | InjectedInputTouchParameters.Orientation
                            });
                        }

                        infos.Add(new InjectedInputTouchInfo
                        {
                            Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = (uint)req.PointerId,
                                PointerOptions = InjectedInputPointerOptions.PointerDown
                                               | InjectedInputPointerOptions.InRange
                                               | InjectedInputPointerOptions.InContact
                                               | InjectedInputPointerOptions.New
                                               | (isPrimary ? InjectedInputPointerOptions.Primary : InjectedInputPointerOptions.None),
                                PixelLocation = new InjectedInputPoint { PositionX = touchX, PositionY = touchY }
                            },
                            Pressure = req.Pressure,
                            Orientation = req.Orientation,
                            TouchParameters = InjectedInputTouchParameters.Pressure
                                            | InjectedInputTouchParameters.Contact
                                            | InjectedInputTouchParameters.Orientation
                        });

                        injector.InjectTouchInput(infos);
                    }
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
            {
                LogToFile($"PointerDown FAILED: {error.Message}");
                return JsonConvert.SerializeObject(new PointerDownResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count,
                    Error = error.Message
                });
            }

            _pointerSession.RecordDown(new PointerSessionState.ActivePointerState
            {
                PointerId = req.PointerId,
                Device = deviceKind,
                LastX = touchX,
                LastY = touchY,
                Pressure = req.Pressure,
                Orientation = req.Orientation,
                ContactWidth = req.ContactWidth,
                ContactHeight = req.ContactHeight,
                TiltX = req.TiltX,
                TiltY = req.TiltY,
                Rotation = req.Rotation,
                Barrel = req.Barrel,
                Eraser = req.Eraser
            });

            LogToFile($"PointerDown succeeded, device={deviceKind}, active pointers: {_pointerSession.Count}");

            return JsonConvert.SerializeObject(new PointerDownResponse
            {
                Success = true,
                PointerId = req.PointerId,
                ResolvedCoordinates = resolved,
                ActivePointers = _pointerSession.Count
            });
        }

        private async Task<string> PointerMoveAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<PointerMoveRequest>(argsJson)
                      ?? new PointerMoveRequest();

            LogToFile($"PointerMoveAsync: id={req.PointerId} ({req.X},{req.Y}) space={req.CoordinateSpace}");

            var moveError = _pointerSession.ValidateMove(req.PointerId);
            if (moveError != null)
            {
                return JsonConvert.SerializeObject(new PointerMoveResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    Error = moveError
                });
            }
            _pointerSession.TryGetPointer(req.PointerId, out var state);

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new PointerMoveResponse
                {
                    Success = false,
                    PointerId = req.PointerId
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            int touchX = 0, touchY = 0;
            Exception? error = null;

            await _runOnUiThread(() =>
            {
                try
                {
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double dpiScale = dispInfo.RawPixelsPerViewPixel;
                    (touchX, touchY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);

                    if (state!.Device == PointerSessionState.DeviceKind.Pen)
                    {
                        double pressure = req.Pressure ?? state.Pressure;
                        int tiltX = req.TiltX ?? state.TiltX;
                        int tiltY = req.TiltY ?? state.TiltY;
                        double rotation = req.Rotation ?? state.Rotation;

                        var penParams = InjectedInputPenParameters.Pressure
                                      | InjectedInputPenParameters.Rotation
                                      | InjectedInputPenParameters.TiltX
                                      | InjectedInputPenParameters.TiltY;

                        injector.InjectPenInput(new InjectedInputPenInfo
                        {
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = (uint)state.PointerId,
                                PointerOptions = InjectedInputPointerOptions.InRange
                                               | InjectedInputPointerOptions.InContact
                                               | InjectedInputPointerOptions.Update,
                                PixelLocation = new InjectedInputPoint { PositionX = touchX, PositionY = touchY }
                            },
                            Pressure = pressure,
                            TiltX = tiltX,
                            TiltY = tiltY,
                            Rotation = rotation,
                            PenButtons = BuildPenButtons(state.Barrel, state.Eraser),
                            PenParameters = penParams
                        });
                    }
                    else
                    {
                        // Build Update frame for ALL active touch pointers (Windows requirement)
                        var infos = new List<InjectedInputTouchInfo>();

                        foreach (var kvp in _pointerSession.PointerMap)
                        {
                            if (kvp.Value.Device != PointerSessionState.DeviceKind.Touch) continue;
                            var st = kvp.Value;
                            int px, py;
                            double pressure;
                            int orientation;
                            int hw, hh;

                            if (kvp.Key == req.PointerId)
                            {
                                px = touchX;
                                py = touchY;
                                pressure = req.Pressure ?? st.Pressure;
                                orientation = st.Orientation;
                                hw = st.ContactWidth / 2;
                                hh = st.ContactHeight / 2;
                            }
                            else
                            {
                                px = st.LastX;
                                py = st.LastY;
                                pressure = st.Pressure;
                                orientation = st.Orientation;
                                hw = st.ContactWidth / 2;
                                hh = st.ContactHeight / 2;
                            }

                            infos.Add(new InjectedInputTouchInfo
                            {
                                Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                                PointerInfo = new InjectedInputPointerInfo
                                {
                                    PointerId = (uint)st.PointerId,
                                    PointerOptions = InjectedInputPointerOptions.InContact
                                                   | InjectedInputPointerOptions.Update,
                                    PixelLocation = new InjectedInputPoint { PositionX = px, PositionY = py }
                                },
                                Pressure = pressure,
                                Orientation = orientation,
                                TouchParameters = InjectedInputTouchParameters.Pressure
                                                | InjectedInputTouchParameters.Contact
                                                | InjectedInputTouchParameters.Orientation
                            });
                        }

                        injector.InjectTouchInput(infos);
                    }
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
            {
                LogToFile($"PointerMove FAILED: {error.Message}");
                return JsonConvert.SerializeObject(new PointerMoveResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    Error = error.Message
                });
            }

            _pointerSession.UpdatePosition(
                req.PointerId, touchX, touchY,
                pressure: req.Pressure,
                tiltX: state!.Device == PointerSessionState.DeviceKind.Pen ? req.TiltX : null,
                tiltY: state.Device == PointerSessionState.DeviceKind.Pen ? req.TiltY : null,
                rotation: state.Device == PointerSessionState.DeviceKind.Pen ? req.Rotation : null);

            return JsonConvert.SerializeObject(new PointerMoveResponse
            {
                Success = true,
                PointerId = req.PointerId,
                ResolvedCoordinates = resolved
            });
        }

        private async Task<string> PointerUpAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<PointerUpRequest>(argsJson)
                      ?? new PointerUpRequest();

            LogToFile($"PointerUpAsync: id={req.PointerId}");

            var upError = _pointerSession.ValidateUp(req.PointerId);
            if (upError != null)
            {
                return JsonConvert.SerializeObject(new PointerUpResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count,
                    Error = upError
                });
            }
            _pointerSession.TryGetPointer(req.PointerId, out var state);

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new PointerUpResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count
                });
            }

            CoordinatePoint lastResolved = new CoordinatePoint(state!.LastX, state.LastY);
            Exception? error = null;

            await _runOnUiThread(() =>
            {
                try
                {
                    if (state!.Device == PointerSessionState.DeviceKind.Pen)
                    {
                        injector.InjectPenInput(new InjectedInputPenInfo
                        {
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = (uint)state.PointerId,
                                PointerOptions = InjectedInputPointerOptions.PointerUp
                                               | InjectedInputPointerOptions.InRange,
                                PixelLocation = new InjectedInputPoint { PositionX = state.LastX, PositionY = state.LastY }
                            },
                            Pressure = 0.0,
                            PenParameters = InjectedInputPenParameters.Pressure
                        });
                    }
                    else
                    {
                        var infos = new List<InjectedInputTouchInfo>();

                        foreach (var kvp in _pointerSession.PointerMap)
                        {
                            var st = kvp.Value;
                            if (st.Device != PointerSessionState.DeviceKind.Touch) continue;

                            int hw = st.ContactWidth / 2;
                            int hh = st.ContactHeight / 2;

                            if (kvp.Key == req.PointerId)
                            {
                                infos.Add(new InjectedInputTouchInfo
                                {
                                    Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                                    PointerInfo = new InjectedInputPointerInfo
                                    {
                                        PointerId = (uint)st.PointerId,
                                        PointerOptions = InjectedInputPointerOptions.PointerUp,
                                        PixelLocation = new InjectedInputPoint { PositionX = st.LastX, PositionY = st.LastY }
                                    },
                                    Pressure = 0.0,
                                    TouchParameters = InjectedInputTouchParameters.Pressure
                                                    | InjectedInputTouchParameters.Contact
                                });
                            }
                            else
                            {
                                infos.Add(new InjectedInputTouchInfo
                                {
                                    Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                                    PointerInfo = new InjectedInputPointerInfo
                                    {
                                        PointerId = (uint)st.PointerId,
                                        PointerOptions = InjectedInputPointerOptions.InContact
                                                       | InjectedInputPointerOptions.Update,
                                        PixelLocation = new InjectedInputPoint { PositionX = st.LastX, PositionY = st.LastY }
                                    },
                                    Pressure = st.Pressure,
                                    Orientation = st.Orientation,
                                    TouchParameters = InjectedInputTouchParameters.Pressure
                                                    | InjectedInputTouchParameters.Contact
                                                    | InjectedInputTouchParameters.Orientation
                                });
                            }
                        }

                        injector.InjectTouchInput(infos);

                        // UninitializeTouchInjection is called below via isLastTouchPointer
                    }
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
            {
                LogToFile($"PointerUp FAILED: {error.Message}");
                return JsonConvert.SerializeObject(new PointerUpResponse
                {
                    Success = false,
                    PointerId = req.PointerId,
                    ActivePointers = _pointerSession.Count,
                    Error = error.Message
                });
            }

            bool isLastTouchPointer = _pointerSession.RecordUp(req.PointerId);

            // Tear down touch session when last touch pointer was released
            if (isLastTouchPointer && injector != null)
            {
                try { injector.UninitializeTouchInjection(); } catch { }
            }

            LogToFile($"PointerUp succeeded, active pointers: {_pointerSession.Count}");

            return JsonConvert.SerializeObject(new PointerUpResponse
            {
                Success = true,
                PointerId = req.PointerId,
                ResolvedCoordinates = lastResolved,
                ActivePointers = _pointerSession.Count
            });
        }

        /// <summary>
        /// Releases all active pointers and tears down injection sessions.
        /// Called during StopAsync cleanup.
        /// </summary>
        private void ReleaseAllPointers(InputInjector injector)
        {
            if (_pointerSession.Count == 0) return;

            try
            {
                // Release any active pen pointers
                foreach (var kvp in _pointerSession.PointerMap)
                {
                    if (kvp.Value.Device != PointerSessionState.DeviceKind.Pen) continue;
                    try
                    {
                        injector.InjectPenInput(new InjectedInputPenInfo
                        {
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = (uint)kvp.Value.PointerId,
                                PointerOptions = InjectedInputPointerOptions.PointerUp
                                               | InjectedInputPointerOptions.InRange,
                                PixelLocation = new InjectedInputPoint { PositionX = kvp.Value.LastX, PositionY = kvp.Value.LastY }
                            },
                            Pressure = 0.0,
                            PenParameters = InjectedInputPenParameters.Pressure
                        });
                    }
                    catch (Exception ex) { LogToFile($"ReleaseAllPointers pen error: {ex.Message}"); }
                }

                // Release any active touch pointers
                var touchInfos = new List<InjectedInputTouchInfo>();
                foreach (var kvp in _pointerSession.PointerMap)
                {
                    if (kvp.Value.Device != PointerSessionState.DeviceKind.Touch) continue;
                    var st = kvp.Value;
                    int hw = st.ContactWidth / 2;
                    int hh = st.ContactHeight / 2;
                    touchInfos.Add(new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -hh, Bottom = hh, Left = -hw, Right = hw },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = (uint)st.PointerId,
                            PointerOptions = InjectedInputPointerOptions.PointerUp,
                            PixelLocation = new InjectedInputPoint { PositionX = st.LastX, PositionY = st.LastY }
                        },
                        Pressure = 0.0,
                        TouchParameters = InjectedInputTouchParameters.Pressure
                                        | InjectedInputTouchParameters.Contact
                    });
                }
                if (touchInfos.Count > 0)
                {
                    injector.InjectTouchInput(touchInfos);
                    injector.UninitializeTouchInjection();
                }
            }
            catch (Exception ex)
            {
                LogToFile($"ReleaseAllPointers error: {ex.Message}");
            }

            _pointerSession.Clear();
            LogToFile("All pointers released");
        }
    }
}
