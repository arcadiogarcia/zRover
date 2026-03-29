using System.Threading.Tasks;

namespace zRover.Core
{
    public interface IDebugCapability
    {
        string Name { get; }

        Task StartAsync(DebugHostContext context);
        Task StopAsync();

        void RegisterTools(IMcpToolRegistry registry);
    }
}
