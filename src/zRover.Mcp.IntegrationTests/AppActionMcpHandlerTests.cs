using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using zRover.Core;

namespace zRover.Mcp.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="AppActionMcpHandlers"/> — the pure-logic JSON layer
/// that implements the list_actions / dispatch_action protocol.
/// No MCP SDK, no UWP dependencies.
/// </summary>
public class AppActionMcpHandlerTests
{
    // ──────────────────────────────────────────────
    //  Fake IActionableApp implementations
    // ──────────────────────────────────────────────

    private sealed class StubApp : IActionableApp
    {
        private readonly IReadOnlyList<ActionDescriptor> _actions;
        private readonly Func<string, string, Task<ActionResult>> _dispatch;

        public StubApp(
            IReadOnlyList<ActionDescriptor> actions,
            Func<string, string, Task<ActionResult>>? dispatch = null)
        {
            _actions = actions;
            _dispatch = dispatch ?? ((_, _) => Task.FromResult(ActionResult.Ok()));
        }

        public IReadOnlyList<ActionDescriptor> GetAvailableActions() => _actions;
        public Task<ActionResult> DispatchAsync(string name, string json) => _dispatch(name, json);
    }

    private static StubApp EmptyApp() => new StubApp(Array.Empty<ActionDescriptor>());

    private static StubApp SingleActionApp(
        string name = "DoThing",
        string? schema = null,
        Func<string, string, Task<ActionResult>>? dispatch = null)
    {
        var descriptor = new ActionDescriptor(
            name,
            "Does the thing.",
            schema ?? @"{""type"":""object"",""properties"":{}}");
        return new StubApp(new[] { descriptor }, dispatch);
    }

    // ──────────────────────────────────────────────
    //  list_actions
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ListActions_WhenNoActions_ReturnsEmptyArray()
    {
        var handlers = new AppActionMcpHandlers(EmptyApp());
        var json = await handlers.HandleListActionsAsync("{}");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("actions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListActions_ReturnsNameDescriptionAndSchema()
    {
        var app = SingleActionApp(
            name: "TestAction",
            schema: @"{""type"":""object"",""required"":[""id""],""properties"":{""id"":{""type"":""integer"",""enum"":[1,2,3]}}}");
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleListActionsAsync("{}");
        var doc = JsonDocument.Parse(json);
        var action = doc.RootElement.GetProperty("actions")[0];

        action.GetProperty("name").GetString().Should().Be("TestAction");
        action.GetProperty("description").GetString().Should().Be("Does the thing.");

        var schema = action.GetProperty("parameterSchema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required")[0].GetString().Should().Be("id");
    }

    [Fact]
    public async Task ListActions_WithMalformedSchema_ReturnsEmptyObjectSchema()
    {
        var app = SingleActionApp(schema: "not valid json");
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleListActionsAsync("{}");
        var doc = JsonDocument.Parse(json);
        var schema = doc.RootElement.GetProperty("actions")[0].GetProperty("parameterSchema");

        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task ListActions_ReturnsMultipleActions()
    {
        var app = new StubApp(new[]
        {
            new ActionDescriptor("ActionA", "A", @"{""type"":""object"",""properties"":{}}"),
            new ActionDescriptor("ActionB", "B", @"{""type"":""object"",""properties"":{}}"),
        });
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleListActionsAsync("{}");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("actions").GetArrayLength().Should().Be(2);
    }

    // ──────────────────────────────────────────────
    //  dispatch_action — request parsing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchAction_MissingActionField_ReturnsValidationError()
    {
        var handlers = new AppActionMcpHandlers(EmptyApp());
        var json = await handlers.HandleDispatchActionAsync(@"{""params"":{}}");

        AssertFailure(json, "validation_error");
    }

    [Fact]
    public async Task DispatchAction_InvalidJson_ReturnsValidationError()
    {
        var handlers = new AppActionMcpHandlers(EmptyApp());
        var json = await handlers.HandleDispatchActionAsync("not json");

        AssertFailure(json, "validation_error");
    }

    [Fact]
    public async Task DispatchAction_MissingParamsField_DefaultsToEmptyObject()
    {
        string? receivedParams = null;
        var app = SingleActionApp(dispatch: (name, paramsJson) =>
        {
            receivedParams = paramsJson;
            return Task.FromResult(ActionResult.Ok());
        });
        var handlers = new AppActionMcpHandlers(app);

        await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing""}");

        receivedParams.Should().Be("{}");
    }

    // ──────────────────────────────────────────────
    //  dispatch_action — success path
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchAction_Success_ReturnsSuccessTrue()
    {
        var handlers = new AppActionMcpHandlers(SingleActionApp());
        var json = await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing"",""params"":{}}");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAction_SuccessWithConsequences_ReturnsConsequencesArray()
    {
        var app = SingleActionApp(dispatch: (_, _) =>
            Task.FromResult(ActionResult.Ok(new[] { "SideEffectA", "SideEffectB" })));
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing"",""params"":{}}");
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var consequences = doc.RootElement.GetProperty("consequences");
        consequences[0].GetString().Should().Be("SideEffectA");
        consequences[1].GetString().Should().Be("SideEffectB");
    }

    [Fact]
    public async Task DispatchAction_PassesActionNameAndParamsToApp()
    {
        string? receivedName = null;
        string? receivedParams = null;
        var app = SingleActionApp(dispatch: (name, paramsJson) =>
        {
            receivedName = name;
            receivedParams = paramsJson;
            return Task.FromResult(ActionResult.Ok());
        });
        var handlers = new AppActionMcpHandlers(app);

        await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing"",""params"":{""x"":42}}");

        receivedName.Should().Be("DoThing");
        var p = JsonDocument.Parse(receivedParams!);
        p.RootElement.GetProperty("x").GetInt32().Should().Be(42);
    }

    // ──────────────────────────────────────────────
    //  dispatch_action — failure paths
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchAction_AppReturnsUnknownAction_PropagatesErrorCode()
    {
        var app = SingleActionApp(dispatch: (_, _) =>
            Task.FromResult(ActionResult.Fail("unknown_action", "No such action.")));
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleDispatchActionAsync(@"{""action"":""Ghost"",""params"":{}}");

        AssertFailure(json, "unknown_action");
    }

    [Fact]
    public async Task DispatchAction_AppReturnsValidationError_PropagatesMessage()
    {
        var app = SingleActionApp(dispatch: (_, _) =>
            Task.FromResult(ActionResult.Fail("validation_error", "params.id: value 99 is not in [1,2,3]")));
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing"",""params"":{""id"":99}}");
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("validation_error");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("99");
    }

    [Fact]
    public async Task DispatchAction_AppThrows_ReturnsExecutionError()
    {
        var app = SingleActionApp(dispatch: (_, _) =>
            throw new InvalidOperationException("Internal boom"));
        var handlers = new AppActionMcpHandlers(app);

        var json = await handlers.HandleDispatchActionAsync(@"{""action"":""DoThing"",""params"":{}}");

        AssertFailure(json, "execution_error");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain("Internal boom");
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private static void AssertFailure(string json, string expectedCode)
    {
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse($"expected failure with code '{expectedCode}'");
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(expectedCode);
    }
}
