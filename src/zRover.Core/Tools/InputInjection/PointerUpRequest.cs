#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class PointerUpRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("pointerId")]
#endif
        [JsonProperty("pointerId")]
        public int PointerId { get; set; } = 1;
    }
}
