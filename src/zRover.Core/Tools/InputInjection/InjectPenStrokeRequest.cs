#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using System.Collections.Generic;
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectPenStrokeRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("points")]
#endif
        [JsonProperty("points")]
        public List<PenStrokePoint> Points { get; set; } = new List<PenStrokePoint>();

#if !WINDOWS_UWP
        [JsonPropertyName("coordinateSpace")]
#endif
        [JsonProperty("coordinateSpace")]
        public string CoordinateSpace { get; set; } = "normalized";

#if !WINDOWS_UWP
        [JsonPropertyName("pressure")]
#endif
        [JsonProperty("pressure")]
        public double Pressure { get; set; } = 0.5;

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

#if !WINDOWS_UWP
        [JsonPropertyName("durationMs")]
#endif
        [JsonProperty("durationMs")]
        public int DurationMs { get; set; } = 400;

#if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; } = false;
    }
}
