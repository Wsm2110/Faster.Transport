namespace Faster.Transport.Contracts
{
    /// <summary>
    /// Defines a high-performance, event-driven server that accepts multiple clients asynchronously.
    /// </summary>
    /// <remarks>
    /// The <see cref="IReactor"/> interface represents the server-side component in the Faster Transport system.  
    /// It manages network or interprocess connections and wraps each accepted connection in an <see cref="IParticle"/> instance.  
    /// Typical implementations include TCP, IPC (Unix domain sockets), or InProc (in-memory) reactors.
    /// </remarks>
    public interface IReactor : IDisposable
    {
        /// <summary>
        /// Occurs when a new client connection is successfully accepted.
        /// </summary>
        /// <remarks>
        /// The provided <see cref="IParticle"/> represents a single connected client.
        /// This callback is typically used to perform per-client initialization or registration.
        /// </remarks>
        Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Occurs when a connected client sends a complete message frame.
        /// </summary>
        /// <remarks>
        /// The <see cref="ReadOnlyMemory{T}"/> contains the fully assembled frame data as received from the client.  
        /// The associated <see cref="IParticle"/> represents the source of the message.
        /// </remarks>
        Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Starts the reactor and begins listening for incoming client connections.
        /// </summary>
        /// <remarks>
        /// This method initializes asynchronous accept loops for TCP or IPC, or  
        /// channel registration for InProc modes.
        /// </remarks>
        void Start();

        /// <summary>
        /// Stops the reactor and gracefully closes all active client connections.
        /// </summary>
        /// <remarks>
        /// After calling <see cref="Stop"/>, the reactor cannot be restarted.  
        /// To accept new connections again, create a new instance.
        /// </remarks>
        void Stop();

        /// <summary>
        /// Releases all resources used by the reactor and closes underlying sockets or channels.
        /// </summary>
        new void Dispose();
    }
}
