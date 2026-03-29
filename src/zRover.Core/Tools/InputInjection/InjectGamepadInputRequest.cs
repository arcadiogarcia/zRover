#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using System.Collections.Generic;
using Newtonsoft.Json;

namespace zRover.Core.Tools.InputInjection
{
    public class InjectGamepadInputRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("buttons")]
#endif
        [JsonProperty("buttons")]
        public List<string> Buttons { get; set; } = new List<string>();

#if !WINDOWS_UWP
        [JsonPropertyName("leftStickX")]
#endif
        [JsonProperty("leftStickX")]
        public double LeftStickX { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("leftStickY")]
#endif
        [JsonProperty("leftStickY")]
        public double LeftStickY { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("rightStickX")]
#endif
        [JsonProperty("rightStickX")]
        public double RightStickX { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("rightStickY")]
#endif
        [JsonProperty("rightStickY")]
        public double RightStickY { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("leftTrigger")]
#endif
        [JsonProperty("leftTrigger")]
        public double LeftTrigger { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("rightTrigger")]
#endif
        [JsonProperty("rightTrigger")]
        public double RightTrigger { get; set; } = 0.0;

#if !WINDOWS_UWP
        [JsonPropertyName("holdDurationMs")]
#endif
        [JsonProperty("holdDurationMs")]
        public int HoldDurationMs { get; set; } = 100;
    }
}
