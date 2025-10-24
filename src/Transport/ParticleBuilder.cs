using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using Faster.Transport.Features.Udp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System;
using System.Net;

namespace Faster.Transport
{
    /// <summary>
    /// Fluent builder for creating <see cref="IParticle"/> instances using multiple transport backends:
    /// - <see cref="TransportMode.Inproc"/> for in-process ultra-fast communication
    /// - <see cref="TransportMode.Ipc"/> for shared-memory cross-process
    /// - <see cref="TransportMode.Tcp"/> for network-based reliable communication
    /// - <see cref="TransportMode.Udp"/> for lightweight datagram-based communication
    /// </summary>
    public sealed class ParticleBuilder
    {
        private TransportMode _mode = TransportMode.Tcp;

        // Common options
        private int _bufferSize = 8192;
        private int _parallelism = 8;
        private int _ringCapacity = 1 << 20;

        // Connection details
        private EndPoint? _remoteEndPoint;
        private EndPoint? _localEndPoint;
        private IPAddress? _multicastGroup;

        private bool _isServer;
        private bool _allowBroadcast;
        private bool _disableLoopback = true;

        // Event handlers
        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;
        private Action<IParticle>? _onConnected;
        private Action<IParticle, Exception?>? _onDisconnected;

        private string? _channelName;

        #region Fluent Configuration

        public ParticleBuilder UseMode(TransportMode mode)
        {
            _mode = mode;
            return this;
        }

        public ParticleBuilder ConnectTo(EndPoint endpoint)
        {
            _remoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            return this;
        }

        public ParticleBuilder BindTo(EndPoint endpoint)
        {
            _localEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            return this;
        }

        public ParticleBuilder WithChannel(string name, bool isServer = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            _channelName = name;
            _isServer = isServer;
            return this;
        }

        public ParticleBuilder WithBufferSize(int bytes)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            _bufferSize = bytes;
            return this;
        }

        public ParticleBuilder WithRingCapacity(int capacity)
        {
            if (capacity < 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ringCapacity = capacity;
            return this;
        }

        public ParticleBuilder WithParallelism(int degree)
        {
            if (degree <= 0) throw new ArgumentOutOfRangeException(nameof(degree));
            _parallelism = degree;
            return this;
        }

        public ParticleBuilder AllowBroadcast(bool enable = true)
        {
            _allowBroadcast = enable;
            return this;
        }

        public ParticleBuilder JoinMulticastGroup(IPAddress address, bool disableLoopback = true)
        {
            _multicastGroup = address ?? throw new ArgumentNullException(nameof(address));
            _disableLoopback = disableLoopback;
            return this;
        }

        public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public ParticleBuilder OnDisconnected(Action<IParticle, Exception?> handler)
        {
            _onDisconnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public ParticleBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        #endregion

        #region Build

        public IParticle Build()
        {
            return _mode switch
            {
                TransportMode.Tcp => BuildTcp(),
                TransportMode.Udp => BuildUdp(),
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
            ApplyHandlers(client);
            return client;
        }

        private IParticle BuildUdp()
        {
            // Always full-duplex: bind + optional remote
            var local = (IPEndPoint?)_localEndPoint ?? new IPEndPoint(IPAddress.Any, 0);
            var remote = (IPEndPoint?)_remoteEndPoint;

            var udp = new UdpParticle(
                localEndPoint: local,
                remoteEndPoint: remote,
                joinMulticast: _multicastGroup,
                disableLoopback: _disableLoopback,
                allowBroadcast: _allowBroadcast,
                bufferSize: _bufferSize,
                maxDegreeOfParallelism: _parallelism
            );

            ApplyHandlers(udp);
            return udp;
        }

        private IParticle BuildIpc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("IPC mode requires WithChannel(name).");

            var ipc = new MappedParticle(_channelName, _isServer, _ringCapacity);
            ApplyHandlers(ipc);
            return ipc;
        }

        private IParticle BuildInproc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("Inproc mode requires WithChannel(name).");

            var inproc = new InprocParticle(_channelName, _isServer, _bufferSize, _ringCapacity, _parallelism);
            ApplyHandlers(inproc);
            return inproc;
        }

        private void ApplyHandlers(IParticle p)
        {
            if (_onReceived != null)
                p.OnReceived = _onReceived;

            if (_onDisconnected != null)
                p.OnDisconnected = x => _onDisconnected(x, null);

            if (_onConnected != null)
                p.OnConnected = _onConnected;
        }

        #endregion
    }

    /// <summary>
    /// Supported transport backends for <see cref="IParticle"/>.
    /// </summary>
    public enum TransportMode
    {
        Inproc,
        Ipc,
        Tcp,
        Udp
    }
}
