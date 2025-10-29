using System;
using System.Threading.Tasks;
using Faster.Transport.Contracts;
using Faster.Transport.Ipc;
using Faster.Transport.Primitives;

namespace Faster.Transport
{
    /// <summary>
    /// Provides a lightweight adapter that allows <see cref="MappedReactor"/> (the IPC server)
    /// to be used as a unified <see cref="IParticle"/> instance.
    /// </summary>
    /// <remarks>
    /// This wrapper makes the server behave like a regular <see cref="IParticle"/>:
    /// <list type="bullet">
    ///   <item><description>Messages can be sent using <see cref="Send"/> or <see cref="SendAsync"/>, which internally broadcast to all connected clients.</description></item>
    ///   <item><description>The <see cref="OnReceived"/> event is forwarded to the server’s <see cref="MappedReactor.OnReceived"/> handler.</description></item>
    ///   <item><description>Useful for uniform APIs where both client and server share the same interface.</description></item>
    /// </list>
    /// </remarks>
    internal sealed class IpcServerWrapper : IParticle
    {
        private readonly MappedReactor _server;

        /// <summary>
        /// Creates a new IPC server wrapper around an existing <see cref="MappedReactor"/>.
        /// </summary>
        /// <param name="server">The underlying reactor instance managing IPC clients.</param>
        public IpcServerWrapper(MappedReactor server) => _server = server;

        /// <summary>
        /// Gets or sets the handler invoked when the server receives a message from any client.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived
        {
            get => _server.OnReceived;
            set => _server.OnReceived = value;
        }

        /// <summary>
        /// Called when the server starts.
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Called when the server stops or is disposed.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// Starts the underlying <see cref="MappedReactor"/> to begin accepting client connections.
        /// </summary>
        public void Start() => _server.Start();

        /// <summary>
        /// Broadcasts a message to all connected clients.
        /// </summary>
        /// <param name="payload">The binary data to send.</param>
        public void Send(ReadOnlySpan<byte> payload) => _server.Broadcast(payload);

        /// <summary>
        /// Broadcasts a message asynchronously to all connected clients.
        /// </summary>
        /// <param name="payload">The message data as read-only memory.</param>
        /// <returns>A completed <see cref="ValueTask"/> once data has been queued for broadcast.</returns>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _server.Broadcast(payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        /// <summary>
        /// Stops the server and releases all associated shared memory resources.
        /// </summary>
        public void Dispose() => _server.Dispose();
    }

    /// <summary>
    /// Represents a single connected IPC client from the perspective of the server.
    /// </summary>
    /// <remarks>
    /// <see cref="IpcClientProxy"/> allows the server to communicate directly
    /// with one specific client instead of broadcasting to all.
    /// <list type="bullet">
    ///   <item><description>Wraps a client ID and a reference to the parent <see cref="MappedReactor"/>.</description></item>
    ///   <item><description>Implements the <see cref="IParticle"/> interface so it can be treated uniformly with other transports.</description></item>
    ///   <item><description>All <see cref="Send"/> calls are routed through <see cref="MappedReactor.Send(ulong, ReadOnlySpan{byte})"/> targeting this specific client.</description></item>
    /// </list>
    /// </remarks>
    internal sealed class IpcClientProxy : IParticle
    {
        private readonly ulong _id;
        private readonly MappedReactor _server;

        /// <summary>
        /// Creates a new proxy representing a connected IPC client within a server context.
        /// </summary>
        /// <param name="id">The unique client ID (as used in <see cref="MappedReactor"/>).</param>
        /// <param name="server">The parent <see cref="MappedReactor"/> managing this client.</param>
        public IpcClientProxy(ulong id, MappedReactor server)
        {
            _id = id;
            _server = server;
        }

        /// <summary>
        /// Event invoked when data is received from this client.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Called when this proxy becomes active (optional, unused in IPC mode).
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Called when this proxy is removed or the client disconnects.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// No-op for IPC; clients are automatically registered and active.
        /// </summary>
        public void Start() { }

        /// <summary>
        /// Sends a message directly to this specific client.
        /// </summary>
        /// <param name="payload">The message to send.</param>
        public void Send(ReadOnlySpan<byte> payload) => _server.Send(_id, payload);

        /// <summary>
        /// Sends a message asynchronously to this specific client.
        /// </summary>
        /// <param name="payload">The message as a read-only memory segment.</param>
        /// <returns>A completed <see cref="ValueTask"/> once the send is enqueued.</returns>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _server.Send(_id, payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        /// <summary>
        /// Cleans up resources. No explicit cleanup is needed here since <see cref="MappedReactor"/> manages lifecycle.
        /// </summary>
        public void Dispose() { }
    }
}
