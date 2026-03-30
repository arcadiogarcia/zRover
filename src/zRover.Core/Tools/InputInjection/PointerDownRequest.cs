#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class PointerDownRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("pointerId")]
#endif
        [JsonProperty("pointerId")]
        public int PointerId { get; set; } = 1;

#if !WINDOWS_UWP
        [JsonPropertyName("x")]
#endif
        [JsonProperty("x")]
        public double X { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("y")]
#endif
        [JsonProperty("y")]
        public double Y { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("coordinateSpace")]
#endif
        [JsonProperty("coordinateSpace")]
        public string CoordinateSpace { get; set; } = "normalized";

#if !WINDOWS_UWP
        [JsonPropertyName("device")]
#endif
        [JsonProperty("device")]
        public string Device { get; set; } = "touch";

#if !WINDOWS_UWP
        [JsonPropertyName("pressure")]
#endif
        [JsonProperty("pressure")]
        public double Pressure { get; set; } = 1.0;

        // Touch-specific properties

#if !WINDOWS_UWP
        [JsonPropertyName("orientation")]
#endif
        [JsonProperty("orientation")]
        public int Orientation { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("contactWidth")]
#endif
        [JsonProperty("contactWidth")]
        public int ContactWidth { get; set; } = 4;

#if !WINDOWS_UWP
        [JsonPropertyName("contactHeight")]
#endif
        [JsonProperty("contactHeight")]
        public int ContactHeight { get; set; } = 4;

        // Pen-specific properties

#if !WINDOWS_UWP
        [JsonPropertyName("tiltX")]
#endif
        [JsonProperty("tiltX")]
        public int TiltX { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("tiltY")]
#endif
        [JsonProperty("tiltY")]
        public int TiltY { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("rotation")]
#endif
        [JsonProperty("rotation")]
        public double Rotation { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("barrel")]
#endif
        [JsonProperty("barrel")]
        public bool Barrel { get; set; } = false;

#if !WINDOWS_UWP
        [JsonPropertyName("eraser")]
#endif
        [JsonProperty("eraser")]
        public bool Eraser { get; set; } = false;
    }
}
