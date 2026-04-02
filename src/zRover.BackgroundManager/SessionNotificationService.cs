using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using zRover.BackgroundManager.Sessions;
using zRover.Core.Sessions;

namespace zRover.BackgroundManager;

/// <summary>
/// Shows a persistent Windows notification while at least one session is connected.
/// The notification auto-updates as sessions change, and clicking it opens the main window.
/// Uses Tag-based replacement so subsequent updates don't re-popup the toast.
/// </summary>
public sealed class SessionNotificationService : IDisposable
{
    private const string NotificationTag = "rover-sessions";
    private const string NotificationGroup = "rover";

    private readonly SessionRegistry _registry;
    private bool _visible;

    public SessionNotificationService(SessionRegistry registry)
    {
        _registry = registry;
        _registry.SessionsChanged += OnChanged;
        _registry.ActiveSessionChanged += OnActiveChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Update();
    private void OnActiveChanged(object? sender, ActiveSessionChangedEventArgs e) => Update();

    public void Update()
    {
        var sessions = _registry.Sessions;
        var activeId = _registry.ActiveSession?.SessionId;

        if (sessions.Count == 0)
        {
            if (_visible)
            {
                _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(NotificationTag, NotificationGroup);
                _visible = false;
            }
            return;
        }

        var lines = sessions.Select(s =>
        {
            var marker = s.SessionId == activeId ? "\u25b6 " : "\u2022 ";
            var suffix = s is PropagatedSession ps ? $" ({ps.Origin.ManagerAlias})" : "";
            return $"{marker}{s.Identity.DisplayName}{suffix}";
        });

        var notification = new AppNotificationBuilder()
            .AddText($"zRover \u2014 {sessions.Count} app(s) connected")
            .AddText(string.Join("\n", lines))
            .BuildNotification();

        notification.Tag = NotificationTag;
        notification.Group = NotificationGroup;

        // Suppress the popup for updates — only the first notification pops up;
        // subsequent changes silently replace it in the Action Center.
        if (_visible)
            notification.SuppressDisplay = true;

        AppNotificationManager.Default.Show(notification);
        _visible = true;
    }

    public void Dispose()
    {
        _registry.SessionsChanged -= OnChanged;
        _registry.ActiveSessionChanged -= OnActiveChanged;

        if (_visible)
        {
            _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(NotificationTag, NotificationGroup);
            _visible = false;
        }
    }
}
