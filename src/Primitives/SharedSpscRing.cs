using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Represents a **shared-memory ring buffer** designed for single-producer, single-consumer (SPSC) communication
    /// between threads or processes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This structure allows **lock-free**, **zero-copy** message passing using a memory-mapped file.
    /// It is the foundation of <see cref="Faster.Transport.Ipc.DirectionalChannel"/> and other IPC primitives.
    /// </para>
    ///
    /// <para>
    /// Memory layout (each number = byte offset):
    /// <code>
    /// | Offset | Field         | Description                      | Updated by |
    /// |---------|---------------|----------------------------------|-------------|
    /// | 0       | int Head      | Consumer read position           | Reader      |
    /// | 64      | int Tail      | Producer write position          | Writer      |
    /// | 96      | long HbWriter | Producer heartbeat (UTC ticks)   | Writer      |
    /// | 104     | long HbReader | Consumer heartbeat (UTC ticks)   | Reader      |
    /// | 128..N  | byte[] Data   | Ring buffer contents (power-of-two capacity) |
    /// </code>
    /// </para>
    ///
    /// <para>
    /// The first 128 bytes are metadata, aligned to cache lines (64 bytes each) to avoid false sharing.
    /// The remaining region is a circular buffer for message data.
    /// </para>
    ///
    /// <example>
    /// Example usage (same process):
    /// <code>
    /// byte* mem = stackalloc byte[1024];
    /// var ring = new SharedSpscRing(mem, 1024);
    ///
    /// // Writer thread
    /// ring.TryEnqueue("Hello"u8);
    ///
    /// // Reader thread
    /// Span&lt;byte&gt; buffer = stackalloc byte[128];
    /// if (ring.TryDequeue(buffer, out int len))
    ///     Console.WriteLine($"Received: {Encoding.UTF8.GetString(buffer[..len])}");
    /// </code>
    /// </example>
    /// </remarks>
    public unsafe struct SharedSpscRing
    {
        private readonly byte* _base;
        private readonly int _cap, _mask;

        /// <summary>
        /// Pointer to the start of the data section (offset 128).
        /// </summary>
        private byte* Data => _base + 128;

        /// <summary>
        /// Initializes a new instance of the ring at the specified base address.
        /// </summary>
        /// <param name="basePtr">Pointer to the beginning of the shared memory region.</param>
        /// <param name="totalBytes">Total allocated bytes (must be 128 + a power of two).</param>
        public SharedSpscRing(byte* basePtr, int totalBytes)
        {
            if (totalBytes < 128)
                throw new ArgumentOutOfRangeException(nameof(totalBytes), "Buffer must be at least 128 bytes.");

            int cap = totalBytes;
            if ((cap & (cap - 1)) != 0)
                throw new ArgumentException("totalBytes - 128 must be a power-of-two.");

            _base = basePtr;
            _cap = cap;
            _mask = cap - 1;
        }

        // ---------------------------------------------------------------------
        // Memory Layout Accessors (cache-aligned)
        // ---------------------------------------------------------------------

        private ref int Head => ref Unsafe.AsRef<int>(_base + 0);      // Consumer cursor
        private ref int Tail => ref Unsafe.AsRef<int>(_base + 64);     // Producer cursor
        private ref long HbWriter => ref Unsafe.AsRef<long>(_base + 96);
        private ref long HbReader => ref Unsafe.AsRef<long>(_base + 104);

        // ---------------------------------------------------------------------
        // Heartbeats (optional monitoring)
        // ---------------------------------------------------------------------

        /// <summary>Updates the producer heartbeat timestamp.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TouchWriterHeartbeat() =>
            Volatile.Write(ref HbWriter, DateTime.UtcNow.Ticks);

        /// <summary>Updates the consumer heartbeat timestamp.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TouchReaderHeartbeat() =>
            Volatile.Write(ref HbReader, DateTime.UtcNow.Ticks);

        /// <summary>Reads the producer heartbeat value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadWriterHeartbeat() =>
            Volatile.Read(ref HbWriter);

        /// <summary>Reads the consumer heartbeat value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadReaderHeartbeat() =>
            Volatile.Read(ref HbReader);

        // ---------------------------------------------------------------------
        // Internal volatile helpers
        // ---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int VRead(ref int loc) => Volatile.Read(ref loc);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void VWrite(ref int loc, int val) => Volatile.Write(ref loc, val);

        // ---------------------------------------------------------------------
        // Read/Write Helpers
        // ---------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteBytes(int pos, ReadOnlySpan<byte> src)
        {
            // Copy wraps around if data spans buffer end
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

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Attempts to enqueue a message into the ring.
        /// </summary>
        /// <param name="payload">The bytes to write (max size: capacity - 4).</param>
        /// <returns><see langword="true"/> if the message was written successfully; otherwise, false.</returns>
        /// <remarks>
        /// Each message is stored as:
        /// <code>[Length: Int32][Payload]</code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryEnqueue(ReadOnlySpan<byte> payload)
        {
            int need = 4 + payload.Length;
            if (need >= _cap) return false; // Too large for buffer

            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            int used = (tail - head) & _mask;

            // Check if there’s enough space
            if (used + need >= _cap) return false;

            int pos = tail & _mask;

            // Write message header
            Span<byte> hdr = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(hdr, payload.Length);
            WriteBytes(pos, hdr);

            // Write payload
            pos = (pos + 4) & _mask;
            WriteBytes(pos, payload);

            // Commit the write
            VWrite(ref Tail, (tail + need) & _mask);
            TouchWriterHeartbeat();
            return true;
        }

        /// <summary>
        /// Attempts to dequeue a message from the ring.
        /// </summary>
        /// <param name="dst">Destination buffer to write data into.</param>
        /// <param name="len">Number of bytes actually read.</param>
        /// <returns><see langword="true"/> if a message was read; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(Span<byte> dst, out int len)
        {
            len = 0;
            int head = VRead(ref Head);
            int tail = VRead(ref Tail);
            if (head == tail) return false; // Empty

            int pos = head & _mask;

            // Read message header
            Span<byte> hdr = stackalloc byte[4];
            ReadBytes(pos, hdr);
            len = BinaryPrimitives.ReadInt32LittleEndian(hdr);

            int need = 4 + len;
            if (len > dst.Length) return false; // Caller’s buffer too small

            pos = (pos + 4) & _mask;
            ReadBytes(pos, dst[..len]);

            // Commit the read
            VWrite(ref Head, (head + need) & _mask);
            TouchReaderHeartbeat();
            return true;
        }
    }
}
