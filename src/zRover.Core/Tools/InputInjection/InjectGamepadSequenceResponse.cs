using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectGamepadSequenceResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("frameCount")]
#endif
        [JsonProperty("frameCount")]
        public int FrameCount { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("totalDurationMs")]
#endif
        [JsonProperty("totalDurationMs")]
        public int TotalDurationMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
