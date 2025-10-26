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
    /// Fluent builder for constructing <see cref="IParticle"/> transport clients or servers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="ParticleBuilder"/> provides a consistent, fluent way to configure and create
    /// transport channels across multiple backends:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>TCP</b> — for network communication between machines.</description></item>
    ///   <item><description><b>UDP</b> — for lightweight datagrams or multicast broadcasts.</description></item>
    ///   <item><description><b>IPC</b> — for ultra-fast interprocess communication via shared memory.</description></item>
    ///   <item><description><b>Inproc</b> — for communication within a single process (useful for testing).</description></item>
    /// </list>
    /// <para>
    /// Each mode can be configured using fluent methods before calling <see cref="Build"/>.
    /// </para>
    /// <example>
    /// Example: Create an IPC server and client
    /// <code>
    /// var server = new ParticleBuilder()
    ///     .UseMode(TransportMode.Ipc)
    ///     .WithChannel("DemoIpc", isServer: true)
    ///     .OnReceived((p, msg) =&gt; Console.WriteLine($"Server got: {msg.Length} bytes"))
    ///     .Build();
    /// 
    /// var client = new ParticleBuilder()
    ///     .UseMode(TransportMode.Ipc)
    ///     .WithChannel("DemoIpc")
    ///     .OnConnected(_ =&gt; Console.WriteLine("Client connected"))
    ///     .Build();
    /// 
    /// client.Send(Encoding.UTF8.GetBytes("Hello Server!"));
    /// </code>
    /// </example>
    /// </remarks>
    public sealed class ParticleBuilder
    {
        // General configuration
        private TransportMode _mode = TransportMode.Tcp;
        private EndPoint? _remoteEndPoint;
        private IPEndPoint? _localEndPoint;
        private string? _channelName;
        private bool _isServer;

        // Performance & resource options
        private int _bufferSize = 8192;
        private int _parallelism = 8;
        private int _ringCapacity = 1 << 20; // 1 MiB default ring size for IPC/Inproc

        // UDP-specific options
        private bool _allowBroadcast;
        private bool _disableLoopback = true;
        private IPAddress? _multicastGroup;

        // Common event handlers
        private Action<IParticle, ReadOnlyMemory<byte>>? _onReceived;
        private Action<IParticle>? _onDisconnected;
        private Action<IParticle>? _onConnected;

        #region Fluent Configuration

        /// <summary>
        /// Selects the desired transport mode (TCP, UDP, IPC, or Inproc).
        /// </summary>
        public ParticleBuilder UseMode(TransportMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>
        /// Sets the remote endpoint for TCP or UDP client connections.
        /// </summary>
        public ParticleBuilder ConnectTo(EndPoint endpoint)
        {
            _remoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            return this;
        }

        /// <summary>
        /// Binds a local endpoint for TCP servers or UDP sockets.
        /// </summary>
        public ParticleBuilder BindTo(IPEndPoint local)
        {
            _localEndPoint = local ?? throw new ArgumentNullException(nameof(local));
            return this;
        }

        /// <summary>
        /// Configures a shared channel name for IPC or Inproc transports.
        /// </summary>
        /// <param name="name">The shared channel identifier.</param>
        /// <param name="isServer">True if this process acts as the server (listener).</param>
        public ParticleBuilder WithChannel(string name, bool isServer = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            _channelName = name;
            _isServer = isServer;
            return this;
        }

        /// <summary>
        /// Sets the socket or buffer size for network transports.
        /// </summary>
        public ParticleBuilder WithBufferSize(int bytes)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            _bufferSize = bytes;
            return this;
        }

        /// <summary>
        /// Sets the total capacity (in bytes) of the shared memory ring buffer (IPC/Inproc).
        /// </summary>
        public ParticleBuilder WithRingCapacity(int capacity)
        {
            if (capacity < 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ringCapacity = capacity;
            return this;
        }

        /// <summary>
        /// Sets the number of worker threads or async loops for parallel message processing.
        /// </summary>
        public ParticleBuilder WithParallelism(int degree)
        {
            if (degree <= 0) throw new ArgumentOutOfRangeException(nameof(degree));
            _parallelism = degree;
            return this;
        }

        /// <summary>
        /// Registers a handler to be called whenever data is received.
        /// </summary>
        public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
        {
            _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Registers a handler to be called when a particle disconnects.
        /// </summary>
        public ParticleBuilder OnDisconnected(Action<IParticle> handler)
        {
            _onDisconnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Registers a handler to be called when a particle successfully connects.
        /// </summary>
        public ParticleBuilder OnConnected(Action<IParticle> handler)
        {
            _onConnected = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Enables UDP multicast support for group-based message delivery.
        /// </summary>
        public ParticleBuilder EnableMulticast(IPAddress group, int port, bool disableLoopback = true)
        {
            _mode = TransportMode.Udp;
            _multicastGroup = group ?? throw new ArgumentNullException(nameof(group));
            _disableLoopback = disableLoopback;

            _localEndPoint = new IPEndPoint(IPAddress.Any, port);
            _remoteEndPoint = new IPEndPoint(group, port);

            return this;
        }

        /// <summary>
        /// Enables or disables UDP broadcast transmission (e.g. 255.255.255.255).
        /// </summary>
        public ParticleBuilder AllowBroadcast(bool allow = true)
        {
            _allowBroadcast = allow;
            return this;
        }

        #endregion

        #region Build Methods

        /// <summary>
        /// Builds and returns the configured <see cref="IParticle"/> instance.
        /// </summary>
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
                // 🧩 IPC Server using shared memory (MappedReactor)
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
                // 🧩 IPC Client (MappedParticle)
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
                throw new InvalidOperationException("UDP mode requires BindTo() or ConnectTo().");

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

    /// <summary>
    /// Represents all supported transport modes for <see cref="ParticleBuilder"/>.
    /// </summary>
    public enum TransportMode
    {
        Inproc,
        Ipc,
        Tcp,
        Udp
    }
}
