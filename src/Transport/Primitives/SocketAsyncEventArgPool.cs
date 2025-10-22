using System.Net.Sockets;

namespace Faster.Transport.Primitives
{
    internal sealed class SocketAsyncEventArgsPool : IDisposable
    {
        private readonly MpmcQueue<SocketAsyncEventArgs> _pool;
        private readonly int _maxSize;
        private int _count;

        public SocketAsyncEventArgsPool(int initialCapacity = 8, int maxSize = 64)
        {
            _maxSize = maxSize;
            _pool = new MpmcQueue<SocketAsyncEventArgs>(maxSize);        
        }

        /// <summary>
        /// Rents a SocketAsyncEventArgs instance. Spins until one is available.
        /// </summary>
        public void TryRent(out SocketAsyncEventArgs args)
        {
            var spinner = new SpinWait();
            while (!_pool.TryDequeue(out args))
            {
                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Adds a pre-created instance into the pool.
        /// </summary>
        public void Add(SocketAsyncEventArgs args)
        {
            if (Interlocked.Increment(ref _count) <= _maxSize)
            {
                _pool.TryEnqueue(args);
            }
            else
            {
                Interlocked.Decrement(ref _count);
                args.Dispose(); // discard extras beyond max capacity
            }
        }

        /// <summary>
        /// Returns a SocketAsyncEventArgs back into the pool after use.
        /// </summary>
        public void Return(SocketAsyncEventArgs args)
        {
            if (!_pool.TryEnqueue(args))
            {
                // Pool full — discard
                Interlocked.Decrement(ref _count);
                args.Dispose();
            }
        }

        /// <summary>
        /// Disposes all SocketAsyncEventArgs objects.
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
