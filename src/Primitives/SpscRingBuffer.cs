using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Lock-free, single-producer single-consumer ring buffer (power-of-two capacity).
    /// </summary>
    public sealed class SpscRingBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _mask;

        private PaddedLong _head; // consumer index
        private PaddedLong _tail; // producer index

        public int Capacity => _buffer.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPowerOfTwo(int x)
        {
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }

        public SpscRingBuffer(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));

            int cap = NextPowerOfTwo(capacity);
            _buffer = new T[cap];
            _mask = cap - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(T item)
        {
            long head = Volatile.Read(ref _head.Value);
            long tail = _tail.Value;

            if ((tail - head) == _buffer.Length)
                return false; // full

            int index = (int)(tail & _mask);

            // Use MemoryMarshal.GetArrayDataReference for zero-bound-check write
            ref T start = ref MemoryMarshalHelper.GetArrayDataReference(_buffer);
            Unsafe.Add(ref start, index) = item;

            Volatile.Write(ref _tail.Value, tail + 1);
            return true;
        }

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

            ref T start = ref MemoryMarshalHelper.GetArrayDataReference(_buffer);
            item = Unsafe.Add(ref start, index);

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Unsafe.Add(ref start, index) = default!;

            Volatile.Write(ref _head.Value, head + 1);
            return true;
        }

        public bool IsEmpty => Volatile.Read(ref _tail.Value) == Volatile.Read(ref _head.Value);

        public bool IsFull
        {
            get
            {
                long head = Volatile.Read(ref _head.Value);
                long tail = Volatile.Read(ref _tail.Value);
                return (tail - head) == _buffer.Length;
            }
        }

        public int CountApprox
        {
            get
            {
                long head = Volatile.Read(ref _head.Value);
                long tail = Volatile.Read(ref _tail.Value);
                return (int)(tail - head);
            }
        }

        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head.Value = 0;
            _tail.Value = 0;
        }

        private struct PaddedLong
        {
            public long Value;
#pragma warning disable CS0169
            private long p1, p2, p3, p4, p5, p6, p7;
#pragma warning restore CS0169
        }
    }
}
