using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectMouseScrollResponse
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
        [JsonPropertyName("deltaY")]
#endif
        [JsonProperty("deltaY")]
        public int DeltaY { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("deltaX")]
#endif
        [JsonProperty("deltaX")]
        public int DeltaX { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("previewScreenshotPath")]
#endif
        [JsonProperty("previewScreenshotPath")]
        public string? PreviewScreenshotPath { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");

#if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }
    }
}
