using Faster.Transport.Features.Tcp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using Faster.Transport.Contracts;
using System.Net;

namespace Faster.Transport
{
    /// <summary>
    /// A fluent builder for creating and configuring a **server-side Reactor**.
    /// </summary>
    /// <remarks>
    /// The <see cref="ReactorBuilder"/> helps you build a high-performance server
    /// that can accept and manage multiple clients simultaneously.
    /// 
    /// <para>Depending on your use case, it can build one of these:</para>
    /// <list type="bullet">
    /// <item><b>TCP Reactor</b> — Listens on a network port (for LAN or Internet connections).</item>
    /// <item><b>IPC Reactor</b> — Allows local processes on the same machine to communicate efficiently.</item>
    /// <item><b>Inproc Reactor</b> — Handles communication entirely inside the same process (fastest option).</item>
    /// </list>
    /// 
    /// Once built, you can call <c>Start()</c> on the returned reactor to begin accepting connections.
    /// </remarks>
    public sealed class ReactorBuilder
    {
        #region === Configuration Fields ===

        // What kind of server we’re building (default: TCP)
        private TransportMode _mode = TransportMode.Tcp;

        // The endpoint the TCP server will bind to (e.g., IP and port)
        private EndPoint? _bindEndPoint;

        // Used by IPC and Inproc modes to identify shared channels
        private string? _channelName;

        // Tuning options
        private int _backlog = 1024; // How many pending connections the OS can queue
        private int _bufferSize = 8192; // Default per-client buffer
        private int _maxDegreeOfParallelism = 8; // How many threads can handle messages in parallel
        private int _ringCapacity = 128 + (1 << 20); // ≈ 1 MB ring buffer for IPC/Inproc

        // Optional event handlers for client connect/receive events
        private Action<IParticle>? _onConnected;
        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;

        #endregion

        #region === Fluent Configuration ===

        /// <summary>
        /// Sets the communication mode for this reactor (TCP, IPC, or Inproc).
        /// </summary>
        /// <param name="mode">Which type of server to build.</param>
        /// <returns>The same builder for chaining.</returns>
        public ReactorBuilder UseMode(TransportMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>
        /// Specifies the IP endpoint to bind to.  
        /// Only required when using <see cref="TransportMode.Tcp"/>.
        /// </summary>
        /// <param name="endpoint">An <see cref="EndPoint"/> (e.g., 0.0.0.0:5000).</param>
        public ReactorBuilder BindTo(EndPoint endpoint)
        {
            _bindEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            return this;
        }

        /// <summary>
        /// Sets the name of the shared channel.  
        /// This is required for <see cref="TransportMode.Ipc"/> and <see cref="TransportMode.Inproc"/>.
        /// </summary>
        /// <param name="channelName">A unique name that both server and clients must share.</param>
        public ReactorBuilder WithChannel(string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                throw new ArgumentException("Channel name cannot be null or empty.", nameof(channelName));

            _channelName = channelName.Trim();
            return this;
        }

        /// <summary>
        /// Adjusts the backlog — how many pending connections can wait before being accepted.
        /// </summary>
        public ReactorBuilder WithBacklog(int backlog)
        {
            if (backlog <= 0)
                throw new ArgumentOutOfRangeException(nameof(backlog));

            _backlog = backlog;
            return this;
        }

        /// <summary>
        /// Sets how much buffer memory is allocated per client connection.
        /// </summary>
        public ReactorBuilder WithBufferSize(int size)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            _bufferSize = size;
            return this;
        }

        /// <summary>
        /// Controls how many operations can process data simultaneously.
        /// </summary>
        public ReactorBuilder WithParallelism(int degree)
        {
            if (degree <= 0)
                throw new ArgumentOutOfRangeException(nameof(degree));

            _maxDegreeOfParallelism = degree;
            return this;
        }

        /// <summary>
        /// Sets the total capacity (in bytes) of the message ring buffer used by IPC/Inproc.
        /// </summary>
        public ReactorBuilder WithRingCapacity(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _ringCapacity = capacity;
            return this;
        }

        /// <summary>
        /// Registers a callback that triggers when a new client connects.
        /// </summary>
        public ReactorBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Registers a callback that triggers when any connected client sends a message.
        /// </summary>
        public ReactorBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        #endregion

        #region === Build ===

        /// <summary>
        /// Builds and returns the appropriate reactor instance depending on the selected mode.
        /// </summary>
        /// <returns>
        /// An <see cref="IReactor"/> that is ready to <see cref="IReactor.Start"/> and accept clients.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown if required configuration is missing.</exception>
        public IReactor Build()
        {
            // Choose which type of reactor to create based on the configured mode
            return _mode switch
            {
                TransportMode.Tcp => BuildTcpReactor(),
                TransportMode.Ipc => BuildIpcReactor(),
                TransportMode.Inproc => BuildInprocReactor(),
                _ => throw new InvalidOperationException($"Unsupported transport mode: {_mode}")
            };
        }

        /// <summary>
        /// Builds a TCP server that listens for network clients.
        /// </summary>
        /// <remarks>
        /// This server accepts multiple simultaneous TCP connections asynchronously.
        /// </remarks>
        private IReactor BuildTcpReactor()
        {
            if (_bindEndPoint == null)
                throw new InvalidOperationException("TCP mode requires a call to BindTo().");

            var reactor = new Reactor(
                bindEndPoint: _bindEndPoint,
                backlog: _backlog,
                bufferSize: _bufferSize,
                maxDegreeOfParallelism: _maxDegreeOfParallelism);

            // Hook up event handlers if provided
            if (_onConnected != null)
                reactor.OnConnected = _onConnected;

            if (_onReceived != null)
                reactor.OnReceived = _onReceived;

            reactor.Start();

            return reactor;
        }

        /// <summary>
        /// Builds an IPC (inter-process communication) reactor.
        /// </summary>
        /// <remarks>
        /// IPC reactors allow multiple **processes** on the same machine
        /// to communicate efficiently using shared memory or Unix domain sockets.
        /// </remarks>
        private IReactor BuildIpcReactor()
        {
            if (string.IsNullOrWhiteSpace(_channelName))
            {
                throw new InvalidOperationException("IPC mode requires WithChannel(channelName).");
            }

            // Create a shared-memory reactor
            var reactor = new MappedReactor(
                baseName: _channelName,
                global: false,
                ringBytes: _ringCapacity);

            if (_onConnected != null)
                reactor.OnConnected = _onConnected;

            if (_onReceived != null)
                reactor.OnReceived = _onReceived;

            reactor.Start();

            return reactor;
        }

        /// <summary>
        /// Builds an in-process (same-application) message hub reactor.
        /// </summary>
        /// <remarks>
        /// This type of reactor doesn’t use sockets or shared memory — 
        /// it simply connects multiple components inside your program using memory queues.
        /// It’s extremely fast because everything happens in RAM within the same process.
        /// </remarks>
        private IReactor BuildInprocReactor()
        {
            if (string.IsNullOrWhiteSpace(_channelName))
                throw new InvalidOperationException("Inproc mode requires WithChannel(channelName).");

            var reactor = new InprocReactor(
                name: _channelName,
                bufferSize: _bufferSize,
                ringCapacity: _ringCapacity,
                maxDegreeOfParallelism: _maxDegreeOfParallelism);

            if (_onConnected != null)
                reactor.OnConnected = _onConnected;

            if (_onReceived != null)
                reactor.OnReceived = _onReceived;
                    
            return reactor;
        }

        #endregion
    }
}