using Faster.Transport.Contracts;
using Faster.Transport.Features.Tcp;
using Faster.Transport.Features.Udp;
using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System.Net;
using System.Net.Sockets;

namespace Faster.Transport;

public enum TransportMode
{
    Inproc,
    Ipc,
    Tcp,
    Udp
}

/// <summary>
/// Fluent builder for constructing <see cref="IParticle"/> clients or servers.
/// Now supports building a TCP server reactor.
/// </summary>
public sealed class ParticleBuilder
{
    // General configuration
    private TransportMode _mode = TransportMode.Tcp;
    private IPEndPoint? _remoteEndPoint;
    private IPEndPoint? _localEndPoint;
    private Socket? _acceptedSocket;
    private string? _channelName;
    private bool _isServer;

    // TCP server options
    private int _backlog = 1024;

    // Performance options
    private int _bufferSize = 8192;
    private int _parallelism = 8;
    private int _ringCapacity = 128 + (1 << 20);

    // UDP options
    private bool _allowBroadcast;
    private bool _disableLoopback = true;
    private IPAddress? _multicastGroup;
    private int _multicastPort;

    // Auto-reconnect
    private bool _autoReconnect;
    private TimeSpan _baseDelay = TimeSpan.FromSeconds(1);
    private TimeSpan _maxDelay = TimeSpan.FromSeconds(30);

    // Event handlers
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
        _remoteEndPoint = endpoint;
        _acceptedSocket = null;
        return this;
    }

    public ParticleBuilder WithLocal(IPEndPoint endpoint)
    {
        _localEndPoint = endpoint;
        return this;
    }

    public ParticleBuilder FromAcceptedSocket(Socket socket)
    {
        _mode = TransportMode.Tcp;
        _acceptedSocket = socket;
        return this;
    }

    public ParticleBuilder WithChannel(string name, bool isServer = false)
    {
        _channelName = name;
        _isServer = isServer;
        return this;
    }

    public ParticleBuilder WithBufferSize(int bytes)
    {
        _bufferSize = bytes;
        return this;
    }

    public ParticleBuilder WithParallelism(int degree)
    {
        _parallelism = degree;
        return this;
    }

    public ParticleBuilder WithRingCapacity(int capacity)
    {
        _ringCapacity = capacity;
        return this;
    }

    public ParticleBuilder WithTcpBacklog(int backlog)
    {
        _backlog = backlog > 0 ? backlog : 1024;
        return this;
    }

    public ParticleBuilder AsServer(bool enable = true)
    {
        _isServer = enable;
        return this;
    }

    public ParticleBuilder AllowBroadcast(bool allow = true)
    {
        _allowBroadcast = allow;
        return this;
    }

    public ParticleBuilder WithMulticast(IPAddress group, int port, bool disableLoopback = true)
    {
        _mode = TransportMode.Udp;
        _multicastGroup = group;
        _multicastPort = port;
        _disableLoopback = disableLoopback;
        return this;
    }

    public ParticleBuilder OnReceived(Action<IParticle, ReadOnlyMemory<byte>> handler)
    {
        _onReceived = handler;
        return this;
    }

    public ParticleBuilder OnConnected(Action<IParticle> handler)
    {
        _onConnected = handler;
        return this;
    }

    public ParticleBuilder OnDisconnected(Action<IParticle> handler)
    {
        _onDisconnected = handler;
        return this;
    }

    public ParticleBuilder WithAutoReconnect(double baseSeconds = 1, double maxSeconds = 30)
    {
        _autoReconnect = true;
        _baseDelay = TimeSpan.FromSeconds(baseSeconds);
        _maxDelay = TimeSpan.FromSeconds(maxSeconds);
        return this;
    }

    #endregion

    public IParticle Build()
    {
        // 🧩 Auto-reconnect wrapper
        if (_autoReconnect)
        {
            return new AutoReconnectWrapper(
                factory: () => BuildCore(),
                baseDelay: _baseDelay,
                maxDelay: _maxDelay,
                onConnected: _onConnected,
                onDisconnected: _onDisconnected,
                onReceived: _onReceived
            );
        }

        return BuildCore();
    }

    private IParticle BuildCore()
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
        // 🧱 TCP Server (Reactor)
        if (_isServer)
        {
            var bind = _localEndPoint ?? new IPEndPoint(IPAddress.Any, 5000);
            var reactor = new Reactor(bind, backlog: _backlog, _bufferSize, _parallelism)
            {
                OnReceived = _onReceived,
                OnConnected = _onConnected
            };

            reactor.Start();
            return new TcpServerWrapper(reactor);
        }

        // 🧱 TCP Client
        if (_acceptedSocket != null)
        {
            var tcp = new Particle(_acceptedSocket, _bufferSize, _parallelism);
            ApplyHandlers(tcp);
            _onConnected?.Invoke(tcp);
            return tcp;
        }

        if (_remoteEndPoint == null)
            throw new InvalidOperationException("TCP requires WithRemote() or AsServer(true).");

        var client = new Particle(_remoteEndPoint, _bufferSize, _parallelism);
        ApplyHandlers(client);
        return client;
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

            if (_onReceived != null) server.OnReceived = _onReceived;
            if (_onConnected != null) server.ClientConnected = _onConnected;
            if (_onDisconnected != null) server.ClientDisconnected = _onDisconnected;

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
        if (_multicastGroup != null)
        {
            var port = _multicastPort > 0 ? _multicastPort : 0;
            var local = new IPEndPoint(IPAddress.Any, port);
            var remote = new IPEndPoint(_multicastGroup, port);

            var udp = new UdpParticle(local, remote, _multicastGroup, _disableLoopback, _allowBroadcast);
            ApplyHandlers(udp);
            return udp;
        }

        var localEp = _localEndPoint ?? new IPEndPoint(IPAddress.Any, 0);
        var remoteEp = _remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);

        var p = new UdpParticle(localEp, remoteEp, allowBroadcast: _allowBroadcast);
        ApplyHandlers(p);
        return p;
    }

    private void ApplyHandlers(IParticle p)
    {
        if (_onReceived != null) p.OnReceived = _onReceived;
        if (_onDisconnected != null) p.OnDisconnected = _onDisconnected;
        if (_onConnected != null) p.OnConnected = _onConnected;
    }
}