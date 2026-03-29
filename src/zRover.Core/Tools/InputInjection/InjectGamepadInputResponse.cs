using System;
using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectGamepadInputResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("buttons")]
#endif
        [JsonProperty("buttons")]
        public List<string> Buttons { get; set; } = new List<string>();

#if !WINDOWS_UWP
        [JsonPropertyName("holdDurationMs")]
#endif
        [JsonProperty("holdDurationMs")]
        public int HoldDurationMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
