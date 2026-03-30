#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class PointerMoveRequest
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
        [JsonPropertyName("pressure")]
#endif
        [JsonProperty("pressure")]
        public double? Pressure { get; set; }

        // Pen-specific per-move overrides (optional, keep previous value if omitted)

#if !WINDOWS_UWP
        [JsonPropertyName("tiltX")]
#endif
        [JsonProperty("tiltX")]
        public int? TiltX { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("tiltY")]
#endif
        [JsonProperty("tiltY")]
        public int? TiltY { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("rotation")]
#endif
        [JsonProperty("rotation")]
        public double? Rotation { get; set; }
    }
}
