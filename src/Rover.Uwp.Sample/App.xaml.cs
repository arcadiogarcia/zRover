using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Rover.Uwp.Sample
{
    sealed partial class App : Application
    {
        public App()
        {
            this.UnhandledException += (s, args) =>
            {
                args.Handled = true;
                try
                {
                    var path = System.IO.Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                        "crash.log");
                    System.IO.File.AppendAllText(path,
                        $"{DateTimeOffset.Now:o} UNHANDLED: {args.Exception}\r\n");
                }
                catch { }
            };
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                Window.Current.Activate();
            }

#if DEBUG
            try
            {
                var actionableApp = rootFrame.Content as Rover.Core.IActionableApp;
                await Rover.Uwp.RoverMcp.StartAsync("Rover.Uwp.Sample", port: 5100,
                    launchFullTrust: () => FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("McpServer").AsTask(),
                    actionableApp: actionableApp);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Rover.Sample] Startup error: {ex.Message}");
            }
#endif
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            if (Rover.Uwp.RoverMcp.HandleBackgroundActivation(args)) return;
            base.OnBackgroundActivated(args);
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
#if DEBUG
            Rover.Uwp.RoverMcp.Stop();
#endif
            deferral.Complete();
        }
    }
}
