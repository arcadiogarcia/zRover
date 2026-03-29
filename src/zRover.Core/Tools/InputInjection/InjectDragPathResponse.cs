using System;
using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectDragPathResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("pointCount")]
#endif
        [JsonProperty("pointCount")]
        public int PointCount { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("durationMs")]
#endif
        [JsonProperty("durationMs")]
        public int DurationMs { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("resolvedPath")]
#endif
        [JsonProperty("resolvedPath")]
        public List<CoordinatePoint> ResolvedPath { get; set; } = new List<CoordinatePoint>();

        #if !WINDOWS_UWP
        [JsonPropertyName("device")]
#endif
        [JsonProperty("device")]
        public string Device { get; set; } = "touch";

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



