using System;

namespace zRover.Core.Logging
{
    /// <summary>
    /// Static façade for structured logging within the zRover debug host and host application.
    /// Writes to an in-memory ring buffer that MCP clients can query via the
    /// <c>get_logs</c> tool at any time.
    ///
    /// <para>
    /// The store is initialized eagerly so that log calls made before
    /// <see cref="DebugHost.StartAsync"/> (e.g. during crash handlers) are captured.
    /// </para>
    /// </summary>
    public static class RoverLog
    {
        private static IInMemoryLogStore _store = new InMemoryLogStore();

        /// <summary>
        /// The active log store. Can be replaced at startup to use a custom capacity or
        /// implementation. Replacing it is not thread-safe; do it once before any logging occurs.
        /// </summary>
        public static IInMemoryLogStore Store
        {
            get => _store;
            set => _store = value ?? throw new ArgumentNullException(nameof(value));
        }

        public static void Trace(string category, string message)
            => _store.Append(new LogEntry(RoverLogLevel.Trace, category, message));

        public static void Debug(string category, string message)
            => _store.Append(new LogEntry(RoverLogLevel.Debug, category, message));

        public static void Info(string category, string message)
            => _store.Append(new LogEntry(RoverLogLevel.Info, category, message));

        public static void Warn(string category, string message, Exception? exception = null)
            => _store.Append(new LogEntry(RoverLogLevel.Warning, category, message, exception));

        public static void Error(string category, string message, Exception? exception = null)
            => _store.Append(new LogEntry(RoverLogLevel.Error, category, message, exception));

        public static void Fatal(string category, string message, Exception? exception = null)
            => _store.Append(new LogEntry(RoverLogLevel.Fatal, category, message, exception));
    }
}
