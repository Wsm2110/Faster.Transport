using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Periodically writes the current UTC timestamp (in ticks) to a shared memory region.
    /// </summary>
    /// <remarks>
    /// This component is used as a lightweight heartbeat mechanism between
    /// an IPC server and client to monitor liveness.
    /// <list type="bullet">
    ///   <item><description>Runs in a background thread and updates the timestamp every 100 milliseconds.</description></item>
    ///   <item><description>The <see cref="HeartbeatReader"/> can later check this timestamp to detect stale or dead connections.</description></item>
    /// </list>
    /// </remarks>
    internal sealed class HeartbeatWriter : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Thread _thread;
        private volatile bool _running;

        /// <summary>
        /// Initializes a new heartbeat writer that periodically updates
        /// a memory-mapped file with the current UTC time.
        /// </summary>
        /// <param name="name">
        /// The name of the memory-mapped file used for the heartbeat.
        /// Must match the reader’s name to be detected.
        /// </param>
        public HeartbeatWriter(string name)
        {
            // Create (or open) a small shared memory file (8 bytes)
            // used to store a single 64-bit timestamp (DateTime.Ticks)
            _mmf = MmfHelper.CreateWithSecurity(name, 8);
            _view = _mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.ReadWrite);

            // Launch a background thread that will continuously update the timestamp
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"hb-writer:{name}"
            };
        }

        /// <summary>
        /// Starts the heartbeat thread.
        /// </summary>
        public void Start()
        {
            _running = true;
            _thread.Start();
        }

        /// <summary>
        /// The main heartbeat loop.
        /// Writes the current UTC ticks to the memory-mapped file every 100 ms.
        /// </summary>
        private void Loop()
        {
            while (_running)
            {
                // Store the current UTC time (as ticks, 1 tick = 100 nanoseconds)
                long ticks = DateTime.UtcNow.Ticks;
                _view.Write(0, ticks);

                // Sleep briefly before writing again
                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Stops the heartbeat and releases resources.
        /// </summary>
        public void Dispose()
        {
            _running = false;
            try { _thread.Join(250); } catch { /* safe to ignore */ }
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    /// <summary>
    /// Reads the last heartbeat timestamp written by <see cref="HeartbeatWriter"/>.
    /// </summary>
    /// <remarks>
    /// This is used to check whether another process is still alive by comparing
    /// the last recorded heartbeat timestamp against the current time.
    /// <para>
    /// For example, if the timestamp is older than 1 second, the process is likely dead or frozen.
    /// </para>
    /// </remarks>
    internal static class HeartbeatReader
    {
        /// <summary>
        /// Attempts to read the latest heartbeat timestamp from a shared memory region.
        /// </summary>
        /// <param name="name">The name of the heartbeat memory-mapped file.</param>
        /// <param name="ticks">
        /// The output parameter that receives the UTC ticks value (if available).
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the heartbeat file was successfully read;
        /// <see langword="false"/> if the file does not exist or cannot be opened.
        /// </returns>
        public static bool TryRead(string name, out long ticks)
        {
            ticks = 0;
            try
            {
                // Try to open the existing memory-mapped heartbeat file
                using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read);

                // Read 8 bytes (a 64-bit integer representing DateTime.Ticks)
                view.Read(0, out ticks);
                return true;
            }
            catch
            {
                // File not found or inaccessible means no heartbeat available
                return false;
            }
        }
    }
}
