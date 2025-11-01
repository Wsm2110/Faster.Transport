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
    /// Array-backed ring buffer with power-of-two capacity.
    /// Producers coordinate via CAS on the tail cursor and per-slot sequence numbers.
    /// The single consumer advances the head with a volatile write.
    /// </remarks>
    public sealed class MpscQueue<T>
    {
        private readonly Slot[] _buffer;
        private readonly int _mask;

        // Padded cursors to reduce false sharing
        private PaddedLong _head; // consumer cursor
        private PaddedLong _tail; // producers cursor

        private struct Slot
        {
            public long Sequence; // slot state
            public T Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPowerOfTwo(int x)
        {
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }

        public MpscQueue(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));

            int cap = NextPowerOfTwo(capacity);
            _buffer = new Slot[cap];
            _mask = cap - 1;

            // Initialize sequence to i, meaning slot i is ready for enqueue at pos=i
            for (int i = 0; i < cap; i++)
                _buffer[i].Sequence = i;

            _head = new PaddedLong(0);
            _tail = new PaddedLong(0);
        }

        public int Capacity => _buffer.Length;

        /// <summary>
        /// Attempts to enqueue an item without blocking.
        /// Returns false if the queue is full.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            var buffer = _buffer;
            var mask = _mask;

            while (true)
            {
                long pos = Volatile.Read(ref _tail.Value);
                ref Slot slot = ref buffer[(int)(pos & mask)];
                long seq = Volatile.Read(ref slot.Sequence);
                long diff = seq - pos;

                if (diff == 0)
                {
                    // Slot available for producer at 'pos' — claim via CAS on tail.
                    if (Interlocked.CompareExchange(ref _tail.Value, pos + 1, pos) == pos)
                    {
                        // Write value, then publish it by incrementing Sequence.
                        slot.Value = item;
                        Volatile.Write(ref slot.Sequence, pos + 1);
                        return true;
                    }
                    continue; // lost race, retry
                }

                if (diff < 0)
                    return false; // full (slot not yet consumed)

                // Mild backoff to reduce contention among producers
                Thread.SpinWait(1);
            }
        }

        /// <summary>
        /// Attempts to dequeue an item without blocking.
        /// Returns false if the queue is empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            var buffer = _buffer;
            var mask = _mask;

            long pos = Volatile.Read(ref _head.Value);
            ref Slot slot = ref buffer[(int)(pos & mask)];
            long seq = Volatile.Read(ref slot.Sequence);
            long diff = seq - (pos + 1);

            if (diff == 0)
            {
                // Slot holds a published value.
                item = slot.Value;

                // Clear value if reference type to allow GC
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    slot.Value = default!;

                // Mark slot free by advancing sequence ahead by capacity
                Volatile.Write(ref slot.Sequence, pos + buffer.Length);

                // Advance head (single consumer, no CAS needed)
                Volatile.Write(ref _head.Value, pos + 1);
                return true;
            }

            item = default!;
            return false; // empty or not yet visible
        }

        /// <summary>
        /// Batch-drains up to <paramref name="max"/> items into <paramref name="dst"/>.
        /// </summary>
        public int Drain(T[] dst, int max)
        {
            int n = 0;
            while (n < max && TryDequeue(out var item))
                dst[n++] = item;
            return n;
        }

        public bool IsEmpty => Volatile.Read(ref _tail.Value) == Volatile.Read(ref _head.Value);

        public int CountApprox
        {
            get
            {
                long head = Volatile.Read(ref _head.Value);
                long tail = Volatile.Read(ref _tail.Value);
                return (int)(tail - head);
            }
        }

        // Padded to avoid cache-line ping-pong between head and tail
        private struct PaddedLong
        {
            public long Value;
#pragma warning disable CS0169
            private long p1, p2, p3, p4, p5, p6, p7;
#pragma warning restore CS0169
            public PaddedLong(long v) => Value = v;
        }
    }
}
