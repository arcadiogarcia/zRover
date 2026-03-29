using System;
using System.Threading.Tasks;
using zRover.Core.Coordinates;
using zRover.Core.Logging;

namespace zRover.Core
{
    public class DebugHostContext
    {
        public DebugHostOptions Options { get; }
        public ICoordinateResolver CoordinateResolver { get; }
        public string ArtifactDirectory { get; }

        /// <summary>
        /// Schedules async work on the app UI thread and awaits its completion.
        /// Null if not provided. Used by capabilities that require UI thread access.
        /// </summary>
        public Func<Func<Task>, Task>? RunOnUiThread { get; }

        /// <summary>
        /// The active in-memory log store. All zRover infrastructure and the host
        /// application write here; MCP clients read from it via the <c>get_logs</c> tool.
        /// </summary>
        public IInMemoryLogStore LogStore { get; }

        public DebugHostContext(
            DebugHostOptions options,
            ICoordinateResolver coordinateResolver,
            string artifactDirectory,
            Func<Func<Task>, Task>? runOnUiThread = null,
            IInMemoryLogStore? logStore = null)
        {
            Options = options;
            CoordinateResolver = coordinateResolver;
            ArtifactDirectory = artifactDirectory;
            RunOnUiThread = runOnUiThread;
            LogStore = logStore ?? RoverLog.Store;
        }
    }
}
