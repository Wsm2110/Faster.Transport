using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using Faster.Transport.Features.Udp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System;
using System.Net;
using System.Net.Sockets;

namespace Faster.Transport
{
    /// <summary>
    /// Fluent builder for constructing <see cref="IParticle"/> transport clients or servers.
    /// </summary>
    public sealed class ParticleBuilder
    {
        // General configuration
        private TransportMode _mode = TransportMode.Tcp;
        private int _multicastPort;
        private IPEndPoint? _remoteEndPoint;
        private IPEndPoint? _localEndPoint;
        private Socket? _acceptedSocket;
        private string? _channelName;
        private bool _isServer;

        // Performance & resource options
        private int _bufferSize = 8192;
        private int _parallelism = 8;
        private int _ringCapacity = 128 + (1 << 20); // 1 MiB

        // UDP options
        private bool _allowBroadcast;
        private bool _disableLoopback = true;
        private IPAddress? _multicastGroup;

        // Auto-reconnect settings
        private bool _autoReconnect;
        private TimeSpan _baseDelay = TimeSpan.FromSeconds(1);
        private TimeSpan _maxDelay = TimeSpan.FromSeconds(30);

        // Common handlers
        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;
        private Action<IParticle>? _onDisconnected;
        private Action<IParticle>? _onConnected;

        #region === Fluent Configuration ===

        public ParticleBuilder UseMode(TransportMode mode)
        {
            _mode = mode;
            return this;
        }

        public ParticleBuilder WithRemote(IPEndPoint endpoint)
        {
            _remoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _acceptedSocket = null;
            return this;
        }

        public ParticleBuilder WithLocal(IPEndPoint local)
        {
            _localEndPoint = local;
            return this;
        }

        public ParticleBuilder FromAcceptedSocket(Socket socket)
        {
            _mode = TransportMode.Tcp;
            _acceptedSocket = socket ?? throw new ArgumentNullException(nameof(socket));
            return this;
        }

        public ParticleBuilder WithChannel(string name, bool isServer = false)
        {
            _channelName = name ?? throw new ArgumentNullException(nameof(name));
            _isServer = isServer;
            return this;
        }

        public ParticleBuilder WithBufferSize(int bytes)
        {
            _bufferSize = bytes > 0 ? bytes : throw new ArgumentOutOfRangeException(nameof(bytes));
            return this;
        }

        public ParticleBuilder WithRingCapacity(int capacity)
        {
            _ringCapacity = capacity >= 1024 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
            return this;
        }

        public ParticleBuilder WithParallelism(int degree)
        {
            _parallelism = degree > 0 ? degree : throw new ArgumentOutOfRangeException(nameof(degree));
            return this;
        }

        public ParticleBuilder WithAutoReconnect(double baseSeconds = 1, double maxSeconds = 30)
        {
            _autoReconnect = true;
            _baseDelay = TimeSpan.FromSeconds(baseSeconds);
            _maxDelay = TimeSpan.FromSeconds(maxSeconds);
            return this;
        }

        public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler;
            return this;
        }

        public ParticleBuilder OnDisconnected(Action<IParticle> handler)
        {
            _onDisconnected = handler;
            return this;
        }

        public ParticleBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler;
            return this;
        }

        public ParticleBuilder WithMulticast(IPAddress group, int port, bool disableLoopback = true)
        {
            _mode = TransportMode.Udp;
            _multicastPort = port;

            _multicastGroup = group ?? throw new ArgumentNullException(nameof(group));
            _disableLoopback = disableLoopback;
            _localEndPoint = new IPEndPoint(IPAddress.Any, port);
            _remoteEndPoint = new IPEndPoint(group, port);
            return this;
        }

        public ParticleBuilder AllowBroadcast(bool allow = true)
        {
            _allowBroadcast = allow;
            return this;
        }

        #endregion

        #region === Build ===

        public IParticle Build()
        {
            // ✅ If auto-reconnect is enabled, wrap a factory that builds the raw transport
            if (_autoReconnect)
            {
                return new AutoReconnectWrapper(
                    factory: () =>
                    {
                        return _mode switch
                        {
                            TransportMode.Tcp => BuildTcp(),
                            TransportMode.Ipc => BuildIpc(),
                            TransportMode.Inproc => BuildInproc(),
                            TransportMode.Udp => BuildUdp(),
                            _ => throw new InvalidOperationException($"Unsupported transport: {_mode}")
                        };
                    },
                    baseDelay: _baseDelay,
                    maxDelay: _maxDelay,
                    onConnected: _onConnected,
                    onDisconnected: _onDisconnected,
                    onReceived: _onReceived
                );
            }

            // 🚀 Normal build path (no reconnect)
            return _mode switch
            {
                TransportMode.Tcp => BuildTcp(),
                TransportMode.Ipc => BuildIpc(),
                TransportMode.Inproc => BuildInproc(),
                TransportMode.Udp => BuildUdp(),
                _ => throw new InvalidOperationException($"Unsupported transport: {_mode}")
            };
        }


        private IParticle BuildTcp()
        {
            if (_acceptedSocket is not null)
            {
                var tcp = new Particle(_acceptedSocket, _bufferSize, _parallelism);
                ApplyHandlers(tcp);
                _onConnected?.Invoke(tcp);
                return tcp;
            }

            if (_remoteEndPoint is null)
                throw new InvalidOperationException("TCP requires WithRemote() or FromAcceptedSocket().");

            var tcpClient = new Particle(_remoteEndPoint, _bufferSize, _parallelism);

            ApplyHandlers(tcpClient);
            return tcpClient;
        }

        private IParticle BuildIpc()
        {
            if (string.IsNullOrWhiteSpace(_channelName))
                throw new InvalidOperationException("IPC mode requires WithChannel(name).");

            int ringBytes = _ringCapacity <= 0 ? (128 + (1 << 20)) : _ringCapacity;

            if (_isServer)
            {
                var server = new MappedReactor(_channelName, global: false, ringBytes: ringBytes);
                if (_onReceived != null) server.OnReceived = _onReceived;
                if (_onConnected != null) server.OnConnected = id => _onConnected(new IpcClientProxy(id, server));
                if (_onDisconnected != null) server.OnClientDisconnected = id => _onDisconnected(new IpcClientProxy(id, server));
                server.Start();
                return new IpcServerWrapper(server);
            }

            var rnd = new Random();
            var buf = new byte[8];
            rnd.NextBytes(buf);
            ulong id = BitConverter.ToUInt64(buf, 0);
            var client = new MappedParticle(_channelName, id, global: false, ringBytes: ringBytes);
            ApplyHandlers(client);
            client.Start();
            return client;
        }

        private IParticle BuildInproc()
        {
            if (_channelName == null)
                throw new InvalidOperationException("Inproc mode requires WithChannel(name).");

            if (_isServer)
            {
                var server = new InprocReactor(_channelName, _bufferSize, _ringCapacity, _parallelism);

                if (_onReceived != null)
                    server.OnReceived = _onReceived;

                if (_onConnected != null)
                    server.ClientConnected = _onConnected;

                if (_onDisconnected != null)
                    server.ClientDisconnected = _onDisconnected;

                server.Start();
                return new InprocServerWrapper(server);
            }

            var client = new InprocParticle(_channelName, isServer: false, _bufferSize, _ringCapacity, _parallelism);
            ApplyHandlers(client);
            client.Start();
            return client;
        }


        private IParticle BuildUdp()
        {
            // 🟢 Multicast mode
            if (_multicastGroup != null)
            {
                var port = _multicastPort > 0 ? _multicastPort : 0;
                var local = new IPEndPoint(IPAddress.Any, port);
                var remote = new IPEndPoint(_multicastGroup, port);

                var particle = new UdpParticle(
                    localEndPoint: local,
                    remoteEndPoint: remote,
                    joinMulticast: _multicastGroup,
                    disableLoopback: _disableLoopback,
                    allowBroadcast: _allowBroadcast);

                ApplyHandlers(particle);
                return particle;
            }

            // 🟢 Unicast / Broadcast mode

            // Infer missing endpoints
            var localEp = _localEndPoint ?? InferLocalFromRemote(_remoteEndPoint);
            var remoteEp = _remoteEndPoint ?? InferRemoteFromLocal(_localEndPoint);

            var p = new UdpParticle(
                localEndPoint: localEp,
                remoteEndPoint: remoteEp,
                allowBroadcast: _allowBroadcast);

            ApplyHandlers(p);
            return p;
        }

        private static IPEndPoint InferLocalFromRemote(IPEndPoint? remote)
        {
            if (remote == null)
                return new IPEndPoint(IPAddress.Loopback, 0);

            // Match address family, use ephemeral port
            return new IPEndPoint(
                remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Loopback
                    : IPAddress.Loopback,
                0);
        }

        private static IPEndPoint InferRemoteFromLocal(IPEndPoint? local)
        {
            if (local == null)
                return new IPEndPoint(IPAddress.Loopback, 0);

            // Match address family, use ephemeral port
            return new IPEndPoint(
                local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Loopback
                    : IPAddress.Loopback,
                0);
        }

        private void ApplyHandlers(IParticle p)
        {
            if (_onReceived != null) p.OnReceived = _onReceived;
            if (_onDisconnected != null) p.OnDisconnected = _onDisconnected;
            if (_onConnected != null) p.OnConnected = _onConnected;
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
