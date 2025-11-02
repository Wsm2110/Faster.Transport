using Faster.Transport.Contracts;
using System;
using System.Collections.Generic;
using System.Text;

namespace Faster.Transport.Features.Tcp;

/// <summary>
/// Wraps a running TCP Reactor server for uniform <see cref="IParticle"/> abstraction.
/// </summary>
public sealed class TcpServerWrapper : IParticle
{
    private readonly Reactor _reactor;

    public TcpServerWrapper(Reactor reactor) => _reactor = reactor;

    public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived
    {
        get => _reactor.OnReceived;
        set => _reactor.OnReceived = value;
    }

    public Action<IParticle>? OnConnected
    {
        get => _reactor.OnConnected;
        set => _reactor.OnConnected = value;
    }

    public Action<IParticle>? OnDisconnected { get; set; }

    public void Send(ReadOnlySpan<byte> payload) =>
        throw new NotSupportedException("Cannot send directly from a server wrapper.");

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload) =>
        throw new NotSupportedException("Cannot send directly from a server wrapper.");

    public void Dispose() => _reactor.Dispose();
}
