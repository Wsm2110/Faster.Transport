using System.Net.Sockets;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// A high-performance pool for reusing <see cref="SocketAsyncEventArgs"/> instances
    /// across asynchronous socket operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pool minimizes GC pressure and socket allocation overhead by reusing
    /// the same <see cref="SocketAsyncEventArgs"/> objects for repeated send/receive calls.
    /// </para>
    ///
    /// <para>
    /// It uses an <see cref="MpmcQueue{T}"/> (multi-producer, multi-consumer queue)
    /// for thread-safe access and supports bounded capacity.
    /// </para>
    ///
    /// <example>
    /// Example: Using the pool with a UDP socket
    /// <code>
    /// var pool = new SocketAsyncEventArgsPool(initialCapacity: 4, maxSize: 16);
    /// 
    /// // Pre-fill with reusable args
    /// for (int i = 0; i &lt; 4; i++)
    /// {
    ///     var args = new SocketAsyncEventArgs();
    ///     pool.Add(args);
    /// }
    ///
    /// // Rent and use
    /// pool.TryRent(out var a);
    /// socket.SendAsync(a);
    ///
    /// // Return after completion
    /// pool.Return(a);
    /// </code>
    /// </example>
    /// </remarks>
    internal sealed class SocketAsyncEventArgsPool : IDisposable
    {
        private readonly MpmcQueue<SocketAsyncEventArgs> _pool;
        private readonly int _maxSize;
        private int _count;

        /// <summary>
        /// Creates a new pool with the specified initial capacity and maximum size.
        /// </summary>
        /// <param name="initialCapacity">Initial number of preallocated slots (optional).</param>
        /// <param name="maxSize">Maximum number of pooled objects allowed.</param>
        public SocketAsyncEventArgsPool(int initialCapacity = 8, int maxSize = 64)
        {
            _maxSize = maxSize;
            _pool = new MpmcQueue<SocketAsyncEventArgs>(maxSize);
        }

        /// <summary>
        /// Retrieves a <see cref="SocketAsyncEventArgs"/> instance from the pool.
        /// Spins briefly until one becomes available.
        /// </summary>
        /// <remarks>
        /// This method blocks only via <see cref="SpinWait"/> (CPU yield-based), avoiding kernel waits.
        /// </remarks>
        public void Rent(out SocketAsyncEventArgs args)
        {
            var spinner = new SpinWait();

            // Busy-wait until one is available (extremely fast in low-contention cases)
            while (!_pool.TryDequeue(out args))
            {
                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Adds a pre-created <see cref="SocketAsyncEventArgs"/> to the pool.
        /// </summary>
        /// <remarks>
        /// If the pool has reached its maximum capacity, the argument is disposed
        /// to prevent unbounded growth.
        /// </remarks>
        public void Add(SocketAsyncEventArgs args)
        {
            if (Interlocked.Increment(ref _count) <= _maxSize)
            {
                _pool.TryEnqueue(args);
            }
            else
            {
                // Too many items — dispose excess and decrement counter
                Interlocked.Decrement(ref _count);
                args.Dispose();
            }
        }

        /// <summary>
        /// Returns a used <see cref="SocketAsyncEventArgs"/> back into the pool.
        /// </summary>
        /// <remarks>
        /// If the pool is full, the argument is disposed.
        /// </remarks>
        public void Return(SocketAsyncEventArgs args)
        {
            if (!_pool.TryEnqueue(args))
            {
                // Pool full — discard to keep bounded
                Interlocked.Decrement(ref _count);
                args.Dispose();
            }
        }

        /// <summary>
        /// Disposes all pooled <see cref="SocketAsyncEventArgs"/> instances.
        /// </summary>
        public void Dispose()
        {
            while (_pool.TryDequeue(out var e))
            {
                e.Dispose();
            }
        }
    }
}
