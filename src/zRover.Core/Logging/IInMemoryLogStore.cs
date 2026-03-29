using System.Collections.Generic;

namespace zRover.Core.Logging
{
    /// <summary>
    /// An in-memory log store that captures <see cref="LogEntry"/> instances produced
    /// by the zRover debug host and the host application. Designed for bounded,
    /// low-overhead capture in a debug/diagnostic context.
    /// </summary>
    public interface IInMemoryLogStore
    {
        /// <summary>Returns a snapshot of all entries currently in the buffer, oldest first.</summary>
        IReadOnlyList<LogEntry> GetSnapshot();

        /// <summary>
        /// Returns a snapshot filtered to entries at or above <paramref name="minimumLevel"/>,
        /// limited to the most recent <paramref name="maxEntries"/> records.
        /// </summary>
        IReadOnlyList<LogEntry> GetSnapshot(RoverLogLevel minimumLevel, int maxEntries = 200);

        /// <summary>Returns the total number of entries written since the store was created (including overwritten ones).</summary>
        long TotalWritten { get; }

        /// <summary>Appends an entry to the ring buffer. Thread-safe.</summary>
        void Append(LogEntry entry);

        /// <summary>Clears all entries from the buffer.</summary>
        void Clear();
    }
}
