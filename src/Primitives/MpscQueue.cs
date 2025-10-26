using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// A fixed-capacity, lock-free, multi-producer single-consumer (MPSC) queue.
    /// </summary>
    /// <typeparam name="T">Type of elements stored in the queue.</typeparam>
    /// <remarks>
    /// Array-backed ring buffer with power-of-two capacity. Producers coordinate via CAS on the tail cursor
    /// and per-slot sequence numbers; the single consumer advances the head with a volatile write.
    /// Memory visibility is enforced using <see cref="Volatile"/> operations and interlocked increments/decrements.
    /// </remarks>
    public sealed class MpscQueue<T>
    {
        private readonly Slot[] _buffer;
        private readonly int _mask;

        // Consumer head (single-reader) and producers tail (multi-writer), each padded to reduce false sharing.
        private PaddedLong _head; // consumer cursor
        private PaddedLong _tail; // producers cursor
        /// <summary>
        /// A single ring-buffer slot with a per-slot sequence number and stored value.
        /// </summary>
        /// <remarks>
        /// Slot state via <c>Sequence</c>:
        /// <list type="bullet">
        /// <item><description>Available for enqueue at position <c>p</c> when <c>Sequence == p</c>.</description></item>
        /// <item><description>Published by producer after write: <c>Sequence = p + 1</c>.</description></item>
        /// <item><description>Freed by consumer after read: <c>Sequence = p + buffer.Length</c>.</description></item>
        /// </list>
        /// This arithmetic allows detection of full/empty without extra flags.
        /// </remarks>
        private struct Slot { public long Sequence; public T Value; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPowerOfTwo(int x) { int p = 1; while (p < x) p <<= 1; return p; }

        /// <summary>
        /// Initializes a new <see cref="MpscQueue{T}"/> with at least the requested capacity.
        /// </summary>
        /// <param name="capacity">Requested capacity; rounded up to the next power of two.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="capacity"/> is less than 2.</exception>
        public MpscQueue(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));

            int cap = NextPowerOfTwo(capacity);
            _buffer = new Slot[cap];

            // Initialize sequence so slot i is initially available for position i.
            for (int i = 0; i < cap; i++) _buffer[i].Sequence = i;

            _mask = cap - 1;
            _head = new PaddedLong(0);
            _tail = new PaddedLong(0);        
        }

        /// <summary>Total fixed capacity (power of two).</summary>
        public int Capacity => _buffer.Length;

        /// <summary>
        /// Attempts to enqueue an item without blocking.
        /// </summary>
        /// <param name="item">The item to enqueue.</param>
        /// <returns><c>true</c> if enqueued; <c>false</c> if the queue is full.</returns>
        /// <remarks>
        /// Producers:
        /// 1) Read tail position <c>pos</c>.
        /// 2) If slot at <c>pos &amp; mask</c> has <c>Sequence == pos</c>, attempt to claim by CAS tail to <c>pos+1</c>.
        /// 3) Write <c>Value</c>, then publish by setting <c>Sequence = pos+1</c>.
        /// 4) Increment <see cref="_count"/> after publication to avoid exposing invisible items.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            var buffer = _buffer;
            var mask = _mask;

            for (; ; )
            {
                var pos = Volatile.Read(ref _tail.Value);
                ref var slot = ref buffer[(int)(pos & mask)];

                var seq = Volatile.Read(ref slot.Sequence);
                var diff = seq - pos;

                if (diff == 0)
                {
                    // Slot available for producer at 'pos' — claim via CAS on tail.
                    if (Interlocked.CompareExchange(ref _tail.Value, pos + 1, pos) == pos)
                    {
                        // Publish value, then advance sequence to make it visible.
                        slot.Value = item;
                        Volatile.Write(ref slot.Sequence, pos + 1);              
                        return true;
                    }
                    continue; // lost the race; retry
                }

                if (diff < 0) return false; // full (wrapped and not yet freed)

                // Back off slightly to reduce contention.
                Thread.SpinWait(1);
            }
        }

        /// <summary>
        /// Attempts to dequeue an item without blocking.
        /// </summary>
        /// <param name="item">On success, receives the dequeued item; otherwise default.</param>
        /// <returns><c>true</c> if an item was dequeued; <c>false</c> if the queue is empty.</returns>
        /// <remarks>
        /// Single consumer:
        /// 1) Read head position <c>pos</c>.
        /// 2) If slot at <c>pos &amp; mask</c> has <c>Sequence == pos+1</c>, the item is published.
        /// 3) Read <c>Value</c>, free the slot by setting <c>Sequence = pos + buffer.Length</c>.
        /// 4) Advance head with a volatile write to <c>pos+1</c> and decrement count.
        /// No CAS on head is required because there is only one consumer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            var buffer = _buffer;
            var mask = _mask;

            var pos = Volatile.Read(ref _head.Value);
            ref var slot = ref buffer[(int)(pos & mask)];

            var seq = Volatile.Read(ref slot.Sequence);
            var diff = seq - (pos + 1);

            if (diff == 0)
            {
                // Slot holds a published value for this position.
                item = slot.Value;

                // Mark slot free by jumping sequence forward by buffer size.
                Volatile.Write(ref slot.Sequence, pos + buffer.Length);

                // Single-consumer: advance head with volatile write.
                Volatile.Write(ref _head.Value, pos + 1);
            
                return true;
            }

            item = default!;
            return false; // empty or not yet published
        }

        /// <summary>
        /// Dequeues up to <paramref name="max"/> items into <paramref name="buffer"/>.
        /// </summary>
        /// <param name="buffer">Destination array to receive items.</param>
        /// <param name="max">Maximum number of items to drain.</param>
        /// <returns>The number of items written to <paramref name="buffer"/>.</returns>
        /// <remarks>Convenience method for batch draining on the single consumer thread.</remarks>
        public int Drain(T[] buffer, int max)
        {
            int n = 0;
            while (n < max && TryDequeue(out var item))
            {
                buffer[n++] = item;
            }
            return n;
        }

        /// <summary>
        /// A long padded to (approximately) a cache line to minimize false sharing.
        /// </summary>
        private struct PaddedLong
        {
            public long Value;
#pragma warning disable CS0169
            // Padding fields to separate hot variables onto different cache lines.
            private long p1, p2, p3, p4, p5, p6, p7;
#pragma warning restore CS0169
            public PaddedLong(long v) => Value = v;
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
            public PaddedInt(int v) => Value = v;
        }
    }
}
