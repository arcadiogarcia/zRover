using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.Core;

namespace zRover.Mcp
{
    /// <summary>
    /// Collects tool registrations from <see cref="IDebugCapability"/> instances
    /// and converts them into <see cref="McpServerTool"/> entries for use with the
    /// official MCP C# SDK.
    /// </summary>
    public sealed class McpToolRegistryAdapter : IMcpToolRegistry
    {
        private readonly McpServerPrimitiveCollection<McpServerTool> _tools = new McpServerPrimitiveCollection<McpServerTool>();

        public McpServerPrimitiveCollection<McpServerTool> Tools => _tools;

        public void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<string>> handler)
        {
            _tools.Add(new DelegateMcpServerTool(name, description, inputSchema, handler));
        }

        /// <summary>
        /// Forces the MCP server to send a <c>tools/list_changed</c> notification to
        /// all connected clients. Used to signal session list changes so remote managers
        /// can re-sync their propagated sessions.
        /// </summary>
        public void NotifyToolsChanged()
        {
            // The McpServerPrimitiveCollection fires CollectionChanged, which the
            // MCP SDK translates into a tools/list_changed notification. We trigger
            // this by adding and immediately removing a sentinel tool.
            var sentinel = new DelegateMcpServerTool(
                "__notify_sentinel__", "", """{"type":"object","properties":{}}""",
                _ => Task.FromResult("{}"));
            _tools.Add(sentinel);
            _tools.Remove(sentinel);
        }
    }

    /// <summary>
    /// An <see cref="McpServerTool"/> subclass that delegates invocation to a
    /// <c>Func&lt;string, Task&lt;string&gt;&gt;</c> that receives the serialized
    /// arguments dictionary and returns a JSON result string.
    /// </summary>
    /// <remarks>
    /// Uses the SDK's experimental subclassing API (MCPEXP001) which is suppressed
    /// project-wide in zRover.Mcp.csproj.
    /// </remarks>
    internal sealed class DelegateMcpServerTool : McpServerTool
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<string, Task<string>> _handler;

        public override Tool ProtocolTool { get; }

        public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

        internal DelegateMcpServerTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<string>> handler)
        {
            _handler = handler;

            ProtocolTool = new Tool
            {
                Name = name,
                Description = description,
                InputSchema = JsonDocument.Parse(inputSchema).RootElement
            };
        }

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var argsJson = request.Params?.Arguments is { } args
                ? JsonSerializer.Serialize(args, SerializerOptions)
                : "{}";

            var resultJson = await _handler(argsJson).ConfigureAwait(false);

            return new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = resultJson }
                }
            };
        }
    }
}
