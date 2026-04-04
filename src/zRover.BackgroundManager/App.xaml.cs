using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using zRover.BackgroundManager.Packages;
using zRover.BackgroundManager.Server;
using zRover.BackgroundManager.Sessions;

namespace zRover.BackgroundManager;

public partial class App : Application
{
    internal static IServiceProvider? Services { get; set; }

    private static App? _instance;
    private static DispatcherQueue? _dispatcherQueue;
    private Window? _window;
    private Window? _keepAlive; // Hidden window that keeps the dispatcher loop alive
    private SessionNotificationService? _notificationService;

    public App()
    {
        _instance = this;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
        }
        catch
        {
            // Notification registration requires COM activation entries in the
            // manifest (packaged) or a registered AUMID (unpackaged).
            // Silently skip — the app works without notifications.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var registry = Services!.GetRequiredService<SessionRegistry>();
        _notificationService = new SessionNotificationService(registry);

        // Create a hidden window to keep the WinUI dispatcher loop alive.
        // Without this, Application.Start() exits when the user closes the
        // main window, making re-activation impossible.
        _keepAlive = new Window { Title = "" };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_keepAlive);
        ShowWindow(hwnd, 0 /* SW_HIDE */);

        ShowMainWindow();
    }

    internal static void ShowMainWindow()
    {
        if (_instance == null) return;

        if (_instance._window == null)
        {
            _instance._window = new MainWindow();
            _instance._window.Closed += (_, _) => _instance._window = null;
        }
        _instance._window.Activate();
        BringToForeground(_instance._window);
    }

    private static void BringToForeground(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9 /* SW_RESTORE */);

        // Windows blocks SetForegroundWindow unless the caller is the foreground
        // process. Temporarily attach to the foreground thread so the OS allows it.
        var foregroundHwnd = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThread = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);

        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);

        if (attached)
            AttachThreadInput(currentThread, foregroundThread, false);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Called from the named-pipe listener (background thread) when a second
    /// instance is launched. Marshals to the UI thread.
    /// </summary>
    internal static void ActivateFromExternal()
    {
        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
    }

    /// <summary>
    /// Handles named-pipe commands beyond simple activation.
    /// Supports: enable-external, enable-external:{port}, disable-external,
    /// connect:{url}, connect:{url}:{token}
    /// </summary>
    internal static void HandlePipeCommand(string message)
    {
        if (Services == null) return;
        var log = Services.GetService<ILogger<App>>();

        _ = Task.Run(async () =>
        {
            try
            {
                if (message.StartsWith("enable-external"))
                {
                    var external = Services.GetRequiredService<ExternalAccessManager>();
                    int port = 5201;
                    var parts = message.Split(':');
                    if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
                    await external.EnableAsync(port);
                    log?.LogInformation("External access enabled via pipe command on port {Port}", port);
                }
                else if (message == "disable-external")
                {
                    var external = Services.GetRequiredService<ExternalAccessManager>();
                    await external.DisableAsync();
                    log?.LogInformation("External access disabled via pipe command");
                }
                else if (message == "enable-package-install")
                {
                    var pkgInstall = Services.GetRequiredService<PackageInstallManager>();
                    await pkgInstall.EnableAsync();
                    log?.LogInformation("Package install enabled via pipe command");
                    _dispatcherQueue?.TryEnqueue(ShowMainWindow);
                }
                else if (message == "disable-package-install")
                {
                    var pkgInstall = Services.GetRequiredService<PackageInstallManager>();
                    pkgInstall.Disable();
                    log?.LogInformation("Package install disabled via pipe command");
                }
                else if (message.StartsWith("connect:"))
                {
                    // Format: connect:{url} or connect:{url}:{token}
                    var payload = message["connect:".Length..];
                    string? token = null;
                    string url;

                    // The URL contains "://" so we need to be careful splitting
                    // Look for token after the last space, or use a pipe-delimited format
                    var tokenSep = payload.LastIndexOf('|');
                    if (tokenSep > 0)
                    {
                        url = payload[..tokenSep];
                        token = payload[(tokenSep + 1)..];
                    }
                    else
                    {
                        url = payload;
                    }

                    var managers = Services.GetRequiredService<RemoteManagerRegistry>();
                    await managers.ConnectAsync(url, token);
                    log?.LogInformation("Connected to remote manager via pipe command: {Url}", url);
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Failed to handle pipe command: {Message}", message);
            }
        });
    }

    /// <summary>
    /// Handles <c>zrover://</c> protocol activation URIs.
    /// Supported paths: enable-external, disable-external, connect, status
    /// </summary>
    internal static void HandleProtocolActivation(Uri uri)
    {
        if (Services == null) return;
        var log = Services.GetService<ILogger<App>>();

        var host = uri.Host.ToLowerInvariant();
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        _ = Task.Run(async () =>
        {
            try
            {
                switch (host)
                {
                    case "enable-external":
                    {
                        var external = Services.GetRequiredService<ExternalAccessManager>();
                        int port = 5201;
                        if (query["port"] is string p && int.TryParse(p, out var parsed)) port = parsed;
                        await external.EnableAsync(port);
                        log?.LogInformation("External access enabled via protocol activation on port {Port}", port);
                        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
                        break;
                    }
                    case "disable-external":
                    {
                        var external = Services.GetRequiredService<ExternalAccessManager>();
                        await external.DisableAsync();
                        log?.LogInformation("External access disabled via protocol activation");
                        break;
                    }
                    case "enable-package-install":
                    {
                        var pkgInstall = Services.GetRequiredService<PackageInstallManager>();
                        await pkgInstall.EnableAsync();
                        log?.LogInformation("Package install enabled via protocol activation");
                        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
                        break;
                    }
                    case "disable-package-install":
                    {
                        var pkgInstall = Services.GetRequiredService<PackageInstallManager>();
                        pkgInstall.Disable();
                        log?.LogInformation("Package install disabled via protocol activation");
                        break;
                    }
                    case "connect":
                    {
                        var url = query["url"];
                        var token = query["token"];
                        var alias = query["alias"];
                        if (string.IsNullOrEmpty(url))
                        {
                            log?.LogWarning("Protocol activation connect: missing 'url' parameter");
                            break;
                        }
                        var managers = Services.GetRequiredService<RemoteManagerRegistry>();
                        await managers.ConnectAsync(url, token, alias);
                        log?.LogInformation("Connected to remote manager via protocol activation: {Url}", url);
                        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
                        break;
                    }
                    case "status":
                        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
                        break;
                    default:
                        log?.LogWarning("Unknown protocol activation host: {Host}", host);
                        break;
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Failed to handle protocol activation: {Uri}", uri);
            }
        });
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            ShowMainWindow();
            // Re-show the notification so it remains visible while sessions exist
            _instance?._notificationService?.Update();
        });
    }
}
