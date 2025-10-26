namespace Faster.Transport.Contracts;

public interface IParticle : IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

    /// <summary>
    /// Triggered when the connection is closed cleanly.
    /// </summary>
    Action<IParticle>? OnDisconnected { get; set; }

    /// <summary>
    /// Triggered once the server and client are connected and ready.
    /// </summary>
    Action<IParticle>? OnConnected { get; set; }

    ValueTask SendAsync(ReadOnlyMemory<byte> payload);

    void Send(ReadOnlySpan<byte> payload);
}
