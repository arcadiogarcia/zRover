using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectRotateResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("pointer1Start")]
#endif
        [JsonProperty("pointer1Start")]
        public CoordinatePoint? Pointer1Start { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("pointer1End")]
#endif
        [JsonProperty("pointer1End")]
        public CoordinatePoint? Pointer1End { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("pointer2Start")]
#endif
        [JsonProperty("pointer2Start")]
        public CoordinatePoint? Pointer2Start { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("pointer2End")]
#endif
        [JsonProperty("pointer2End")]
        public CoordinatePoint? Pointer2End { get; set; }

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
