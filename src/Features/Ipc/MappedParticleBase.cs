using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;

namespace Faster.Transport.Ipc
{
    public abstract class MappedParticleBase : IParticle
    {
        protected readonly DirectionalChannel _rx;
        protected readonly DirectionalChannel _tx;

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }
        public Action<IParticle>? OnConnected { get; set; }

        private readonly Action<ReadOnlyMemory<byte>> _onFrameForward;

        protected MappedParticleBase(DirectionalChannel rx, DirectionalChannel tx)
        {
            _rx = rx;
            _tx = tx;

            // Avoid per-frame closure/allocs: capture once
            _onFrameForward = static (mem) =>
            {
                // placeholder – replaced in Start() when we have 'this'
            };

            // We can’t assign static to instance, so wire directly with a non-capturing lambda via local method:
            _rx.OnFrame += OnRxFrame;
        }

        private void OnRxFrame(ReadOnlyMemory<byte> mem)
            => OnReceived?.Invoke(this, mem);

        public virtual void Start()
        {
            _rx.Start();
            OnConnected?.Invoke(this);
        }

        public void Send(ReadOnlySpan<byte> payload)
        {
            _tx.Send(payload);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _tx.Send(payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        public virtual void Dispose()
        {
            _rx.Dispose();
            _tx.Dispose();
            OnDisconnected?.Invoke(this);
        }
    }
}
