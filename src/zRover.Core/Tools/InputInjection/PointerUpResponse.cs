using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class PointerUpResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("pointerId")]
#endif
        [JsonProperty("pointerId")]
        public int PointerId { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("resolvedCoordinates")]
#endif
        [JsonProperty("resolvedCoordinates")]
        public CoordinatePoint? ResolvedCoordinates { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("activePointers")]
#endif
        [JsonProperty("activePointers")]
        public int ActivePointers { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("error")]
#endif
        [JsonProperty("error")]
        public string? Error { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
