using System.Collections.Concurrent;

namespace zRover.Retriever.Server;

/// <summary>
/// Tracks active inbound MCP client connections so they can be surfaced in the UI.
/// Dedupes by the MCP session id (sent in the Mcp-Session-Id header by spec-compliant
/// clients) so a single client appears as one entry regardless of how many concurrent
/// HTTP requests it has in flight. Falls back to a random per-request key when missing.
/// </summary>
public sealed class ControllerRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _controllers = new();

    public event EventHandler? ControllersChanged;

    public IReadOnlyList<ControllerInfo> Controllers =>
        _controllers.Values.Select(e => e.Info).OrderBy(c => c.ConnectedSince).ToList();

    public string Track(string remoteAddress, string? userAgent, string? sessionId)
    {
        var key = !string.IsNullOrEmpty(sessionId)
            ? sessionId!
            : Guid.NewGuid().ToString("N")[..8];

        bool added = false;
        _controllers.AddOrUpdate(key,
            _ =>
            {
                added = true;
                return new Entry
                {
                    Info = new ControllerInfo
                    {
                        Key = key,
                        RemoteAddress = remoteAddress,
                        UserAgent = userAgent,
                        ConnectedSince = DateTimeOffset.UtcNow,
                    },
                    RefCount = 1,
                };
            },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.RefCount);
                if (string.IsNullOrEmpty(existing.Info.UserAgent) && !string.IsNullOrEmpty(userAgent))
                    existing.Info = existing.Info with { UserAgent = userAgent };
                return existing;
            });

        if (added)
            ControllersChanged?.Invoke(this, EventArgs.Empty);
        return key;
    }

    public void Untrack(string key)
    {
        if (!_controllers.TryGetValue(key, out var entry)) return;

        if (Interlocked.Decrement(ref entry.RefCount) <= 0)
        {
            if (_controllers.TryRemove(key, out _))
                ControllersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Entry
    {
        public ControllerInfo Info = null!;
        public int RefCount;
    }
}

public record ControllerInfo
{
    public string Key { get; init; } = "";
    public string RemoteAddress { get; init; } = "";
    public string? UserAgent { get; init; }
    public DateTimeOffset ConnectedSince { get; init; }
}