#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.WaitFor
{
    public sealed class WaitForRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("condition")]
#endif
        [JsonProperty("condition")]
        public string Condition { get; set; } = "visual_stable";

#if !WINDOWS_UWP
        [JsonPropertyName("timeoutMs")]
#endif
        [JsonProperty("timeoutMs")]
        public int TimeoutMs { get; set; } = 5000;

#if !WINDOWS_UWP
        [JsonPropertyName("stabilityMs")]
#endif
        [JsonProperty("stabilityMs")]
        public int StabilityMs { get; set; } = 400;

#if !WINDOWS_UWP
        [JsonPropertyName("intervalMs")]
#endif
        [JsonProperty("intervalMs")]
        public int IntervalMs { get; set; } = 150;

#if !WINDOWS_UWP
        [JsonPropertyName("pattern")]
#endif
        [JsonProperty("pattern", NullValueHandling = NullValueHandling.Ignore)]
        public string? Pattern { get; set; }
    }
}
