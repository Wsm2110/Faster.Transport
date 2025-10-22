namespace Faster.Transport.Contracts;

public interface IParticleBurst : IDisposable
{
    Action<IParticleBurst, Exception?>? Disconnected { get; set; }

    Action<ReadOnlyMemory<byte>>? OnReceived { get; set; }

    void Send(ReadOnlyMemory<byte> payload);
}


public interface IParticle : IDisposable
{
    Action<IParticle, Exception?>? Disconnected { get; set; }
    Action<ReadOnlyMemory<byte>>? OnReceived { get; set; }

    ValueTask SendAsync(ReadOnlyMemory<byte> payload);
}
