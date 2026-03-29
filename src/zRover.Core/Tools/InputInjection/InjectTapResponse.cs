using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectTapResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("resolvedCoordinates")]
#endif
        [JsonProperty("resolvedCoordinates")]
        public CoordinatePoint? ResolvedCoordinates { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("device")]
#endif
        [JsonProperty("device")]
        public string Device { get; set; } = "touch";

        #if !WINDOWS_UWP
        [JsonPropertyName("button")]
#endif
        [JsonProperty("button")]
        public string Button { get; set; } = "left";

        #if !WINDOWS_UWP
        [JsonPropertyName("clickCount")]
#endif
        [JsonProperty("clickCount")]
        public int ClickCount { get; set; } = 1;

        #if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");

        #if !WINDOWS_UWP
        [JsonPropertyName("previewScreenshotPath")]
#endif
        [JsonProperty("previewScreenshotPath")]
        public string? PreviewScreenshotPath { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }
    }
}



