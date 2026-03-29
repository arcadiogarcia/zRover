using zRover.Core.Coordinates;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.Graphics.Display;

namespace zRover.Uwp.Coordinates
{
    internal sealed class UwpCoordinateResolver : ICoordinateResolver
    {
        /// <summary>
        /// Resolves caller-supplied coordinates into DIP screen coords suitable for
        /// InjectedInputPoint (which is then multiplied by RawPixelsPerViewPixel by callers).
        ///
        /// Both spaces are window-content-relative — origin is the top-left corner of
        /// Window.Current.Content, matching the reference used by get_ui_tree and
        /// capture_current_view.
        ///
        /// Normalized (default): 0..1 maps to the content ActualWidth/ActualHeight.
        /// Pixels: render-pixel coordinates (same unit as bitmapWidth/bitmapHeight from
        ///   capture_current_view). Internally divided by dpiScale to get DIPs.
        /// </summary>
        public CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space)
        {
            var windowContent = Window.Current?.Content as FrameworkElement;

            // Compute content-relative DIP coords
            double dipX, dipY;
            if (space == CoordinateSpace.Pixels)
            {
                // Render pixels → DIPs (undo the dpiScale that RenderTargetBitmap applies)
                double dpiScale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
                dipX = dpiScale > 0 ? point.X / dpiScale : point.X;
                dipY = dpiScale > 0 ? point.Y / dpiScale : point.Y;
            }
            else // Normalized
            {
                double w = windowContent?.ActualWidth  ?? 0;
                double h = windowContent?.ActualHeight ?? 0;

                // When the app is backgrounded or not yet laid out, ActualWidth/Height can
                // be 0 even on the UI thread.  CoreWindow.Bounds is always valid and equals
                // the content size in a standard UWP app (no custom title bar).
                if (w == 0 || h == 0)
                {
                    try
                    {
                        var cw = CoreWindow.GetForCurrentThread();
                        if (cw != null)
                        {
                            if (w == 0) w = cw.Bounds.Width;
                            if (h == 0) h = cw.Bounds.Height;
                        }
                    }
                    catch { /* best-effort */ }
                }

                dipX = point.X * w;
                dipY = point.Y * h;
            }

            // Add the window's screen-relative origin so the final coordinate is in
            // screen DIP space (what InputInjector absolute mouse/touch APIs expect).
            //
            // TransformToVisual(null) in UWP returns coordinates relative to the
            // XamlRoot — i.e. the window's own client-area origin, which is always
            // (0,0) regardless of where the window sits on screen.  CoreWindow.Bounds
            // gives the window's actual position on screen in DIPs.
            try
            {
                var coreWindow = CoreWindow.GetForCurrentThread();
                if (coreWindow != null)
                {
                    var bounds = coreWindow.Bounds;
                    return new CoordinatePoint(dipX + bounds.X, dipY + bounds.Y);
                }
            }
            catch { /* best-effort */ }

            // Fallback (e.g. called off the UI thread): TransformToVisual will at least
            // pick up any intra-tree offset, even if the window origin is missing.
            if (windowContent != null)
            {
                try
                {
                    var transform = windowContent.TransformToVisual(null);
                    var origin = transform.TransformPoint(new Point(0, 0));
                    return new CoordinatePoint(dipX + origin.X, dipY + origin.Y);
                }
                catch { /* disconnected element — return content-relative coords */ }
            }

            return new CoordinatePoint(dipX, dipY);
        }
    }
}
