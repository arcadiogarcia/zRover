#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Window
{
    public sealed class ResizeWindowRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("width")]
#endif
        [JsonProperty("width")]
        public int Width { get; set; } = 1024;

#if !WINDOWS_UWP
        [JsonPropertyName("height")]
#endif
        [JsonProperty("height")]
        public int Height { get; set; } = 768;
    }
}
