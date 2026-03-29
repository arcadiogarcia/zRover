#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.WaitFor
{
    public sealed class WaitForResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("condition")]
#endif
        [JsonProperty("condition")]
        public string Condition { get; set; } = "";

#if !WINDOWS_UWP
        [JsonPropertyName("elapsedMs")]
#endif
        [JsonProperty("elapsedMs")]
        public int ElapsedMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("reason")]
#endif
        [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)]
        public string? Reason { get; set; }
    }
}
