#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectRotateRequest
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
        [JsonPropertyName("distance")]
#endif
        [JsonProperty("distance")]
        public double Distance { get; set; } = 0.2;

#if !WINDOWS_UWP
        [JsonPropertyName("startAngle")]
#endif
        [JsonProperty("startAngle")]
        public double StartAngle { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("endAngle")]
#endif
        [JsonProperty("endAngle")]
        public double EndAngle { get; set; } = 90;

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
