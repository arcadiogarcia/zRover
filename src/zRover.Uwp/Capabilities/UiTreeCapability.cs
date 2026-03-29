using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using zRover.Core;
using zRover.Core.Tools.Screenshot;
using zRover.Core.Tools.UiTree;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace zRover.Uwp.Capabilities
{
    /// <summary>
    /// Exposes the XAML visual tree as a JSON hierarchy via the <c>get_ui_tree</c> MCP tool.
    /// Each node includes element type, x:Name, AutomationProperties.Name, text content,
    /// normalized bounds (0–1 relative to the app window), visibility, and enabled state.
    /// </summary>
    internal sealed class UiTreeCapability : IDebugCapability
    {
        private DebugHostContext? _context;

        public string Name => "UiTree";

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

        private const string Schema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""maxDepth"": { ""type"": ""integer"", ""default"": 32, ""description"": ""Maximum depth to traverse. Reduce to limit output for large trees."" },
    ""visibleOnly"": { ""type"": ""boolean"", ""default"": false, ""description"": ""When true, skips elements where Visibility != Visible."" }
  }
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "get_ui_tree",
                "Returns the XAML visual tree of the running UWP app as a JSON hierarchy. " +
                "Each node includes: 'type' (XAML class name e.g. Button, TextBlock, Grid), " +
                "'name' (x:Name), 'automationName' (AutomationProperties.Name), " +
                "'text' (text content for TextBlock/TextBox/ContentControl), " +
                "'bounds' (normalized 0.0–1.0 rect relative to the app window — same coordinate space as inject_tap), " +
                "'isVisible', 'isEnabled', and 'children'. " +
                "Use this to locate UI elements by name or type and extract their exact bounds for input injection, " +
                "without relying on screenshot pixel estimation.",
                Schema,
                GetUiTreeAsync);
        }

        private async Task<string> GetUiTreeAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<UiTreeRequest>(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)
                    ?? new UiTreeRequest();

                UiTreeNode? root = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(() =>
                    {
                        root = BuildTree(req);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    root = BuildTree(req);
                }

                return JsonConvert.SerializeObject(new UiTreeResponse { Success = true, Root = root });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new UiTreeResponse { Success = false, Error = ex.Message });
            }
        }

        private static UiTreeNode? BuildTree(UiTreeRequest req)
        {
            var windowContent = Window.Current?.Content as FrameworkElement;
            if (windowContent == null)
                return null;

            double winW = windowContent.ActualWidth;
            double winH = windowContent.ActualHeight;
            return WalkElement(windowContent, windowContent, winW, winH, 0, req.MaxDepth, req.VisibleOnly);
        }

        private static UiTreeNode? WalkElement(
            FrameworkElement element,
            FrameworkElement root,
            double winW,
            double winH,
            int depth,
            int maxDepth,
            bool visibleOnly)
        {
            if (visibleOnly && element.Visibility != Visibility.Visible)
                return null;

            var bounds = new NormalizedRect();
            try
            {
                var transform = element.TransformToVisual(root);
                var rect = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                bounds = new NormalizedRect
                {
                    X = winW > 0 ? rect.X / winW : 0,
                    Y = winH > 0 ? rect.Y / winH : 0,
                    Width = winW > 0 ? rect.Width / winW : 0,
                    Height = winH > 0 ? rect.Height / winH : 0
                };
            }
            catch { /* disconnected or zero-size element — leave bounds at zero */ }

            string? name = string.IsNullOrEmpty(element.Name) ? null : element.Name;
            string? automationName = AutomationProperties.GetName(element);
            if (string.IsNullOrEmpty(automationName)) automationName = null;

            bool isVisible = element.Visibility == Visibility.Visible && element.Opacity > 0;
            bool isEnabled = element is Control ctrl ? ctrl.IsEnabled : true;

            var node = new UiTreeNode
            {
                Type = element.GetType().Name,
                Name = name,
                AutomationName = automationName,
                Text = ExtractText(element),
                Bounds = bounds,
                IsVisible = isVisible,
                IsEnabled = isEnabled,
                Children = new List<UiTreeNode>()
            };

            if (depth < maxDepth)
            {
                int childCount = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < childCount; i++)
                {
                    if (VisualTreeHelper.GetChild(element, i) is FrameworkElement childFe)
                    {
                        var childNode = WalkElement(childFe, root, winW, winH, depth + 1, maxDepth, visibleOnly);
                        if (childNode != null)
                            node.Children.Add(childNode);
                    }
                }
            }

            return node;
        }

        private static string? ExtractText(FrameworkElement element)
        {
            if (element is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                return tb.Text;
            if (element is TextBox txb && !string.IsNullOrEmpty(txb.Text))
                return txb.Text;
            if (element is ContentControl cc && cc.Content is string s && !string.IsNullOrEmpty(s))
                return s;
            return null;
        }
    }
}
