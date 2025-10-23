using Faster.Transport.Contracts;
using System.Net;

namespace Faster.Transport;

/// <summary>
/// Fluent builder for creating high-performance framed TCP clients.
/// </summary>
/// <remarks>
/// This builder automatically returns the correct client type based on configuration:
/// <list type="bullet">
/// <item><see cref="Particle"/> → single-threaded async (<see cref="IParticle"/>)</item>
/// <item><see cref="ParticleFlux"/> → multi-threaded async (<see cref="IParticle"/>)</item>
/// <item><see cref="ParticleBurst"/> → high-throughput fire-and-forget (<see cref="IParticleBurst"/>)</item>
/// </list>
/// </remarks>
public sealed class ParticleBuilder
{
    private EndPoint? _remoteEndPoint;
    private int _bufferSize = 8192;
    private int _maxDegreeOfParallelism = 8;
    private bool _useConcurrent = false;
    private bool _useBurst = false;

    private Action<ReadOnlyMemory<byte>>? _onReceived;
    private Action<IParticle, Exception?>? _onDisconnectedClient;
    private Action<IParticleBurst, Exception?>? _onDisconnectedBurst;

    #region Fluent Configuration

    /// <summary>
    /// Sets the remote endpoint to connect to.
    /// </summary>
    public ParticleBuilder ConnectTo(EndPoint endpoint)
    {
        _remoteEndPoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        return this;
    }

    /// <summary>
    /// Sets the per-operation buffer size (default 8192 bytes).
    /// </summary>
    public ParticleBuilder WithBufferSize(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _bufferSize = size;
        return this;
    }

    /// <summary>
    /// Sets the concurrency level used for internal buffer pools.
    /// </summary>
    public ParticleBuilder WithParallelism(int degree)
    {
        if (degree <= 0) throw new ArgumentOutOfRangeException(nameof(degree));
        _maxDegreeOfParallelism = degree;
        return this;
    }

    /// <summary>
    /// Configures the client for multi-threaded concurrent sends (async safe).
    /// </summary>
    public ParticleBuilder AsConcurrent()
    {
        _useConcurrent = true;
        _useBurst = false;
        return this;
    }

    /// <summary>
    /// Configures the client for ultra-high-throughput fire-and-forget sending.
    /// </summary>
    public ParticleBuilder AsBurst()
    {
        _useConcurrent = true;
        _useBurst = true;
        return this;
    }

    /// <summary>
    /// Attaches a callback triggered when a complete frame is received.
    /// </summary>
    public ParticleBuilder OnReceived(Action<ReadOnlyMemory<byte>> handler)
    {
        _onReceived = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Attaches a disconnect callback for async particles (<see cref="IParticle"/>).
    /// </summary>
    public ParticleBuilder OnParticleDisconnected(Action<IParticle, Exception?> handler)
    {
        _onDisconnectedClient = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Attaches a disconnect callback for burst-mode particles (<see cref="IParticleBurst"/>).
    /// </summary>
    public ParticleBuilder OnBurstDisconnected(Action<IParticleBurst, Exception?> handler)
    {
        _onDisconnectedBurst = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }
    #endregion

    #region Build

    /// <summary>
    /// Builds and connects the appropriate async client (<see cref="IParticle"/>).
    /// </summary>
    public IParticle Build()
    {
        if (_useBurst)
            throw new InvalidOperationException("Use BuildBurst() for burst clients.");

        if (_remoteEndPoint == null)
            throw new InvalidOperationException("Remote endpoint must be set via ConnectTo().");

        if (_useConcurrent)
        {
            var c = new ParticleFlux(_remoteEndPoint, _bufferSize, _maxDegreeOfParallelism);
            if (_onReceived != null) c.OnReceived = _onReceived;
            if (_onDisconnectedClient != null) c.Disconnected = (cli, ex) => _onDisconnectedClient(c, ex);
            return c;
        }
        else
        {
            var c = new Particle(_remoteEndPoint, _bufferSize, _maxDegreeOfParallelism);
            if (_onReceived != null) c.OnReceived = _onReceived;
            if (_onDisconnectedClient != null) c.Disconnected = (cli, ex) => _onDisconnectedClient(c, ex);
            return c;
        }
    }

    /// <summary>
    /// Builds and connects the appropriate burst-mode client (<see cref="IParticleBurst"/>).
    /// </summary>
    public IParticleBurst BuildBurst()
    {
        if (!_useBurst)
            throw new InvalidOperationException("Call AsBurst() before BuildBurst().");

        if (_remoteEndPoint == null)
            throw new InvalidOperationException("Remote endpoint must be set via ConnectTo().");

        var c = new ParticleBurst(_remoteEndPoint, _bufferSize, _maxDegreeOfParallelism);
        if (_onReceived != null) c.OnReceived = _onReceived;
        if (_onDisconnectedBurst != null) c.Disconnected = (cli, ex) => _onDisconnectedBurst(c, ex);
        return c;
    }

    #endregion
}

