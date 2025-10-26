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
    /// Fluent builder for creating <see cref="IParticle"/> transport clients or servers.
    /// Supports: Inproc, IPC, TCP, UDP.
    /// </summary>
    public sealed class ParticleBuilder
    {
        private TransportMode _mode = TransportMode.Tcp;
        private EndPoint? _remoteEndPoint;
        private IPEndPoint? _localEndPoint;
        private string? _channelName;
        private bool _isServer;
        private int _bufferSize = 8192;
        private int _parallelism = 8;
        private int _ringCapacity = 1 << 20; // 1 MiB for IPC/Inproc
        private bool _allowBroadcast;
        private bool _disableLoopback = true;
        private IPAddress? _multicastGroup;

        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;
        private Action<IParticle>? _onDisconnected;
        private Action<IParticle>? _onConnected;

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

        public ParticleBuilder BindTo(IPEndPoint local)
        {
            _localEndPoint = local ?? throw new ArgumentNullException(nameof(local));
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

        public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public ParticleBuilder OnDisconnected(Action<IParticle> handler)
        {
            _onDisconnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        public ParticleBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Enables UDP multicast support for the configured <see cref="UdpParticle"/>.
        /// </summary>
        public ParticleBuilder EnableMulticast(IPAddress group, int port, bool disableLoopback = true)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            _mode = TransportMode.Udp;
            _multicastGroup = group;
            _disableLoopback = disableLoopback;

            // Automatically configure local & remote endpoints
            _localEndPoint = new IPEndPoint(IPAddress.Any, port);
            _remoteEndPoint = new IPEndPoint(group, port);

            return this;
        }

        /// <summary>
        /// Allows UDP broadcast sending to 255.255.255.255 or subnet broadcasts.
        /// </summary>
        public ParticleBuilder AllowBroadcast(bool allow = true)
        {
            _allowBroadcast = allow;
            return this;
        }

        #endregion

        #region Build

        public IParticle Build()
        {
            return _mode switch
            {
                TransportMode.Tcp => BuildTcp(),
                TransportMode.Ipc => BuildIpc(),
                TransportMode.Inproc => BuildInproc(),
                TransportMode.Udp => BuildUdp(),
                _ => throw new InvalidOperationException($"Unsupported transport mode: {_mode}")
            };
        }

        private IParticle BuildTcp()
        {
            if (_remoteEndPoint == null)
                throw new InvalidOperationException("TCP mode requires ConnectTo(EndPoint).");

            var tcp = new Particle(_remoteEndPoint, _bufferSize, _parallelism);
            ApplyHandlers(tcp);
            return tcp;
        }

        private IParticle BuildIpc()
        {
            if (string.IsNullOrWhiteSpace(_channelName))
                throw new InvalidOperationException("IPC mode requires WithChannel(name).");

            int ringBytes = _ringCapacity <= 0 ? (128 + (1 << 20)) : _ringCapacity;

            if (_isServer)
            {
                // 🚀 IPC Server (MappedReactor)
                var server = new MappedReactor(_channelName, global: false, ringBytes: ringBytes);

                if (_onReceived != null)
                    server.OnReceived = _onReceived;

                if (_onConnected != null)
                    server.OnConnected = id =>
                    {
                        var proxy = new IpcClientProxy(id, server);
                        _onConnected(proxy);
                    };

                if (_onDisconnected != null)
                    server.OnClientDisconnected = id =>
                    {
                        var proxy = new IpcClientProxy(id, server);
                        _onDisconnected(proxy);
                    };

                server.Start();
                return new IpcServerWrapper(server);
            }
            else
            {
                // 🚀 IPC Client (MappedParticle)
                ulong id = (ulong)Random.Shared.NextInt64();
                var client = new MappedParticle(_channelName, id, global: false, ringBytes: ringBytes);

                if (_onReceived != null)
                    client.OnReceived = _onReceived;

                if (_onConnected != null)
                    client.OnConnected = _onConnected;

                if (_onDisconnected != null)
                    client.OnDisconnected = _onDisconnected;

                client.Start();
                return client;
            }
        }



        private IParticle BuildInproc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("Inproc mode requires WithChannel(name).");

            var inproc = new InprocParticle(_channelName, _isServer, _bufferSize, _ringCapacity, _parallelism);
            ApplyHandlers(inproc);
            return inproc;
        }

        private IParticle BuildUdp()
        {
            if (_localEndPoint == null && _remoteEndPoint == null)
                throw new InvalidOperationException(
                    "UDP mode requires at least BindTo() or ConnectTo() — both cannot be null.");

            var udp = new UdpParticle(
                localEndPoint: _localEndPoint ?? new IPEndPoint(IPAddress.Any, 0),
                remoteEndPoint: _remoteEndPoint as IPEndPoint,
                joinMulticast: _multicastGroup,
                disableLoopback: _disableLoopback,
                allowBroadcast: _allowBroadcast,
                bufferSize: _bufferSize,
                maxDegreeOfParallelism: _parallelism
            );

            ApplyHandlers(udp);
            return udp;
        }

        private void ApplyHandlers(IParticle p)
        {
            if (_onReceived != null)
                p.OnReceived = _onReceived;

            if (_onDisconnected != null)
                p.OnDisconnected = particle => _onDisconnected(particle);

            if (_onConnected != null)
                p.OnConnected = _onConnected;
        }

        #endregion
    }

    public enum TransportMode
    {
        Inproc,
        Ipc,
        Tcp,
        Udp
    }
}
