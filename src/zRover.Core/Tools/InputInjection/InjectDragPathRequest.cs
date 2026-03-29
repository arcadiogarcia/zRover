using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectDragPathRequest
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("points")]
#endif
        [JsonProperty("points")]
        public List<CoordinatePoint> Points { get; set; } = new List<CoordinatePoint>();

        #if !WINDOWS_UWP
        [JsonPropertyName("durationMs")]
#endif
        [JsonProperty("durationMs")]
        public int DurationMs { get; set; } = 300;

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
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; } = false;
    }
}



