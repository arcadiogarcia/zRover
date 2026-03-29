#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using System.Collections.Generic;
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectKeyPressRequest
    {
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
        [JsonPropertyName("holdDurationMs")]
#endif
        [JsonProperty("holdDurationMs")]
        public int HoldDurationMs { get; set; } = 0;
    }
}
