using System.Collections.Generic;
using System.Threading.Tasks;

namespace zRover.Core
{
    /// <summary>
    /// Describes a tool discovered from the UWP host.
    /// </summary>
    public sealed class DiscoveredTool
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string InputSchema { get; set; } = "{}";
    }

    /// <summary>
    /// Abstracts the IPC channel between the FullTrust MCP server and the UWP
    /// tool host.  The real implementation wraps <c>AppServiceConnection</c>;
    /// tests can supply an in-memory implementation that reproduces the same
    /// JSON-over-ValueSet serialization without a running UWP app.
    /// </summary>
    public interface IToolBackend
    {
        /// <summary>
        /// Discovers all tools the UWP host has registered.
        /// Equivalent to the "list_tools" AppService command.
        /// </summary>
        Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync();

        /// <summary>
        /// Invokes a tool by name, passing JSON-serialized arguments and
        /// returning a JSON result string.
        /// Equivalent to the "invoke_tool" AppService command.
        /// </summary>
        Task<string> InvokeToolAsync(string toolName, string argumentsJson);
    }
}
