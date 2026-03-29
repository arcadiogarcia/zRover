namespace zRover.Core.Coordinates
{
    public enum CoordinateSpace
    {
        /// <summary>0..1 range relative to the app window content area.</summary>
        Normalized,

        /// <summary>
        /// Render pixels relative to the app window content area.
        /// 1 pixel = the same unit as bitmapWidth/bitmapHeight returned by capture_current_view.
        /// </summary>
        Pixels
    }
}
