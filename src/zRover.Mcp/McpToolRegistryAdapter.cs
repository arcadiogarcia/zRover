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
    /// <remarks>
    /// <para>
    /// The underlying <see cref="McpServerPrimitiveCollection{T}"/> raises
    /// <c>Changed</c> automatically on every <c>Add</c>/<c>Remove</c>/<c>Clear</c>,
    /// which the SDK translates into a <c>notifications/tools/list_changed</c>
    /// notification on connected sessions whose transport supports unsolicited
    /// notifications (i.e. stateful HTTP and stdio).
    /// </para>
    /// <para>
    /// To remove a tool (for example when a session disconnects) call
    /// <see cref="TryUnregisterTool"/>. To force a notification when the catalog
    /// shape is unchanged but tool behaviour has materially changed underneath
    /// (e.g. the active session rotated and proxy tools now target a different
    /// app), call <see cref="NotifyToolsChanged"/>. Routine add/remove operations
    /// already raise the event automatically — no caller-side notification is
    /// required.
    /// </para>
    /// </remarks>
    public sealed class McpToolRegistryAdapter : IMcpToolRegistry
    {
        private readonly NotifyingToolCollection _tools = new NotifyingToolCollection();
        private readonly Dictionary<string, McpServerTool> _toolsByName = new(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public McpServerPrimitiveCollection<McpServerTool> Tools => _tools;

        public void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<string>> handler)
        {
            AddInternal(new DelegateMcpServerTool(name, description, inputSchema, handler));
        }

        public void RegisterTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<RoverToolResult>> handler)
        {
            AddInternal(new RichDelegateMcpServerTool(name, description, inputSchema, handler));
        }

        private void AddInternal(McpServerTool tool)
        {
            lock (_gate)
            {
                if (_toolsByName.ContainsKey(tool.ProtocolTool.Name))
                    throw new InvalidOperationException(
                        $"A tool named '{tool.ProtocolTool.Name}' is already registered.");

                _toolsByName.Add(tool.ProtocolTool.Name, tool);
            }

            // Add outside the lock — the underlying collection is itself
            // thread-safe and may invoke Changed handlers synchronously, which
            // could otherwise deadlock against callers that hold _gate.
            _tools.Add(tool);
        }

        /// <summary>
        /// Removes a previously registered tool. Returns <c>true</c> if a tool
        /// with that name was found and removed, otherwise <c>false</c>. The
        /// underlying collection raises a <c>tools/list_changed</c> notification
        /// automatically when removal succeeds.
        /// </summary>
        public bool TryUnregisterTool(string name)
        {
            McpServerTool? tool;
            lock (_gate)
            {
                if (!_toolsByName.TryGetValue(name, out tool))
                    return false;
                _toolsByName.Remove(name);
            }

            return _tools.Remove(tool);
        }

        /// <summary>
        /// Forces an unsolicited <c>tools/list_changed</c> notification to all
        /// connected clients without mutating the catalog. Use this only when the
        /// shape of the tool list is identical but the behaviour behind one or
        /// more tools has materially changed (for example, the active session
        /// rotated, so the targets of the proxy tools have changed).
        /// </summary>
        public void NotifyToolsChanged() => _tools.RaiseChangedExternal();

        /// <summary>
        /// Returns <c>true</c> when a tool with the given name is already
        /// registered.
        /// </summary>
        public bool IsToolRegistered(string name)
        {
            lock (_gate)
                return _toolsByName.ContainsKey(name);
        }

        /// <summary>
        /// <see cref="McpServerPrimitiveCollection{T}"/> subclass that exposes the
        /// otherwise-protected <see cref="McpServerPrimitiveCollection{T}.RaiseChanged"/>
        /// method. The SDK subscribes to <c>Changed</c> on this collection when
        /// the server starts and emits the MCP <c>tools/list_changed</c>
        /// notification in response.
        /// </summary>
        private sealed class NotifyingToolCollection : McpServerPrimitiveCollection<McpServerTool>
        {
            internal void RaiseChangedExternal() => RaiseChanged();
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

    /// <summary>
    /// An <see cref="McpServerTool"/> subclass whose handler returns a
    /// <see cref="RoverToolResult"/>. When the result carries image bytes they are
    /// emitted as an MCP <c>ImageContentBlock</c> alongside the text block,
    /// enabling MCP-native image delivery without filesystem path sharing.
    /// </summary>
    internal sealed class RichDelegateMcpServerTool : McpServerTool
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<string, Task<RoverToolResult>> _handler;

        public override Tool ProtocolTool { get; }

        public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

        internal RichDelegateMcpServerTool(
            string name,
            string description,
            string inputSchema,
            Func<string, Task<RoverToolResult>> handler)
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

            var result = await _handler(argsJson).ConfigureAwait(false);

            var content = new List<ContentBlock>
            {
                new TextContentBlock { Text = result.Text }
            };

            if (result.HasImage)
            {
                content.Add(ImageContentBlock.FromBytes(result.ImageBytes!, result.ImageMimeType!));
            }

            return new CallToolResult { Content = content };
        }
    }
}
