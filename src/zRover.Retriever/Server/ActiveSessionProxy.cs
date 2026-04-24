using System.Text.Json;
using zRover.Core;
using zRover.Core.Sessions;
using zRover.Retriever.Sessions;
using zRover.Mcp;

namespace zRover.Retriever.Server;

/// <summary>
/// Owns the dynamic proxy layer between the master MCP server and the active
/// session.
/// </summary>
/// <remarks>
/// <para>
/// On every session registration (and on every remote re-publish via
/// <c>tools/list_changed</c>), this class fetches the session's tool list and
/// registers a forwarding proxy for any tool name not yet present in the
/// <see cref="McpToolRegistryAdapter"/>. The published catalog is the union of
/// every session's capability set, so federated remote sessions that expose
/// tools local sessions don't (and vice-versa) are all reachable.
/// </para>
/// <para>
/// Each proxy tool is reference-counted by the set of sessions that contributed
/// it. When a session disconnects the proxy automatically drops the tool's
/// refcount; when it reaches zero the tool is unregistered from the adapter and
/// a <c>tools/list_changed</c> notification is emitted. This keeps the
/// advertised catalog honest — disconnected sessions do not leave dead tools
/// behind that would later return <c>no_active_session</c> to every caller.
/// </para>
/// <para>
/// Every proxy tool delegates to <see cref="ISessionRegistry.ActiveSession"/>
/// at call time — the tool registration itself never references a particular
/// session, so the same registration survives the active session rotating
/// between apps that all expose the same capability.
/// </para>
/// <para>
/// A <see cref="CancellationTokenSource"/> scoped to the active session is
/// cancelled whenever the active session changes or disconnects, which
/// interrupts all in-flight calls and returns an <c>interrupted</c> error to
/// the caller.
/// </para>
/// </remarks>
public sealed class ActiveSessionProxy
{
    private readonly ISessionRegistry _sessions;
    private readonly McpToolRegistryAdapter _adapter;
    private readonly ILogger<ActiveSessionProxy> _logger;

    // Replaced atomically whenever the active session changes.
    // All in-flight proxy calls hold a reference to their token at dispatch time.
    private volatile CancellationTokenSource _activeCts = new();

    // Per-session ownership. Maps sessionId -> set of proxy-tool names that
    // session contributed. Tool names already provided by the device-management
    // layer (set_active_app, list_apps, …) are NOT tracked here and are never
    // auto-removed.
    private readonly Dictionary<string, HashSet<string>> _toolsBySession = new(StringComparer.Ordinal);

    // Reference counts per proxy-tool name. When a tool's refcount drops to
    // zero we unregister it from the adapter.
    private readonly Dictionary<string, int> _refCount = new(StringComparer.Ordinal);

    // True once at least one session has contributed a proxy tool.
    private bool _toolsInitialised;
    private readonly object _stateLock = new();

    /// <summary>
    /// Whether proxy (app-interaction) tools have been registered in the adapter.
    /// </summary>
    public bool IsInitialized
    {
        get { lock (_stateLock) return _toolsInitialised; }
    }

    public ActiveSessionProxy(
        ISessionRegistry sessions,
        McpToolRegistryAdapter adapter,
        ILogger<ActiveSessionProxy> logger)
    {
        _sessions = sessions;
        _adapter = adapter;
        _logger = logger;

        _sessions.ActiveSessionChanged += OnActiveSessionChanged;
    }

    /// <summary>
    /// Called when a session registers (or re-publishes its tool list). Fetches
    /// the session's current tool list and merges any tools not yet present in
    /// the adapter as forwarding proxies. Also wires the session's
    /// <c>Disconnected</c> event so contributed tools are released when the
    /// session goes away.
    ///
    /// Idempotent and incremental — a re-publish only applies the delta:
    /// previously-contributed tools the session no longer advertises have their
    /// refcount decremented (and are removed from the catalog when refs hit
    /// zero); newly-advertised tools get registered.
    /// </summary>
    public async Task OnSessionRegisteredAsync(IRoverSession session)
    {
        IReadOnlyList<DiscoveredTool> tools;
        try
        {
            tools = await session.ListToolsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list tools from session {SessionId}", session.SessionId);
            return;
        }

        var advertised = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);

        List<DiscoveredTool> toAdd;
        List<string> toUnregisterFromAdapter = new();
        bool firstInit;

        lock (_stateLock)
        {
            // Hook the disconnect event the first time we see this session.
            if (!_toolsBySession.TryGetValue(session.SessionId, out var owned))
            {
                owned = new HashSet<string>(StringComparer.Ordinal);
                _toolsBySession[session.SessionId] = owned;
                session.Disconnected += OnSessionDisconnected;
            }

            // 1. Tools the session previously contributed but no longer offers.
            var toRelease = owned.Where(name => !advertised.Contains(name)).ToList();
            foreach (var name in toRelease)
            {
                owned.Remove(name);
                if (DecrementRefLocked(name))
                    toUnregisterFromAdapter.Add(name);
            }

            // 2. Tools the session newly contributes — those advertised that
            // are not yet registered anywhere in the adapter.
            toAdd = tools.Where(t => !_adapter.IsToolRegistered(t.Name)).ToList();

            // 3. Tools the session still offers but didn't yet own (because
            // another session contributed them first). Bump their refcount so
            // future disconnects are accounted for.
            foreach (var t in tools)
            {
                if (owned.Contains(t.Name)) continue;
                if (toAdd.Any(a => a.Name == t.Name)) continue; // counted below
                if (!_refCount.ContainsKey(t.Name)) continue;   // device tool, untracked

                owned.Add(t.Name);
                _refCount[t.Name]++;
            }

            // 4. Account for the new contributions.
            foreach (var t in toAdd)
            {
                owned.Add(t.Name);
                _refCount[t.Name] = 1;
            }

            if (toAdd.Count == 0 && toUnregisterFromAdapter.Count == 0)
            {
                if (!_toolsInitialised)
                {
                    _logger.LogWarning(
                        "Session {SessionId} ({DisplayName}) produced no new proxy tools (list was empty or all already registered). Will retry with the next session.",
                        session.SessionId, session.Identity.DisplayName);
                }
                else
                {
                    _logger.LogDebug(
                        "Session {SessionId} ({DisplayName}) added no new tools — adapter already covers its capabilities.",
                        session.SessionId, session.Identity.DisplayName);
                }
                return;
            }

            firstInit = !_toolsInitialised;
            if (toAdd.Count > 0) _toolsInitialised = true;
        }

        _logger.LogInformation(
            firstInit
                ? "Initialising proxy tool skeleton from session {SessionId} ({DisplayName}): +{Added} new, -{Released} released"
                : "Merging proxy tools from session {SessionId} ({DisplayName}): +{Added} new, -{Released} released",
            session.SessionId, session.Identity.DisplayName,
            toAdd.Count, toUnregisterFromAdapter.Count);

        // Apply mutations OUTSIDE the lock — adapter operations may invoke
        // Changed handlers synchronously which could re-enter our code.
        foreach (var name in toUnregisterFromAdapter)
            _adapter.TryUnregisterTool(name);

        foreach (var tool in toAdd)
        {
            var capturedName = tool.Name;
            _adapter.RegisterTool(tool.Name, tool.Description, tool.InputSchema,
                (Func<string, Task<RoverToolResult>>)(argsJson =>
                    ProxyInvokeAsync(capturedName, argsJson)));
        }

        // The Add/Remove calls above already raise tools/list_changed
        // notifications via the underlying SDK collection. No explicit notify
        // needed.
    }

    /// <summary>
    /// Releases all proxy tools attributed to the given session. If a tool's
    /// reference count drops to zero (i.e. no other session offers it), the
    /// tool is unregistered from the adapter and the SDK emits a
    /// <c>tools/list_changed</c> notification automatically.
    /// </summary>
    public void DropSession(IRoverSession session)
    {
        List<string> toUnregister;
        lock (_stateLock)
        {
            if (!_toolsBySession.TryGetValue(session.SessionId, out var owned))
                return;

            session.Disconnected -= OnSessionDisconnected;
            _toolsBySession.Remove(session.SessionId);

            toUnregister = new List<string>();
            foreach (var name in owned)
            {
                if (DecrementRefLocked(name))
                    toUnregister.Add(name);
            }
        }

        if (toUnregister.Count == 0) return;

        _logger.LogInformation(
            "Releasing {Count} proxy tool(s) attributed to disconnected session {SessionId} ({DisplayName})",
            toUnregister.Count, session.SessionId, session.Identity.DisplayName);

        foreach (var name in toUnregister)
            _adapter.TryUnregisterTool(name);
    }

    /// <summary>
    /// Decrements the refcount for <paramref name="name"/>. Returns <c>true</c>
    /// if the count reached zero and the caller should now unregister the tool
    /// from the adapter. Must be called under <see cref="_stateLock"/>.
    /// </summary>
    private bool DecrementRefLocked(string name)
    {
        if (!_refCount.TryGetValue(name, out var count)) return false;

        count--;
        if (count <= 0)
        {
            _refCount.Remove(name);
            return true;
        }

        _refCount[name] = count;
        return false;
    }

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        if (sender is IRoverSession s) DropSession(s);
    }

    private async Task<RoverToolResult> ProxyInvokeAsync(string toolName, string argsJson)
    {
        var activeSession = _sessions.ActiveSession;
        if (activeSession == null || !activeSession.IsConnected)
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "no_active_session",
                message = "No active app session is set. Use set_active_app to choose one, then retry."
            }));

        // Capture the CTS for the current active session so that if the session changes
        // mid-call we cancel this invocation, not a future one.
        var sessionCts = _activeCts;

        try
        {
            var raw = await activeSession.InvokeToolAsync(toolName, argsJson, sessionCts.Token);
            var augmentedText = AugmentResult(raw.Text, activeSession);
            return raw.HasImage
                ? RoverToolResult.WithImage(augmentedText, raw.ImageBytes!, raw.ImageMimeType ?? "image/png")
                : RoverToolResult.FromText(augmentedText);
        }
        catch (OperationCanceledException)
        {
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "interrupted",
                message = "Tool call was interrupted because the active session changed or disconnected. Use set_active_app to set a new active session and retry."
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed on session {SessionId}", toolName, activeSession.SessionId);
            return RoverToolResult.FromText(JsonSerializer.Serialize(new { error = "invocation_failed", message = ex.Message }));
        }
    }

    /// <summary>
    /// Injects a <c>_rover_session</c> key into the result so the MCP client always
    /// knows which app instance handled the call, even if it lost track of the active session.
    /// </summary>
    private static string AugmentResult(string raw, IRoverSession session)
    {
        object? originNode = null;
        if (session is PropagatedSession ps)
        {
            originNode = new
            {
                type = ps.Origin.Type,
                managerId = ps.Origin.ManagerId,
                managerAlias = ps.Origin.ManagerAlias,
                hops = ps.Origin.Hops
            };
        }

        var sessionNode = new
        {
            sessionId   = session.SessionId,
            appName     = session.Identity.AppName,
            version     = session.Identity.Version,
            instanceId  = session.Identity.InstanceId,
            displayName = session.Identity.DisplayName,
            origin      = originNode
        };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                using var ms = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WritePropertyName("_rover_session");
                JsonSerializer.Serialize(writer, sessionNode);
                writer.WriteEndObject();
                writer.Flush();
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch { /* unparseable — fall through to wrapper */ }

        return JsonSerializer.Serialize(new { _result = raw, _rover_session = sessionNode });
    }

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e)
    {
        // Cancel all in-flight calls from the previous session
        var oldCts = Interlocked.Exchange(ref _activeCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();

        if (e.Current != null)
            _logger.LogInformation("Active session → {DisplayName} (session {SessionId})",
                e.Current.Identity.DisplayName, e.Current.SessionId);
        else if (e.Previous != null)
            _logger.LogWarning("Active session {DisplayName} disconnected — no active session",
                e.Previous.Identity.DisplayName);

        // The catalog itself may be unchanged but downstream behaviour has
        // rotated. Force a tools/list_changed so any client that annotates its
        // tool list with the active app name has a chance to refresh.
        if (_toolsInitialised)
            _adapter.NotifyToolsChanged();
    }
}
