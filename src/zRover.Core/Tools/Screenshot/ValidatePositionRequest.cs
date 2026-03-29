#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    public sealed class ValidatePositionRequest
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
        [JsonPropertyName("maxWidth")]
#endif
        [JsonProperty("maxWidth")]
        public int? MaxWidth { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("maxHeight")]
#endif
        [JsonProperty("maxHeight")]
        public int? MaxHeight { get; set; }
    }
}
