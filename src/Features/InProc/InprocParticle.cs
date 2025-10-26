using Faster.Transport.Contracts;
using Faster.Transport.Primitives;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// Represents a single in-process transport endpoint ("particle").
    /// 
    /// ✅ Think of this as a lightweight connection between two endpoints
    /// that live **inside the same process** — no TCP or file I/O involved.
    /// 
    /// Each <see cref="InprocParticle"/> has two unidirectional message rings:
    /// - `ToServer`  → messages from client to server
    /// - `ToClient`  → messages from server to client
    /// 
    /// The class automatically starts a reader loop that listens for messages
    /// from its linked peer and raises <see cref="OnReceived"/> events.
    /// 
    /// This mirrors how `MappedParticle` or `PipeParticle` work — but it's
    /// entirely in-memory and designed for **extreme speed** and low latency.
    /// </summary>
    public sealed class InprocParticle : IParticle, IDisposable
    {
        #region Fields

        private readonly string _name;
        private readonly bool _isServer;
        private readonly CancellationTokenSource _cts = new();

        // Buffer manager used to efficiently reuse message buffers
        private readonly ConcurrentBufferManager _bufferManager;

        // Frame parser for splitting byte streams into messages
        private readonly FrameParser _parser;

        // Represents the bidirectional link between this particle and its peer
        private InprocLink? _link;

        // Background reader task
        private Task? _readerLoop;

        private volatile bool _isDisposed;

        #endregion

        #region Events

        /// <summary>
        /// Triggered whenever a message is received from the connected peer.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Triggered when the connection is closed or an error occurs.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// Triggered once the server and client are connected and ready.
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }


        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new in-process particle endpoint.
        /// </summary>
        /// <param name="name">Channel name (must match between both ends).</param>
        /// <param name="isServer">True if this particle is the server side.</param>
        /// <param name="bufferSize">Size of each buffer chunk (default: 8 KB).</param>
        /// <param name="ringCapacity">Capacity of the in-memory message ring.</param>
        /// <param name="maxDegreeOfParallelism">Number of concurrent Send() operations allowed.</param>
        public InprocParticle(string name, bool isServer, int bufferSize = 8192, int ringCapacity = 4096, int maxDegreeOfParallelism = 8)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _isServer = isServer;

            // Create reusable buffer and parser
            _bufferManager = new ConcurrentBufferManager(bufferSize, bufferSize * maxDegreeOfParallelism);
            _parser = new FrameParser(bufferSize * maxDegreeOfParallelism);

            // Hook up parser events
            _parser.OnFrame = p => OnReceived?.Invoke(this, p);
            _parser.OnError = ex => Close(ex);

            // If this is the client side, connect immediately
            if (!isServer)
            {
                var link = InprocRegistry.Connect(name, ringCapacity);
                AttachLink(link);
            }

            OnConnected?.Invoke(this);  
        }

        #endregion

        #region Connection setup

        /// <summary>
        /// Attaches an existing in-process link (used by server side).
        /// This also starts the background reader loop.
        /// </summary>
        internal void AttachLink(InprocLink link)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));

            _link = link ?? throw new ArgumentNullException(nameof(link));

            // Start reading incoming messages
            _readerLoop = Task.Run(ReaderLoopAsync, _cts.Token);
        }

        #endregion

        #region Send methods

        /// <summary>
        /// Sends a message synchronously to the connected peer.
        /// The message is copied into the peer’s inbound ring.
        /// </summary>
        /// <param name="payload">The data to send.</param>
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));
            if (_link is null)
                throw new InvalidOperationException("Not connected.");

            // Select which direction to send based on role
            var ring = _isServer ? _link.ToClient : _link.ToServer;
            var spin = new SpinWait();

            // Apply backpressure: if ring is full, spin until it has space
            while (!ring.TryEnqueue(payload.ToArray()))
            {
                spin.SpinOnce(); // adaptive spinning avoids busy waiting
                if (_cts.IsCancellationRequested)
                    throw new OperationCanceledException("Send operation canceled.");
            }
        }

        /// <summary>
        /// Sends a message asynchronously (still synchronous under the hood for speed).
        /// </summary>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));
            if (_link is null)
                throw new InvalidOperationException("Not connected.");

            var ring = _isServer ? _link.ToClient : _link.ToServer;
            var spin = new SpinWait();

            // Same logic as Send() but with ReadOnlyMemory<byte>
            while (!ring.TryEnqueue(payload))
            {
                spin.SpinOnce();
                if (_cts.IsCancellationRequested)
                    throw new OperationCanceledException("Send operation canceled.");
            }

            return TaskCompat.CompletedValueTask;
        }

        #endregion

        #region Reader loop

        /// <summary>
        /// Continuously reads messages from the inbound ring and raises <see cref="OnReceived"/>.
        /// </summary>
        private async Task ReaderLoopAsync()
        {
            try
            {
                var link = _link!;
                var inbound = _isServer ? link.ToServer : link.ToClient;

                while (!_cts.IsCancellationRequested)
                {
                    // Drain all available messages before yielding
                    while (inbound.TryDequeue(out var msg))
                    {
                        OnReceived?.Invoke(this, msg);
                    }

                    // Yield to let other tasks run — prevents busy CPU loop
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                Close(ex);
            }
        }

        #endregion

        #region Cleanup and disposal

        /// <summary>
        /// Gracefully closes the connection and raises disconnect events.
        /// </summary>
        private void Close(Exception? ex = null)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _cts.Cancel(); } catch { }

            try { OnDisconnected?.Invoke(this); } catch { }
        }

        /// <summary>
        /// Disposes all resources and stops background tasks.
        /// </summary>
        public void Dispose()
        {
            Close();
            _cts.Dispose();
            _parser.Dispose();
        }

        #endregion
    }
}
