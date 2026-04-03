using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.Core;
using zRover.Mcp;

namespace zRover.Mcp.IntegrationTests;

/// <summary>
/// Integration tests for the zRover MCP server tool layer.
///
/// These tests verify:
///   1. McpToolRegistryAdapter correctly registers tools as McpServerTool instances
///   2. An MCP server exposes registered tools over the protocol
///   3. An MCP client can discover and invoke those tools end-to-end
///   4. Tool argument passing and result serialization work correctly
///   5. Error handling for missing/failing tools
/// </summary>
public class McpServerToolTests : IAsyncLifetime
{
    private McpToolRegistryAdapter _adapter = null!;
    private McpClient _client = null!;
    private McpServer _server = null!;
    private StreamServerTransport _serverTransport = null!;

    // Two duplex pipe pairs form the bidirectional channel
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();

    public async Task InitializeAsync()
    {
        _adapter = new McpToolRegistryAdapter();

        // Register a simple echo tool
        _adapter.RegisterTool(
            "echo",
            "Echoes back the input message.",
            """{ "type": "object", "properties": { "message": { "type": "string" } }, "required": ["message"] }""",
            (Func<string, Task<string>>)(async (argsJson) =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var msg = args?["message"].GetString() ?? "";
                return JsonSerializer.Serialize(new { echo = msg });
            }));

        // Register a tool that adds two numbers
        _adapter.RegisterTool(
            "add_numbers",
            "Adds two numbers together.",
            """{ "type": "object", "properties": { "a": { "type": "number" }, "b": { "type": "number" } }, "required": ["a", "b"] }""",
            (Func<string, Task<string>>)(async (argsJson) =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var a = args?["a"].GetDouble() ?? 0;
                var b = args?["b"].GetDouble() ?? 0;
                return JsonSerializer.Serialize(new { result = a + b });
            }));

        // Register a tool that simulates an error
        _adapter.RegisterTool(
            "fail_tool",
            "Always throws an error for testing.",
            """{ "type": "object", "properties": {} }""",
            (Func<string, Task<string>>)(async (argsJson) =>
            {
                throw new InvalidOperationException("Intentional test failure");
            }));

        // Register a tool with no arguments
        _adapter.RegisterTool(
            "get_status",
            "Returns a status object with no arguments.",
            """{ "type": "object", "properties": {} }""",
            (Func<string, Task<string>>)(async (argsJson) =>
            {
                return JsonSerializer.Serialize(new { status = "ok", version = "1.0.0" });
            }));

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "RoverTest", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true }
            },
            ToolCollection = _adapter.Tools
        };

        // Server reads from clientToServer.Reader, writes to serverToClient.Writer
        _serverTransport = new StreamServerTransport(
            _clientToServer.Reader.AsStream(),
            _serverToClient.Writer.AsStream(),
            "RoverTest");

        _server = McpServer.Create(_serverTransport, serverOptions);

        // Start the server in the background
        _ = Task.Run(() => _server.RunAsync());

        // Client reads from serverToClient.Reader, writes to clientToServer.Writer
        var clientTransport = new StreamClientTransport(
            _clientToServer.Writer.AsStream(),
            _serverToClient.Reader.AsStream());

        _client = await McpClient.CreateAsync(clientTransport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable clientDisposable)
            await clientDisposable.DisposeAsync();

        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();

        if (_serverTransport is IAsyncDisposable transportDisposable)
            await transportDisposable.DisposeAsync();
    }

    [Fact]
    public async Task ServerInfo_ReturnsCorrectName()
    {
        _client.ServerInfo.Name.Should().Be("RoverTest");
        _client.ServerInfo.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task ListTools_ReturnsAllRegisteredTools()
    {
        var tools = await _client.ListToolsAsync();

        tools.Should().HaveCount(4);
        tools.Select(t => t.Name).Should().Contain(new[] { "echo", "add_numbers", "fail_tool", "get_status" });
    }

    [Fact]
    public async Task ListTools_EachToolHasDescriptionAndSchema()
    {
        var tools = await _client.ListToolsAsync();

        var echo = tools.First(t => t.Name == "echo");
        echo.Description.Should().Be("Echoes back the input message.");
        echo.JsonSchema.GetProperty("properties").GetProperty("message").GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public async Task CallTool_Echo_ReturnsMessage()
    {
        var result = await _client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { { "message", "hello world" } });

        result.Content.Should().HaveCount(1);
        var text = result.Content[0] as TextContentBlock;
        text.Should().NotBeNull();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text!.Text);
        parsed!["echo"].Should().Be("hello world");
    }

    [Fact]
    public async Task CallTool_AddNumbers_ComputesCorrectSum()
    {
        var result = await _client.CallToolAsync(
            "add_numbers",
            new Dictionary<string, object?> { { "a", 42 }, { "b", 58 } });

        result.Content.Should().HaveCount(1);
        var text = result.Content[0] as TextContentBlock;
        text.Should().NotBeNull();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text!.Text);
        parsed!["result"].GetDouble().Should().Be(100);
    }

    [Fact]
    public async Task CallTool_GetStatus_WorksWithNoArguments()
    {
        var result = await _client.CallToolAsync("get_status", new Dictionary<string, object?>());

        result.Content.Should().HaveCount(1);
        var text = result.Content[0] as TextContentBlock;
        text.Should().NotBeNull();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text!.Text);
        parsed!["status"].GetString().Should().Be("ok");
        parsed!["version"].GetString().Should().Be("1.0.0");
    }

    [Fact]
    public async Task CallTool_FailingTool_ReturnsError()
    {
        var result = await _client.CallToolAsync("fail_tool", new Dictionary<string, object?>());

        // The MCP SDK wraps tool exceptions in IsError=true responses
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Ping_ServerResponds()
    {
        // Ping should not throw
        await _client.PingAsync();
    }
}

/// <summary>
/// Unit tests for McpToolRegistryAdapter in isolation (no MCP protocol).
/// </summary>
public class McpToolRegistryAdapterTests
{
    [Fact]
    public void RegisterTool_AddsToToolsCollection()
    {
        var adapter = new McpToolRegistryAdapter();

        adapter.RegisterTool("test_tool", "A test", """{"type":"object","properties":{}}""", _ => Task.FromResult("{}"));

        adapter.Tools.Should().HaveCount(1);
    }

    [Fact]
    public void RegisterTool_ProtocolToolHasCorrectMetadata()
    {
        var adapter = new McpToolRegistryAdapter();

        adapter.RegisterTool(
            "my_tool",
            "Does something useful",
            """{"type":"object","properties":{"x":{"type":"integer"}},"required":["x"]}""",
            _ => Task.FromResult("{}"));

        var tool = adapter.Tools.First();
        tool.ProtocolTool.Name.Should().Be("my_tool");
        tool.ProtocolTool.Description.Should().Be("Does something useful");
        tool.ProtocolTool.InputSchema.GetProperty("required")[0].GetString().Should().Be("x");
    }

    [Fact]
    public void RegisterMultipleTools_AllPresent()
    {
        var adapter = new McpToolRegistryAdapter();

        adapter.RegisterTool("tool_a", "A", """{"type":"object","properties":{}}""", _ => Task.FromResult("{}"));
        adapter.RegisterTool("tool_b", "B", """{"type":"object","properties":{}}""", _ => Task.FromResult("{}"));
        adapter.RegisterTool("tool_c", "C", """{"type":"object","properties":{}}""", _ => Task.FromResult("{}"));

        adapter.Tools.Should().HaveCount(3);
        adapter.Tools.Select(t => t.ProtocolTool.Name).Should().Contain(new[] { "tool_a", "tool_b", "tool_c" });
    }

    [Fact]
    public void ImplementsIMcpToolRegistry()
    {
        var adapter = new McpToolRegistryAdapter();
        adapter.Should().BeAssignableTo<IMcpToolRegistry>();
    }
}

/// <summary>
/// Tests verifying that DelegateMcpServerTool correctly invokes the handler.
/// </summary>
public class DelegateMcpServerToolInvocationTests : IAsyncLifetime
{
    private McpToolRegistryAdapter _adapter = null!;
    private McpClient _client = null!;
    private McpServer _server = null!;
    private StreamServerTransport _serverTransport = null!;
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private string _lastReceivedArgs = "";

    public async Task InitializeAsync()
    {
        _adapter = new McpToolRegistryAdapter();

        // Register tool that captures its received arguments
        _adapter.RegisterTool(
            "capture_args",
            "Captures the arguments received.",
            """{ "type": "object", "properties": { "name": { "type": "string" }, "count": { "type": "integer" } } }""",
            async (argsJson) =>
            {
                _lastReceivedArgs = argsJson;
                return """{"captured": true}""";
            });

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "ArgTest", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability()
            },
            ToolCollection = _adapter.Tools
        };

        _serverTransport = new StreamServerTransport(
            _clientToServer.Reader.AsStream(),
            _serverToClient.Writer.AsStream(),
            "ArgTest");

        _server = McpServer.Create(_serverTransport, serverOptions);
        _ = Task.Run(() => _server.RunAsync());

        var clientTransport = new StreamClientTransport(
            _clientToServer.Writer.AsStream(),
            _serverToClient.Reader.AsStream());

        _client = await McpClient.CreateAsync(clientTransport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
        if (_serverTransport is IAsyncDisposable t) await t.DisposeAsync();
    }

    [Fact]
    public async Task CallTool_PassesSerializedArguments()
    {
        await _client.CallToolAsync(
            "capture_args",
            new Dictionary<string, object?> { { "name", "Alice" }, { "count", 7 } });

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_lastReceivedArgs);
        parsed!["name"].GetString().Should().Be("Alice");
        parsed!["count"].GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task CallTool_WithEmptyArgs_PassesEmptyObject()
    {
        await _client.CallToolAsync("capture_args", new Dictionary<string, object?>());

        // Should receive "{}" or equivalent
        _lastReceivedArgs.Should().NotBeNullOrEmpty();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_lastReceivedArgs);
        parsed.Should().NotBeNull();
    }
}