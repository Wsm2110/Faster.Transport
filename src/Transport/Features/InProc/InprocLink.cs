using Faster.Transport.Primitives;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// Represents a bidirectional ("duplex") in-process connection link between two <see cref="InprocParticle"/> instances.
    /// 
    /// ✅ Think of this as an ultra-lightweight, lock-free pipe that lives entirely in memory.
    /// 
    /// It contains two **MPSC (Multi-Producer Single-Consumer)** queues:
    /// - <see cref="ToServer"/> → messages from clients going to the server.
    /// - <see cref="ToClient"/> → messages from the server going to the client.
    /// 
    /// These queues are extremely fast because they:
    /// - Avoid any OS calls or locks (pure user-space communication).
    /// - Support multiple threads calling <c>Enqueue()</c> simultaneously.
    /// - Have exactly one consumer reading messages (either server or client).
    /// 
    /// This makes <see cref="InprocLink"/> ideal for **low-latency, high-throughput** message passing
    /// between in-process components such as simulators, subsystems, or microservices running in the same process.
    /// </summary>
    internal sealed class InprocLink
    {
        /// <summary>
        /// Queue for messages flowing **from the client to the server**.
        /// 
        /// - Multiple clients can enqueue messages concurrently.
        /// - Only one server thread consumes them.
        /// </summary>
        public readonly MpscQueue<ReadOnlyMemory<byte>> ToServer;

        /// <summary>
        /// Queue for messages flowing **from the server to the client**.
        /// 
        /// - The server can enqueue messages concurrently.
        /// - Each client consumes messages intended for it.
        /// </summary>
        public readonly MpscQueue<ReadOnlyMemory<byte>> ToClient;

        /// <summary>
        /// Creates a new in-process duplex link with two message queues.
        /// </summary>
        /// <param name="capacity">
        /// The maximum number of messages each queue can hold before backpressure is applied.
        /// Typical value: 4096.
        /// </param>
        public InprocLink(int capacity)
        {
            // Create both queues with the specified capacity
            ToServer = new MpscQueue<ReadOnlyMemory<byte>>(capacity);
            ToClient = new MpscQueue<ReadOnlyMemory<byte>>(capacity);
        }
    }
}
