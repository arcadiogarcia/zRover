#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.UiTree
{
    public sealed class UiTreeRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("maxDepth")]
#endif
        [JsonProperty("maxDepth")]
        public int MaxDepth { get; set; } = 32;

#if !WINDOWS_UWP
        [JsonPropertyName("visibleOnly")]
#endif
        [JsonProperty("visibleOnly")]
        public bool VisibleOnly { get; set; } = false;
    }
}
