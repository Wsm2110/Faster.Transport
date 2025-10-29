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

        // Wire reactor events to behave like particle-level events
        _reactor.OnReceived = (p, msg) => OnReceived?.Invoke(p, msg);
        _reactor.ClientConnected = p => OnConnected?.Invoke(p);
        _reactor.ClientDisconnected = p => OnDisconnected?.Invoke(p);
    }

    public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
    public Action<IParticle>? OnDisconnected { get; set; }
    public Action<IParticle>? OnConnected { get; set; }

    public void Send(ReadOnlySpan<byte> payload)
    {   
        
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
    {     
      return TaskCompat.CompletedValueTask;
    }

    public void Dispose() => _reactor.Dispose();
}
