using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    public sealed class ValidatePositionResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("width")]
#endif
        [JsonProperty("width")]
        public int Width { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("height")]
#endif
        [JsonProperty("height")]
        public int Height { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("markerX")]
#endif
        [JsonProperty("markerX")]
        public double MarkerX { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("markerY")]
#endif
        [JsonProperty("markerY")]
        public double MarkerY { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
