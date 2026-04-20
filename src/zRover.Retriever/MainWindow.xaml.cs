using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using zRover.Retriever.Packages;
using zRover.Retriever.Server;
using zRover.Retriever.Sessions;
using zRover.Core.Sessions;

namespace zRover.Retriever;

public sealed partial class MainWindow : Window
{
    private readonly SessionRegistry _registry;
    private readonly RemoteManagerRegistry _managers;
    private readonly ExternalAccessManager _external;
    private readonly PackageInstallManager _packageInstall;
    private readonly ControllerRegistry _controllers;
    private readonly IConfiguration _config;
    private readonly RetrieverSettingsStore _settingsStore;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainWindow()
    {
        InitializeComponent();
        Title = "zRover Retriever";

        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        AppVersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

        LocalDeviceInfoText.Text = BuildLocalDeviceInfo();

        var services = App.Services!;
        _registry = services.GetRequiredService<SessionRegistry>();
        _managers = services.GetRequiredService<RemoteManagerRegistry>();
        _external = services.GetRequiredService<ExternalAccessManager>();
        _packageInstall = services.GetRequiredService<PackageInstallManager>();
        _controllers = services.GetRequiredService<ControllerRegistry>();
        _config = services.GetRequiredService<IConfiguration>();
        _settingsStore = services.GetRequiredService<RetrieverSettingsStore>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _registry.SessionsChanged += OnSessionsChanged;
        _registry.ActiveSessionChanged += OnActiveSessionChanged;
        _managers.ManagersChanged += OnManagersChanged;
        _external.StateChanged += OnExternalStateChanged;
        _packageInstall.StateChanged += OnPackageInstallStateChanged;
        _controllers.ControllersChanged += OnControllersChanged;
        Closed += OnClosed;

        RefreshState();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _registry.SessionsChanged -= OnSessionsChanged;
        _registry.ActiveSessionChanged -= OnActiveSessionChanged;
        _managers.ManagersChanged -= OnManagersChanged;
        _external.StateChanged -= OnExternalStateChanged;
        _packageInstall.StateChanged -= OnPackageInstallStateChanged;
        _controllers.ControllersChanged -= OnControllersChanged;
    }

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnManagersChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshState);

    private void OnExternalStateChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshExternalState);

    private void OnPackageInstallStateChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshPackageInstallState);

    private void OnControllersChanged(object? sender, EventArgs e) =>
        _dispatcherQueue.TryEnqueue(RefreshControllers);

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

        // Remote managers (controlled)
        var managerItems = _managers.Managers.Select(m =>
        {
            var namePart = m.MachineName ?? m.Alias;
            var archPart = m.Architecture ?? "unknown";
            var subtitle = string.IsNullOrWhiteSpace(m.OsDescription)
                ? archPart
                : $"{archPart} \u2022 {m.OsDescription}";
            return new ManagerViewModel
            {
                ManagerId = m.ManagerId,
                DisplayText = $"{namePart} ({subtitle})",
                DetailText = $"{m.McpUrl} \u2014 {m.AppCount} app(s)",
                IsConnected = m.IsConnected
            };
        }).ToList();

        ManagersList.ItemsSource = managerItems;
        ManagersList.Visibility = managerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoManagersText.Visibility = managerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Past remote retrievers: saved entries not currently connected
        var connectedUrls = _managers.Managers.Select(m => m.McpUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pastItems = _settingsStore.Load().SavedRemoteManagers
            .Where(s => !connectedUrls.Contains(s.McpUrl))
            .Select(s => new PastManagerViewModel { McpUrl = s.McpUrl, BearerToken = s.BearerToken, Alias = s.Alias })
            .ToList();
        PastManagersList.ItemsSource = pastItems;
        PastManagersPanel.Visibility = pastItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshExternalState();
        RefreshPackageInstallState();
        RefreshControllers();
    }

    private void RefreshControllers()
    {
        var controllerItems = _controllers.Controllers.Select(c => new ControllerViewModel
        {
            DisplayText = c.RemoteAddress,
            DetailText = $"Connected since {c.ConnectedSince.LocalDateTime:g}",
        }).ToList();

        ControllersList.ItemsSource = controllerItems;
        ControllersList.Visibility = controllerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoControllersText.Visibility = controllerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Builds the one-line description of the local device shown under the title bar,
    /// e.g. "MY-PC \u2022 x64 \u2022 Microsoft Windows 10.0.26100".
    /// </summary>
    private static string BuildLocalDeviceInfo()
    {
        var name = Environment.MachineName;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64   => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86   => "x86",
            System.Runtime.InteropServices.Architecture.Arm   => "arm",
            var other                                          => other.ToString().ToLowerInvariant(),
        };
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        return $"{name} \u2022 {arch} \u2022 {os}";
    }

    private void RefreshPackageInstallState()
    {
        PackageInstallToggle.Toggled -= OnPackageInstallToggled;
        PackageInstallToggle.IsOn = _packageInstall.IsEnabled;
        PackageInstallToggle.Toggled += OnPackageInstallToggled;
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
            _ = UpdateQrCodeAsync(_external.GetConnectionLink());
        }
        else
        {
            QrCodeImage.Source = null;
        }
    }

    private async Task UpdateQrCodeAsync(string? link)
    {
        if (string.IsNullOrEmpty(link)) return;

        // Generate PNG bytes off the UI thread
        byte[] pngBytes = await Task.Run(() =>
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrData      = qrGenerator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
            var code              = new PngByteQRCode(qrData);
            return code.GetGraphic(8, darkColorRgba: new byte[] { 0, 0, 0, 255 },
                                      lightColorRgba: new byte[] { 255, 255, 255, 255 });
        });

        // Decode into a BitmapImage on the UI thread
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        QrCodeImage.Source = bitmap;
    }

    private async void OnExternalToggled(object sender, RoutedEventArgs e)
    {
        if (ExternalToggle.IsOn)
            await _external.EnableAsync();
        else
            await _external.DisableAsync();
    }

    private async void OnPackageInstallToggled(object sender, RoutedEventArgs e)
    {
        if (PackageInstallToggle.IsOn)
            await _packageInstall.EnableAsync();
        else
            _packageInstall.Disable();
    }

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        var link = _external.GetConnectionLink();
        if (link == null) return;

        var dp = new DataPackage();
        dp.SetText(link);
        Clipboard.SetContent(dp);
    }

    private async void OnReconnectManagerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button btn ||
            btn.Tag is not PastManagerViewModel vm) return;

        btn.IsEnabled = false;
        try { await _managers.ConnectAsync(vm.McpUrl, vm.BearerToken, vm.Alias); }
        catch { /* connection failed — stays in past list */ }
        finally { btn.IsEnabled = true; }
    }

    private void OnForgetManagerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button btn ||
            btn.Tag is not PastManagerViewModel vm) return;

        var settings = _settingsStore.Load();
        settings.SavedRemoteManagers.RemoveAll(s =>
            string.Equals(s.McpUrl, vm.McpUrl, StringComparison.OrdinalIgnoreCase));
        _settingsStore.Save(settings);
        _dispatcherQueue.TryEnqueue(RefreshState);
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
    public string DetailText { get; set; } = "";
    public bool IsConnected { get; set; }

    public SolidColorBrush StatusColor => IsConnected
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);
}

public class ControllerViewModel
{
    public string DisplayText { get; set; } = "";
    public string DetailText { get; set; } = "";
}

public class PastManagerViewModel
{
    public string McpUrl { get; set; } = "";
    public string? BearerToken { get; set; }
    public string Alias { get; set; } = "";

    public string DisplayText => string.IsNullOrEmpty(Alias) ? McpUrl : $"{Alias} — {McpUrl}";
}
