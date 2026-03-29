using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Tools.Screenshot;

namespace zRover.Core.Tools.UiTree
{
    public sealed class UiTreeNode
    {
#if !WINDOWS_UWP
        [JsonPropertyName("type")]
#endif
        [JsonProperty("type")]
        public string Type { get; set; } = "";

#if !WINDOWS_UWP
        [JsonPropertyName("name")]
#endif
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("automationName")]
#endif
        [JsonProperty("automationName", NullValueHandling = NullValueHandling.Ignore)]
        public string? AutomationName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("text")]
#endif
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
        public string? Text { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("bounds")]
#endif
        [JsonProperty("bounds")]
        public NormalizedRect Bounds { get; set; } = new NormalizedRect();

#if !WINDOWS_UWP
        [JsonPropertyName("isVisible")]
#endif
        [JsonProperty("isVisible")]
        public bool IsVisible { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("isEnabled")]
#endif
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("children")]
#endif
        [JsonProperty("children")]
        public List<UiTreeNode> Children { get; set; } = new List<UiTreeNode>();
    }
}
