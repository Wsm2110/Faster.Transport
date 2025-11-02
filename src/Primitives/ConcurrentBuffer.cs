using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        private sealed class Slab : IDisposable
        {
            public readonly byte[] Buffer;   // pinned
            private readonly GCHandle _handle;
            public readonly int Length;
            public readonly int SliceSize;
            private int _currentIndex;

            public Slab(int length, int sliceSize)
            {
#if NET5_0_OR_GREATER
                Buffer = GC.AllocateUninitializedArray<byte>(length);
#else
                Buffer = new byte[length];
#endif
                _handle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);
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

            public void Dispose()
            {
                if (_handle.IsAllocated) _handle.Free();
            }
        }

        private readonly int _sliceSize;
        private readonly int _slabBytes;
        private readonly long _maxTotalBytes;
        private long _allocatedBytes;

        private volatile Slab _currentSlab;
        private readonly ConcurrentBag<Slab> _slabs = new ConcurrentBag<Slab>();
        private readonly ConcurrentStack<Tuple<Slab, int>> _free = new ConcurrentStack<Tuple<Slab, int>>();

        private int _allocating;
        private bool _disposed;

        public int SliceSize { get { return _sliceSize; } }
        public int SlabBytes { get { return _slabBytes; } }

        public ConcurrentBufferManager(int sliceSize, int initialSlabBytes, long maxTotalBytes = 0)
        {
            if (sliceSize <= 0) throw new ArgumentOutOfRangeException(nameof(sliceSize));
            if (initialSlabBytes < sliceSize || (initialSlabBytes % sliceSize) != 0)
                throw new ArgumentOutOfRangeException(nameof(initialSlabBytes), "Must be a multiple of sliceSize and >= sliceSize.");

            _sliceSize = sliceSize;
            _slabBytes = initialSlabBytes;
            _maxTotalBytes = maxTotalBytes;

            Slab slab = NewSlab(_slabBytes);
            _slabs.Add(slab);
            _currentSlab = slab;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetBuffer(SocketAsyncEventArgs args)
        {
            ThrowIfDisposed();

            Tuple<Slab, int> entry;
            if (_free.TryPop(out entry))
            {
                args.SetBuffer(entry.Item1.Buffer, entry.Item2, _sliceSize);
                args.UserToken = entry;
                return true;
            }

            Slab slab = _currentSlab;
            int off;
            if (slab != null && slab.TryRent(out off))
            {
                args.SetBuffer(slab.Buffer, off, _sliceSize);
                args.UserToken = Tuple.Create(slab, off);
                return true;
            }

            if (TryAllocateNewSlab(out slab) && slab.TryRent(out off))
            {
                args.SetBuffer(slab.Buffer, off, _sliceSize);
                args.UserToken = Tuple.Create(slab, off);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FreeBuffer(SocketAsyncEventArgs args)
        {
            if (_disposed)
                return;

            var entry = args.UserToken as Tuple<Slab, int>;
            if (entry != null)
            {
                args.UserToken = null;
                args.SetBuffer(null, 0, 0); // Clear buffer
                _free.Push(entry);
            }
        }

        public int FreeCount
        {
            get { return _free.Count; }
        }

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
            Slab slab = new Slab(bytes, _sliceSize);
            Interlocked.Add(ref _allocatedBytes, bytes);
            return slab;
        }

        private bool TryAllocateNewSlab(out Slab slab)
        {
            slab = _currentSlab;

            int dummy;
            if (slab != null && slab.TryRent(out dummy))
                return true;

            if (Interlocked.CompareExchange(ref _allocating, 1, 0) != 0)
            {
                Thread.Yield();
                Slab s = _currentSlab;
                if (s != null && s.TryRent(out dummy))
                {
                    slab = s;
                    return true;
                }
                slab = s;
                return false;
            }

            try
            {
                long cur = Interlocked.Read(ref _allocatedBytes);
                if (_maxTotalBytes != 0 && cur + _slabBytes > _maxTotalBytes)
                    return false;

                Slab newSlab = NewSlab(_slabBytes);
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
            Slab s;
            while (_slabs.TryTake(out s))
            {
                s.Dispose();
            }

            Tuple<Slab, int> freeEntry;
            while (_free.TryPop(out freeEntry))
            {
                freeEntry.Item1.Dispose();
            }

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
