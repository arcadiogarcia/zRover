#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using System.Collections.Generic;
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectGamepadSequenceRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("frames")]
#endif
        [JsonProperty("frames")]
        public List<GamepadFrame> Frames { get; set; } = new List<GamepadFrame>();
    }
}
