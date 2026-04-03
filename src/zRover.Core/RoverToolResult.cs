namespace zRover.Core
{
    /// <summary>
    /// The structured return value for all tool handlers. Replaces the bare
    /// <c>string</c> (JSON) used previously and adds an optional inline image payload.
    ///
    /// At each MCP boundary this maps 1:1 to a <c>CallToolResult</c> with one
    /// <c>TextContentBlock</c> and, when an image is present, one
    /// <c>ImageContentBlock</c> — enabling MCP-native image delivery without any
    /// filesystem path sharing between machines.
    /// </summary>
    public readonly struct RoverToolResult
    {
        /// <summary>JSON metadata string (the text payload of the tool result).</summary>
        public string Text { get; }

        /// <summary>Raw PNG bytes. <c>null</c> for non-image tools.</summary>
        public byte[]? ImageBytes { get; }

        /// <summary><c>"image/png"</c> when <see cref="ImageBytes"/> is set.</summary>
        public string? ImageMimeType { get; }

        /// <summary>True when there is a non-empty image to send.</summary>
        public bool HasImage => ImageBytes != null && ImageBytes.Length > 0;

        private RoverToolResult(string text, byte[]? imageBytes, string? imageMimeType)
        {
            Text          = text;
            ImageBytes    = imageBytes;
            ImageMimeType = imageMimeType;
        }

        /// <summary>Creates a text-only result.</summary>
        public static RoverToolResult FromText(string text) =>
            new RoverToolResult(text, null, null);

        /// <summary>Creates a result that carries both JSON metadata and a PNG image.</summary>
        public static RoverToolResult WithImage(string text, byte[] imageBytes,
            string mimeType = "image/png") =>
            new RoverToolResult(text, imageBytes, mimeType);
    }
}
