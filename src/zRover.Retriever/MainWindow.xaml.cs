using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using System.Collections.Generic;
using System.Linq;
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

        // Modern WinUI 3 title bar: extend content into the caption area and
        // designate our custom drag region. Caption buttons remain handled by
        // the framework, including theme/accent updates.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Apply Mica backdrop from code-behind. Constructing MicaBackdrop in
        // XAML can fail on unpackaged hosts; setting it here is more reliable
        // and lets us silently fall back if the OS doesn't support it (Win10).
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch { /* Mica unsupported — keep default backdrop. */ }

        // Sensible default window size so the layout opens fully visible.
        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
        }
        catch { /* AppWindow may not be available in some hosts */ }

        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        AppVersionText.Text = $"• v{v.Major}.{v.Minor}.{v.Build}";

        LocalDeviceInfoText.Text = BuildLocalDeviceInfo();

#if DEBUG
        // Screenshot / marketing mode: when ZROVER_MOCK_UI=1 is set we bypass
        // every real service and stuff the UI with synthetic data so the app
        // can be captured for the Store listing without needing real sessions
        // or external pairing. Only compiled into Debug builds.
        if (Environment.GetEnvironmentVariable("ZROVER_MOCK_UI") == "1")
        {
            _registry      = null!;
            _managers      = null!;
            _external      = null!;
            _packageInstall = null!;
            _controllers   = null!;
            _config        = null!;
            _settingsStore = null!;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            ApplyMockData();
            return;
        }
#endif

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

        // AdaptiveTrigger in WinUI 3 desktop windows can miss size changes,
        // so drive the narrow/wide layout swap directly from the root grid's
        // SizeChanged event. This guarantees the right column collapses
        // beneath the left one whenever the window gets too narrow.
        if (Content is FrameworkElement root)
        {
            root.SizeChanged += OnRootSizeChanged;
        }

        RefreshState();
    }

    private const double NarrowBreakpoint = 900.0;
    private bool? _isWide;

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= NarrowBreakpoint;
        if (_isWide == wide) return;
        _isWide = wide;

        if (wide)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(380);
            FirstRow.Height = GridLength.Auto;
            SecondRow.Height = new GridLength(0);
            BodyGrid.ColumnSpacing = 20;
            Grid.SetColumn(RightPanel, 1);
            Grid.SetRow(RightPanel, 0);
        }
        else
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(0);
            FirstRow.Height = GridLength.Auto;
            SecondRow.Height = GridLength.Auto;
            BodyGrid.ColumnSpacing = 0;
            Grid.SetColumn(RightPanel, 0);
            Grid.SetRow(RightPanel, 1);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // In mock mode (#if DEBUG, ZROVER_MOCK_UI=1) the service fields are null,
        // so we never subscribed to their events — skip the unhook.
        if (_registry is null) return;

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
        ListeningUrlText.Text = url;

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
        SessionsBadge.Value = items.Count;
        NoSessionsPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ManagersBadge.Value = managerItems.Count;
        ManagersList.Visibility = managerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoManagersPanel.Visibility = managerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
            DisplayText = string.IsNullOrWhiteSpace(c.UserAgent) ? c.RemoteAddress : c.UserAgent!,
            DetailText = string.IsNullOrWhiteSpace(c.UserAgent)
                ? $"Connected since {c.ConnectedSince.LocalDateTime:g}"
                : $"{c.RemoteAddress} \u2014 connected since {c.ConnectedSince.LocalDateTime:g}",
        }).ToList();

        ControllersList.ItemsSource = controllerItems;
        ControllersBadge.Value = controllerItems.Count;
        ControllersList.Visibility = controllerItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoControllersPanel.Visibility = controllerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

#if DEBUG
    /// <summary>
    /// Populate every visible piece of the UI with synthetic, screenshot-friendly
    /// data loaded from the embedded mock-ui-data.json resource. Active when the
    /// env var ZROVER_MOCK_UI=1 is set on a Debug build.
    /// </summary>
    private void ApplyMockData()
    {
        var mock = LoadMockData();

        // ── Hero ─────────────────────────────────────────────────────────────
        ListeningUrlText.Text    = mock.LocalUrl;
        LocalDeviceInfoText.Text = mock.LocalDeviceInfo;
        StatusText.Text          = mock.StatusText;

        // ── Sessions ─────────────────────────────────────────────────────────
        var sessions = mock.Sessions.Select(s => new SessionViewModel
        {
            SessionId   = s.SessionId,
            DisplayName = s.DisplayName,
            McpUrl      = s.McpUrl,
            IsConnected = s.IsConnected,
            IsActive    = s.IsActive,
        }).ToList();
        SessionsList.ItemsSource = sessions;
        SessionsBadge.Value = sessions.Count;
        NoSessionsPanel.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsList.Visibility    = sessions.Count > 0  ? Visibility.Visible : Visibility.Collapsed;

        // ── MCP clients ──────────────────────────────────────────────────────
        var controllers = mock.Controllers.Select(c => new ControllerViewModel
        {
            DisplayText = c.DisplayText,
            DetailText  = c.DetailText,
        }).ToList();
        ControllersList.ItemsSource = controllers;
        ControllersBadge.Value = controllers.Count;
        NoControllersPanel.Visibility = controllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ControllersList.Visibility    = controllers.Count > 0  ? Visibility.Visible : Visibility.Collapsed;

        // ── Remote retrievers ────────────────────────────────────────────────
        var managers = mock.RemoteRetrievers.Select(m => new ManagerViewModel
        {
            ManagerId   = m.ManagerId,
            DisplayText = m.DisplayText,
            DetailText  = m.DetailText,
            IsConnected = m.IsConnected,
        }).ToList();
        ManagersList.ItemsSource = managers;
        ManagersBadge.Value = managers.Count;
        NoManagersPanel.Visibility = managers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ManagersList.Visibility    = managers.Count > 0  ? Visibility.Visible : Visibility.Collapsed;
        PastManagersPanel.Visibility = Visibility.Collapsed;

        // ── External access ──────────────────────────────────────────────────
        ExternalToggle.IsOn = mock.ExternalAccess.Enabled;
        ExternalToggle.Toggled += MockExternalToggled;

        if (mock.ExternalAccess.Enabled)
        {
            ExternalLoadingPanel.Visibility = Visibility.Collapsed;
            ExternalInfoPanel.Visibility    = Visibility.Visible;
            ExternalUrlText.Text   = mock.ExternalAccess.Url;
            ExternalTokenText.Text = mock.ExternalAccess.BearerToken;
            _ = UpdateQrCodeAsync(mock.ExternalAccess.QrCodePayload);
        }
        else
        {
            ExternalLoadingPanel.Visibility = Visibility.Collapsed;
            ExternalInfoPanel.Visibility    = Visibility.Collapsed;
        }

        // ── Advanced toggles ─────────────────────────────────────────────────
        PackageInstallToggle.IsOn = mock.PackageInstall.Enabled;
        PackageInstallToggle.Toggled += MockPackageInstallToggled;
    }

    private MockUiData LoadMockData()
    {
        var asm = typeof(MainWindow).Assembly;
        using var stream = asm.GetManifestResourceStream("MockUiData.json")
            ?? throw new InvalidOperationException(
                "Mock UI data resource not embedded. Rebuild the Debug configuration.");
        using var reader = new System.IO.StreamReader(stream);
        var json = reader.ReadToEnd();
        return System.Text.Json.JsonSerializer.Deserialize<MockUiData>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new InvalidOperationException("mock-ui-data.json is empty or malformed.");
    }

    // No-op handlers so the toggles snap back if a viewer tries to flip them.
    private void MockExternalToggled(object sender, RoutedEventArgs e)
    {
        ExternalToggle.Toggled -= MockExternalToggled;
        ExternalToggle.IsOn = true;
        ExternalToggle.Toggled += MockExternalToggled;
    }

    private void MockPackageInstallToggled(object sender, RoutedEventArgs e)
    {
        PackageInstallToggle.Toggled -= MockPackageInstallToggled;
        PackageInstallToggle.IsOn = false;
        PackageInstallToggle.Toggled += MockPackageInstallToggled;
    }

    private sealed class MockUiData
    {
        public string LocalDeviceInfo { get; set; } = "";
        public string LocalUrl        { get; set; } = "";
        public string StatusText      { get; set; } = "";
        public List<MockSession>     Sessions         { get; set; } = new();
        public List<MockController>  Controllers      { get; set; } = new();
        public List<MockManager>     RemoteRetrievers { get; set; } = new();
        public MockExternalAccess    ExternalAccess   { get; set; } = new();
        public MockPackageInstall    PackageInstall   { get; set; } = new();
    }
    private sealed class MockSession
    {
        public string SessionId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string McpUrl { get; set; } = "";
        public bool IsConnected { get; set; }
        public bool IsActive { get; set; }
    }
    private sealed class MockController
    {
        public string DisplayText { get; set; } = "";
        public string DetailText  { get; set; } = "";
    }
    private sealed class MockManager
    {
        public string ManagerId { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public string DetailText { get; set; } = "";
        public bool IsConnected { get; set; }
    }
    private sealed class MockExternalAccess
    {
        public bool Enabled { get; set; }
        public string Url { get; set; } = "";
        public string BearerToken { get; set; } = "";
        public string QrCodePayload { get; set; } = "";
    }
    private sealed class MockPackageInstall
    {
        public bool Enabled { get; set; }
    }
#endif

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

        // The info panel is only meaningful once we actually have a URL/token.
        // While EnableAsync is still spinning up the listener (or creating the
        // firewall rule on first run), show a small loading indicator instead.
        var hasDetails = _external.IsEnabled && !string.IsNullOrEmpty(_external.ExternalUrl);
        ExternalInfoPanel.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        ExternalLoadingPanel.Visibility =
            (_external.IsEnabled && !hasDetails) ? Visibility.Visible : Visibility.Collapsed;

        if (hasDetails)
        {
            ExternalUrlText.Text = _external.ExternalUrl ?? string.Empty;
            ExternalTokenText.Text = _external.BearerToken ?? string.Empty;
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
        try
        {
            if (ExternalToggle.IsOn)
            {
                // Show the loading panel immediately so the user sees feedback
                // while the listener and firewall rule are being set up.
                ExternalLoadingPanel.Visibility = Visibility.Visible;
                ExternalInfoPanel.Visibility = Visibility.Collapsed;
                await _external.EnableAsync();
            }
            else
            {
                await _external.DisableAsync();
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape an async void handler — it would
            // tear down the UI thread. Snap the toggle back to a safe state
            // and log; future iterations can surface a Toast.
            System.Diagnostics.Debug.WriteLine($"OnExternalToggled failed: {ex}");
            ExternalLoadingPanel.Visibility = Visibility.Collapsed;
            ExternalInfoPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnPackageInstallToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PackageInstallToggle.IsOn)
                await _packageInstall.EnableAsync();
            else
                _packageInstall.Disable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnPackageInstallToggled failed: {ex}");
        }
    }

    private void OnCopyLocalUrlClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = _config["Urls"] ?? "http://localhost:5200";
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy local URL failed: {ex}");
        }
    }

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var link = _external.GetConnectionLink();
            if (link == null) return;

            var dp = new DataPackage();
            dp.SetText(link);
            Clipboard.SetContent(dp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy link failed: {ex}");
        }
    }

    private async void OnReconnectManagerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button btn ||
            btn.Tag is not PastManagerViewModel vm) return;

        btn.IsEnabled = false;
        try { await _managers.ConnectAsync(vm.McpUrl, vm.BearerToken, vm.Alias); }
        catch (Exception ex)
        {
            // connection failed — stays in past list
            System.Diagnostics.Debug.WriteLine($"OnReconnectManagerClicked failed: {ex}");
        }
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
        try
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button btn && btn.Tag is string managerId)
                await _managers.DisconnectAsync(managerId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnDisconnectManagerClicked failed: {ex}");
        }
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

    public string DisconnectAutomationName => $"Disconnect {DisplayText}";
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

    public string ReconnectAutomationName => $"Reconnect to {DisplayText}";
    public string ForgetAutomationName => $"Forget {DisplayText}";
}
