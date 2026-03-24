using Windows.UI.ViewManagement;
using Rover.Core.Coordinates;

namespace Rover.Uwp.Coordinates
{
    internal sealed class UwpCoordinateResolver : ICoordinateResolver
    {
        public CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space)
        {
            switch (space)
            {
                case CoordinateSpace.Absolute:
                    return point;

                case CoordinateSpace.Client:
                    // Client-relative pixels: offset by window position.
                    // For a full-screen UWP app the window origin is (0,0), so this
                    // is effectively the same as absolute.
                    return point;

                case CoordinateSpace.Normalized:
                default:
                    // 0..1 → DIP screen coordinates.
                    // VisibleBounds is already in DIPs.
                    // Callers must convert to the target coordinate space:
                    //   Touch (InjectedInputPoint): multiply by RawPixelsPerViewPixel → raw screen pixels
                    //   Mouse (InjectedInputMouseInfo Absolute): map to 0-65535 normalized range
                    var bounds = ApplicationView.GetForCurrentView().VisibleBounds;

                    double absX = point.X * bounds.Width  + bounds.X;
                    double absY = point.Y * bounds.Height + bounds.Y;
                    return new CoordinatePoint(absX, absY);
            }
        }
    }
}
