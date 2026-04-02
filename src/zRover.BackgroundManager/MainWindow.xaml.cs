using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using zRover.BackgroundManager.Server;
using zRover.BackgroundManager.Sessions;
using zRover.Core.Sessions;

namespace zRover.BackgroundManager;

public sealed partial class MainWindow : Window
{
    private readonly SessionRegistry _registry;
    private readonly RemoteManagerRegistry _managers;
    private readonly ExternalAccessManager _external;
    private readonly IConfiguration _config;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainWindow()
    {
        InitializeComponent();
        Title = "zRover Background Manager";

        var services = App.Services!;
        _registry = services.GetRequiredService<SessionRegistry>();
        _managers = services.GetRequiredService<RemoteManagerRegistry>();
        _external = services.GetRequiredService<ExternalAccessManager>();
        _config = services.GetRequiredService<IConfiguration>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _registry.SessionsChanged += OnSessionsChanged;
        _registry.ActiveSessionChanged += OnActiveSessionChanged;
        _managers.ManagersChanged += OnManagersChanged;
        _external.StateChanged += OnExternalStateChanged;
        Closed += OnClosed;

        RefreshState();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _registry.SessionsChanged -= OnSessionsChanged;
        _registry.ActiveSessionChanged -= OnActiveSessionChanged;
        _managers.ManagersChanged -= OnManagersChanged;
        _external.StateChanged -= OnExternalStateChanged;
    }

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnManagersChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnExternalStateChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshExternalState);

    private void RefreshState()
    {
        var url = _config["Urls"] ?? "http://localhost:5200";
        ListeningUrlText.Text = $"URL: {url}";

        // Sessions
        var sessions = _registry.Sessions;
        var activeId = _registry.ActiveSession?.SessionId;

        var items = sessions.Select(s =>
        {
            var originLabel = s is PropagatedSession ps ? $" (via {ps.Origin.ManagerAlias})" : "";
            return new SessionViewModel
            {
                SessionId = s.SessionId,
                DisplayName = s.Identity.DisplayName + originLabel,
                McpUrl = s.McpUrl,
                IsConnected = s.IsConnected,
                IsActive = s.SessionId == activeId
            };
        }).ToList();

        SessionsList.ItemsSource = items;
        NoSessionsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Remote managers
        var managerItems = _managers.Managers.Select(m => new ManagerViewModel
        {
            ManagerId = m.ManagerId,
            DisplayText = $"{m.Alias} — {m.McpUrl} — {m.AppCount} app(s)",
            IsConnected = m.IsConnected
        }).ToList();

        ManagersList.ItemsSource = managerItems;
        ManagersList.Visibility = managerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoManagersText.Visibility = managerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshExternalState();
    }

    private void RefreshExternalState()
    {
        // Sync toggle without firing event
        ExternalToggle.Toggled -= OnExternalToggled;
        ExternalToggle.IsOn = _external.IsEnabled;
        ExternalToggle.Toggled += OnExternalToggled;

        ExternalInfoPanel.Visibility = _external.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (_external.IsEnabled)
        {
            ExternalUrlText.Text = $"External URL: {_external.ExternalUrl}";
            ExternalTokenText.Text = $"Token: {_external.BearerToken}";
        }
    }

    private async void OnExternalToggled(object sender, RoutedEventArgs e)
    {
        if (ExternalToggle.IsOn)
            await _external.EnableAsync();
        else
            await _external.DisableAsync();
    }

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        var link = _external.GetConnectionLink();
        if (link == null) return;

        var dp = new DataPackage();
        dp.SetText(link);
        Clipboard.SetContent(dp);
    }

    private async void OnDisconnectManagerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button btn && btn.Tag is string managerId)
            await _managers.DisconnectAsync(managerId);
    }
}

public class SessionViewModel
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string McpUrl { get; set; } = "";
    public bool IsConnected { get; set; }
    public bool IsActive { get; set; }

    public string Details => $"{SessionId} • {McpUrl}";

    public SolidColorBrush StatusColor => IsConnected
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);

    public Visibility ActiveVisibility => IsActive
        ? Visibility.Visible
        : Visibility.Collapsed;
}

public class ManagerViewModel
{
    public string ManagerId { get; set; } = "";
    public string DisplayText { get; set; } = "";
    public bool IsConnected { get; set; }

    public SolidColorBrush StatusColor => IsConnected
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);
}
