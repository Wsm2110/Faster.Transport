using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// A fixed-capacity, lock-free, multiple-producer multiple-consumer (MPMC) queue.
    /// </summary>
    /// <typeparam name="T">The element type stored in the queue.</typeparam>
    /// <remarks>
    /// This queue is array-backed with power-of-two capacity and uses per-slot sequence numbers
    /// to coordinate producers and consumers without locks. Head/tail are advanced via CAS.
    /// Visibility is enforced with <see cref="Volatile"/> reads/writes and interlocked operations.
    /// </remarks>
    public sealed class MpmcQueue<T>
    {
        private readonly Cell[] _buffer;
        private readonly int _mask;

        // Producer (tail) and consumer (head) cursors, each padded to a cache line
        private PaddedLong _head;
        private PaddedLong _tail;

        // Exact, atomic count with padding to minimize cache-line contention
        private PaddedInt _count;

        /// <summary>
        /// A single ring-buffer slot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Sequence</c> encodes the slot state:
        /// - For enqueue at position <c>p</c>, the slot is available when <c>Sequence == p</c>.
        /// - After publishing a value at <c>p</c>, producer sets <c>Sequence = p + 1</c>.
        /// - After consuming the value at <c>p</c>, consumer sets <c>Sequence = p + buffer.Length</c> to free it.
        /// </para>
        /// The arithmetic allows detecting full/empty without extra flags.
        /// </remarks>
        private struct Cell
        {
            public long Sequence;
            public T Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPow2(int x)
        {
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }

        /// <summary>
        /// Creates a new <see cref="MpmcQueue{T}"/> with at least the requested capacity.
        /// </summary>
        /// <param name="capacity">Requested capacity (will be rounded up to the next power of two).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 2.</exception>
        public MpmcQueue(int capacity)
        {
            if (capacity < 2)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            int cap = NextPow2(capacity);
            _buffer = new Cell[cap];

            // Initialize sequence numbers so that slot i is initially available for position i
            for (int i = 0; i < cap; i++)
                _buffer[i].Sequence = i;

            _mask = cap - 1;
            _head = new PaddedLong(0);
            _tail = new PaddedLong(0);
            _count = new PaddedInt(0);
        }

        /// <summary>Total fixed capacity of the queue (a power of two).</summary>
        public int Capacity => _buffer.Length;

        /// <summary>Exact number of items currently in the queue.</summary>
        /// <remarks>
        /// This value is maintained atomically and reflects the number of published (visible) items.
        /// It may briefly over-report during dequeue publication windows, which is safe.
        /// </remarks>
        public int Count => Volatile.Read(ref _count.Value);

        /// <summary>True if the queue has no items.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>True if the queue is full.</summary>
        public bool IsFull => Count >= Capacity;

        /// <summary>
        /// Attempts to enqueue an item without blocking.
        /// </summary>
        /// <param name="item">The item to enqueue.</param>
        /// <returns><c>true</c> if the item was enqueued; <c>false</c> if the queue is full.</returns>
        /// <remarks>
        /// Algorithm:
        /// 1) Read tail position <c>pos</c>.
        /// 2) Inspect slot at <c>pos &amp; mask</c>. If <c>Sequence == pos</c>, the slot is available.
        /// 3) CAS tail from <c>pos</c> to <c>pos+1</c> to claim the slot.
        /// 4) Store <c>Value</c>, then set <c>Sequence = pos+1</c> to publish.
        /// 5) Increment <see cref="_count"/> after publication so readers never see invisible items.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            var buffer = _buffer;
            var mask = _mask;
            var pos = Volatile.Read(ref _tail.Value);

            for (; ; )
            {
                ref var cell = ref buffer[(int)(pos & mask)];
                var seq = Volatile.Read(ref cell.Sequence);
                var dif = seq - pos;

                if (dif == 0)
                {
                    // Slot is available for producer at position 'pos' — attempt to claim it
                    if (Interlocked.CompareExchange(ref _tail.Value, pos + 1, pos) == pos)
                    {
                        // Publish the value, then advance sequence to make it visible
                        cell.Value = item;
                        Volatile.Write(ref cell.Sequence, pos + 1);

                        // Increment count AFTER publication to avoid exposing invisible items
                        Interlocked.Increment(ref _count.Value);
                        return true;
                    }
                }
                else if (dif < 0)
                {
                    // If seq < pos the ring wrapped and this slot is not yet free => queue is full
                    return false;
                }

                // Lost the race or transient state—re-read tail and retry
                pos = Volatile.Read(ref _tail.Value);
            }
        }

        /// <summary>
        /// Attempts to dequeue an item without blocking.
        /// </summary>
        /// <param name="item">When this method returns, contains the dequeued item if successful; otherwise the default value.</param>
        /// <returns><c>true</c> if an item was dequeued; <c>false</c> if the queue is empty.</returns>
        /// <remarks>
        /// Algorithm:
        /// 1) Read head position <c>pos</c>.
        /// 2) Inspect slot at <c>pos &amp; mask</c>. If <c>Sequence == pos+1</c>, an item is available.
        /// 3) CAS head from <c>pos</c> to <c>pos+1</c> to claim the item.
        /// 4) Read <c>Value</c>, then set <c>Sequence = pos + buffer.Length</c> to free the slot.
        /// 5) Decrement <see cref="_count"/> after freeing; may briefly over-report during the window, which is safe.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            var buffer = _buffer;
            var mask = _mask;
            var pos = Volatile.Read(ref _head.Value);

            for (; ; )
            {
                ref var cell = ref buffer[(int)(pos & mask)];
                var seq = Volatile.Read(ref cell.Sequence);
                var dif = seq - (pos + 1);

                if (dif == 0)
                {
                    // Slot holds a published value for consumer at position 'pos' — attempt to claim it
                    if (Interlocked.CompareExchange(ref _head.Value, pos + 1, pos) == pos)
                    {
                        // Read value, then mark slot free by jumping sequence forward by buffer size
                        item = cell.Value;
                        Volatile.Write(ref cell.Sequence, pos + buffer.Length);

                        // Decrement AFTER freeing slot; small window may over-report count, which is safe
                        Interlocked.Decrement(ref _count.Value);
                        return true;
                    }
                }
                else if (dif < 0)
                {
                    // If seq < pos+1, no item is currently published at this position => empty
                    item = default!;
                    return false;
                }

                // Lost the race or transient state—re-read head and retry
                pos = Volatile.Read(ref _head.Value);
            }
        }

        /// <summary>
        /// A long padded to (approximately) a cache line to minimize false sharing between hot fields.
        /// </summary>
        private struct PaddedLong
        {
            public long Value;
#pragma warning disable CS0169
            // Padding fields to push Value onto its own cache line on common architectures
            private long _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169
            public PaddedLong(long value) => Value = value;
        }

        /// <summary>
        /// An int padded to (approximately) a cache line to reduce cross-core contention on counters.
        /// </summary>
        private struct PaddedInt
        {
            public int Value;
#pragma warning disable CS0169
            // Pad to separate cache lines (use longs to keep layout simple/aligned)
            private long _p1, _p2, _p3, _p4, _p5, _p6, _p7;
#pragma warning restore CS0169
            public PaddedInt(int value) => Value = value;
        }
    }
}
