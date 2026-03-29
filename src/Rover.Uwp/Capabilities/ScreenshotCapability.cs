using System;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Tools.Screenshot;
using Windows.Graphics.Imaging;
using Windows.UI.Xaml;

namespace Rover.Uwp.Capabilities
{
    public sealed class ScreenshotCapability : IDebugCapability
    {
        private const int DefaultMaxDimension = 1280;

        private const string CaptureSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""format"": { ""type"": ""string"", ""enum"": [""png""], ""default"": ""png"" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  }
}";

        private const string RegionSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Left edge in normalized coordinates (0.0–1.0)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Top edge in normalized coordinates (0.0–1.0)."" },
    ""width"": { ""type"": ""number"", ""description"": ""Width of the region in normalized coordinates (0.0–1.0)."" },
    ""height"": { ""type"": ""number"", ""description"": ""Height of the region in normalized coordinates (0.0–1.0)."" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  },
  ""required"": [""x"", ""y"", ""width"", ""height""]
}";

        private const string ValidatePositionSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate in normalized space (0.0–1.0)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate in normalized space (0.0–1.0)."" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  },
  ""required"": [""x"", ""y""]
}";

        private DebugHostContext? _context;

        public string Name => "Screenshot";

        public Task StartAsync(DebugHostContext context)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _context = null;
            return Task.CompletedTask;
        }

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "capture_current_view",
                "Captures the current app window as a PNG screenshot. " +
                "Returns bitmapWidth/bitmapHeight (the returned image dimensions, may be smaller if maxWidth/maxHeight was applied) " +
                "and windowWidth/windowHeight (the full render-pixel size of the window before any resize constraint). " +
                "Use windowWidth/windowHeight as the coordinate space for coordinateSpace='pixels' injection. " +
                "To convert a pixel position (px, py) in the bitmap to normalized coordinates: x = px / bitmapWidth, y = py / bitmapHeight. " +
                "To use pixels directly with injection: scale by windowWidth/bitmapWidth first if the bitmap was resized. " +
                "If you are unsure about precise element positions, use capture_region to zoom into a smaller area and verify before interacting.",
                CaptureSchema,
                CaptureAsync);

            registry.RegisterTool(
                "validate_position",
                "Captures the current app window and draws a high-visibility crosshair marker at the specified normalized coordinates."+
                " Use this BEFORE calling inject_tap or inject_drag_path to visually confirm that your estimated coordinates land on the intended UI element. " +
                "The crosshair is drawn with contrasting black and cyan outlines so it is visible on any background. " +
                "Returns the annotated screenshot file path and the marker position.",
                ValidatePositionSchema,
                ValidatePositionAsync);

            registry.RegisterTool(
                "capture_region",
                "Captures a cropped region of the app window as a PNG screenshot. Coordinates are in normalized space (0.0–1.0). " +
                "PURPOSE: Use this tool to verify and refine coordinates you have estimated from a full screenshot before injecting taps or drags. " +
                "Eyeballing positions from a full-window screenshot is error-prone — this tool lets you zoom in on a smaller area to confirm " +
                "exactly where UI elements are, then adjust your coordinates accordingly. " +
                "WORKFLOW: (1) Call capture_current_view to see the full UI. (2) Estimate the normalized region containing your target element. " +
                "(3) Call capture_region with that region to get a zoomed-in view. (4) Inspect the cropped image to verify element positions. " +
                "(5) If needed, adjust and capture again. (6) Once confident, use inject_tap or inject_drag_path with the confirmed coordinates. " +
                "COORDINATE CONVERSION: The response includes fullWidth/fullHeight (the full screenshot dimensions) and the normalizedRegion you requested. " +
                "To convert a pixel position (px, py) within the cropped image to normalized coordinates for injection: " +
                "normalizedX = region.x + (px / windowWidth), normalizedY = region.y + (py / windowHeight), " +
                "where windowWidth/windowHeight are from capture_current_view.",

                RegionSchema,
                CaptureRegionAsync);
        }

        private async Task<string> CaptureAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<CaptureViewRequest>(argsJson)
                          ?? new CaptureViewRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                int windowWidth  = bitmap.PixelWidth;
                int windowHeight = bitmap.PixelHeight;

                bitmap = await ScreenshotAnnotator.ResizeBitmapAsync(bitmap, maxW, maxH).ConfigureAwait(false);

                var storageFile = await ScreenshotAnnotator.SaveScreenshotAsync(bitmap, "frame").ConfigureAwait(false);

                var response = new CaptureViewResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    BitmapWidth  = bitmap.PixelWidth,
                    BitmapHeight = bitmap.PixelHeight,
                    WindowWidth  = windowWidth,
                    WindowHeight = windowHeight
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        private async Task<string> CaptureRegionAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<CaptureRegionRequest>(argsJson)
                          ?? new CaptureRegionRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                double nx = Math.Max(0, Math.Min(1, req.X));
                double ny = Math.Max(0, Math.Min(1, req.Y));
                double nw = Math.Max(0, Math.Min(1 - nx, req.Width));
                double nh = Math.Max(0, Math.Min(1 - ny, req.Height));

                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                int fullW = bitmap.PixelWidth;
                int fullH = bitmap.PixelHeight;

                int px = (int)(nx * fullW);
                int py = (int)(ny * fullH);
                int pw = Math.Max(1, (int)(nw * fullW));
                int ph = Math.Max(1, (int)(nh * fullH));

                px = Math.Min(px, fullW - 1);
                py = Math.Min(py, fullH - 1);
                pw = Math.Min(pw, fullW - px);
                ph = Math.Min(ph, fullH - py);

                var cropped = ScreenshotAnnotator.CropBitmap(bitmap, px, py, pw, ph);

                int originalCropW = pw;
                int originalCropH = ph;
                cropped = await ScreenshotAnnotator.ResizeBitmapAsync(cropped, maxW, maxH).ConfigureAwait(false);
                int resizedCropW = cropped.PixelWidth;
                int resizedCropH = cropped.PixelHeight;

                double scaleX = (double)resizedCropW / originalCropW;
                double scaleY = (double)resizedCropH / originalCropH;
                int scaledFullW = (int)(fullW * scaleX);
                int scaledFullH = (int)(fullH * scaleY);

                var storageFile = await ScreenshotAnnotator.SaveScreenshotAsync(cropped, "region").ConfigureAwait(false);

                var response = new CaptureRegionResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    RegionWidth = resizedCropW,
                    RegionHeight = resizedCropH,
                    FullWidth = scaledFullW,
                    FullHeight = scaledFullH,
                    NormalizedRegion = new NormalizedRect { X = nx, Y = ny, Width = nw, Height = nh }
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        private async Task<string> ValidatePositionAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ValidatePositionRequest>(argsJson)
                          ?? new ValidatePositionRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                double nx = Math.Max(0, Math.Min(1, req.X));
                double ny = Math.Max(0, Math.Min(1, req.Y));

                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                int w = bitmap.PixelWidth;
                int h = bitmap.PixelHeight;
                int cx = (int)(nx * w);
                int cy = (int)(ny * h);

                var annotated = ScreenshotAnnotator.DrawCrosshair(bitmap, cx, cy);
                annotated = await ScreenshotAnnotator.ResizeBitmapAsync(annotated, maxW, maxH).ConfigureAwait(false);

                var storageFile = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, "validate").ConfigureAwait(false);

                var response = new ValidatePositionResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    Width = annotated.PixelWidth,
                    Height = annotated.PixelHeight,
                    MarkerX = nx,
                    MarkerY = ny
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }
    }
}

