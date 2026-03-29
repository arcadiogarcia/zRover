#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using System.Collections.Generic;
using Newtonsoft.Json;
using zRover.Core.Coordinates;

namespace zRover.Core.Tools.InputInjection
{
    public class TouchPointerPath
    {
#if !WINDOWS_UWP
        [JsonPropertyName("id")]
#endif
        [JsonProperty("id")]
        public int Id { get; set; } = 1;

#if !WINDOWS_UWP
        [JsonPropertyName("path")]
#endif
        [JsonProperty("path")]
        public List<CoordinatePoint> Path { get; set; } = new List<CoordinatePoint>();

#if !WINDOWS_UWP
        [JsonPropertyName("pressure")]
#endif
        [JsonProperty("pressure")]
        public double Pressure { get; set; } = 1.0;

#if !WINDOWS_UWP
        [JsonPropertyName("orientation")]
#endif
        [JsonProperty("orientation")]
        public int Orientation { get; set; } = 0;

#if !WINDOWS_UWP
        [JsonPropertyName("contactWidth")]
#endif
        [JsonProperty("contactWidth")]
        public int ContactWidth { get; set; } = 4;

#if !WINDOWS_UWP
        [JsonPropertyName("contactHeight")]
#endif
        [JsonProperty("contactHeight")]
        public int ContactHeight { get; set; } = 4;
    }

    public class InjectMultiTouchRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("pointers")]
#endif
        [JsonProperty("pointers")]
        public List<TouchPointerPath> Pointers { get; set; } = new List<TouchPointerPath>();

#if !WINDOWS_UWP
        [JsonPropertyName("durationMs")]
#endif
        [JsonProperty("durationMs")]
        public int DurationMs { get; set; } = 400;

#if !WINDOWS_UWP
        [JsonPropertyName("coordinateSpace")]
#endif
        [JsonProperty("coordinateSpace")]
        public string CoordinateSpace { get; set; } = "normalized";

#if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; } = false;
    }
}
