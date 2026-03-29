using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace zRover.Core
{
    /// <summary>
    /// Pure-logic JSON handlers for the App Action API protocol (list_actions / dispatch_action).
    /// Framework-agnostic: receives and returns JSON strings, delegates execution to
    /// <see cref="IActionableApp"/>. No MCP or UWP dependencies.
    /// </summary>
    public sealed class AppActionMcpHandlers
    {
        private readonly IActionableApp _app;

        public AppActionMcpHandlers(IActionableApp app)
        {
            _app = app;
        }

        /// <summary>
        /// Handles a <c>list_actions</c> call. Returns a JSON object with an
        /// <c>actions</c> array containing all currently dispatchable actions.
        /// </summary>
        public Task<string> HandleListActionsAsync(string _argsJson)
        {
            IReadOnlyList<ActionDescriptor> actions;
            try
            {
                actions = _app.GetAvailableActions();
            }
            catch (Exception ex)
            {
                // GetAvailableActions should never throw, but be resilient.
                var errObj = new JObject
                {
                    ["actions"] = new JArray(),
                    ["_error"] = ex.Message
                };
                return Task.FromResult(errObj.ToString(Formatting.None));
            }

            var actionsArray = new JArray();

            foreach (var descriptor in actions)
            {
                JToken schemaToken;
                try
                {
                    schemaToken = JObject.Parse(descriptor.ParameterSchema);
                }
                catch (JsonException)
                {
                    schemaToken = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject()
                    };
                }

                actionsArray.Add(new JObject
                {
                    ["name"] = descriptor.Name,
                    ["description"] = descriptor.Description,
                    ["parameterSchema"] = schemaToken
                });
            }

            var response = new JObject { ["actions"] = actionsArray };
            return Task.FromResult(response.ToString(Formatting.None));
        }

        /// <summary>
        /// Handles a <c>dispatch_action</c> call. Parses <paramref name="argsJson"/>
        /// for <c>action</c> and <c>params</c>, delegates to
        /// <see cref="IActionableApp.DispatchAsync"/>, and returns a JSON result object.
        /// </summary>
        public async Task<string> HandleDispatchActionAsync(string argsJson)
        {
            JObject args;
            try
            {
                args = JObject.Parse(argsJson);
            }
            catch (JsonException ex)
            {
                return BuildFailureJson("validation_error", $"Request body is not valid JSON: {ex.Message}");
            }

            var actionName = args["action"]?.Value<string>();
            if (string.IsNullOrEmpty(actionName))
                return BuildFailureJson("validation_error", "Missing required field: action");

            var paramsToken = args["params"] ?? new JObject();
            var paramsJson = paramsToken.ToString(Formatting.None);

            ActionResult result;
            try
            {
                result = await _app.DispatchAsync(actionName!, paramsJson).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return BuildFailureJson("execution_error", ex.Message);
            }

            if (result.Success)
            {
                var response = new JObject { ["success"] = true };
                if (result.Consequences != null)
                    response["consequences"] = new JArray(result.Consequences);
                return response.ToString(Formatting.None);
            }
            else
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = new JObject
                    {
                        ["code"] = result.ErrorCode ?? "execution_error",
                        ["message"] = result.ErrorMessage ?? "An error occurred."
                    }
                }.ToString(Formatting.None);
            }
        }

        private static string BuildFailureJson(string code, string message)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            }.ToString(Formatting.None);
        }
    }
}
