#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    public sealed class CaptureViewRequest
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("format")]
#endif
        [JsonProperty("format")]
        public string Format { get; set; } = "png";

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



