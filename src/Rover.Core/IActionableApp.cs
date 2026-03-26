using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rover.Core
{
    /// <summary>
    /// Exposes the application's currently dispatchable actions under the App Action API protocol.
    /// Implement this interface in your UWP app and register it via <c>DebugHostOptions.ActionableApp</c>.
    /// </summary>
    public interface IActionableApp
    {
        /// <summary>
        /// Returns the set of currently dispatchable actions with JSON Schema parameter contracts.
        /// Dynamic enum constraints reflect valid object IDs as of this call.
        /// Always returns a fresh snapshot. Returns an empty list when no actions are possible.
        /// </summary>
        IReadOnlyList<ActionDescriptor> GetAvailableActions();

        /// <summary>
        /// Validates and executes the named action. Marshals to the appropriate thread internally.
        /// Never throws — all errors are returned as <see cref="ActionResult"/> failures.
        /// </summary>
        Task<ActionResult> DispatchAsync(string actionName, string parametersJson);
    }

    /// <summary>
    /// Describes a single dispatchable action. Returned by <see cref="IActionableApp.GetAvailableActions"/>.
    /// </summary>
    public class ActionDescriptor
    {
        /// <summary>The identifier used to dispatch the action.</summary>
        public string Name { get; }

        /// <summary>A human/LLM-readable explanation of what the action does and when it makes sense.</summary>
        public string Description { get; }

        /// <summary>
        /// A JSON Schema (draft-07) string describing the expected <c>params</c> object.
        /// Use <c>enum</c> on integer properties to constrain values to currently valid IDs.
        /// Pass <c>"{\"type\":\"object\",\"properties\":{}}"</c> for parameter-free actions.
        /// </summary>
        public string ParameterSchema { get; }

        public ActionDescriptor(string name, string description, string parameterSchema)
        {
            Name = name;
            Description = description;
            ParameterSchema = parameterSchema;
        }
    }

    /// <summary>
    /// Result of <see cref="IActionableApp.DispatchAsync"/>.
    /// Use <see cref="Ok"/> and <see cref="Fail"/> factory methods for convenient construction.
    /// </summary>
    public class ActionResult
    {
        public bool Success { get; set; }

        /// <summary>One of: unknown_action, validation_error, not_available, execution_error. Null on success.</summary>
        public string? ErrorCode { get; set; }

        /// <summary>Human-readable detail identifying which property failed and why. Null on success.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Names of all cascaded side-effects triggered by the action, in execution order. Null on failure.</summary>
        public IReadOnlyList<string>? Consequences { get; set; }

        public static ActionResult Ok(IReadOnlyList<string>? consequences = null)
            => new ActionResult { Success = true, Consequences = consequences };

        public static ActionResult Fail(string code, string message)
            => new ActionResult { Success = false, ErrorCode = code, ErrorMessage = message };
    }
}
