using System.Threading.Tasks;
using Rover.Core;

namespace Rover.Uwp.Capabilities
{
    /// <summary>
    /// Capability that exposes an <see cref="IActionableApp"/> implementation as two fixed MCP tools:
    /// <c>list_actions</c> and <c>dispatch_action</c>.
    /// The tool definitions never change; the dynamic action catalog lives entirely inside the
    /// <c>list_actions</c> response payload.
    /// All JSON protocol logic lives in <see cref="AppActionMcpHandlers"/> (Rover.Core).
    /// </summary>
    public sealed class AppActionCapability : IDebugCapability
    {
        private const string ListActionsInputSchema = @"{""type"":""object"",""properties"":{}}";

        private const string DispatchActionInputSchema = @"{
  ""type"": ""object"",
  ""required"": [""action"", ""params""],
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""The name of the action to dispatch, as returned by list_actions.""
    },
    ""params"": {
      ""type"": ""object"",
      ""description"": ""The parameters for the action. Pass {} for actions with no parameters. Must conform to the parameterSchema of the named action.""
    }
  }
}";

        private readonly AppActionMcpHandlers _handlers;

        public string Name => "AppActions";

        public AppActionCapability(IActionableApp app)
        {
            _handlers = new AppActionMcpHandlers(app);
        }

        public Task StartAsync(DebugHostContext context) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "list_actions",
                "Returns the set of actions that can be dispatched to the application right now. " +
                "Each action includes a JSON Schema describing its parameters, with enum constraints " +
                "reflecting the currently valid values. Re-call after each dispatch to get a fresh snapshot.",
                ListActionsInputSchema,
                _handlers.HandleListActionsAsync);

            registry.RegisterTool(
                "dispatch_action",
                "Dispatches a named action to the application with the given parameters. " +
                "The action name and params must match a descriptor returned by list_actions. " +
                "Returns success/failure with consequences or an error code.",
                DispatchActionInputSchema,
                _handlers.HandleDispatchActionAsync);
        }
    }
}
