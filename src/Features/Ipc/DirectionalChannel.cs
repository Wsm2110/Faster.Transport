using System;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using Faster.Transport.Primitives;

namespace Faster.Transport.Ipc
{
    public unsafe sealed class DirectionalChannel : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle? _signal; // optional
        private readonly bool _reader;
        private readonly SharedSpscRing _ring;
        private readonly byte[] _recv;
        private readonly Thread? _rxThread;
        private volatile bool _running;

        public event Action<ReadOnlyMemory<byte>>? OnFrame;

        public DirectionalChannel(string mapName, string? evName, int totalBytes, bool create, bool isReader, string? rxThreadName = null, bool useEvent = false)
        {
            _reader = isReader;

            _mmf = create ? MmfHelper.CreateWithSecurity(mapName, totalBytes)
                          : MmfHelper.OpenExistingWithRetry(mapName);

            _signal = useEvent && evName != null
                      ? (create ? MmfHelper.CreateOrOpenEvent(evName) : MmfHelper.OpenEventWithRetry(evName))
                      : null;

            _view = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _ring = new SharedSpscRing(p, totalBytes);

            // Allocate a single large reusable buffer (no per-message allocs)
            _recv = ArrayPool<byte>.Shared.Rent(Math.Max(1 << 16, totalBytes));

            if (_reader)
                _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = rxThreadName ?? "mmf-rx" };
        }

        public void Start()
        {
            if (_reader && _rxThread is not null)
            {
                _running = true;
                _rxThread.Start();
            }
        }

        private void ReceiveLoop()
        {
            FasterIpcTrace.Info("[Directional] RX start");
            var spin = new SpinWait();
            while (_running)
            {
                if (_ring.TryDequeue(_recv, out int len))
                {
                    OnFrame?.Invoke(new ReadOnlyMemory<byte>(_recv, 0, len));
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce();
                    if (spin.NextSpinWillYield)
                    {
                        // Avoid kernel transitions; very light backoff
                        Thread.Sleep(0);
                        // optional signal wait as fallback
                        _signal?.WaitOne(0);
                    }
                }
            }
            FasterIpcTrace.Info("[Directional] RX stop");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_reader) return;
            var spin = new SpinWait();
            while (!_ring.TryEnqueue(payload))
            {
                spin.SpinOnce();
                if (spin.NextSpinWillYield) Thread.Sleep(0);
            }
            _signal?.Set();
        }

        public void Dispose()
        {
            _running = false;
            try { _signal?.Set(); } catch { }
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _view.Dispose();
            _mmf.Dispose();
            ArrayPool<byte>.Shared.Return(_recv, clearArray: false);
            _signal?.Dispose();
        }
    }
}
