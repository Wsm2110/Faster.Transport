using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Length-prefixed (little-endian) frame parser with **immediate dispatch**.
    /// Single-producer (Feed) only. Calls <see cref="OnFrame"/> synchronously for each parsed frame.
    ///
    /// WARNING (zero-copy mode): When <paramref name="copyOnDispatch"/> is false, the provided
    /// ReadOnlyMemory<byte> points into the parser's internal buffer. Do NOT retain it after the callback returns.
    /// Use copyOnDispatch: true if you need to store the payload.
    /// </summary>
    public sealed class FrameParser : IDisposable
    {
        private readonly byte[] _buffer;       // ring buffer
        private readonly byte[] _tempBuffer;   // scratch for wrapped frames (or copy source)
        private readonly int _capacity;        // power of two
        private readonly int _mask;

        private readonly int _maxFrameSize;
        private readonly bool _copyOnDispatch;

        private volatile bool _disposed;
        private int _head;   // write index
        private int _tail;   // read index
        private int _length; // bytes in ring

        /// <summary>
        /// Invoked for each complete frame (payload only; 4-byte header excluded).
        /// </summary>
        public Action<ReadOnlyMemory<byte>>? OnFrame { get; set; }

        /// <summary>
        /// Raised for non-fatal parser errors (overflow, invalid length, queue policy, etc.).
        /// </summary>
        public Action<Exception>? OnError { get; set; }

        /// <param name="capacity">Ring size in bytes (power of two). 64 KiB is a solid default.</param>
        /// <param name="maxFrameSize">Max allowed payload size (bytes). Defaults to capacity.</param>
        /// <param name="copyOnDispatch">
        /// If true, the parser copies every payload into a fresh array before invoking <see cref="OnFrame"/>.
        /// Safer if your callback stores payloads. If false, contiguous frames are zero-copy.
        /// </param>
        public FrameParser(int capacity = 64 * 1024, int? maxFrameSize = null, bool copyOnDispatch = false)
        {
            if (!IsPowerOfTwo(capacity))
                throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));

            _capacity = capacity;
            _mask = capacity - 1;

            _buffer = ArrayPool<byte>.Shared.Rent(capacity);
            _tempBuffer = ArrayPool<byte>.Shared.Rent(capacity);

            _maxFrameSize = Math.Min(maxFrameSize ?? capacity, capacity);
            _copyOnDispatch = copyOnDispatch;
        }

        /// <summary>
        /// Feed bytes from your socket ReceiveAsync. Returns false if the ring is full (apply backpressure or drop).
        /// </summary>
        public bool Feed(ReadOnlySpan<byte> data)
        {
            if (_disposed) return false;

            int dataPos = 0;

            // Append to ring (with wrap)
            while (dataPos < data.Length)
            {
                int writable = Math.Min(data.Length - dataPos, _capacity - _length);
                if (writable == 0)
                {
                    OnError?.Invoke(new IOException("Parser ring buffer full (backpressure required)."));
                    return false;
                }

                int firstPart = Math.Min(writable, _capacity - _head);
                data.Slice(dataPos, firstPart).CopyTo(_buffer.AsSpan(_head, firstPart));

                int remaining = writable - firstPart;
                if (remaining > 0)
                    data.Slice(dataPos + firstPart, remaining).CopyTo(_buffer.AsSpan(0, remaining));

                _head = (_head + writable) & _mask;
                _length += writable;
                dataPos += writable;
            }

            // Parse & dispatch frames immediately
            ParseAndDispatch();
            return true;
        }

        private void ParseAndDispatch()
        {
            while (true)
            {
                if (_length < 4) return; // need header

                // Read length (handle wrap)
                int len;
                if (_tail + 4 <= _capacity)
                {
                    len = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_tail, 4));
                }
                else
                {
                    Span<byte> tmp = stackalloc byte[4];
                    int first = _capacity - _tail;
                    _buffer.AsSpan(_tail, first).CopyTo(tmp);
                    _buffer.AsSpan(0, 4 - first).CopyTo(tmp[first..]);
                    len = BinaryPrimitives.ReadInt32LittleEndian(tmp);
                }

                // Validate
                if (len <= 0 || len > _maxFrameSize)
                {
                    // attempt resync
                    _tail = (_tail + 1) & _mask;
                    _length -= 1;
                    OnError?.Invoke(new InvalidDataException($"Invalid frame length: {len} (max {_maxFrameSize})."));
                    continue;
                }

                if (_length < 4 + len) return; // wait for more bytes

                // Compute payload start
                int payloadStart = (_tail + 4) & _mask;

                if (_copyOnDispatch)
                {
                    // Always copy payload into a fresh array
                    var copy = new byte[len];
                    if (payloadStart + len <= _capacity)
                    {
                        _buffer.AsSpan(payloadStart, len).CopyTo(copy);
                    }
                    else
                    {
                        int firstPart = _capacity - payloadStart;
                        int secondPart = len - firstPart;
                        _buffer.AsSpan(payloadStart, firstPart).CopyTo(copy.AsSpan(0, firstPart));
                        _buffer.AsSpan(0, secondPart).CopyTo(copy.AsSpan(firstPart));
                    }

                    OnFrame?.Invoke(copy);
                }
                else
                {
                    // Zero-copy for contiguous; wrapped uses tempBuffer then dispatch
                    if (payloadStart + len <= _capacity)
                    {
                        var mem = new ReadOnlyMemory<byte>(_buffer, payloadStart, len);
                        OnFrame?.Invoke(mem);
                    }
                    else
                    {
                        // Copy into temp buffer, then dispatch a view over temp
                        int firstPart = _capacity - payloadStart;
                        int secondPart = len - firstPart;
                        _buffer.AsSpan(payloadStart, firstPart).CopyTo(_tempBuffer.AsSpan(0, firstPart));
                        _buffer.AsSpan(0, secondPart).CopyTo(_tempBuffer.AsSpan(firstPart));

                        // WARNING: do not capture this beyond the callback; tempBuffer is reused
                        var mem = new ReadOnlyMemory<byte>(_tempBuffer, 0, len);
                        OnFrame?.Invoke(mem);
                    }
                }

                // Advance past this frame
                _tail = (_tail + 4 + len) & _mask;
                _length -= 4 + len;
            }
        }

        /// <summary> Clears internal state (drops unconsumed bytes). </summary>
        public void Reset()
        {
            _head = 0;
            _tail = 0;
            _length = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Ensure no outstanding references when copyOnDispatch=false.
            ArrayPool<byte>.Shared.Return(_buffer);
            ArrayPool<byte>.Shared.Return(_tempBuffer);
        }

        private static bool IsPowerOfTwo(int v) => (v & (v - 1)) == 0;
    }
}
