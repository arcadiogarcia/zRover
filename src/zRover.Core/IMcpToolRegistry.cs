using System;
using System.Threading.Tasks;

namespace zRover.Core
{
    public interface IMcpToolRegistry
    {
        /// <summary>Register a plain text-returning tool (non-image capabilities).</summary>
        void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<string>> handler);

        /// <summary>Register a rich tool whose result may include an inline image.</summary>
        void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<RoverToolResult>> handler);

        /// <summary>
        /// Unregister a tool by name. Returns <c>true</c> if a tool was found
        /// and removed; <c>false</c> if no tool with that name was registered.
        /// Implementations are expected to fire a <c>tools/list_changed</c>
        /// notification on success.
        /// </summary>
        bool TryUnregisterTool(string name);

        /// <summary>
        /// Returns <c>true</c> when a tool with the given name is already
        /// registered.
        /// </summary>
        bool IsToolRegistered(string name);
    }
}
