using System;
using Faster.Transport.Contracts;

namespace Faster.Transport.Ipc
{
    public abstract class MappedParticleBase : IParticle
    {
        protected readonly DirectionalChannel _rx;
        protected readonly DirectionalChannel _tx;
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }
        public Action<IParticle>? OnConnected { get; set; }

        protected MappedParticleBase(DirectionalChannel rx, DirectionalChannel tx)
        {
            _rx = rx; _tx = tx;
            _rx.OnFrame += mem => OnReceived?.Invoke(this, mem);
        }

        public virtual void Start()
        {
            _rx.Start();
            OnConnected?.Invoke(this);
        }

        public void Send(ReadOnlySpan<byte> payload) => _tx.Send(payload);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _tx.Send(payload.Span);
            return ValueTask.CompletedTask;
        }

        public virtual void Dispose()
        {
            _rx.Dispose();
            _tx.Dispose();
            OnDisconnected?.Invoke(this);
        }
    }
}
