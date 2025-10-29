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

        // Volatile helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int VRead(ref int loc) => Volatile.Read(ref loc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void VWrite(ref int loc, int val) => Volatile.Write(ref loc, val);

        // -------- Core I/O (safe wrap handling, no overflow on pos+len) --------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBytes(int pos, ReadOnlySpan<byte> src)
        {
            int len = src.Length;
            if (len == 0) return;

            int room = _dataCap - pos;                 // contiguous bytes to end
            if (len <= room)
            {
                src.CopyTo(new Span<byte>(Data + pos, len));
            }
            else
            {
                src[..room].CopyTo(new Span<byte>(Data + pos, room));
                src[room..].CopyTo(new Span<byte>(Data, len - room));
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
                new ReadOnlySpan<byte>(Data + pos, room).CopyTo(dst[..room]);
                new ReadOnlySpan<byte>(Data, len - room).CopyTo(dst[room..]);
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

            // Snapshot cursors (masked values)
            int head = VRead(ref Head);
            int tail = VRead(ref Tail);

            // Used bytes (cyclic)
            int used = (tail - head) & _mask;

            // Require at least 1 byte free at all times
            if (used + need >= _dataCap) return false;

            int pos = tail;

            // Write header
            Span<byte> hdr = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(hdr, len);
            WriteBytes(pos, hdr);

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

            int pos = head;

            // Read header if at least 4 bytes available
            if (avail < 4) return false; // not yet fully published header
            Span<byte> hdr = stackalloc byte[4];
            ReadBytes(pos, hdr);
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(hdr);

            // Validate header
            if ((uint)payloadLen > (uint)(_dataCap - 5)) return false; // invalid/corrupt
            if (payloadLen > dst.Length) return false;

            int need = 4 + payloadLen;

            // Ensure the full message is available (writer publishes tail after payload)
            if (avail < need) return false;

            // Read payload
            pos = (pos + 4) & _mask;
            if (payloadLen != 0)
                ReadBytes(pos, dst[..payloadLen]);

            // Consume (release): advance head with wrap
            VWrite(ref Head, (head + need) & _mask);
            len = payloadLen;
            return true;
        }
    }
}
