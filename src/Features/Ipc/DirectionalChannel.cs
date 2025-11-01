﻿using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using Faster.Transport.Primitives;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Ultra-fast SPSC MMF channel with fixed back-buffer ring and batched receive.
    /// Keeps API: OnFrame(ReadOnlyMemory<byte>).
    /// </summary>
    public unsafe sealed class DirectionalChannel : IDisposable
    {
        private const int HeaderBytes = 128;      // 0..63: head padding, 64..127: tail padding
        private const int BatchMax = 32;        // messages per loop iteration before re-checking _running

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle? _signal;
        private readonly bool _reader;
        private readonly SharedSpscRing _ring;

        // Fixed ring of back-buffers; avoids ArrayPool churn and reuse hazards
        private readonly byte[][] _buffers;
        private int _bufIndex; // next buffer slot

        private readonly Thread? _rxThread;
        private volatile bool _running;

        // Derived limits
        public int Length { get; }
        private readonly int _maxPayload; // totalBytes - header - 5

        /// <summary>Raised when a complete message frame is received.</summary>
        public event Action<ReadOnlyMemory<byte>>? OnFrame;

        /// <param name="backBufferCount">How many back buffers to rotate (default 8). Increase if consumer is slower.</param>
        /// <param name="rxThreadPriority">RX thread priority (default Highest). Use Normal if you prefer.</param>
        public DirectionalChannel(
            string mapName,
            string? evName,
            int totalBytes,
            bool create,
            bool isReader,
            string? rxThreadName = null,
            bool useEvent = false,
            int backBufferCount = 8,
            ThreadPriority rxThreadPriority = ThreadPriority.Highest)
        {
            if (backBufferCount < 2) backBufferCount = 2;

            _reader = isReader;
            Length = totalBytes;

            // Real max payload the ring can ever accept: (dataCap - 5)
            // dataCap = totalBytes - 128
            _maxPayload = Math.Max(0, totalBytes - HeaderBytes - 5);

            _mmf = create
                ? MmfHelper.CreateWithSecurity(mapName, totalBytes)
                : MmfHelper.OpenExistingWithRetry(mapName);

            _signal = useEvent && evName != null
                ? (create ? MmfHelper.CreateOrOpenEvent(evName)
                          : MmfHelper.OpenEventWithRetry(evName))
                : null;

            _view = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);

            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

            _ring = new SharedSpscRing(p, totalBytes);

            // Pre-allocate fixed back buffers sized to max payload (keep <85KB if you want to avoid LOH).
            // If your messages can be larger than ~64KB often, set totalBytes accordingly at construction time.
            int bufSize = Math.Max(64 * 1024, _maxPayload); // minimum 64KiB, or up to max payload
            _buffers = new byte[backBufferCount][];
            for (int i = 0; i < backBufferCount; i++)
                _buffers[i] = new byte[bufSize];

            if (_reader)
            {
                _rxThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = rxThreadName ?? "mmf-rx",
                    Priority = rxThreadPriority
                };
            }
        }

        public void Start()
        {
            if (_reader && _rxThread is not null)
            {
                _running = true;
                _rxThread.Start();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte[] NextBuffer()
        {
            var idx = _bufIndex;
            _bufIndex = (idx + 1) % _buffers.Length;
            return ref _buffers[idx];
        }

        private void ReceiveLoop()
        {
            var spinExp = 1; // exponential spin budget
            while (_running)
            {
                int delivered = 0;

                // Batch: drain up to BatchMax messages before any backoff
                while (delivered < BatchMax && _ring.TryDequeue(NextBuffer(), out int len))
                {
                    // NOTE: len can be == 0 for empty logical frames; still signal
                    OnFrame?.Invoke(new ReadOnlyMemory<byte>(_buffers[(_bufIndex - 1 + _buffers.Length) % _buffers.Length], 0, len));
                    delivered++;
                }

                if (delivered > 0)
                {
                    // reset backoff on progress
                    spinExp = 1;
                    continue;
                }

                // Tight CPU-local backoff; no Sleep(0) in hot path
                Thread.SpinWait(spinExp);
                if (spinExp < (1 << 12)) spinExp <<= 1; // cap escalation

                // Optional: very light event wait only if you enabled a signal
                // if (spinExp >= (1 << 10)) _signal?.WaitOne(0);
            }
        }

        /// <summary>
        /// Enqueue payload. Will busy-spin locally until space is available.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_reader) return;
            if (payload.Length > _maxPayload)
                throw new ArgumentException($"Payload size {payload.Length} exceeds max {_maxPayload} bytes for this ring.");

            int spinExp = 1;
            while (!_ring.TryEnqueue(payload))
            {
                Thread.SpinWait(spinExp);
                if (spinExp < (1 << 12)) spinExp <<= 1;
            }

            _signal?.Set(); // no cost if null; cheap if manual-reset not used
        }

        public void Dispose()
        {
            _running = false;

            try { _signal?.Set(); } catch { /* wake reader if waiting */ }
            if (_rxThread is not null && _rxThread.IsAlive)
            {
                if (!_rxThread.Join(TimeSpan.FromMilliseconds(200)))
                    _rxThread.Interrupt();
            }

            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _view.Dispose();
            _mmf.Dispose();
            _signal?.Dispose();
        }
    }
}
