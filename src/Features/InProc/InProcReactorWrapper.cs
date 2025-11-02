using Faster.Transport.Contracts;
using Faster.Transport.Primitives;

namespace Faster.Transport.Inproc;

/// <summary>
/// Lightweight adapter that wraps an <see cref="InprocReactor"/> so it behaves like an <see cref="IParticle"/>.
/// 
/// - Forwards messages to all connected clients (broadcast semantics)
/// - Relays OnReceived, OnConnected, and OnDisconnected events
/// - Supports Send() / SendAsync() for unified usage
/// </summary>
internal sealed class InprocServerWrapper : IParticle
{
    private readonly InprocReactor _reactor;

    public InprocServerWrapper(InprocReactor reactor)
    {
        _reactor = reactor ?? throw new ArgumentNullException(nameof(reactor));

        // Wire reactor events to particle-level events
        OnReceived = _reactor.OnReceived;
        OnConnected = _reactor.ClientConnected;
        OnDisconnected = _reactor.ClientDisconnected;
    }

    public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
    public Action<IParticle>? OnDisconnected { get; set; }
    public Action<IParticle>? OnConnected { get; set; }

    public void Send(ReadOnlySpan<byte> payload)
    {
        throw new NotImplementedException("Servers arent suppose to send data, just echo");
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
    {
        throw new NotImplementedException("Servers arent suppose to send data, just echo");
    }

    public void Dispose() => _reactor.Dispose();
}
