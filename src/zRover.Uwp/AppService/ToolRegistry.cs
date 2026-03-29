using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace zRover.Uwp.AppService
{
    public sealed class ToolEntry
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string InputSchema { get; set; } = "{}";
        public Func<string, Task<string>> Handler { get; set; } = _ => Task.FromResult("{}");
    }

    /// <summary>
    /// Thread-safe singleton registry for UWP tools accessible from AppService background task.
    /// Shared between the main app (which registers tools) and the AppService background task (which invokes them).
    /// </summary>
    public sealed class ToolRegistry
    {
        private static readonly Lazy<ToolRegistry> _instance = new Lazy<ToolRegistry>(() => new ToolRegistry());
        private readonly object _lock = new object();
        private readonly Dictionary<string, ToolEntry> _tools = new Dictionary<string, ToolEntry>();

        public static ToolRegistry Instance => _instance.Value;

        private ToolRegistry() { }

        public void RegisterTool(string name, string description, string inputSchema, Func<string, Task<string>> handler)
        {
            lock (_lock)
            {
                _tools[name] = new ToolEntry { Name = name, Description = description, InputSchema = inputSchema, Handler = handler };
                System.Diagnostics.Debug.WriteLine($"[ToolRegistry] Registered tool: {name}");
            }
        }

        public bool TryGetTool(string name, out Func<string, Task<string>> handler)
        {
            lock (_lock)
            {
                if (_tools.TryGetValue(name, out var entry))
                {
                    handler = entry.Handler;
                    return true;
                }
                handler = null!;
                return false;
            }
        }

        public List<ToolEntry> GetAllTools()
        {
            lock (_lock)
            {
                return _tools.Values.ToList();
            }
        }

        public IEnumerable<string> GetToolNames()
        {
            lock (_lock)
            {
                return _tools.Keys.ToList();
            }
        }
    }
}
