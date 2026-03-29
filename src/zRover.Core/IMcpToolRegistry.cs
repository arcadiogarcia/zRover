using System;
using System.Threading.Tasks;

namespace zRover.Core
{
    public interface IMcpToolRegistry
    {
        void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<string>> handler);
    }
}
