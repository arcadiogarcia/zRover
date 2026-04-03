using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    public sealed class CaptureRegionResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("regionWidth")]
#endif
        [JsonProperty("regionWidth")]
        public int RegionWidth { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("regionHeight")]
#endif
        [JsonProperty("regionHeight")]
        public int RegionHeight { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("fullWidth")]
#endif
        [JsonProperty("fullWidth")]
        public int FullWidth { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("fullHeight")]
#endif
        [JsonProperty("fullHeight")]
        public int FullHeight { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("normalizedRegion")]
#endif
        [JsonProperty("normalizedRegion")]
        public NormalizedRect? NormalizedRegion { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }

    public sealed class NormalizedRect
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
        [JsonPropertyName("width")]
#endif
        [JsonProperty("width")]
        public double Width { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("height")]
#endif
        [JsonProperty("height")]
        public double Height { get; set; }
    }
}
