using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Base class for all IPC message participants (both client and server-side).
    /// Provides a simple abstraction over a pair of <see cref="DirectionalChannel"/> objects
    /// — one for receiving and one for sending messages through shared memory.
    /// </summary>
    /// <remarks>
    /// This class defines the core lifecycle for an IPC participant:
    /// <list type="bullet">
    ///   <item><description>Starts reading from the receive channel when <see cref="Start"/> is called.</description></item>
    ///   <item><description>Provides <see cref="Send"/> and <see cref="SendAsync"/> methods to transmit messages.</description></item>
    ///   <item><description>Raises <see cref="OnConnected"/>, <see cref="OnReceived"/>, and <see cref="OnDisconnected"/> events to notify users of connection activity.</description></item>
    /// </list>
    /// Derived classes such as <see cref="MappedParticle"/> (client) and 
    /// <see cref="MappedReactor"/>.<see cref="MappedReactor.ClientParticle"/> (server-side client handler)
    /// extend this base to add registration, connection logic, or server management.
    /// </remarks>
    public abstract class MappedParticleBase : IParticle
    {
        /// <summary>
        /// The receive channel (incoming data). 
        /// Handles reading messages from the shared memory ring.
        /// </summary>
        protected readonly DirectionalChannel _rx;

        /// <summary>
        /// The transmit channel (outgoing data). 
        /// Handles writing messages to the shared memory ring.
        /// </summary>
        protected readonly DirectionalChannel _tx;

        /// <summary>
        /// Triggered whenever a new message arrives from the other process.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Triggered when the connection is closed or disposed.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// Triggered when the receive loop starts (i.e., when the participant becomes active).
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Initializes a new <see cref="MappedParticleBase"/> using existing directional channels.
        /// </summary>
        /// <param name="rx">The channel used to receive data (read from shared memory).</param>
        /// <param name="tx">The channel used to send data (write to shared memory).</param>
        /// <remarks>
        /// The receive channel hooks into its <see cref="DirectionalChannel.OnFrame"/> event 
        /// and forwards incoming data to <see cref="OnReceived"/>.
        /// </remarks>
        protected MappedParticleBase(DirectionalChannel rx, DirectionalChannel tx)
        {
            _rx = rx;
            _tx = tx;

            // When a new data frame is received, forward it to any user-defined handler.
            _rx.OnFrame += mem => OnReceived?.Invoke(this, mem);
        }

        /// <summary>
        /// Starts the receive loop for this participant.
        /// </summary>
        /// <remarks>
        /// The receive channel runs in a background thread, continuously monitoring 
        /// the shared memory region for new messages.
        /// </remarks>
        public virtual void Start()
        {
            _rx.Start();
            OnConnected?.Invoke(this);
        }

        /// <summary>
        /// Sends a binary message to the other process through the transmit channel.
        /// </summary>
        /// <param name="payload">The data to send as a <see cref="ReadOnlySpan{T}"/>.</param>
        /// <remarks>
        /// Sending is synchronous and typically completes very quickly, since 
        /// it just writes into a shared memory ring buffer.
        /// </remarks>
        public void Send(ReadOnlySpan<byte> payload) => _tx.Send(payload);

        /// <summary>
        /// Sends a message asynchronously. 
        /// (Non-blocking wrapper around <see cref="Send(ReadOnlySpan{T})"/>.)
        /// </summary>
        /// <param name="payload">The message data to send.</param>
        /// <returns>A completed <see cref="ValueTask"/> once the data is written.</returns>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            _tx.Send(payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        /// <summary>
        /// Stops the receive channel, releases memory-mapped resources, and raises <see cref="OnDisconnected"/>.
        /// </summary>
        public virtual void Dispose()
        {
            _rx.Dispose();
            _tx.Dispose();
            OnDisconnected?.Invoke(this);
        }
    }
}
