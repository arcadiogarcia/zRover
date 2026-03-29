#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectTextRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("text")]
#endif
        [JsonProperty("text")]
        public string Text { get; set; } = "";

#if !WINDOWS_UWP
        [JsonPropertyName("delayBetweenKeysMs")]
#endif
        [JsonProperty("delayBetweenKeysMs")]
        public int DelayBetweenKeysMs { get; set; } = 30;
    }
}
