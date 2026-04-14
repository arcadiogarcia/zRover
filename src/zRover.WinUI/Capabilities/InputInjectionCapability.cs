using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Coordinates;
using zRover.Core.Logging;
using zRover.Core.Tools.InputInjection;
using zRover.Core.Tools.UiTree;
using Windows.Foundation;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI.Input.Preview.Injection;
using Microsoft.UI.Xaml;

namespace zRover.WinUI.Capabilities
{
    public sealed partial class InputInjectionCapability : IDebugCapability
    {
        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int SW_RESTORE = 9;

        private Microsoft.UI.Xaml.Window? _window;

        private InputInjector? _injector;
        private ICoordinateResolver? _resolver;
        private Func<Func<Task>, Task>? _runOnUiThread;
        private string? _logDir;
        private string? _injectorError;

        private const string InjectorUnavailableMessage =
            "InputInjector is not available. Ensure the 'inputInjectionBrokered' restricted capability " +
            "is declared in Package.appxmanifest and the app is running with the required permissions.";

        /// <summary>
        /// True if the InputInjector API was successfully created during startup.
        /// </summary>
        public bool InjectorAvailable => _injector != null;

        /// <summary>
        /// If <see cref="InjectorAvailable"/> is false, contains the reason.
        /// </summary>
        public string? InjectorError => _injectorError;

        public InputInjectionCapability(Microsoft.UI.Xaml.Window window)
        {
            _window = window;
        }

        /// <summary>
        /// Forces the WinUI window to the foreground using the Win32 AttachThreadInput trick,
        /// which reliably steals foreground activation even when the app is in the background.
        /// Falls back to Window.Activate() if Win32 calls fail.
        /// Must be called on the UI thread.
        /// </summary>
        private void ActivateWindow()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
                var foregroundHwnd = GetForegroundWindow();
                uint currentThreadId = GetCurrentThreadId();
                uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);

                bool attached = false;
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);

                if (attached)
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
            catch
            {
                try { _window!.Activate(); } catch { }
            }
        }

        private double GetDpiScale()
        {
            try
            {
                if (_window?.Content?.XamlRoot?.RasterizationScale is double s && s > 0) return s;
                if (_window != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    int dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0) return dpi / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// Returns a JSON error response with the standard unavailable message.
        /// The JSON includes "success": false and "error": "...".
        /// </summary>
        private string InjectorUnavailableResponse(object responseObj)
        {
            var json = JsonConvert.SerializeObject(responseObj);
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            obj["error"] = _injectorError ?? InjectorUnavailableMessage;
            return obj.ToString(Formatting.None);
        }

        private void LogToFile(string message)
        {
            try
            {
                if (_logDir == null) return;
                var path = System.IO.Path.Combine(_logDir, "input-injection.log");
                System.IO.File.AppendAllText(path,
                    $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
        }

        public string Name => "InputInjection";

        public async Task StartAsync(DebugHostContext context)
        {
            _resolver = context.CoordinateResolver;
            _runOnUiThread = context.RunOnUiThread;
            _logDir = context.ArtifactDirectory;

            // InputInjector must be created on the UI thread.
            if (context.RunOnUiThread != null)
            {
                await context.RunOnUiThread(() =>
                {
                    TryCreateInjector();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            else
            {
                TryCreateInjector();
            }
        }

        private void TryCreateInjector()
        {
            try
            {
                _injector = InputInjector.TryCreate();
                if (_injector != null)
                {
                    LogToFile("InputInjector created");
                    _injectorError = null;
                }
                else
                {
                    _injectorError = InjectorUnavailableMessage;
                    LogToFile("InputInjector.TryCreate() returned null — " + _injectorError);
                }
            }
            catch (Exception ex)
            {
                _injectorError = $"InputInjector.TryCreate() threw {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8}). " +
                                 "Ensure the 'inputInjectionBrokered' restricted capability is declared in Package.appxmanifest.";
                LogToFile($"InputInjector.TryCreate() FAILED: {ex.GetType().FullName}: {ex.Message}");
                LogToFile($"HRESULT: 0x{ex.HResult:X8}");
                LogToFile($"StackTrace: {ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine($"[InputInjection] Error creating InputInjector: {ex}");
                _injector = null;
            }
        }

        public Task StopAsync()
        {
            if (_injector != null)
            {
                ReleaseAllPointers(_injector);
                try { _injector.UninitializeTouchInjection(); } catch { }
            }
            _injector = null;
            return Task.CompletedTask;
        }

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_tap",
                "Injects a tap/touch event at the specified coordinates. " +
                "PREFER tap_element (by name/type) or find_element (returns exact centerX/centerY) over manual coordinates — they eliminate coordinate guessing errors. " +
                "If you must use coordinates: get them from get_ui_tree bounds (center = bounds.x + bounds.width/2, bounds.y + bounds.height/2), NOT from eyeballing screenshots. " +
                "Normalized coordinates map directly to percentages: x=0.33 means '33% across the window'. " +
                "Returns an annotated screenshot showing the tap location BEFORE injection so you can verify. " +
                "Set dryRun=true to preview without injecting. " +
                "Use coordinateSpace='normalized' (default, 0.0-1.0 relative to window) or 'pixels' (render pixels matching windowWidth/windowHeight from capture_current_view).",
                ToolSchemas.TapSchema,
                InjectTapAsync);

            registry.RegisterTool(
                "inject_drag_path",
                "Injects a drag gesture along a path of points. " +
                "Returns an annotated screenshot showing the drag path BEFORE the input is injected, " +
                "with the start point (green circle), path (cyan line), waypoints (yellow dots), and end point (red diamond) visualized. " +
                "Set dryRun=true to preview the drag path without actually injecting input. " +
                "Use coordinateSpace='normalized' (default, 0.0-1.0 relative to window) or 'pixels' (render pixels matching windowWidth/windowHeight from capture_current_view). " +
                "PREFER find_element to get exact start/end coordinates from the UI tree rather than estimating from screenshots.",
                ToolSchemas.DragSchema,
                InjectDragPathAsync);

            registry.RegisterTool(
                "tap_element",
                "Finds a UI element by name, automationName, or type and taps its center. " +
                "This is the MOST RELIABLE way to click UI elements — no coordinate estimation needed. " +
                "The server resolves the element's exact position from the XAML visual tree and injects the tap automatically. " +
                "Provide at least one of: name (x:Name), automationName (AutomationProperties.Name), or type (e.g. 'Button'). " +
                "Use 'parent' to scope the search under a specific container. " +
                "Use 'timeout' to wait for dynamically appearing elements (e.g. after navigation). " +
                "Returns the matched element info and the exact coordinates where the tap was injected. " +
                "Set dryRun=true to see where the tap would land without actually injecting.",
                ToolSchemas.TapElementSchema,
                TapElementAsync);

            registry.RegisterTool(
                "activate_element",
                "Finds a UI element by name/type and activates it using XAML AutomationPeer patterns — NO coordinates needed. " +
                "This uses the element's native automation support (Invoke for buttons, Toggle for checkboxes, Select for list items, " +
                "Expand/Collapse for tree items and combo boxes). More reliable than coordinate-based tapping because " +
                "it works even if the element is partially obscured or the window has moved. " +
                "Falls back to tapping the element center if no matching automation pattern is available. " +
                "Actions: 'invoke' (default — clicks buttons), 'toggle' (checkboxes/toggle buttons), " +
                "'select' (list items), 'expand'/'collapse' (tree items, combo boxes), 'focus' (set keyboard focus).",
                ToolSchemas.ActivateElementSchema,
                ActivateElementAsync);

            RegisterKeyboardTools(registry);
            RegisterMouseTools(registry);
            RegisterTouchTools(registry);
            RegisterPenTools(registry);
            RegisterGamepadTools(registry);
            RegisterPointerTools(registry);
        }

        private async Task<string> InjectTapAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectTapRequest>(argsJson)
                      ?? new InjectTapRequest();

            LogToFile($"InjectTapAsync: ({req.X},{req.Y}) space={req.CoordinateSpace} device={req.Device} dryRun={req.DryRun}");

            // --- Capture and annotate a preview screenshot BEFORE injection ---
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(req).ConfigureAwait(false);
                    LogToFile($"Preview screenshot: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Preview screenshot failed: {ex.Message}");
                }
            }

            // If dry run, return preview without injecting
            if (req.DryRun)
            {
                // Resolve coordinates so the response reflects true screen-space position.
                CoordinatePoint dryResolved;
                try
                {
                    string? dryResolvedResult = null;
                    await (_runOnUiThread?.Invoke(() =>
                    {
                        var space = ParseSpace(req.CoordinateSpace);
                        dryResolvedResult = JsonConvert.SerializeObject(_resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space));
                        return Task.CompletedTask;
                    }) ?? Task.CompletedTask).ConfigureAwait(false);
                    dryResolved = dryResolvedResult != null
                        ? JsonConvert.DeserializeObject<CoordinatePoint>(dryResolvedResult)
                        : new CoordinatePoint(req.X, req.Y);
                }
                catch
                {
                    dryResolved = new CoordinatePoint(req.X, req.Y);
                }

                var dryResponse = new InjectTapResponse
                {
                    Success = true,
                    ResolvedCoordinates = dryResolved,
                    Device = req.Device,
                    DryRun = true,
                    PreviewScreenshotPath = previewPath
                };
                return JsonConvert.SerializeObject(dryResponse);
            }

            // --- Proceed with actual injection ---
            var injector = _injector;
            if (injector != null && _runOnUiThread != null)
            {
                var useTouch = !string.Equals(req.Device, "mouse", StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (useTouch)
                    {
                        // Touch tap: dispatch Down on UI thread, await 80ms, then dispatch Up in a
                        // second UI-thread turn. InitializeTouchInjection makes a synchronous RPC so
                        // it must NOT be followed by Thread.Sleep (would deadlock the message pump).
                        CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
                        int touchX = 0, touchY = 0;
                        Exception? innerEx = null;

                        await _runOnUiThread(() =>
                        {
                            try
                            {
                                ActivateWindow();
                                var space = ParseSpace(req.CoordinateSpace);

                                var wc = _window?.Content as FrameworkElement;
                                Windows.Foundation.Rect? cwBounds = null; _ = cwBounds; // CoreWindow not available in WinUI 3
                                double dipScaleDiag = GetDpiScale();
                                var resolverMsg = $"Inject context: contentW={wc?.ActualWidth:F1} contentH={wc?.ActualHeight:F1} dpi={dipScaleDiag}";
                                LogToFile(resolverMsg);

                                resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                                double dpiScale = GetDpiScale();

                                (touchX, touchY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);
                                LogToFile($"Tap DIP=({resolved.X:F1},{resolved.Y:F1}) inject=({touchX},{touchY}) dpi={dpiScale}");

                                var contact = new InjectedInputRectangle { Top = -8, Bottom = 8, Left = -8, Right = 8 };
                                var location = new InjectedInputPoint { PositionX = touchX, PositionY = touchY };

                                injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
                                injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                                {
                                    Contact = contact,
                                    PointerInfo = new InjectedInputPointerInfo
                                    {
                                        PointerId = 1,
                                        PointerOptions = InjectedInputPointerOptions.PointerDown
                                                       | InjectedInputPointerOptions.InRange
                                                       | InjectedInputPointerOptions.InContact
                                                       | InjectedInputPointerOptions.New
                                                       | InjectedInputPointerOptions.Primary,
                                        PixelLocation = location
                                    },
                                    Pressure = 1.0,
                                    TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                                }});
                                LogToFile("Touch PointerDown injected");
                            }
                            catch (Exception ex) { innerEx = ex; }
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                        if (innerEx == null)
                        {
                            // Let the input system process PointerDown before sending PointerUp.
                            await Task.Delay(80).ConfigureAwait(false);

                            await _runOnUiThread(() =>
                            {
                                try
                                {
                                    var contact = new InjectedInputRectangle { Top = -8, Bottom = 8, Left = -8, Right = 8 };
                                    var location = new InjectedInputPoint { PositionX = touchX, PositionY = touchY };
                                    injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                                    {
                                        Contact = contact,
                                        PointerInfo = new InjectedInputPointerInfo
                                        {
                                            PointerId = 1,
                                            PointerOptions = InjectedInputPointerOptions.PointerUp,
                                            PixelLocation = location
                                        },
                                        Pressure = 0.0,
                                        TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                                    }});
                                    injector.UninitializeTouchInjection();
                                    LogToFile("Touch PointerUp injected");
                                }
                                catch (Exception ex) { innerEx = ex; }
                                return Task.CompletedTask;
                            }).ConfigureAwait(false);
                        }

                        if (innerEx != null)
                            LogToFile($"Touch tap FAILED: {innerEx.GetType().FullName}: {innerEx.Message}");
                        else
                            LogToFile("InputInjector tap succeeded");

                        return AttachPreviewPath(JsonConvert.SerializeObject(new InjectTapResponse
                        {
                            Success = innerEx == null,
                            ResolvedCoordinates = resolved,
                            Device = req.Device,
                            PreviewScreenshotPath = previewPath
                        }), previewPath);
                    }
                    else
                    {
                        // Mouse tap: handled synchronously on UI thread.
                        string? result = null;
                        Exception? innerEx = null;
                        await _runOnUiThread(() =>
                        {
                            try { result = InjectTapViaInjector(injector, req); }
                            catch (Exception ex) { innerEx = ex; }
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                        if (result != null)
                        {
                            LogToFile("InputInjector tap succeeded");
                            return AttachPreviewPath(result, previewPath);
                        }
                        if (innerEx != null)
                            LogToFile($"InputInjector tap FAILED: {innerEx.GetType().FullName}: {innerEx.Message} HRESULT=0x{innerEx.HResult:X8}");
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"InputInjector tap outer FAILED: {ex.GetType().FullName}: {ex.Message}");
                }
            }

            LogToFile("InputInjector is not available. Ensure the inputInjectionBrokered capability is declared.");
            var errorResponse = new InjectTapResponse
            {
                Success = false,
                Device = req.Device,
                ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                PreviewScreenshotPath = previewPath,
                Timestamp = DateTimeOffset.UtcNow.ToString("o")
            };
            return InjectorUnavailableResponse(errorResponse);
        }

        private string InjectTapViaInjector(InputInjector injector, InjectTapRequest req)
        {
            ActivateWindow();

            var space = ParseSpace(req.CoordinateSpace);
            var resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);

                        double dpiScale = GetDpiScale();
            double rawX = resolved.X * dpiScale;
            double rawY = resolved.Y * dpiScale;

            LogToFile($"Tap DIP=({resolved.X:F1},{resolved.Y:F1}) RAW=({rawX:F0},{rawY:F0}) dpi={dpiScale}");

            // Mouse path only — touch is handled asynchronously in InjectTapAsync.
            var (normX, normY) = ToMouseNormalized(resolved, dpiScale);

            injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
            {
                MouseOptions = InjectedInputMouseOptions.Move
                             | InjectedInputMouseOptions.Absolute
                             | InjectedInputMouseOptions.VirtualDesk,
                DeltaX = normX, DeltaY = normY
            }});
            System.Threading.Thread.Sleep(50);
            injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
            {
                MouseOptions = InjectedInputMouseOptions.LeftDown
            }});
            System.Threading.Thread.Sleep(50);
            injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
            {
                MouseOptions = InjectedInputMouseOptions.LeftUp
            }});

            var response = new InjectTapResponse
            {
                Success = true,
                ResolvedCoordinates = resolved,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private async Task<string> TapElementAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<TapElementRequest>(argsJson)
                          ?? new TapElementRequest();

                if (string.IsNullOrEmpty(req.Name) && string.IsNullOrEmpty(req.AutomationName) && string.IsNullOrEmpty(req.TypeName) && string.IsNullOrEmpty(req.Text))
                    return JsonConvert.SerializeObject(new TapElementResponse { Success = false, Error = "At least one of 'name', 'automationName', 'type', or 'text' is required." });

                var criteria = new ElementSearchHelper.SearchCriteria
                {
                    Name = req.Name,
                    AutomationName = req.AutomationName,
                    TypeName = req.TypeName,
                    ParentName = req.Parent,
                    Text = req.Text,
                    Index = req.Index
                };

                // All XAML property access (including _window.Content) must happen on the UI thread
                List<ElementSearchHelper.ElementMatch> matches = new();
                if (_runOnUiThread != null)
                {
                    var deadline = req.Timeout > 0 ? DateTime.UtcNow.AddMilliseconds(req.Timeout) : DateTime.MinValue;
                    do
                    {
                        await _runOnUiThread(() =>
                        {
                            var windowContent = _window?.Content as Microsoft.UI.Xaml.FrameworkElement;
                            if (windowContent != null)
                                matches = ElementSearchHelper.FindElements(windowContent, criteria);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                        if (matches.Count > 0) break;
                        if (req.Timeout <= 0) break;
                        await Task.Delay(Math.Max(50, req.Poll)).ConfigureAwait(false);
                    } while (DateTime.UtcNow < deadline);
                }
                else
                {
                    var windowContent = _window?.Content as Microsoft.UI.Xaml.FrameworkElement;
                    if (windowContent != null)
                        matches = ElementSearchHelper.FindElements(windowContent, criteria);
                }

                if (matches.Count == 0)
                {
                    var searchDesc = req.Name != null ? $"name='{req.Name}'" :
                                     req.AutomationName != null ? $"automationName='{req.AutomationName}'" :
                                     $"type='{req.TypeName}'";
                    return JsonConvert.SerializeObject(new TapElementResponse { Success = false, Error = $"Element not found: {searchDesc}" });
                }

                var match = matches[0];

                // Now inject the tap at the element's center using the existing tap logic
                var tapReq = new InjectTapRequest
                {
                    X = match.CenterX,
                    Y = match.CenterY,
                    CoordinateSpace = "normalized",
                    Device = req.Device,
                    Button = req.Button,
                    DryRun = req.DryRun
                };

                // Delegate to existing InjectTapAsync
                var tapResultJson = await InjectTapAsync(JsonConvert.SerializeObject(tapReq)).ConfigureAwait(false);

                // Build enriched response
                var response = new TapElementResponse
                {
                    Success = true,
                    ElementName = match.Name,
                    ElementType = match.Type,
                    AutomationName = match.AutomationName,
                    Bounds = match.Bounds,
                    TappedAt = new zRover.Core.Tools.Screenshot.NormalizedRect
                    {
                        X = match.CenterX,
                        Y = match.CenterY,
                        Width = 0,
                        Height = 0
                    },
                    Device = req.Device,
                    DryRun = req.DryRun
                };

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new TapElementResponse { Success = false, Error = ex.Message });
            }
        }

        private async Task<string> ActivateElementAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ActivateElementRequest>(argsJson)
                          ?? new ActivateElementRequest();

                if (string.IsNullOrEmpty(req.Name) && string.IsNullOrEmpty(req.AutomationName) && string.IsNullOrEmpty(req.TypeName) && string.IsNullOrEmpty(req.Text))
                    return JsonConvert.SerializeObject(new ActivateElementResponse { Success = false, Error = "At least one of 'name', 'automationName', 'type', or 'text' is required." });

                var criteria = new ElementSearchHelper.SearchCriteria
                {
                    Name = req.Name,
                    AutomationName = req.AutomationName,
                    TypeName = req.TypeName,
                    ParentName = req.Parent,
                    Text = req.Text
                };

                // All XAML property access (including _window.Content) must happen on the UI thread
                List<ElementSearchHelper.ElementMatch> matches = new();
                if (_runOnUiThread != null)
                {
                    var deadline = req.Timeout > 0 ? DateTime.UtcNow.AddMilliseconds(req.Timeout) : DateTime.MinValue;
                    do
                    {
                        await _runOnUiThread(() =>
                        {
                            var windowContent = _window?.Content as Microsoft.UI.Xaml.FrameworkElement;
                            if (windowContent != null)
                                matches = ElementSearchHelper.FindElements(windowContent, criteria);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                        if (matches.Count > 0) break;
                        if (req.Timeout <= 0) break;
                        await Task.Delay(Math.Max(50, req.Poll)).ConfigureAwait(false);
                    } while (DateTime.UtcNow < deadline);
                }
                else
                {
                    var windowContent = _window?.Content as Microsoft.UI.Xaml.FrameworkElement;
                    if (windowContent != null)
                        matches = ElementSearchHelper.FindElements(windowContent, criteria);
                }

                if (matches.Count == 0)
                {
                    var searchDesc = req.Name != null ? $"name='{req.Name}'" :
                                     req.AutomationName != null ? $"automationName='{req.AutomationName}'" :
                                     $"type='{req.TypeName}'";
                    return JsonConvert.SerializeObject(new ActivateElementResponse { Success = false, Error = $"Element not found: {searchDesc}" });
                }

                var match = matches[0];
                string? method = null;
                bool activated = false;
                Exception? activationError = null;

                // Try to activate via AutomationPeer on the UI thread
                if (_runOnUiThread != null)
                {
                    await _runOnUiThread(() =>
                    {
                        try
                        {
                            (activated, method) = TryAutomationActivate(match.Element, req.Action);
                        }
                        catch (Exception ex)
                        {
                            activationError = ex;
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        (activated, method) = TryAutomationActivate(match.Element, req.Action);
                    }
                    catch (Exception ex)
                    {
                        activationError = ex;
                    }
                }

                // If automation activation failed, fall back to tap
                if (!activated && activationError == null)
                {
                    var tapReq = new InjectTapRequest
                    {
                        X = match.CenterX,
                        Y = match.CenterY,
                        CoordinateSpace = "normalized",
                        Device = "touch"
                    };
                    await InjectTapAsync(JsonConvert.SerializeObject(tapReq)).ConfigureAwait(false);
                    method = "tap_fallback";
                    activated = true;
                }

                if (activationError != null)
                    return JsonConvert.SerializeObject(new ActivateElementResponse { Success = false, Error = activationError.Message });

                return JsonConvert.SerializeObject(new ActivateElementResponse
                {
                    Success = true,
                    Method = method,
                    ElementName = match.Name,
                    ElementType = match.Type,
                    AutomationName = match.AutomationName,
                    Bounds = match.Bounds
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new ActivateElementResponse { Success = false, Error = ex.Message });
            }
        }

        /// <summary>
        /// Attempts to activate a XAML element via its AutomationPeer.
        /// Returns (success, methodName). Must be called on the UI thread.
        /// </summary>
        private static (bool success, string? method) TryAutomationActivate(Microsoft.UI.Xaml.FrameworkElement element, string action)
        {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element);
            if (peer == null)
            {
                // Try creating a peer
                peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(element);
            }
            if (peer == null)
                return (false, null);

            action = action?.ToLowerInvariant() ?? "invoke";

            switch (action)
            {
                case "invoke":
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
                        is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoker)
                    {
                        invoker.Invoke();
                        return (true, "IInvokeProvider.Invoke");
                    }
                    // Toggle buttons can also be "invoked" by toggling
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)
                        is Microsoft.UI.Xaml.Automation.Provider.IToggleProvider toggler2)
                    {
                        toggler2.Toggle();
                        return (true, "IToggleProvider.Toggle (invoke fallback)");
                    }
                    break;

                case "toggle":
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)
                        is Microsoft.UI.Xaml.Automation.Provider.IToggleProvider toggler)
                    {
                        toggler.Toggle();
                        return (true, "IToggleProvider.Toggle");
                    }
                    break;

                case "select":
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.SelectionItem)
                        is Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider selector)
                    {
                        selector.Select();
                        return (true, "ISelectionItemProvider.Select");
                    }
                    break;

                case "expand":
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)
                        is Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider expander)
                    {
                        expander.Expand();
                        return (true, "IExpandCollapseProvider.Expand");
                    }
                    break;

                case "collapse":
                    if (peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)
                        is Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider collapser)
                    {
                        collapser.Collapse();
                        return (true, "IExpandCollapseProvider.Collapse");
                    }
                    break;

                case "focus":
                    if (element is Microsoft.UI.Xaml.Controls.Control ctrl)
                    {
                        ctrl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                        return (true, "Control.Focus");
                    }
                    break;
            }

            return (false, null);
        }

        private async Task<string> InjectDragPathAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectDragPathRequest>(argsJson)
                      ?? new InjectDragPathRequest();

            LogToFile($"InjectDragPathAsync called: {req.Points?.Count ?? 0} points, device={req.Device} dryRun={req.DryRun}");

            // --- Capture and annotate a preview screenshot BEFORE injection ---
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedDragPreview(req).ConfigureAwait(false);
                    LogToFile($"Preview screenshot: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Preview screenshot failed: {ex.Message}");
                }
            }

            // If dry run, return preview without injecting
            if (req.DryRun)
            {
                var resolvedPath = new List<CoordinatePoint>();
                foreach (var pt in req.Points)
                    resolvedPath.Add(new CoordinatePoint(pt.X, pt.Y));

                var dryResponse = new InjectDragPathResponse
                {
                    Success = true,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    ResolvedPath = resolvedPath,
                    Device = req.Device,
                    DryRun = true,
                    PreviewScreenshotPath = previewPath
                };
                return JsonConvert.SerializeObject(dryResponse);
            }

            // --- Proceed with actual injection ---
            var injector = _injector;
            if (injector != null && _runOnUiThread != null)
            {
                LogToFile("Using InputInjector drag path (async)");
                try
                {
                    var result = await InjectDragViaInjectorAsync(injector, req).ConfigureAwait(false);
                    if (result != null)
                    {
                        LogToFile("InputInjector drag succeeded");
                        return AttachPreviewPath(result, previewPath);
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"InputInjector drag FAILED: {ex.GetType().FullName}: {ex.Message} HRESULT=0x{ex.HResult:X8}");
                }
            }

            LogToFile("InputInjector is not available. Ensure the inputInjectionBrokered capability is declared.");
            var errorResponse = new InjectDragPathResponse
            {
                Success = false,
                Device = req.Device,
                DurationMs = req.DurationMs,
                PointCount = 0,
                ResolvedPath = new List<CoordinatePoint>(),
                PreviewScreenshotPath = previewPath
            };
            return InjectorUnavailableResponse(errorResponse);
        }

        private async Task<string?> InjectDragViaInjectorAsync(InputInjector injector, InjectDragPathRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            var resolvedPath = new List<CoordinatePoint>();
            double dpiScale = 0;
            var useTouch = !string.Equals(req.Device, "mouse", StringComparison.OrdinalIgnoreCase);

            // Resolve coordinates and get DPI info on UI thread
            await _runOnUiThread!(() =>
            {
                ActivateWindow();
                dpiScale = GetDpiScale();
                foreach (var pt in req.Points)
                    resolvedPath.Add(_resolver!.Resolve(pt, space));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            LogToFile($"Drag resolved DIP path: {string.Join(" -> ", resolvedPath.Select(p => $"({p.X:F0},{p.Y:F0})"))} dpi={dpiScale}");

            if (resolvedPath.Count < 2 || dpiScale <= 0)
                return null;

            var allPoints = resolvedPath;
            int delayPerPoint = Math.Max(1, req.DurationMs / Math.Max(1, allPoints.Count - 1));

            if (useTouch)
            {
                // Touch drag: dispatch each event on UI thread with async waits between.
                // InjectedInputPoint uses physical pixels offset by virtual desktop origin.
                var (startRawX, startRawY) = ToTouchInjectionPoint(allPoints[0].X, allPoints[0].Y, dpiScale);
                LogToFile($"Touch drag start inject=({startRawX},{startRawY}) dpi={dpiScale}");

                await _runOnUiThread!(() =>
                {
                    injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
                    injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = InjectedInputPointerOptions.PointerDown
                                           | InjectedInputPointerOptions.InRange
                                           | InjectedInputPointerOptions.InContact
                                           | InjectedInputPointerOptions.New
                                           | InjectedInputPointerOptions.Primary,
                            PixelLocation = new InjectedInputPoint { PositionX = startRawX, PositionY = startRawY }
                        },
                        Pressure = 1.0,
                        TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                for (int i = 1; i < allPoints.Count; i++)
                {
                    await Task.Delay(delayPerPoint).ConfigureAwait(false);
                    var (rawX, rawY) = ToTouchInjectionPoint(allPoints[i].X, allPoints[i].Y, dpiScale);
                    await _runOnUiThread!(() =>
                    {
                        injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                        {
                            Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = 1,
                                PointerOptions = InjectedInputPointerOptions.InContact
                                               | InjectedInputPointerOptions.InRange
                                               | InjectedInputPointerOptions.Update,
                                PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                            },
                            Pressure = 1.0,
                            TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                        }});
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                var (endRawX, endRawY) = ToTouchInjectionPoint(allPoints[allPoints.Count - 1].X, allPoints[allPoints.Count - 1].Y, dpiScale);
                LogToFile($"Touch drag end inject=({endRawX},{endRawY})");

                await _runOnUiThread!(() =>
                {
                    injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = InjectedInputPointerOptions.PointerUp,
                            PixelLocation = new InjectedInputPoint { PositionX = endRawX, PositionY = endRawY }
                        },
                        Pressure = 0.0,
                        TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                    }});
                    injector.UninitializeTouchInjection();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                // Small delay to let the PointerUp/PointerCanceled event reach XAML before
                // any follow-up injection reuses the same pointer ID.
                await Task.Delay(80).ConfigureAwait(false);
            }
            else
            {
                // Mouse drag with virtual-desktop-relative 0-65535 coordinates
                await _runOnUiThread!(() =>
                {
                    var (normX, normY) = ToMouseNormalized(allPoints[0], dpiScale);
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX, DeltaY = normY
                    }});
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.LeftDown
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                for (int i = 1; i < allPoints.Count; i++)
                {
                    await Task.Delay(delayPerPoint).ConfigureAwait(false);
                    int idx = i;
                    await _runOnUiThread!(() =>
                    {
                        var (normX, normY) = ToMouseNormalized(allPoints[idx], dpiScale);
                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.Move
                                         | InjectedInputMouseOptions.Absolute
                                         | InjectedInputMouseOptions.VirtualDesk,
                            DeltaX = normX, DeltaY = normY
                        }});
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                await _runOnUiThread!(() =>
                {
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.LeftUp
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            return JsonConvert.SerializeObject(new InjectDragPathResponse
            {
                Success = true,
                PointCount = resolvedPath.Count,
                DurationMs = req.DurationMs,
                ResolvedPath = resolvedPath,
                Device = useTouch ? "touch" : "mouse"
            });
        }

        #region Preview screenshot helpers

        /// <summary>
        /// Captures a screenshot and draws a crosshair at the tap location.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedTapPreview(InjectTapRequest req)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            double normX = req.X;
            double normY = req.Y;

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync(_window!.Content).ConfigureAwait(false);

                // Convert coordinates to normalized if needed
                var space = ParseSpace(req.CoordinateSpace);
                if (space == CoordinateSpace.Pixels)
                {
                    normX = bitmap!.PixelWidth > 0 ? req.X / bitmap.PixelWidth : req.X;
                    normY = bitmap!.PixelHeight > 0 ? req.Y / bitmap.PixelHeight : req.Y;
                }
                // Normalized: pass-through (already 0..1)
            }).ConfigureAwait(false);

            if (bitmap == null) return null;

            int cx = (int)(normX * bitmap.PixelWidth);
            int cy = (int)(normY * bitmap.PixelHeight);

            var annotated = ScreenshotAnnotator.DrawCrosshair(bitmap, cx, cy);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, "tap_preview").ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Captures a screenshot and draws the drag path visualization.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedDragPreview(InjectDragPathRequest req)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            var normalizedPoints = new List<(double x, double y)>();

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync(_window!.Content).ConfigureAwait(false);

                var space = ParseSpace(req.CoordinateSpace);
                foreach (var pt in req.Points)
                {
                    double nx = pt.X;
                    double ny = pt.Y;
                    if (space == CoordinateSpace.Pixels)
                    {
                        nx = bitmap!.PixelWidth > 0 ? pt.X / bitmap.PixelWidth : pt.X;
                        ny = bitmap!.PixelHeight > 0 ? pt.Y / bitmap.PixelHeight : pt.Y;
                    }
                    // Normalized: pass-through (already 0..1)
                    normalizedPoints.Add((nx, ny));
                }
            }).ConfigureAwait(false);

            if (bitmap == null || normalizedPoints.Count < 2) return null;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            var pixelPoints = new List<(int x, int y)>();
            foreach (var (nx, ny) in normalizedPoints)
                pixelPoints.Add(((int)(nx * w), (int)(ny * h)));

            var annotated = ScreenshotAnnotator.DrawDragPath(bitmap, pixelPoints);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, "drag_preview").ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Captures a screenshot and draws multiple pointer path visualizations.
        /// Used for multi-touch, pinch, and rotate previews.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedMultiPathPreview(
            List<List<CoordinatePoint>> pointerPaths, string? coordinateSpace, string filePrefix)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            var normalizedPaths = new List<List<(double x, double y)>>();

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync(_window!.Content).ConfigureAwait(false);

                var space = ParseSpace(coordinateSpace);
                foreach (var path in pointerPaths)
                {
                    var nPath = new List<(double x, double y)>();
                    foreach (var pt in path)
                    {
                        double nx = pt.X;
                        double ny = pt.Y;
                        if (space == CoordinateSpace.Pixels)
                        {
                            nx = bitmap!.PixelWidth > 0 ? pt.X / bitmap.PixelWidth : pt.X;
                            ny = bitmap!.PixelHeight > 0 ? pt.Y / bitmap.PixelHeight : pt.Y;
                        }
                        // Normalized: pass-through (already 0..1)
                        nPath.Add((nx, ny));
                    }
                    normalizedPaths.Add(nPath);
                }
            }).ConfigureAwait(false);

            if (bitmap == null || normalizedPaths.Count == 0) return null;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            var pixelPaths = new List<List<(int x, int y)>>();
            foreach (var nPath in normalizedPaths)
            {
                var pxPath = new List<(int x, int y)>();
                foreach (var (nx, ny) in nPath)
                    pxPath.Add(((int)(nx * w), (int)(ny * h)));
                pixelPaths.Add(pxPath);
            }

            var annotated = ScreenshotAnnotator.DrawMultiPointerPaths(bitmap, pixelPaths);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, filePrefix).ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Injects <c>previewScreenshotPath</c> into an already-serialized JSON response string.
        /// </summary>
        private static string AttachPreviewPath(string resultJson, string? previewPath)
        {
            if (previewPath == null) return resultJson;
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(resultJson);
                obj["previewScreenshotPath"] = previewPath;
                return obj.ToString(Formatting.None);
            }
            catch
            {
                return resultJson;
            }
        }

        #endregion

        private static CoordinateSpace ParseSpace(string? value) =>
            value?.ToLowerInvariant() switch
            {
                "pixels" => CoordinateSpace.Pixels,
                _ => CoordinateSpace.Normalized
            };
    }
}




