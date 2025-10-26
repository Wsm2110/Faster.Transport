using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Cross-process SPSC ring with cacheline padded head/tail and inline heartbeats.
    /// Layout (totalBytes must be 128 + power-of-two):
    ///  0   : int Head      (consumer updates)   -- cacheline 0
    /// 64   : int Tail      (producer updates)   -- cacheline 1
    /// 96   : long HbWriter (producer heartbeat ticks)
    /// 104  : long HbReader (consumer heartbeat ticks)
    /// 128..: byte[data]  (capacity = totalBytes - 128, must be power-of-two)
    /// </summary>
    public unsafe struct SharedSpscRing
    {
        private readonly byte* _base;
        private readonly int _cap, _mask;
        private byte* Data => _base + 128;

        public SharedSpscRing(byte* basePtr, int totalBytes)
        {
            if (totalBytes < 128) throw new ArgumentOutOfRangeException(nameof(totalBytes));
            int cap = totalBytes;
            if ((cap & (cap - 1)) != 0) throw new ArgumentException("totalBytes-128 must be power-of-two");
            _base = basePtr;
            _cap = cap;
            _mask = cap - 1;
        }

        private ref int Head => ref Unsafe.AsRef<int>(_base + 0);
        private ref int Tail => ref Unsafe.AsRef<int>(_base + 64);
        private ref long HbWriter => ref Unsafe.AsRef<long>(_base + 96);
        private ref long HbReader => ref Unsafe.AsRef<long>(_base + 104);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TouchWriterHeartbeat() => Volatile.Write(ref HbWriter, DateTime.UtcNow.Ticks);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TouchReaderHeartbeat() => Volatile.Write(ref HbReader, DateTime.UtcNow.Ticks);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadWriterHeartbeat() => Volatile.Read(ref HbWriter);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadReaderHeartbeat() => Volatile.Read(ref HbReader);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int VRead(ref int loc) => Volatile.Read(ref loc);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void VWrite(ref int loc, int val) => Volatile.Write(ref loc, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBytes(int pos, ReadOnlySpan<byte> src)
        {
            int first = Math.Min(src.Length, _cap - pos);
            src[..first].CopyTo(new Span<byte>(Data + pos, first));
            if (src.Length > first)
                src[first..].CopyTo(new Span<byte>(Data, src.Length - first));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadBytes(int pos, Span<byte> dst)
        {
            int first = Math.Min(dst.Length, _cap - pos);
            new ReadOnlySpan<byte>(Data + pos, first).CopyTo(dst[..first]);
            if (dst.Length > first)
                new ReadOnlySpan<byte>(Data, dst.Length - first).CopyTo(dst[first..]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(ReadOnlySpan<byte> payload)
        {
            int need = 4 + payload.Length;
            if (need >= _cap) return false;

            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            int used = (tail - head) & _mask;
            if (used + need >= _cap) return false;

            int pos = tail & _mask;
            Span<byte> hdr = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(hdr, payload.Length);
            WriteBytes(pos, hdr);
            pos = (pos + 4) & _mask;
            WriteBytes(pos, payload);

            VWrite(ref Tail, (tail + need) & _mask);
            TouchWriterHeartbeat();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(Span<byte> dst, out int len)
        {
            len = 0;
            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            if (head == tail) return false;

            int pos = head & _mask;
            Span<byte> hdr = stackalloc byte[4];
            ReadBytes(pos, hdr);
            len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
            int need = 4 + len;
            if (len > dst.Length) return false;

            pos = (pos + 4) & _mask;
            ReadBytes(pos, dst[..len]);

            VWrite(ref Head, (head + need) & _mask);
            TouchReaderHeartbeat();
            return true;
        }
    }
}
