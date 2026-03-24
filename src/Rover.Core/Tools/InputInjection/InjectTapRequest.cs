#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace Rover.Core.Tools.InputInjection
{
    public class InjectTapRequest
    {
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
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; } = false;
    }
}

