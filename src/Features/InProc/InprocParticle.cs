using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// In-process communication endpoint using lock-free MPSC queues with hybrid event-driven polling.
    /// Extremely low latency, zero allocations, and minimal CPU overhead.
    /// </summary>
    public sealed class InprocParticle : IParticle, IDisposable
    {
        #region === FastBufferPool ===

        internal static class FastBufferPool
        {
            [ThreadStatic]
            private static Stack<byte[]>[]? _buckets;

            private static readonly int[] Sizes = { 128, 512, 2048, 8192, 32768 };
            private const int MaxPerBucket = 64;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Stack<byte[]>[] GetBuckets()
            {
                var b = _buckets;
                if (b != null)
                    return b;

                b = new Stack<byte[]>[Sizes.Length];
                for (int i = 0; i < b.Length; i++)
                    b[i] = new Stack<byte[]>(MaxPerBucket);

                _buckets = b;
                return b;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int GetBucketIndex(int length)
            {
                for (int i = 0; i < Sizes.Length; i++)
                    if (length <= Sizes[i])
                        return i;
                return -1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static byte[] Rent(int length)
            {
                var buckets = GetBuckets();
                int idx = GetBucketIndex(length);
                if (idx < 0)
                    return new byte[length];

                var stack = buckets[idx];
                return stack.Count > 0 ? stack.Pop() : new byte[Sizes[idx]];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return(byte[] buffer)
            {
                var buckets = GetBuckets();
                int idx = GetBucketIndex(buffer.Length);
                if (idx < 0)
                    return;

                var stack = buckets[idx];
                if (stack.Count < MaxPerBucket)
                    stack.Push(buffer);
            }
        }

        #endregion

        #region === Fields / Events ===

        private readonly string _name;
        private readonly bool _isServer;
        private readonly int _ringCapacity;

        private InprocLink? _link;
        private Thread? _rxThread;
        private volatile bool _running;
        private volatile bool _isDisposed;

        private readonly AutoResetEvent _dataReady = new(false);

        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }
        public Action<IParticle>? OnConnected { get; set; }

        #endregion

        #region === Constructor ===

        public InprocParticle(
            string name,
            bool isServer,
            int ringCapacity = 4096,
            ThreadPriority rxThreadPriority = ThreadPriority.Highest)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _isServer = isServer;
            _ringCapacity = ringCapacity;

            _rxThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = $"{(_isServer ? "inproc-srv" : "inproc-cli")}-rx:{_name}",
                Priority = rxThreadPriority
            };
        }

        #endregion

        #region === Linking / Start-Stop ===

        internal void AttachLink(InprocLink link)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));

            _link = link ?? throw new ArgumentNullException(nameof(link));

            if (_isServer)
                link.OnServerDataAvailable = () => _dataReady.Set();
            else
                link.OnClientDataAvailable = () => _dataReady.Set();

            StartRx();
            OnConnected?.Invoke(this);
        }

        public void Start()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));

            if (!_isServer)
            {
                var link = InprocRegistry.Connect(_name, _ringCapacity);
                _link = link;

                // assign proper wake-up callback
                link.OnClientDataAvailable = () => _dataReady.Set();
            }

            StartRx();
            OnConnected?.Invoke(this);
        }

        private void StartRx()
        {
            if (_running)
                return;

            _running = true;
            _rxThread?.Start();
        }

        #endregion

        #region === Sending ===

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (payload.Length == 0)
                return;

            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InprocParticle));

            var link = _link ?? throw new InvalidOperationException("Not connected.");
            var ring = _isServer ? link.ToClient : link.ToServer;

            var buf = FastBufferPool.Rent(payload.Length);
            payload.CopyTo(buf);
            var segment = new ArraySegment<byte>(buf, 0, payload.Length);

            bool wasEmpty = ring.IsEmpty;
            SpinWaitUntil(() => ring.TryEnqueue(segment));

            // Notify the peer if queue transitioned from empty
            if (wasEmpty)
            {
                if (_isServer)
                    link.SignalClientPeer();
                else
                    link.SignalServerPeer();
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            Send(payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        #endregion

        #region === Receiving (Hybrid Event Loop) ===

        private void ReceiveLoop()
        {
            try
            {
                var link = _link;
                while (link is null && _running)
                {
                    link = _link;
                    Thread.SpinWait(64);
                }

                if (link is null)
                    return;

                var inbound = _isServer ? link.ToServer : link.ToClient;
                int idleSpins = 0;

                while (_running)
                {
                    bool gotMsg = false;

                    // Drain all pending messages
                    while (inbound.TryDequeue(out var seg))
                    {
                        gotMsg = true;
                        OnReceived?.Invoke(this, new ReadOnlyMemory<byte>(seg.Array!, seg.Offset, seg.Count));

                        if (seg.Array is not null)
                            FastBufferPool.Return(seg.Array);
                    }

                    if (gotMsg)
                    {
                        idleSpins = 0;
                        continue;
                    }

                    // Spin briefly, then block
                    if (idleSpins < 64)
                    {
                        Thread.SpinWait(4 << idleSpins);
                        idleSpins++;
                    }
                    else
                    {
                        _dataReady.WaitOne(1); // Sleep until data or timeout
                        idleSpins = 0;
                    }
                }
            }
            catch
            {
                // swallow teardown exceptions
            }
            finally
            {
                try { OnDisconnected?.Invoke(this); } catch { }
            }
        }

        #endregion

        #region === Helpers / Cleanup ===

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SpinWaitUntil(Func<bool> condition)
        {
            int spins = 0;
            while (!condition())
            {
                if (spins < 64)
                    Thread.SpinWait(4 << spins);
                else
                    Thread.Yield();
                spins++;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _running = false;

            _dataReady.Set(); // wake up blocked thread

            if (_rxThread is not null && _rxThread.IsAlive)
            {
                if (!_rxThread.Join(TimeSpan.FromMilliseconds(200)))
                    _rxThread.Interrupt();
            }

            _rxThread = null;
            _link = null;
        }

        #endregion
    }
}
