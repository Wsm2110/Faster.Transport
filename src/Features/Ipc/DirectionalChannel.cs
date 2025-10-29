using System;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using Faster.Transport.Primitives;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Represents one half of an inter-process communication (IPC) channel using shared memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="DirectionalChannel"/> is either a **sender** (writer) or a **receiver** (reader).
    /// It wraps a memory-mapped file containing a <see cref="SharedSpscRing"/> buffer,
    /// providing ultra-fast, lock-free, single-producer single-consumer message transfer between processes.
    /// </para>
    /// <para>
    /// Optionally, it can use an <see cref="EventWaitHandle"/> to signal new data,
    /// though by default it relies on a lightweight spin-wait loop for performance.
    /// </para>
    /// <example>
    /// Client (sender) → Server (reader):
    /// <code>
    /// var tx = new DirectionalChannel("Local\\MyApp.C2S", null, 1&lt;&lt;20, create: true, isReader: false);
    /// var rx = new DirectionalChannel("Local\\MyApp.C2S", null, 1&lt;&lt;20, create: false, isReader: true);
    /// 
    /// rx.OnFrame += msg =&gt; Console.WriteLine($"Received: {msg.Length} bytes");
    /// rx.Start();
    /// 
    /// tx.Send(Encoding.UTF8.GetBytes("Hello from client"));
    /// </code>
    /// </example>
    /// </remarks>
    public unsafe sealed class DirectionalChannel : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle? _signal;
        private readonly bool _reader;    
        private readonly SharedSpscRing _ring;
        private readonly byte[] _recv;
        private readonly Thread? _rxThread;
        private volatile bool _running;

        public int Length { get; private set; }

        /// <summary>
        /// Raised when a complete message frame is received.
        /// </summary>
        public event Action<ReadOnlyMemory<byte>>? OnFrame;

        /// <summary>
        /// Creates a new <see cref="DirectionalChannel"/> over a memory-mapped file.
        /// </summary>
        /// <param name="mapName">The unique name of the shared memory region (e.g. "Global\\App.C2S.1234.map").</param>
        /// <param name="evName">Optional name of the associated event signal (if used).</param>
        /// <param name="totalBytes">Total size (in bytes) of the shared memory buffer, including header + ring.</param>
        /// <param name="create">
        /// Set to <see langword="true"/> to create the shared memory file,
        /// or <see langword="false"/> to attach to an existing one.
        /// </param>
        /// <param name="isReader">If <see langword="true"/>, this instance will read messages; otherwise, it writes them.</param>
        /// <param name="rxThreadName">Optional name for the background receiver thread.</param>
        /// <param name="useEvent">If <see langword="true"/>, uses a system event to signal new messages.</param>
        public DirectionalChannel(
            string mapName,
            string? evName,
            int totalBytes,
            bool create,
            bool isReader,
            string? rxThreadName = null,
            bool useEvent = false)
        {
            _reader = isReader;
            Length = totalBytes;

            // Create or attach to the shared memory region
            _mmf = create
                ? MmfHelper.CreateWithSecurity(mapName, totalBytes)
                : MmfHelper.OpenExistingWithRetry(mapName);

            // Optional synchronization signal
            _signal = useEvent && evName != null
                ? (create
                    ? MmfHelper.CreateOrOpenEvent(evName)
                    : MmfHelper.OpenEventWithRetry(evName))
                : null;

            // Create a view into the shared memory for direct access
            _view = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);

            // Obtain the raw pointer to the shared region
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

            // Initialize the lock-free single-producer/single-consumer ring
            _ring = new SharedSpscRing(p, totalBytes);

            // Rent a reusable buffer to hold incoming data (avoid per-message allocations)
            _recv = ArrayPool<byte>.Shared.Rent(Math.Max(1 << 16, totalBytes));

            // If this is a reader, start a dedicated receive thread
            if (_reader)
                _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = rxThreadName ?? "mmf-rx" };
        }

        /// <summary>
        /// Starts the background receiver thread (only applies to reader channels).
        /// </summary>
        public void Start()
        {
            if (_reader && _rxThread is not null)
            {
                _running = true;
                _rxThread.Start();
            }
        }

        /// <summary>
        /// Background loop that continuously polls the ring buffer for new messages.
        /// </summary>
        private void ReceiveLoop()
        {
            FasterIpcTrace.Info("[Directional] RX start");
            var spin = new SpinWait();

            while (_running)
            {
                // Try to dequeue a frame from the shared memory ring
                if (_ring.TryDequeue(_recv, out int len))
                {
                    // Fire the OnFrame event to deliver the received message
                    OnFrame?.Invoke(new ReadOnlyMemory<byte>(_recv, 0, len));
                    spin.Reset();
                }
                else
                {
                    // No data — perform lightweight backoff
                    spin.SpinOnce();

                    if (spin.NextSpinWillYield)
                    {
                        // Yield CPU briefly, minimal delay
                        Thread.Sleep(0);

                        // Optionally wait on event if configured
                        _signal?.WaitOne(0);
                    }
                }
            }

            FasterIpcTrace.Info("[Directional] RX stop");
        }

        /// <summary>
        /// Sends a message into the shared memory ring buffer.
        /// </summary>
        /// <param name="payload">The data to send.</param>
        /// <remarks>
        /// If the ring buffer is full, this method will spin-wait briefly
        /// until space becomes available (non-blocking at kernel level).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_reader) return; // readers cannot send

            var spin = new SpinWait();

            // Keep trying until there’s space in the ring
            while (!_ring.TryEnqueue(payload))
            {
                spin.SpinOnce();
                if (spin.NextSpinWillYield)
                    Thread.Sleep(0);
            }

            // Notify any waiting receiver
            _signal?.Set();
        }

        /// <summary>
        /// Stops the receiver (if running) and releases unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            _running = false;

            try { _signal?.Set(); } catch { /* wake up receiver */ }
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* ignore */ }

            _view.Dispose();
            _mmf.Dispose();

            // Return the buffer to the shared pool
            ArrayPool<byte>.Shared.Return(_recv, clearArray: false);
            _signal?.Dispose();
        }
    }
}
