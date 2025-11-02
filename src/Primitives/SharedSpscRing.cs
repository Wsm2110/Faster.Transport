using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Ultra-fast lock-free SPSC ring buffer for shared memory (wrap-around cursors).
    /// Layout (byte offsets):
    ///   0   : int Head  (consumer cursor, masked 0..dataCap-1)
    ///   64  : int Tail  (producer cursor, masked 0..dataCap-1)
    ///   128 : byte[] Data (size = dataCap = totalBytes - 128, power-of-two)
    /// </summary>
    public unsafe struct SharedSpscRing
    {
        private readonly byte* _base;
        private readonly int _dataCap; // power-of-two
        private readonly int _mask;    // _dataCap - 1
        private byte* Data => _base + 128;
        private ref int Head => ref Unsafe.AsRef<int>(_base + 0);
        private ref int Tail => ref Unsafe.AsRef<int>(_base + 64);
        private static int VRead(ref int loc) => Volatile.Read(ref loc);
        private static void VWrite(ref int loc, int val) => Volatile.Write(ref loc, val);

        public SharedSpscRing(byte* basePtr, int totalBytes)
        {
            if (totalBytes < 128)
                throw new ArgumentOutOfRangeException(nameof(totalBytes), "Buffer must be at least 128 bytes.");

            int dataCap = totalBytes - 128;
            if (dataCap <= 0 || (dataCap & (dataCap - 1)) != 0)
                throw new ArgumentException("totalBytes - 128 must be a power-of-two.");

            _base = basePtr;
            _dataCap = dataCap;
            _mask = dataCap - 1;
        }

        // -------- Internal copy helpers (wrap-safe) --------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBytes(int pos, ReadOnlySpan<byte> src)
        {
            int len = src.Length;
            if (len == 0) return;
            int room = _dataCap - pos; // contiguous bytes to end

            if (len <= room)
            {
                // contiguous
                src.CopyTo(new Span<byte>(Data + pos, len));
            }
            else
            {
                // wrapped
                src.Slice(0, room).CopyTo(new Span<byte>(Data + pos, room));
                src.Slice(room).CopyTo(new Span<byte>(Data, len - room));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadBytes(int pos, Span<byte> dst)
        {
            int len = dst.Length;
            if (len == 0) return;
            int room = _dataCap - pos;

            if (len <= room)
            {
                new ReadOnlySpan<byte>(Data + pos, len).CopyTo(dst);
            }
            else
            {
                new ReadOnlySpan<byte>(Data + pos, room).CopyTo(dst.Slice(0, room));
                new ReadOnlySpan<byte>(Data, len - room).CopyTo(dst.Slice(room));
            }
        }

        // --------------------------- Public API ---------------------------

        /// <summary>
        /// Enqueue [len:int32][payload]. Returns false if not enough space or payload too large.
        /// Keeps 1 byte free to disambiguate full vs empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(ReadOnlySpan<byte> payload)
        {
            int len = payload.Length;

            // Ensure message can ever fit while preserving 1-byte gap:
            // need = 4 + len <= _dataCap - 1  => len <= _dataCap - 5
            if ((uint)len > (uint)(_dataCap - 5)) return false;

            int need = 4 + len;

            // Snapshot cursors (masked)
            int head = VRead(ref Head);
            int tail = VRead(ref Tail);

            // Used bytes (cyclic)
            int used = (tail - head) & _mask;

            // Require at least 1 byte free at all times
            if (used + need >= _dataCap) return false;

            int pos = tail;

            // Fast header write if contiguous
            int room = _dataCap - pos;
            if (room >= 4)
            {
                // write little-endian int32 directly
                if (BitConverter.IsLittleEndian)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>(Data + pos), len);
                }
                else
                {
                    int rev = BinaryPrimitives.ReverseEndianness(len);
                    Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>(Data + pos), rev);
                }
            }
            else
            {
                // wrapped header: rare path, use small stack hdr
                Span<byte> hdr = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(hdr, len);
                WriteBytes(pos, hdr);
            }

            // Write payload
            pos = (pos + 4) & _mask;
            if (len != 0)
                WriteBytes(pos, payload);

            // Publish (release): advance tail with wrap
            VWrite(ref Tail, (tail + need) & _mask);
            return true;
        }

        /// <summary>
        /// Dequeue into dst. Returns false if empty, not yet fully published, or dst too small.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(Span<byte> dst, out int len)
        {
            len = 0;

            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            int avail = (tail - head) & _mask;
            if (avail == 0) return false; // empty
            if (avail < 4) return false;  // header not fully published

            int pos = head;

            // Fast header read if contiguous
            int payloadLen;
            int room = _dataCap - pos;
            if (room >= 4)
            {
                int raw = Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef<byte>(Data + pos));
                payloadLen = BitConverter.IsLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            }
            else
            {
                Span<byte> hdr = stackalloc byte[4];
                ReadBytes(pos, hdr);
                payloadLen = BinaryPrimitives.ReadInt32LittleEndian(hdr);
            }

            // Validate header & capacity
            if ((uint)payloadLen > (uint)(_dataCap - 5)) return false; // invalid/corrupt
            if (payloadLen > dst.Length) return false;

            int need = 4 + payloadLen;

            // Ensure the full message is available (writer publishes tail after payload)
            if (avail < need) return false;

            // Read payload
            pos = (pos + 4) & _mask;
            if (payloadLen != 0)
                ReadBytes(pos, dst.Slice(0, payloadLen));

            // Consume (release): advance head with wrap
            VWrite(ref Head, (head + need) & _mask);
            len = payloadLen;
            return true;
        }

        /// <summary>
        /// Zero-copy dequeue: returns up to two spans into the shared memory (handles wrap-around) and total length.
        /// Returns false if empty or not fully published.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out ReadOnlySpan<byte> span1, out ReadOnlySpan<byte> span2, out int len)
        {
            span1 = default;
            span2 = default;
            len = 0;

            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            int avail = (tail - head) & _mask;
            if (avail < 4) return false;

            int pos = head;
            int room = _dataCap - pos;

            // Header read
            int payloadLen;
            if (room >= 4)
            {
                int raw = Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef<byte>(Data + pos));
                payloadLen = BitConverter.IsLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
            }
            else
            {
                Span<byte> hdr = stackalloc byte[4];
                ReadBytes(pos, hdr);
                payloadLen = BinaryPrimitives.ReadInt32LittleEndian(hdr);
            }

            if ((uint)payloadLen > (uint)(_dataCap - 5)) return false;
            int need = 4 + payloadLen;
            if (avail < need) return false;

            // Payload spans
            len = payloadLen;
            if (payloadLen == 0)
            {
                // advance & return
                VWrite(ref Head, (head + 4) & _mask);
                return true;
            }

            int ppos = (pos + 4) & _mask;
            int proom = _dataCap - ppos;

            if (payloadLen <= proom)
            {
                span1 = new ReadOnlySpan<byte>(Data + ppos, payloadLen);
                span2 = default;
            }
            else
            {
                span1 = new ReadOnlySpan<byte>(Data + ppos, proom);
                span2 = new ReadOnlySpan<byte>(Data, payloadLen - proom);
            }

            VWrite(ref Head, (head + need) & _mask);
            return true;
        }

        // --------------------------- Optional helpers ---------------------------

        public int CapacityBytes => _dataCap;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int UsedBytes()
        {
            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            return (tail - head) & _mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FreeBytes() => _dataCap - 1 - UsedBytes(); // 1 byte reserved
    }
}
