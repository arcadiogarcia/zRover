#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectPinchRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("centerX")]
#endif
        [JsonProperty("centerX")]
        public double CenterX { get; set; } = 0.5;

#if !WINDOWS_UWP
        [JsonPropertyName("centerY")]
#endif
        [JsonProperty("centerY")]
        public double CenterY { get; set; } = 0.5;

#if !WINDOWS_UWP
        [JsonPropertyName("startDistance")]
#endif
        [JsonProperty("startDistance")]
        public double StartDistance { get; set; } = 0.3;

#if !WINDOWS_UWP
        [JsonPropertyName("endDistance")]
#endif
        [JsonProperty("endDistance")]
        public double EndDistance { get; set; } = 0.1;

#if !WINDOWS_UWP
        [JsonPropertyName("angle")]
#endif
        [JsonProperty("angle")]
        public double Angle { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("durationMs")]
#endif
        [JsonProperty("durationMs")]
        public int DurationMs { get; set; } = 400;

#if !WINDOWS_UWP
        [JsonPropertyName("coordinateSpace")]
#endif
        [JsonProperty("coordinateSpace")]
        public string CoordinateSpace { get; set; } = "normalized";

#if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; } = false;
    }
}
