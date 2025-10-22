using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Thread-safe, high-performance buffer manager with dynamic slab growth.
    /// Provides fixed-size slices backed by one or more pinned slabs.
    /// Safe for heavy multi-threaded SocketAsyncEventArgs use.
    /// </summary>
    public sealed class ConcurrentBufferManager : IDisposable
    {
        private sealed class Slab
        {
            public readonly byte[] Buffer;   // pinned
            public readonly int Length;
            public readonly int SliceSize;
            private int _currentIndex;

            public Slab(int length, int sliceSize)
            {
                Buffer = GC.AllocateUninitializedArray<byte>(length, pinned: true);
                Length = length;
                SliceSize = sliceSize;
                _currentIndex = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryRent(out int offset)
            {
                int idx = Interlocked.Add(ref _currentIndex, SliceSize) - SliceSize;
                if ((uint)(idx + SliceSize) <= (uint)Length)
                {
                    offset = idx;
                    return true;
                }
                offset = -1;
                return false;
            }
        }

        private readonly int _sliceSize;
        private readonly int _slabBytes;
        private readonly long _maxTotalBytes;
        private long _allocatedBytes;

        private volatile Slab? _currentSlab;
        private readonly ConcurrentBag<Slab> _slabs = new();
        private readonly ConcurrentStack<(Slab slab, int offset)> _free = new();

        private int _allocating;
        private bool _disposed;

        public int SliceSize => _sliceSize;
        public int SlabBytes => _slabBytes;

        public ConcurrentBufferManager(int sliceSize, int initialSlabBytes, long maxTotalBytes = 0)
        {
            if (sliceSize <= 0) throw new ArgumentOutOfRangeException(nameof(sliceSize));
            if (initialSlabBytes < sliceSize || (initialSlabBytes % sliceSize) != 0)
                throw new ArgumentOutOfRangeException(nameof(initialSlabBytes), "Must be a multiple of sliceSize and >= sliceSize.");

            _sliceSize = sliceSize;
            _slabBytes = initialSlabBytes;
            _maxTotalBytes = maxTotalBytes;

            var slab = NewSlab(_slabBytes);
            _slabs.Add(slab);
            _currentSlab = slab;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetBuffer(SocketAsyncEventArgs args)
        {
            ThrowIfDisposed();

            if (_free.TryPop(out var entry))
            {
                var mem = new Memory<byte>(entry.slab.Buffer, entry.offset, _sliceSize);
                args.SetBuffer(mem);
                args.UserToken = entry; // O(1) free
                return true;
            }

            var slab = _currentSlab;
            if (slab is not null && slab.TryRent(out int off))
            {
                var mem = new Memory<byte>(slab.Buffer, off, _sliceSize);
                args.SetBuffer(mem);
                args.UserToken = (slab, off);
                return true;
            }

            if (TryAllocateNewSlab(out slab) && slab.TryRent(out off))
            {
                var mem = new Memory<byte>(slab.Buffer, off, _sliceSize);
                args.SetBuffer(mem);
                args.UserToken = (slab, off);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FreeBuffer(SocketAsyncEventArgs args)
        {
            if (_disposed)
                return;

            if (args.UserToken is (Slab slab, int offset))
            {
                args.UserToken = null;
                args.SetBuffer(Memory<byte>.Empty);
                _free.Push((slab, offset));
            }
        }

        public int FreeCount => _free.Count;

        public long TotalCapacitySlices
        {
            get
            {
                long bytes = Interlocked.Read(ref _allocatedBytes);
                return bytes / _sliceSize;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Slab NewSlab(int bytes)
        {
            var slab = new Slab(bytes, _sliceSize);
            Interlocked.Add(ref _allocatedBytes, bytes);
            return slab;
        }

        private bool TryAllocateNewSlab(out Slab slab)
        {
            slab = _currentSlab!;

            if (slab is not null && slab.TryRent(out _))
                return true;

            if (Interlocked.CompareExchange(ref _allocating, 1, 0) != 0)
            {
                Thread.Yield();
                var s = _currentSlab;
                if (s is not null && s.TryRent(out _))
                {
                    slab = s;
                    return true;
                }
                slab = s!;
                return false;
            }

            try
            {
                long cur = Interlocked.Read(ref _allocatedBytes);
                if (_maxTotalBytes != 0 && cur + _slabBytes > _maxTotalBytes)
                    return false;

                var newSlab = NewSlab(_slabBytes);
                _slabs.Add(newSlab);
                Volatile.Write(ref _currentSlab, newSlab);
                slab = newSlab;
                return true;
            }
            finally
            {
                Volatile.Write(ref _allocating, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _currentSlab = null;
            while (_slabs.TryTake(out _)) { }
            while (_free.TryPop(out _)) { }
            Interlocked.Exchange(ref _allocatedBytes, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConcurrentBufferManager));
        }
    }
}
