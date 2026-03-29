using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace zRover.Uwp.Sample
{
    sealed partial class App : Application
    {
        public App()
        {
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
            var actionableApp = rootFrame.Content as zRover.Core.IActionableApp;
            await zRover.Uwp.RoverMcp.StartAsync("zRover.Uwp.Sample", port: 5100,
                launchFullTrust: () => FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("McpServer").AsTask(),
                actionableApp: actionableApp);
            zRover.Uwp.RoverMcp.Log("App", $"zRover MCP started — port 5100, launch kind: {e.Kind}");
#endif
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            if (zRover.Uwp.RoverMcp.HandleBackgroundActivation(args)) return;
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
            zRover.Uwp.RoverMcp.Stop();
#endif
            deferral.Complete();
        }
    }
}
