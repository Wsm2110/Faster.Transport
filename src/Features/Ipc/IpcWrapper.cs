using System;
using System.Threading.Tasks;
using Faster.Transport.Contracts;
using Faster.Transport.Ipc;

namespace Faster.Transport
{
    /// <summary>
    /// Wraps MappedReactor as a unified IParticle for broadcast sends.
    /// </summary>
    internal sealed class IpcServerWrapper : IParticle
    {
        private readonly MappedReactor _server;

        public IpcServerWrapper(MappedReactor server) => _server = server;

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived
        {
            get => _server.OnReceived;
            set => _server.OnReceived = value;
        }

        public Action<IParticle>? OnConnected { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }

        public void Start() => _server.Start();

        public void Send(ReadOnlySpan<byte> payload) => _server.Broadcast(payload);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _server.Broadcast(payload.Span);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _server.Dispose();
    }

    /// <summary>
    /// Proxy that represents a connected client within the server context.
    /// Allows per-client targeted sends.
    /// </summary>
    internal sealed class IpcClientProxy : IParticle
    {
        private readonly ulong _id;
        private readonly MappedReactor _server;

        public IpcClientProxy(ulong id, MappedReactor server)
        {
            _id = id;
            _server = server;
        }

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnConnected { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }

        public void Start() { }

        public void Send(ReadOnlySpan<byte> payload) => _server.Send(_id, payload);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _server.Send(_id, payload.Span);
            return ValueTask.CompletedTask;
        }

        public void Dispose() { }
    }
}
