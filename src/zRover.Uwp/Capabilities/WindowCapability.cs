using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using zRover.Core;
using zRover.Core.Tools.Window;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace zRover.Uwp.Capabilities
{
    /// <summary>
    /// Exposes window management tools — currently <c>resize_page</c>.
    /// </summary>
    internal sealed class WindowCapability : IDebugCapability
    {
        private DebugHostContext? _context;

        public string Name => "Window";

        public Task StartAsync(DebugHostContext context)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _context = null;
            return Task.CompletedTask;
        }

        private const string ResizeSchema = @"{
  ""type"": ""object"",
  ""required"": [""width"", ""height""],
  ""properties"": {
    ""width"": { ""type"": ""integer"", ""description"": ""Requested window width in DIPs (device-independent pixels)."" },
    ""height"": { ""type"": ""integer"", ""description"": ""Requested window height in DIPs (device-independent pixels)."" }
  }
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "resize_page",
                "Requests a resize of the UWP app window to the specified width and height in DIPs " +
                "(device-independent pixels — the same unit as XAML layout). " +
                "The OS may adjust the final size to enforce minimum dimensions or display constraints. " +
                "Returns the actual window VisibleBounds after the resize attempt. " +
                "Useful for testing adaptive/responsive layouts and VisualStateManager breakpoints.",
                ResizeSchema,
                ResizeAsync);
        }

        private async Task<string> ResizeAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ResizeWindowRequest>(argsJson)
                          ?? new ResizeWindowRequest();

                bool accepted = false;
                double actualWidth = 0, actualHeight = 0;

                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(() =>
                    {
                        var view = ApplicationView.GetForCurrentView();
                        accepted = view.TryResizeView(new Size(req.Width, req.Height));
                        // Return ActualWidth/Height of Window.Current.Content — same reference
                        // as get_ui_tree bounds and capture_current_view windowWidth/windowHeight.
                        var content = Windows.UI.Xaml.Window.Current?.Content as Windows.UI.Xaml.FrameworkElement;
                        if (content != null)
                        {
                            actualWidth  = content.ActualWidth;
                            actualHeight = content.ActualHeight;
                        }
                        else
                        {
                            var bounds = view.VisibleBounds;
                            actualWidth  = bounds.Width;
                            actualHeight = bounds.Height;
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                return JsonConvert.SerializeObject(new ResizeWindowResponse
                {
                    Success = accepted,
                    ActualWidth = (int)actualWidth,
                    ActualHeight = (int)actualHeight
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }
    }
}
