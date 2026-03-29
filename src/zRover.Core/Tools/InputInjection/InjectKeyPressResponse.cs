using System;
using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectKeyPressResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("key")]
#endif
        [JsonProperty("key")]
        public string Key { get; set; } = "";

#if !WINDOWS_UWP
        [JsonPropertyName("modifiers")]
#endif
        [JsonProperty("modifiers")]
        public List<string> Modifiers { get; set; } = new List<string>();

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
