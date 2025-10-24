using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// High-performance interprocess communication (IPC) transport using shared memory.
    /// 
    /// Instead of using TCP or Named Pipes (which go through the OS kernel),
    /// this class uses a shared memory region and ring buffers to exchange data
    /// directly between processes at **very low latency**.
    /// 
    /// ✅ Key features:
    /// - Two **SPSC rings** (one per direction: server→client and client→server)
    /// - **MPSC staging queue** for multi-threaded Send() calls
    /// - **Zero-copy** memory transfer between processes
    /// - Messages are framed as `[length:int32][payload]`
    /// 
    /// ✅ Design goals:
    /// - Under 3ms latency for 10k tiny messages
    /// - Avoid StackOverflow when user code re-enters OnMessage
    /// - Minimal synchronization and memory fencing
    /// </summary>
    public sealed unsafe class MappedParticle : IParticle, IDisposable
    {
        #region Tunable constants

        private const int HeaderBytes = 64;                     // Header padding for cacheline alignment
        private const int MaxFrameBytes = 8 * 1024 * 1024;      // Maximum message size (8 MB safety limit)
        private const int DefaultRingBytes = 1 << 20;           // 1 MiB per ring buffer (good balance)
        private const int DefaultQueueCapacity = 8192;          // Staging queue size for Send()

        #endregion

        #region Fields

        private readonly string _name;
        private readonly bool _isServer;
        private readonly int _ringBytes;
        private readonly CancellationTokenSource _cts = new();

        // Shared memory objects
        private EventWaitHandle? _evTx; // "doorbell" for peer (wake-up signal)
        private EventWaitHandle? _evRx; // we listen on this event
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _view;
        private Microsoft.Win32.SafeHandles.SafeMemoryMappedViewHandle? _handle;
        private byte* _base; // raw pointer to the mapped memory

        // The two ring buffers for send/receive
        private Ring _tx; // outgoing ring (write side)
        private Ring _rx; // incoming ring (read side)

        private readonly MpscQueue<OwnedBuffer> _outQueue; // staging queue for multiple Send() threads
        private readonly MemoryPool<byte> _pool = MemoryPool<byte>.Shared;

        private Task? _readerLoop;
        private Task? _writerLoop;
        private volatile bool _isDisposed;

        // Prevent recursive reentry into OnMessage (per-thread)
        [ThreadStatic] private static int _inUserCallback;

        #endregion

        #region Events and properties

        /// <summary>
        /// Triggered when a message is received, including the sender instance.
        /// Use this for replying with <see cref="Reply(ReadOnlySpan{byte})"/>.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Triggered when the connection is closed or an error occurs.
        /// </summary>
        public Action<IParticle, Exception?>? Disconnected { get; set; }

        /// <summary>
        /// Triggered when the connection is closed cleanly.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// Triggered once the server and client are connected and ready.
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Total bytes of ring buffer capacity (useful for diagnostics).
        /// </summary>
        public int RingCapacityBytes => _ringBytes;

        #endregion

        #region Constructor and initialization

        /// <summary>
        /// Creates a new shared-memory IPC channel.
        /// </summary>
        /// <param name="name">Unique channel name (must match on both ends).</param>
        /// <param name="isServer">True for server, false for client.</param>
        /// <param name="ringBytes">Optional size of the shared ring (default 1 MiB).</param>
        /// <param name="outQueueCapacity">Optional send queue size.</param>
        public MappedParticle(string name, bool isServer, int ringBytes = DefaultRingBytes, int outQueueCapacity = DefaultQueueCapacity)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            if (ringBytes < 64 * 1024)
                throw new ArgumentOutOfRangeException(nameof(ringBytes), ">= 64 KiB recommended");

            _name = name;
            _isServer = isServer;
            _ringBytes = NextPow2(ringBytes);
            _outQueue = new MpscQueue<OwnedBuffer>(outQueueCapacity);

            Initialize();
        }

        /// <summary>
        /// Allocates shared memory and sets up event handles for signaling.
        /// </summary>
        private void Initialize()
        {
            try
            {
                // Layout: [S2C header][S2C data][C2S header][C2S data]
                long totalBytes = HeaderBytes + _ringBytes + HeaderBytes + _ringBytes;

                if (_isServer)
                {
                    // Server creates the shared memory and event handles
                    _mmf = MemoryMappedFile.CreateOrOpen(_name + "_map", totalBytes, MemoryMappedFileAccess.ReadWrite);
                    _view = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
                    _evTx = new EventWaitHandle(false, EventResetMode.AutoReset, _name + "_s2c_ev");
                    _evRx = new EventWaitHandle(false, EventResetMode.AutoReset, _name + "_c2s_ev");
                }
                else
                {
                    // Client connects to existing memory and events
                    _mmf = MemoryMappedFile.OpenExisting(_name + "_map", MemoryMappedFileRights.ReadWrite);
                    _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                    _evTx = EventWaitHandle.OpenExisting(_name + "_c2s_ev");
                    _evRx = EventWaitHandle.OpenExisting(_name + "_s2c_ev");
                }

                // Get raw memory pointer
                _handle = _view!.SafeMemoryMappedViewHandle;
                _handle.AcquirePointer(ref _base);

                // Compute positions of each ring buffer
                long s2cHeader = 0;
                long s2cData = s2cHeader + HeaderBytes;
                long c2sHeader = s2cData + _ringBytes;
                long c2sData = c2sHeader + HeaderBytes;

                // Assign ring buffers depending on whether we're server or client
                if (_isServer)
                {
                    _tx = new Ring(_base + s2cHeader, _base + s2cData, _ringBytes);
                    _rx = new Ring(_base + c2sHeader, _base + c2sData, _ringBytes);
                    _tx.Reset(); _rx.Reset();
                }
                else
                {
                    _tx = new Ring(_base + c2sHeader, _base + c2sData, _ringBytes);
                    _rx = new Ring(_base + s2cHeader, _base + s2cData, _ringBytes);
                }

                // Start background loops
                _readerLoop = Task.Run(ReaderLoop, _cts.Token);
                _writerLoop = Task.Run(WriterLoop, _cts.Token);

                // Notify user asynchronously that we're connected
                Task.Run(() => { try { OnConnected?.Invoke(this); } catch { } });
            }
            catch (Exception ex)
            {
                Close(ex);
            }
        }

        /// <summary>
        /// Gracefully closes the connection and releases all resources.
        /// </summary>
        private void Close(Exception? ex)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _cts.Cancel(); } catch { }
            try { _evRx?.Set(); } catch { }

            try { _readerLoop?.Wait(50); } catch { }
            try { _writerLoop?.Wait(50); } catch { }

            // Dispose any remaining buffers in the send queue
            while (_outQueue.TryDequeue(out var ob))
                ob.Owner.Dispose();

            // Release memory pointers and OS handles
            try
            {
                if (_handle is not null && !_handle.IsInvalid && !_handle.IsClosed)
                    _handle.ReleasePointer();
            }
            catch { }

            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            try { _evTx?.Dispose(); } catch { }
            try { _evRx?.Dispose(); } catch { }

            try { Disconnected?.Invoke(this, ex); } catch { }
            try { OnDisconnected?.Invoke(this); } catch { }
        }

        public void Dispose() => Close(null);

        #endregion

        #region Sending messages

        /// <summary>
        /// Sends a message to the other process.
        /// Copies data into a shared buffer and queues it for the writer loop.
        /// </summary>
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(MappedParticle));
            if (payload.Length == 0)
                return;
            if (payload.Length > MaxFrameBytes)
                throw new ArgumentOutOfRangeException(nameof(payload));

            // Rent buffer from memory pool (to avoid frequent allocations)
            var owner = _pool.Rent(payload.Length);
            payload.CopyTo(owner.Memory.Span);

            var item = new OwnedBuffer(owner, payload.Length);
            var spin = new SpinWait();

            // Keep trying to enqueue until space is available
            while (!_outQueue.TryEnqueue(item))
            {
                spin.SpinOnce();
                if (_cts.IsCancellationRequested)
                {
                    owner.Dispose();
                    throw new OperationCanceledException();
                }
            }
        }

        /// <summary>
        /// Async wrapper around Send() (still synchronous internally for performance).
        /// </summary>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            Send(payload.Span);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Reply to a message (alias for Send()).
        /// </summary>
        public void Reply(ReadOnlySpan<byte> payload) => Send(payload);

        #endregion

        #region Worker loops

        /// <summary>
        /// The writer loop dequeues outgoing messages and copies them into the shared ring buffer.
        /// </summary>
        private void WriterLoop()
        {
            try
            {
                Span<byte> hdr = stackalloc byte[4]; // 4-byte header for message length

                while (!_cts.IsCancellationRequested)
                {
                    bool wrote = false;

                    // Try to drain the output queue
                    while (_outQueue.TryDequeue(out var item))
                    {
                        wrote = true;
                        int total = 4 + item.Length;

                        // Wait until enough free space in the ring
                        while (!HasFreeSpace(ref _tx, total))
                        {
                            _evTx?.Set(); // wake the peer
                            Thread.SpinWait(64);
                            if (_cts.IsCancellationRequested) break;
                        }

                        // Write message length header
                        BinaryPrimitives.WriteInt32LittleEndian(hdr, item.Length);
                        _tx.WriteBytes(_tx.WriteIndex, hdr);

                        // Write payload after header
                        _tx.WriteBytes(_tx.WriteIndex + 4, item.Owner.Memory.Span.Slice(0, item.Length));

                        // Update write pointer (makes data visible to peer)
                        _tx.WriteIndex = _tx.WriteIndex + total;

                        item.Owner.Dispose(); // release pooled buffer
                        _evTx?.Set(); // signal peer that new data is ready
                    }

                    if (!wrote)
                        Thread.SpinWait(256); // brief idle spin (low latency)
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Close(ex); }
        }

        /// <summary>
        /// The reader loop reads messages from the shared ring buffer and invokes user callbacks.
        /// </summary>
        private void ReaderLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    bool progressed = false;

                    while (true)
                    {
                        long r = _rx.ReadIndex;
                        long w = _rx.WriteIndexCached();
                        if (r == w) break; // no new data

                        Span<byte> hdr = stackalloc byte[4];
                        _rx.ReadBytes(r, hdr);

                        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
                        if (len <= 0 || len > MaxFrameBytes)
                            throw new InvalidOperationException($"Invalid frame len {len}");

                        int total = 4 + len;
                        if ((w - r) < total)
                            break; // message not fully written yet

                        // Rent memory for payload
                        var owner = _pool.Rent(len);
                        var mem = owner.Memory.Slice(0, len);

                        _rx.ReadBytes(r + 4, mem.Span); // read payload
                        _rx.ReadIndex = r + total;      // advance read pointer
                        progressed = true;

                        // Reentrancy guard: only allow nested calls via Task.Run()
                        bool disposeOwner = true;
                        try
                        {
                            if (_inUserCallback++ == 0)
                            {                              
                                OnReceived?.Invoke(this, mem);
                            }
                            else
                            {
                                disposeOwner = false;
                                _ = Task.Run(() =>
                                {
                                    try
                                    {                                      
                                        OnReceived?.Invoke(this, mem);
                                    }
                                    finally { owner.Dispose(); }
                                });
                            }
                        }
                        finally
                        {
                            _inUserCallback--;
                            if (disposeOwner) owner.Dispose();
                        }
                    }

                    if (!progressed)
                        Thread.SpinWait(256);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Close(ex); }
        }

        #endregion

        #region Helper methods and structs

        /// <summary>
        /// Checks if there’s enough free space in the ring buffer.
        /// </summary>
        private bool HasFreeSpace(ref Ring tx, int needed)
        {
            long w = tx.WriteIndex;
            long r = tx.ReadIndexCached();
            return (tx.Capacity - (w - r)) >= needed;
        }

        /// <summary>
        /// Rounds a number up to the next power of two.
        /// </summary>
        private static int NextPow2(int v)
        {
            v--;
            v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16;
            return ++v;
        }

        /// <summary>
        /// Represents a circular buffer in shared memory.
        /// </summary>
        private struct Ring
        {
            private readonly byte* _hdr;   // header pointer (read/write indices)
            private readonly byte* _data;  // data region
            private readonly int _cap;     // ring capacity

            private long _cachedRead;
            private long _cachedWrite;
            private int _rdTick, _wrTick;

            private const int OffRead = 0;
            private const int OffWrite = 8;

            public Ring(byte* hdr, byte* data, int cap)
            {
                _hdr = hdr;
                _data = data;
                _cap = cap;
                _cachedRead = 0;
                _cachedWrite = 0;
                _rdTick = 0;
                _wrTick = 0;
            }

            public void Reset()
            {
                *(long*)(_hdr + OffRead) = 0;
                *(long*)(_hdr + OffWrite) = 0;
            }

            public int Capacity => _cap;

            public long ReadIndex
            {
                get => Volatile.Read(ref *(long*)(_hdr + OffRead));
                set => Volatile.Write(ref *(long*)(_hdr + OffRead), value);
            }

            public long WriteIndex
            {
                get => Volatile.Read(ref *(long*)(_hdr + OffWrite));
                set => Volatile.Write(ref *(long*)(_hdr + OffWrite), value);
            }

            public long ReadIndexCached()
            {
                if ((_rdTick++ & 0xF) == 0)
                    _cachedRead = Volatile.Read(ref *(long*)(_hdr + OffRead));
                return _cachedRead;
            }

            public long WriteIndexCached()
            {
                if ((_wrTick++ & 0xF) == 0)
                    _cachedWrite = Volatile.Read(ref *(long*)(_hdr + OffWrite));
                return _cachedWrite;
            }

            /// <summary>
            /// Writes bytes to the ring buffer (wraps around automatically).
            /// </summary>
            public void WriteBytes(long pos, ReadOnlySpan<byte> src)
            {
                int cap = _cap;
                int offset = (int)(pos & (cap - 1));
                int first = Math.Min(cap - offset, src.Length);

                fixed (byte* pSrc = src)
                {
                    Buffer.MemoryCopy(pSrc, _data + offset, cap - offset, first);
                    int remain = src.Length - first;
                    if (remain > 0)
                        Buffer.MemoryCopy(pSrc + first, _data, cap, remain);
                }
            }

            /// <summary>
            /// Reads bytes from the ring buffer (wraps around automatically).
            /// </summary>
            public void ReadBytes(long pos, Span<byte> dst)
            {
                int cap = _cap;
                int offset = (int)(pos & (cap - 1));
                int first = Math.Min(cap - offset, dst.Length);

                fixed (byte* pDst = dst)
                {
                    Buffer.MemoryCopy(_data + offset, pDst, dst.Length, first);
                    int remain = dst.Length - first;
                    if (remain > 0)
                        Buffer.MemoryCopy(_data, pDst + first, dst.Length - first, remain);
                }
            }
        }

        /// <summary>
        /// Represents a pooled message buffer.
        /// </summary>
        private readonly struct OwnedBuffer
        {
            public readonly IMemoryOwner<byte> Owner;
            public readonly int Length;
            public OwnedBuffer(IMemoryOwner<byte> owner, int length)
            {
                Owner = owner;
                Length = length;
            }
        }

        #endregion
    }
}
