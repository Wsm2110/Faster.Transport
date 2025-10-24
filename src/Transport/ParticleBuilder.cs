using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System;
using System.Net;

namespace Faster.Transport
{
    /// <summary>
    /// Fluent builder for creating <see cref="IParticle"/> transport clients or servers.
    /// 
    /// ✅ Supports multiple transport backends:
    /// - <see cref="TransportMode.Inproc"/> (in-process, zero-copy)
    /// - <see cref="TransportMode.Ipc"/> (shared-memory cross-process)
    /// - <see cref="TransportMode.Tcp"/> (network transport)
    /// 
    /// Each transport implements <see cref="IParticle"/>, providing a unified API for sending,
    /// receiving, connection events, and graceful shutdown.
    /// </summary>
    public sealed class ParticleBuilder
    {
        private TransportMode _mode = TransportMode.Tcp;
        private EndPoint? _remoteEndPoint;
        private string? _channelName;
        private bool _isServer;
        private int _bufferSize = 8192;
        private int _parallelism = 8;
        private int _ringCapacity = 1 << 20; // 1 MiB for IPC/Inproc

        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;
        private Action<IParticle, Exception?>? _onDisconnected;
        private Action<IParticle>? _onConnected;

        #region Fluent Configuration

        /// <summary>
        /// Selects the desired transport backend.
        /// </summary>
        public ParticleBuilder UseMode(TransportMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>
        /// Specifies a TCP endpoint for <see cref="TransportMode.Tcp"/>.
        /// </summary>
        public ParticleBuilder ConnectTo(EndPoint endpoint)
        {
            _remoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            return this;
        }

        /// <summary>
        /// Sets the shared channel name (for IPC/Inproc).
        /// Both peers must use the same name to connect.
        /// </summary>
        public ParticleBuilder WithChannel(string name, bool isServer = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            _channelName = name;
            _isServer = isServer;
            return this;
        }

        /// <summary>
        /// Configures the buffer size used for send/receive operations.
        /// </summary>
        public ParticleBuilder WithBufferSize(int bytes)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            _bufferSize = bytes;
            return this;
        }

        /// <summary>
        /// Configures the internal shared-memory ring capacity for IPC/Inproc.
        /// </summary>
        public ParticleBuilder WithRingCapacity(int capacity)
        {
            if (capacity < 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ringCapacity = capacity;
            return this;
        }

        /// <summary>
        /// Sets the degree of parallelism for internal buffer management.
        /// </summary>
        public ParticleBuilder WithParallelism(int degree)
        {
            if (degree <= 0) throw new ArgumentOutOfRangeException(nameof(degree));
            _parallelism = degree;
            return this;
        }

        /// <summary>
        /// Registers a callback invoked when a message is received.
        /// Provides the <see cref="IParticle"/> sender, so replies can be sent directly.
        /// </summary>
        /// <param name="handler">
        /// Example: <c>(p, data) =&gt; p.Send("pong"u8.ToArray());</c>
        /// </param>
        public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Registers a callback invoked when disconnected or an error occurs.
        /// </summary>
        public ParticleBuilder OnDisconnected(Action<IParticle, Exception?> handler)
        {
            _onDisconnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Registers a callback invoked when the connection is fully established and ready.
        /// </summary>
        public ParticleBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        #endregion

        #region Build

        /// <summary>
        /// Builds and initializes an <see cref="IParticle"/> transport client or server
        /// based on the configured <see cref="TransportMode"/>.
        /// </summary>
        public IParticle Build()
        {
            return _mode switch
            {
                TransportMode.Tcp => BuildTcp(),
                TransportMode.Ipc => BuildIpc(),
                TransportMode.Inproc => BuildInproc(),
                _ => throw new InvalidOperationException($"Unsupported transport mode: {_mode}")
            };
        }

        private IParticle BuildTcp()
        {
            if (_remoteEndPoint == null)
                throw new InvalidOperationException("TCP mode requires ConnectTo(EndPoint).");

            var client = new Particle(_remoteEndPoint, _bufferSize, _parallelism);

            if (_onReceived != null)
                client.OnReceived = _onReceived;

            if (_onDisconnected != null)
                client.OnDisconnected = p => _onDisconnected(p, null);

            if (_onConnected != null)
                client.OnConnected = _onConnected;

            return client;
        }

        private IParticle BuildIpc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("IPC mode requires WithChannel(name).");

            var ipc = new MappedParticle(_channelName, _isServer, _ringCapacity);

            if (_onReceived != null)
                ipc.OnReceived = _onReceived;

            if (_onDisconnected != null)
                ipc.OnDisconnected = p => _onDisconnected(p, null);

            if (_onConnected != null)
                ipc.OnConnected = _onConnected;

            return ipc;
        }

        private IParticle BuildInproc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("Inproc mode requires WithChannel(name).");

            var inproc = new InprocParticle(_channelName, _isServer, _bufferSize, _ringCapacity, _parallelism);

            if (_onReceived != null)
                inproc.OnReceived = _onReceived;

            if (_onDisconnected != null)
                inproc.OnDisconnected = p => _onDisconnected(p, null);

            if (_onConnected != null)
                inproc.OnConnected = _onConnected;

            return inproc;
        }

        #endregion
    }

    /// <summary>
    /// Supported transport backends for <see cref="IParticle"/>.
    /// </summary>
    public enum TransportMode
    {
        /// <summary>In-process (no OS calls, fastest possible).</summary>
        Inproc,

        /// <summary>Cross-process shared memory transport.</summary>
        Ipc,

        /// <summary>Network-based TCP transport.</summary>
        Tcp
    }
}
