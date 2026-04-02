using System.Text.Json;
using zRover.Core;
using zRover.Core.Sessions;
using zRover.BackgroundManager.Sessions;

namespace zRover.BackgroundManager.Server;

/// <summary>
/// Registers the two management tools that are always present in the master MCP server
/// regardless of active session state:
/// <list type="bullet">
///   <item><c>list_apps</c> — enumerate all connected sessions</item>
///   <item><c>set_active_app</c> — choose which session receives forwarded tool calls</item>
/// </list>
/// </summary>
public static class SessionManagementTools
{
    public static void Register(IMcpToolRegistry registry, ISessionRegistry sessions)
    {
        registry.RegisterTool(
            "list_apps",
            "Lists all app instances currently connected to the zRover Background Manager. " +
            "Returns session IDs, app names, versions, instance IDs, and which one is active. " +
            "Use set_active_app to choose which instance receives tool calls.",
            """{"type":"object","properties":{}}""",
            _ =>
            {
                var apps = sessions.Sessions.Select(s =>
                {
                    var origin = s is PropagatedSession ps
                        ? new { type = ps.Origin.Type, managerId = ps.Origin.ManagerId, managerAlias = ps.Origin.ManagerAlias, managerUrl = ps.Origin.ManagerUrl, hops = ps.Origin.Hops }
                        : new { type = "local", managerId = (string?)null, managerAlias = (string?)null, managerUrl = (string?)null, hops = 0 };

                    return new
                    {
                        sessionId   = s.SessionId,
                        appName     = s.Identity.AppName,
                        version     = s.Identity.Version,
                        instanceId  = s.Identity.InstanceId,
                        displayName = s.Identity.DisplayName,
                        mcpUrl      = s.McpUrl,
                        isConnected = s.IsConnected,
                        isActive    = s.SessionId == sessions.ActiveSession?.SessionId,
                        origin
                    };
                });

                return Task.FromResult(JsonSerializer.Serialize(new { apps }));
            });

        registry.RegisterTool(
            "set_active_app",
            "Sets the active app instance that all subsequent instance-level tool calls " +
            "(capture_current_view, inject_tap, etc.) will be forwarded to. " +
            "Use list_apps to discover the available session IDs. " +
            "Any in-flight tool calls on the previously active session will be interrupted.",
            """
            {
              "type": "object",
              "properties": {
                "sessionId": {
                  "type": "string",
                  "description": "The session ID of the app instance to activate, as returned by list_apps."
                }
              },
              "required": ["sessionId"]
            }
            """,
            argsJson =>
            {
                string sessionId;
                try
                {
                    sessionId = JsonDocument.Parse(argsJson).RootElement
                        .GetProperty("sessionId").GetString() ?? "";
                }
                catch
                {
                    return Task.FromResult(
                        JsonSerializer.Serialize(new { success = false, error = "invalid_arguments", message = "Expected {\"sessionId\": \"...\"}" }));
                }

                if (!sessions.TrySetActive(sessionId))
                    return Task.FromResult(
                        JsonSerializer.Serialize(new { success = false, error = "session_not_found", message = $"No session with ID '{sessionId}'. Use list_apps to see available sessions." }));

                var active = sessions.ActiveSession!;
                return Task.FromResult(
                    JsonSerializer.Serialize(new
                    {
                        success = true,
                        active = new
                        {
                            sessionId   = active.SessionId,
                            displayName = active.Identity.DisplayName,
                            mcpUrl      = active.McpUrl
                        }
                    }));
            });
    }
}
