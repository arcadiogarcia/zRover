namespace zRover.Core;

/// <summary>
/// Well-known default TCP ports used by zRover components.
/// <para>
/// These are starting-point defaults only. Every component accepts an explicit port
/// override so that multiple instances can coexist without conflicts.
/// </para>
/// </summary>
public static class RoverPorts
{
    /// <summary>
    /// Default port for per-app <c>zRover.FullTrust.McpServer</c> instances
    /// (exposed via <c>zRover.Uwp.RoverMcp</c> / <c>DebugHost</c>).
    /// The first instance on a device will reliably be at this port when no
    /// explicit override is supplied.
    /// </summary>
    public const int App = 5100;

    /// <summary>
    /// Default port for the <c>zRover.Retriever</c> superset MCP server.
    /// Kept separate from <see cref="App"/> so the manager and a single running
    /// app instance can both use their predictable ports without collision.
    /// </summary>
    public const int Manager = 5200;
}
