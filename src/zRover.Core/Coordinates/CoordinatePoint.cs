#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Coordinates
{
    public record CoordinatePoint
    {
#if !WINDOWS_UWP
        [JsonPropertyName("x")]
#endif
        [JsonProperty("x")]
        public double X { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("y")]
#endif
        [JsonProperty("y")]
        public double Y { get; set; }

        public CoordinatePoint() { }

        public CoordinatePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }
}

