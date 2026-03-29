using System;
using System.Collections.Generic;
using System.Threading;

namespace zRover.Core.Logging
{
    /// <summary>
    /// Thread-safe bounded ring buffer implementation of <see cref="IInMemoryLogStore"/>.
    /// Older entries are silently overwritten once the buffer is full.
    /// </summary>
    public sealed class InMemoryLogStore : IInMemoryLogStore
    {
        private readonly LogEntry[] _buffer;
        private readonly int _capacity;
        private long _totalWritten;
        private readonly object _lock = new object();

        /// <param name="capacity">Maximum number of entries retained at any time. Defaults to 2000.</param>
        public InMemoryLogStore(int capacity = 2000)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _buffer = new LogEntry[capacity];
        }

        /// <inheritdoc/>
        public long TotalWritten => Interlocked.Read(ref _totalWritten);

        /// <inheritdoc/>
        public void Append(LogEntry entry)
        {
            if (entry == null) return;
            lock (_lock)
            {
                _buffer[_totalWritten % _capacity] = entry;
                _totalWritten++;
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<LogEntry> GetSnapshot()
        {
            return GetSnapshot(RoverLogLevel.Trace, _capacity);
        }

        /// <inheritdoc/>
        public IReadOnlyList<LogEntry> GetSnapshot(RoverLogLevel minimumLevel, int maxEntries = 200)
        {
            if (maxEntries <= 0) maxEntries = 200;

            lock (_lock)
            {
                var count = (int)Math.Min(_totalWritten, _capacity);
                if (count == 0) return Array.Empty<LogEntry>();

                // The ring buffer is ordered: entries go from (totalWritten - count) to (totalWritten - 1)
                // index in _buffer = (totalWritten - count + i) % capacity
                var start = (_totalWritten - count) % _capacity;

                // Collect into a list (oldest first), applying filters
                var result = new List<LogEntry>(Math.Min(count, maxEntries));
                for (int i = 0; i < count; i++)
                {
                    var entry = _buffer[(_totalWritten - count + i) % _capacity];
                    if (entry == null) continue;
                    if (entry.Level >= minimumLevel)
                        result.Add(entry);
                }

                // Return only the newest maxEntries
                if (result.Count > maxEntries)
                    return result.GetRange(result.Count - maxEntries, maxEntries);

                return result;
            }
        }

        /// <inheritdoc/>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _capacity);
                _totalWritten = 0;
            }
        }
    }
}
