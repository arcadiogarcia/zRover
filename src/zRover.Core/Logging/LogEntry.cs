using System;

namespace zRover.Core.Logging
{
    /// <summary>
    /// The severity level of a log entry.
    /// </summary>
    public enum RoverLogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
        Fatal = 5
    }

    /// <summary>
    /// A single structured log entry captured in the zRover in-memory log store.
    /// </summary>
    public sealed class LogEntry
    {
        public DateTimeOffset Timestamp { get; }
        public RoverLogLevel Level { get; }
        public string Category { get; }
        public string Message { get; }
        public string? ExceptionMessage { get; }
        public string? ExceptionType { get; }
        public string? ExceptionStackTrace { get; }

        public LogEntry(
            RoverLogLevel level,
            string category,
            string message,
            Exception? exception = null)
        {
            Timestamp = DateTimeOffset.UtcNow;
            Level = level;
            Category = category ?? "";
            Message = message ?? "";

            if (exception != null)
            {
                ExceptionType = exception.GetType().FullName;
                ExceptionMessage = exception.Message;
                ExceptionStackTrace = exception.StackTrace;
            }
        }
    }
}
