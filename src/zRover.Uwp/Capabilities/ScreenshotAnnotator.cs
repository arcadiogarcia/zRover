using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace zRover.Uwp.Capabilities
{
    /// <summary>
    /// Shared screenshot capture, annotation, and image-processing utilities.
    /// Used by <see cref="ScreenshotCapability"/> and <see cref="InputInjectionCapability"/>
    /// to capture the app UI and draw visual overlays (crosshairs, drag paths, etc.).
    /// </summary>
    internal static class ScreenshotAnnotator
    {
        public const int DefaultMaxDimension = 1280;

        #region Capture

        /// <summary>
        /// Renders the current XAML UI tree to a <see cref="SoftwareBitmap"/>.
        /// Must be called from the UI thread.
        /// </summary>
        public static async Task<SoftwareBitmap> CaptureUiAsBitmapAsync()
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Window.Current.Content);
            var pixels = await rtb.GetPixelsAsync();
            return SoftwareBitmap.CreateCopyFromBuffer(
                pixels,
                BitmapPixelFormat.Bgra8,
                rtb.PixelWidth,
                rtb.PixelHeight,
                BitmapAlphaMode.Premultiplied);
        }

        #endregion

        #region Annotation — Crosshair / Tap marker

        /// <summary>
        /// Draws a high-visibility crosshair at the given pixel coordinates.
        /// Uses a thick black outline with a bright cyan center so the marker
        /// is visible on any background color.
        /// </summary>
        public static SoftwareBitmap DrawCrosshair(SoftwareBitmap source, int cx, int cy)
        {
            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            var pixels = new byte[4 * w * h];
            bgra.CopyToBuffer(pixels.AsBuffer());

            const int armLength = 30;
            const int outerThick = 5;
            const int innerThick = 2;
            const int circleRadius = 10;

            byte[] black = { 0, 0, 0, 255 };
            byte[] cyan = { 255, 255, 0, 255 };

            void SetPixel(int px, int py, byte[] color)
            {
                if (px < 0 || px >= w || py < 0 || py >= h) return;
                int idx = (py * w + px) * 4;
                pixels[idx] = color[0];
                pixels[idx + 1] = color[1];
                pixels[idx + 2] = color[2];
                pixels[idx + 3] = color[3];
            }

            void FillRect(int rx, int ry, int rw, int rh, byte[] color)
            {
                for (int dy = 0; dy < rh; dy++)
                    for (int dx = 0; dx < rw; dx++)
                        SetPixel(rx + dx, ry + dy, color);
            }

            // Horizontal arm
            FillRect(cx - armLength, cy - outerThick, armLength * 2 + 1, outerThick * 2 + 1, black);
            FillRect(cx - armLength, cy - innerThick, armLength * 2 + 1, innerThick * 2 + 1, cyan);

            // Vertical arm
            FillRect(cx - outerThick, cy - armLength, outerThick * 2 + 1, armLength * 2 + 1, black);
            FillRect(cx - innerThick, cy - armLength, innerThick * 2 + 1, armLength * 2 + 1, cyan);

            // Circle outline
            for (int r = circleRadius - outerThick; r <= circleRadius + outerThick; r++)
                DrawCirclePixels(r, cx, cy, black, SetPixel);
            for (int r = circleRadius - innerThick; r <= circleRadius + innerThick; r++)
                DrawCirclePixels(r, cx, cy, cyan, SetPixel);

            // Center dot (magenta)
            byte[] magenta = { 255, 0, 255, 255 };
            FillRect(cx - 2, cy - 2, 5, 5, magenta);

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        #endregion

        #region Annotation — Drag path

        /// <summary>
        /// Draws a drag path visualization on the bitmap. The path is rendered as
        /// a thick line with black outline and cyan fill. The start point gets a
        /// green circle, the end point gets a red diamond/arrowhead, and intermediate
        /// waypoints get small yellow dots.
        /// </summary>
        /// <param name="source">Source bitmap to annotate.</param>
        /// <param name="pixelPoints">Path points in bitmap pixel coordinates.</param>
        public static SoftwareBitmap DrawDragPath(SoftwareBitmap source, List<(int x, int y)> pixelPoints)
        {
            if (pixelPoints == null || pixelPoints.Count < 2) return source;

            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            var pixels = new byte[4 * w * h];
            bgra.CopyToBuffer(pixels.AsBuffer());

            byte[] black = { 0, 0, 0, 255 };
            byte[] cyan = { 255, 255, 0, 255 };
            byte[] green = { 0, 200, 0, 255 };   // B=0,G=200,R=0 → green
            byte[] red = { 0, 0, 255, 255 };     // B=0,G=0,R=255 → red

            void SetPixel(int px, int py, byte[] color)
            {
                if (px < 0 || px >= w || py < 0 || py >= h) return;
                int idx = (py * w + px) * 4;
                pixels[idx] = color[0];
                pixels[idx + 1] = color[1];
                pixels[idx + 2] = color[2];
                pixels[idx + 3] = color[3];
            }

            void DrawThickLine(int x0, int y0, int x1, int y1, int thickness, byte[] color)
            {
                // Bresenham with thickness
                int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;

                int half = thickness / 2;
                while (true)
                {
                    for (int tx = -half; tx <= half; tx++)
                        for (int ty = -half; ty <= half; ty++)
                            SetPixel(x0 + tx, y0 + ty, color);

                    if (x0 == x1 && y0 == y1) break;
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; x0 += sx; }
                    if (e2 <= dx) { err += dx; y0 += sy; }
                }
            }

            void FillCircle(int cx, int cy, int radius, byte[] color)
            {
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                        if (dx * dx + dy * dy <= radius * radius)
                            SetPixel(cx + dx, cy + dy, color);
            }

            void FillDiamond(int cx, int cy, int size, byte[] color)
            {
                for (int dy = -size; dy <= size; dy++)
                    for (int dx = -size; dx <= size; dx++)
                        if (Math.Abs(dx) + Math.Abs(dy) <= size)
                            SetPixel(cx + dx, cy + dy, color);
            }

            // Draw path lines — black outline then cyan center
            for (int i = 0; i < pixelPoints.Count - 1; i++)
            {
                var (x0, y0) = pixelPoints[i];
                var (x1, y1) = pixelPoints[i + 1];
                DrawThickLine(x0, y0, x1, y1, 7, black);
            }
            for (int i = 0; i < pixelPoints.Count - 1; i++)
            {
                var (x0, y0) = pixelPoints[i];
                var (x1, y1) = pixelPoints[i + 1];
                DrawThickLine(x0, y0, x1, y1, 3, cyan);
            }

            // Intermediate waypoints (small yellow dots)
            byte[] yellow = { 0, 255, 255, 255 }; // B=0,G=255,R=255 → yellow
            for (int i = 1; i < pixelPoints.Count - 1; i++)
            {
                var (px, py) = pixelPoints[i];
                FillCircle(px, py, 5, black);
                FillCircle(px, py, 3, yellow);
            }

            // Start point: green circle with black outline
            var start = pixelPoints[0];
            FillCircle(start.x, start.y, 12, black);
            FillCircle(start.x, start.y, 9, green);

            // End point: red diamond with black outline
            var end = pixelPoints[pixelPoints.Count - 1];
            FillDiamond(end.x, end.y, 14, black);
            FillDiamond(end.x, end.y, 11, red);

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        #endregion

        #region Annotation — Multi-pointer paths

        /// <summary>
        /// Draws multiple pointer paths on the bitmap, each in a distinct color.
        /// Used for multi-touch, pinch, and rotate preview annotations.
        /// Each path gets a colored line, green-tinted start circle, and red-tinted end diamond.
        /// </summary>
        public static SoftwareBitmap DrawMultiPointerPaths(SoftwareBitmap source, List<List<(int x, int y)>> pointerPaths)
        {
            if (pointerPaths == null || pointerPaths.Count == 0) return source;

            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            var pixels = new byte[4 * w * h];
            bgra.CopyToBuffer(pixels.AsBuffer());

            byte[] black = { 0, 0, 0, 255 };

            // Per-pointer color palette (BGRA premultiplied)
            byte[][] pathColors = {
                new byte[]{ 255, 255, 0, 255 },   // cyan
                new byte[]{ 255, 0, 255, 255 },   // magenta
                new byte[]{ 0, 255, 255, 255 },   // yellow
                new byte[]{ 0, 255, 0, 255 },     // green
                new byte[]{ 0, 165, 255, 255 },   // orange
            };
            byte[][] startColors = {
                new byte[]{ 0, 200, 0, 255 },     // green
                new byte[]{ 200, 0, 0, 255 },     // blue
                new byte[]{ 0, 200, 200, 255 },   // dark yellow
                new byte[]{ 0, 128, 0, 255 },     // dark green
                new byte[]{ 128, 128, 0, 255 },   // teal
            };
            byte[][] endColors = {
                new byte[]{ 0, 0, 255, 255 },     // red
                new byte[]{ 0, 128, 255, 255 },   // orange
                new byte[]{ 0, 0, 200, 255 },     // dark red
                new byte[]{ 128, 0, 128, 255 },   // purple
                new byte[]{ 0, 0, 128, 255 },     // dark red
            };

            void SetPixel(int px, int py, byte[] color)
            {
                if (px < 0 || px >= w || py < 0 || py >= h) return;
                int idx = (py * w + px) * 4;
                pixels[idx] = color[0];
                pixels[idx + 1] = color[1];
                pixels[idx + 2] = color[2];
                pixels[idx + 3] = color[3];
            }

            void DrawThickLine(int x0, int y0, int x1, int y1, int thickness, byte[] color)
            {
                int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;
                int half = thickness / 2;
                while (true)
                {
                    for (int tx = -half; tx <= half; tx++)
                        for (int ty = -half; ty <= half; ty++)
                            SetPixel(x0 + tx, y0 + ty, color);
                    if (x0 == x1 && y0 == y1) break;
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; x0 += sx; }
                    if (e2 <= dx) { err += dx; y0 += sy; }
                }
            }

            void FillCircle(int cx, int cy, int radius, byte[] color)
            {
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                        if (dx * dx + dy * dy <= radius * radius)
                            SetPixel(cx + dx, cy + dy, color);
            }

            void FillDiamond(int cx, int cy, int size, byte[] color)
            {
                for (int dy = -size; dy <= size; dy++)
                    for (int dx = -size; dx <= size; dx++)
                        if (Math.Abs(dx) + Math.Abs(dy) <= size)
                            SetPixel(cx + dx, cy + dy, color);
            }

            for (int p = 0; p < pointerPaths.Count; p++)
            {
                var path = pointerPaths[p];
                if (path == null || path.Count < 1) continue;

                int ci = p % pathColors.Length;
                var lineColor = pathColors[ci];
                var startColor = startColors[ci];
                var endColor = endColors[ci];

                // Draw path lines — black outline then colored center
                if (path.Count >= 2)
                {
                    for (int i = 0; i < path.Count - 1; i++)
                        DrawThickLine(path[i].x, path[i].y, path[i + 1].x, path[i + 1].y, 7, black);
                    for (int i = 0; i < path.Count - 1; i++)
                        DrawThickLine(path[i].x, path[i].y, path[i + 1].x, path[i + 1].y, 3, lineColor);
                }

                // Start circle
                var start = path[0];
                FillCircle(start.x, start.y, 12, black);
                FillCircle(start.x, start.y, 9, startColor);

                // End diamond
                var end = path[path.Count - 1];
                FillDiamond(end.x, end.y, 14, black);
                FillDiamond(end.x, end.y, 11, endColor);
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        #endregion

        #region Image processing

        /// <summary>
        /// Scales a bitmap down proportionally so that neither dimension exceeds the limits.
        /// Returns the original bitmap unchanged if it already fits.
        /// </summary>
        public static async Task<SoftwareBitmap> ResizeBitmapAsync(SoftwareBitmap source, int maxWidth, int maxHeight)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;

            if (w <= maxWidth && h <= maxHeight)
                return source;

            double scale = Math.Min((double)maxWidth / w, (double)maxHeight / h);
            uint newW = (uint)Math.Max(1, (int)(w * scale));
            uint newH = (uint)Math.Max(1, (int)(h * scale));

            using var memStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
            encoder.SetSoftwareBitmap(
                SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
            await encoder.FlushAsync();

            memStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(memStream);
            var transform = new BitmapTransform
            {
                ScaledWidth = newW,
                ScaledHeight = newH,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var pixels = pixelData.DetachPixelData();

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, (int)newW, (int)newH, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        /// <summary>
        /// Crops a rectangular region from a bitmap.
        /// </summary>
        public static SoftwareBitmap CropBitmap(SoftwareBitmap source, int x, int y, int width, int height)
        {
            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var srcBytes = new byte[4 * bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyToBuffer(srcBytes.AsBuffer());

            var dstBytes = new byte[4 * width * height];
            int srcStride = 4 * bgra.PixelWidth;
            int dstStride = 4 * width;

            for (int row = 0; row < height; row++)
            {
                Array.Copy(srcBytes, (y + row) * srcStride + x * 4, dstBytes, row * dstStride, dstStride);
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(dstBytes.AsBuffer());
            return result;
        }

        #endregion

        #region Storage

        /// <summary>
        /// Saves a bitmap as a PNG file and returns the <see cref="StorageFile"/>.
        /// </summary>
        public static async Task<StorageFile> SaveScreenshotAsync(SoftwareBitmap bitmap, string filePrefix)
        {
            var folder = await EnsureFolderAsync("debug-artifacts\\screenshots").ConfigureAwait(false);
            var fileName = $"{filePrefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            var storageFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            using var fileStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream);
            var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            encoder.SetSoftwareBitmap(bgra);
            await encoder.FlushAsync();

            return storageFile;
        }

        /// <summary>
        /// Ensures a subfolder path exists under LocalFolder and returns it.
        /// </summary>
        public static async Task<StorageFolder> EnsureFolderAsync(string relativePath)
        {
            var root = ApplicationData.Current.LocalFolder;
            StorageFolder folder = root;
            foreach (var part in relativePath.Split('\\', '/'))
                folder = await folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
            return folder;
        }

        #endregion

        #region Circle helper

        private static void DrawCirclePixels(int radius, int cx, int cy, byte[] color,
            Action<int, int, byte[]> setPixel)
        {
            if (radius <= 0) return;
            int x = radius, y = 0;
            int d = 1 - radius;
            while (x >= y)
            {
                setPixel(cx + x, cy + y, color);
                setPixel(cx - x, cy + y, color);
                setPixel(cx + x, cy - y, color);
                setPixel(cx - x, cy - y, color);
                setPixel(cx + y, cy + x, color);
                setPixel(cx - y, cy + x, color);
                setPixel(cx + y, cy - x, color);
                setPixel(cx - y, cy - x, color);
                y++;
                if (d <= 0)
                    d += 2 * y + 1;
                else
                {
                    x--;
                    d += 2 * (y - x) + 1;
                }
            }
        }

        #endregion
    }
}
