using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace zRover.Retriever.Server;

[McpServerResourceType]
public class IntegrationGuideResource
{
    [McpServerResource(
        UriTemplate = "docs://zrover/integration-guide",
        Name = "zRover Integration Guide",
        MimeType = "text/markdown")]
    [Description("Complete guide for integrating zRover MCP tools into UWP and WinUI 3 apps. Covers NuGet setup, manifest configuration, App.xaml.cs wiring, MCP client connection, and all available tools.")]
    public static string GetIntegrationGuide()
    {
        var assembly = typeof(IntegrationGuideResource).Assembly;
        using var stream = assembly.GetManifestResourceStream("IntegrationGuide.md");
        if (stream is null)
            return "# Integration guide not available\n\nThe embedded resource could not be loaded.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
