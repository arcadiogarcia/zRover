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
    }
}
