using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using zRover.Core;
using zRover.Core.Logging;
using zRover.Core.Tools.WaitFor;
using Windows.Graphics.Imaging;

namespace zRover.Uwp.Capabilities
{
    /// <summary>
    /// Exposes the <c>wait_for</c> MCP tool. The call blocks until a condition is
    /// satisfied or the timeout expires — the client does not need to poll.
    ///
    /// Supported conditions:
    ///   visual_stable — polls screenshots; returns when the screen hash has not changed
    ///                   for <c>stabilityMs</c> consecutive milliseconds.
    ///   log_match     — polls the in-memory log store; returns as soon as any entry
    ///                   written after the call started matches the given pattern.
    /// </summary>
    internal sealed class WaitForCapability : IDebugCapability
    {
        private DebugHostContext? _context;

        public string Name => "WaitFor";

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
  ""required"": [""condition""],
  ""properties"": {
    ""condition"": {
      ""type"": ""string"",
      ""enum"": [""visual_stable"", ""log_match""],
      ""description"": ""'visual_stable': waits until the screen stops changing. 'log_match': waits until a log entry matching 'pattern' appears.""
    },
    ""timeoutMs"": { ""type"": ""integer"", ""default"": 5000, ""description"": ""Maximum time to wait in milliseconds before returning with success=false."" },
    ""stabilityMs"": { ""type"": ""integer"", ""default"": 400, ""description"": ""[visual_stable] How long the screen must remain pixel-identical before returning success."" },
    ""intervalMs"": { ""type"": ""integer"", ""default"": 150, ""description"": ""How often to re-check the condition, in milliseconds. Min 50."" },
    ""pattern"": { ""type"": ""string"", ""description"": ""[log_match] Substring or regex matched (case-insensitive) against log message and category."" }
  }
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "wait_for",
                "Waits until a condition is met and returns only then (blocking call). " +
                "Use after inject_tap, dispatch_action, or any operation that triggers async work. " +
                "Conditions: " +
                "'visual_stable' — repeatedly captures screenshots and returns once the screen has not changed " +
                "for stabilityMs ms. Handles animations, page transitions, and loading indicators. " +
                "'log_match' — watches the in-memory log store and returns as soon as a log entry matching " +
                "'pattern' (substring or regex) is written. Useful when the app logs semantic events " +
                "(e.g. 'NavigationCompleted', 'DataLoaded'). " +
                "Returns { success, condition, elapsedMs } on success or { success: false, reason: 'timeout' } " +
                "if the condition was not met within timeoutMs.",
                Schema,
                WaitForAsync);
        }

        private async Task<string> WaitForAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<WaitForRequest>(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)
                    ?? new WaitForRequest();

                int timeout = req.TimeoutMs > 0 ? req.TimeoutMs : 5000;
                int interval = Math.Max(50, req.IntervalMs);

                var sw = Stopwatch.StartNew();

                bool success = string.Equals(req.Condition, "log_match", StringComparison.OrdinalIgnoreCase)
                    ? await WaitForLogMatchAsync(req.Pattern, sw, timeout, interval).ConfigureAwait(false)
                    : await WaitForVisualStableAsync(req.StabilityMs > 0 ? req.StabilityMs : 400, sw, timeout, interval).ConfigureAwait(false);

                return JsonConvert.SerializeObject(new WaitForResponse
                {
                    Success = success,
                    Condition = req.Condition,
                    ElapsedMs = (int)sw.ElapsedMilliseconds,
                    Reason = success ? null : "timeout"
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        // ---------------------------------------------------------------
        // visual_stable

        private async Task<bool> WaitForVisualStableAsync(int stabilityMs, Stopwatch sw, int timeout, int interval)
        {
            ulong? lastHash = null;
            long stableFromMs = -1;

            while (sw.ElapsedMilliseconds < timeout)
            {
                ulong hash = await CaptureFrameHashAsync().ConfigureAwait(false);

                if (hash == lastHash)
                {
                    if (stableFromMs < 0) stableFromMs = sw.ElapsedMilliseconds;
                    if (sw.ElapsedMilliseconds - stableFromMs >= stabilityMs)
                        return true;
                }
                else
                {
                    lastHash = hash;
                    stableFromMs = -1;
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Captures a screenshot and returns a fast FNV-1a hash over a sampled subset
        /// of pixel bytes. Equal hashes mean the screen has not visually changed.
        /// </summary>
        private async Task<ulong> CaptureFrameHashAsync()
        {
            SoftwareBitmap? bitmap = null;

            if (_context!.RunOnUiThread != null)
            {
                await _context.RunOnUiThread(async () =>
                {
                    bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            else
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);
            }

            if (bitmap == null) return 0;

            var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());

            // FNV-1a over every 64th pixel (256-byte stride) — fast and sufficient for stability detection
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < pixels.Length; i += 256)
            {
                hash ^= pixels[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }

        // ---------------------------------------------------------------
        // log_match

        private async Task<bool> WaitForLogMatchAsync(string? pattern, Stopwatch sw, int timeout, int interval)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            // Only consider entries written after this call started
            var startTime = DateTimeOffset.UtcNow;

            while (sw.ElapsedMilliseconds < timeout)
            {
                var snapshot = _context!.LogStore.GetSnapshot();
                for (int i = snapshot.Count - 1; i >= 0; i--)
                {
                    var entry = snapshot[i];
                    if (entry.Timestamp < startTime) break; // entries are oldest-first; stop when we pass our start
                    if (MatchesPattern(entry, pattern!))
                        return true;
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }

            return false;
        }

        private static bool MatchesPattern(LogEntry entry, string pattern)
        {
            try
            {
                var opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                return Regex.IsMatch(entry.Message, pattern, opts)
                    || Regex.IsMatch(entry.Category, pattern, opts);
            }
            catch (ArgumentException)
            {
                // Pattern is not valid regex — fall back to plain substring match
                return entry.Message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
                    || entry.Category.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
