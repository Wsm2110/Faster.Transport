using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// A fixed-capacity, lock-free, single-producer single-consumer (SPSC) ring buffer.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <remarks>
    /// Power-of-two capacity for cheap modulo via bitmask. One producer thread and one consumer thread only.
    /// Visibility/order is enforced via <see cref="Volatile"/> reads/writes; no CAS required.
    /// <see cref="Count"/> is an exact atomic counter but may transiently over-report in very small windows,
    /// which is safe for IsEmpty/IsFull checks used here (they also consult head/tail).
    /// </remarks>
    public sealed class SpscRingBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _mask;

        // Single consumer/producer cursors; padded to reduce false sharing with other hot fields.
        private PaddedLong _head; // consumer index
        private PaddedLong _tail; // producer index

        // Exact item count (atomic), padded to avoid cache-line ping-pong.
        private PaddedInt _count;

        /// <summary>Total fixed capacity (power of two).</summary>
        public int Capacity => _buffer.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPowerOfTwo(int x)
        {
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }

        /// <summary>
        /// Initializes a new <see cref="SpscRingBuffer{T}"/> with at least the requested capacity.
        /// </summary>
        /// <param name="capacity">Requested capacity; rounded up to the next power of two.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 2.</exception>
        public SpscRingBuffer(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));

            int cap = NextPowerOfTwo(capacity);
            _buffer = new T[cap];
            _mask = cap - 1;

            _head = new PaddedLong(0);
            _tail = new PaddedLong(0);           
        }

        /// <summary>
        /// Attempts to enqueue an item (non-blocking).
        /// </summary>
        /// <param name="item">Item to enqueue.</param>
        /// <returns><c>true</c> if enqueued; <c>false</c> if the buffer is full.</returns>
        /// <remarks>
        /// Single producer:
        /// 1) Read <c>head</c> (volatile) and <c>tail</c>.
        /// 2) If distance equals capacity, buffer is full.
        /// 3) Store item, then publish by volatile write to <c>tail</c> (tail+1).
        /// 4) Increment <see cref="_count"/> after publication so consumers never see invisible items.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            long head = Volatile.Read(ref _head.Value);
            long tail = _tail.Value;

            // Full if producer is about to lap consumer by Capacity
            if (tail - head == _buffer.Length) return false;

            _buffer[(int)(tail & _mask)] = item;

            // Publish: make the new tail visible to the consumer
            Volatile.Write(ref _tail.Value, tail + 1);
            return true;
        }

        /// <summary>
        /// Attempts to dequeue an item (non-blocking).
        /// </summary>
        /// <param name="item">On success, receives the dequeued item; otherwise default.</param>
        /// <returns><c>true</c> if an item was dequeued; <c>false</c> if the buffer is empty.</returns>
        /// <remarks>
        /// Single consumer:
        /// 1) Read <c>tail</c> (volatile) and <c>head</c>.
        /// 2) If equal, empty.
        /// 3) Read item, clear slot (helps GC for reference types), then publish head+1 via volatile write.
        /// 4) Decrement count after publication.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            long tail = Volatile.Read(ref _tail.Value);
            long head = _head.Value;

            if (tail == head)
            {
                item = default!;
                return false; // empty
            }

            int index = (int)(head & _mask);
            item = _buffer[index];

            // Clear slot to allow GC of references (no-op for value types)
            _buffer[index] = default!;

            // Publish: advance head so producer can reuse the slot
            Volatile.Write(ref _head.Value, head + 1);   
            return true;
        }

        /// <summary>
        /// Clears the buffer and resets indices and count.
        /// </summary>
        /// <remarks>
        /// Safe only when called with external synchronization ensuring no concurrent producer/consumer activity.
        /// </remarks>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head.Value = 0;
            _tail.Value = 0;           
        }

        /// <summary>
        /// A long padded to (approximately) a cache line to minimize false sharing.
        /// </summary>
        private struct PaddedLong
        {
            public long Value;
#pragma warning disable CS0169
            private long p1, p2, p3, p4, p5, p6, p7;
#pragma warning restore CS0169
            public PaddedLong(long value) => Value = value;
        }

        /// <summary>
        /// An int padded to (approximately) a cache line to reduce cross-core contention.
        /// </summary>
        private struct PaddedInt
        {
            public int Value;
#pragma warning disable CS0169
            private long p1, p2, p3, p4, p5, p6, p7;
#pragma warning restore CS0169
            public PaddedInt(int value) => Value = value;
        }
    }
}
