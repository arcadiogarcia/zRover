using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    public sealed class CaptureViewResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Width of the returned bitmap file in pixels (may be less than windowWidth
        /// if maxWidth rescaling was applied).
        /// </summary>
        #if !WINDOWS_UWP
        [JsonPropertyName("bitmapWidth")]
#endif
        [JsonProperty("bitmapWidth")]
        public int BitmapWidth { get; set; }

        /// <summary>
        /// Height of the returned bitmap file in pixels (may be less than windowHeight
        /// if maxHeight rescaling was applied).
        /// </summary>
        #if !WINDOWS_UWP
        [JsonPropertyName("bitmapHeight")]
#endif
        [JsonProperty("bitmapHeight")]
        public int BitmapHeight { get; set; }

        /// <summary>
        /// Render-pixel width of the window content before any maxWidth rescaling.
        /// Use this as the coordinate space width for coordinateSpace="pixels" injection.
        /// Equals bitmapWidth when no rescaling occurred.
        /// </summary>
        #if !WINDOWS_UWP
        [JsonPropertyName("windowWidth")]
#endif
        [JsonProperty("windowWidth")]
        public int WindowWidth { get; set; }

        /// <summary>
        /// Render-pixel height of the window content before any maxHeight rescaling.
        /// Use this as the coordinate space height for coordinateSpace="pixels" injection.
        /// Equals bitmapHeight when no rescaling occurred.
        /// </summary>
        #if !WINDOWS_UWP
        [JsonPropertyName("windowHeight")]
#endif
        [JsonProperty("windowHeight")]
        public int WindowHeight { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}

