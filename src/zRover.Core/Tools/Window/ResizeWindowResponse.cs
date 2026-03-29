#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Window
{
    public sealed class ResizeWindowResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("actualWidth")]
#endif
        [JsonProperty("actualWidth")]
        public int ActualWidth { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("actualHeight")]
#endif
        [JsonProperty("actualHeight")]
        public int ActualHeight { get; set; }
    }
}
