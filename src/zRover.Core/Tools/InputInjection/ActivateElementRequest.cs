#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class ActivateElementRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("name")]
#endif
        [JsonProperty("name")]
        public string? Name { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("automationName")]
#endif
        [JsonProperty("automationName")]
        public string? AutomationName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("type")]
#endif
        [JsonProperty("type")]
        public string? TypeName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("parent")]
#endif
        [JsonProperty("parent")]
        public string? Parent { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("text")]
#endif
        [JsonProperty("text")]
        public string? Text { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("action")]
#endif
        [JsonProperty("action")]
        public string Action { get; set; } = "invoke";

#if !WINDOWS_UWP
        [JsonPropertyName("timeout")]
#endif
        [JsonProperty("timeout")]
        public int Timeout { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("poll")]
#endif
        [JsonProperty("poll")]
        public int Poll { get; set; } = 500;
    }
}
