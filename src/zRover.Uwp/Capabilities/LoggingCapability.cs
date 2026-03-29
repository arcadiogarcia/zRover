using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Logging;

namespace zRover.Uwp.Capabilities
{
    /// <summary>
    /// zRover debug capability that exposes the in-memory log store to MCP clients
    /// via the <c>get_logs</c> tool. Allows agents and developers to inspect what
    /// is happening inside the host application at any point without attaching a debugger.
    /// </summary>
    internal class LoggingCapability : IDebugCapability
    {
        public string Name => "Logging";

        private IInMemoryLogStore? _store;

        public Task StartAsync(DebugHostContext context)
        {
            _store = RoverLog.Store;
            RoverLog.Info("zRover.Logging", "LoggingCapability started — get_logs tool registered");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            RoverLog.Info("zRover.Logging", "LoggingCapability stopped");
            return Task.CompletedTask;
        }

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                name: "get_logs",
                description:
                    "Returns recent diagnostic log entries from the zRover-instrumented host application. " +
                    "Use this to understand what the app is doing, diagnose failures, inspect lifecycle " +
                    "events, unhandled exceptions, XAML binding errors, and any custom log messages " +
                    "written by the host app. Logs are captured in a bounded ring buffer.",
                inputSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""minimumLevel"": {
      ""type"": ""string"",
      ""enum"": [""trace"", ""debug"", ""info"", ""warning"", ""error"", ""fatal""],
      ""description"": ""Minimum log level to include. Defaults to 'info'."",
      ""default"": ""info""
    },
    ""maxEntries"": {
      ""type"": ""integer"",
      ""description"": ""Maximum number of recent entries to return (newest first). Defaults to 100."",
      ""default"": 100
    },
    ""category"": {
      ""type"": ""string"",
      ""description"": ""Optional category prefix filter — only entries whose category starts with this string are included.""
    },
    ""clear"": {
      ""type"": ""boolean"",
      ""description"": ""When true, clears the log buffer after returning the snapshot. Defaults to false."",
      ""default"": false
    }
  }
}",
                handler: HandleGetLogsAsync);
        }

        private Task<string> HandleGetLogsAsync(string argsJson)
        {
            try
            {
                var args = Newtonsoft.Json.Linq.JObject.Parse(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);

                var minimumLevel = RoverLogLevel.Info;
                var levelToken = args["minimumLevel"];
                if (levelToken != null)
                    minimumLevel = ParseLevel(levelToken.ToString());

                var maxEntries = 100;
                var maxToken = args["maxEntries"];
                if (maxToken != null)
                {
                    var m = (int)maxToken;
                    if (m > 0) maxEntries = m;
                }

                string? categoryFilter = args["category"]?.ToString();

                var shouldClear = false;
                var clearToken = args["clear"];
                if (clearToken != null)
                    shouldClear = (bool)clearToken;

                var store = _store ?? RoverLog.Store;
                var snapshot = store.GetSnapshot(minimumLevel, maxEntries);

                // Apply optional category filter
                IEnumerable<LogEntry> entries = snapshot;
                if (!string.IsNullOrEmpty(categoryFilter))
                    entries = FilterByCategory(snapshot, categoryFilter!);

                if (shouldClear)
                    store.Clear();

                return Task.FromResult(FormatResponse(entries, store.TotalWritten, minimumLevel, maxEntries, shouldClear));
            }
            catch (Exception ex)
            {
                RoverLog.Error("zRover.Logging", "get_logs handler failed", ex);
                return Task.FromResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    error = ex.Message,
                    entries = Array.Empty<object>()
                }));
            }
        }

        private static IEnumerable<LogEntry> FilterByCategory(IReadOnlyList<LogEntry> entries, string prefix)
        {
            foreach (var e in entries)
                if (e.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return e;
        }

        private static string FormatResponse(
            IEnumerable<LogEntry> entries,
            long totalWritten,
            RoverLogLevel minimumLevel,
            int maxEntries,
            bool cleared)
        {
            var entryArray = new System.Collections.Generic.List<object>();
            foreach (var e in entries)
            {
                var obj = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["timestamp"] = e.Timestamp.ToString("o"),
                    ["level"] = e.Level.ToString(),
                    ["category"] = e.Category,
                    ["message"] = e.Message,
                };
                if (e.ExceptionType != null) obj["exceptionType"] = e.ExceptionType;
                if (e.ExceptionMessage != null) obj["exceptionMessage"] = e.ExceptionMessage;
                if (e.ExceptionStackTrace != null) obj["exceptionStackTrace"] = e.ExceptionStackTrace;
                entryArray.Add(obj);
            }

            var result = new System.Collections.Generic.Dictionary<string, object>
            {
                ["entries"] = entryArray,
                ["returnedCount"] = entryArray.Count,
                ["totalWritten"] = totalWritten,
                ["minimumLevel"] = minimumLevel.ToString(),
                ["maxEntries"] = maxEntries,
                ["snapshotTime"] = DateTimeOffset.UtcNow.ToString("o"),
                ["cleared"] = cleared,
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented);
        }

        private static RoverLogLevel ParseLevel(string level)
        {
            switch (level.ToLowerInvariant())
            {
                case "trace": return RoverLogLevel.Trace;
                case "debug": return RoverLogLevel.Debug;
                case "info": return RoverLogLevel.Info;
                case "warning": case "warn": return RoverLogLevel.Warning;
                case "error": return RoverLogLevel.Error;
                case "fatal": return RoverLogLevel.Fatal;
                default: return RoverLogLevel.Info;
            }
        }
    }
}
