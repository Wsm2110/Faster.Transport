using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.Buffers;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Faster.Transport.Inproc;

/// <summary>
/// In-process particle optimized for extreme performance.
/// - No per-message allocs
/// - Dedicated RX thread with batched draining
/// - Fixed back-buffer ring to avoid overwrite
/// </summary>
public sealed class InprocParticle : IParticle, IDisposable
{
    #region Fields

    private readonly string _name;
    private readonly bool _isServer;
    private readonly int _ringCapacity;

    private InprocLink? _link;

    // RX thread
    private Thread? _rxThread;
    private volatile bool _running;

    // Fixed back-buffer ring (avoids ArrayPool churn & reuse hazards)
    private readonly byte[][] _buffers;
    private int _bufIndex;

    // Tunables
    private readonly int _bufSize;
    private readonly int _bufferCount;
    private readonly int _batchMax;

    private volatile bool _isDisposed;

    #endregion

    #region Events

    public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
    public Action<IParticle>? OnDisconnected { get; set; }
    public Action<IParticle>? OnConnected { get; set; }

    #endregion

    #region Ctor

    public InprocParticle(
        string name,
        bool isServer,
        int ringCapacity = 4096,
        int backBufferCount = 8,
        int bufferSize = 64 * 1024,
        int batchMax = 32,
        ThreadPriority rxThreadPriority = ThreadPriority.Highest)
    {
        if (backBufferCount < 2) backBufferCount = 2;

        _name = name ?? throw new ArgumentNullException(nameof(name));
        _isServer = isServer;
        _ringCapacity = ringCapacity;

        _bufferCount = backBufferCount;
        _bufSize = bufferSize;
        _batchMax = batchMax;

        _buffers = new byte[_bufferCount][];
        for (int i = 0; i < _bufferCount; i++)
            _buffers[i] = new byte[_bufSize];

        _rxThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"{(_isServer ? "inproc-srv" : "inproc-cli")}-rx:{_name}",
            Priority = rxThreadPriority
        };
    }

    #endregion

    #region Link

    internal void AttachLink(InprocLink link)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(InprocParticle));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        StartRx();
        OnConnected?.Invoke(this);
    }

    #endregion

    #region Start/Stop

    public void Start()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(InprocParticle));

        if (!_isServer)
        {
            var link = InprocRegistry.Connect(_name, _ringCapacity);
            _link = link;
        }

        StartRx();
        OnConnected?.Invoke(this);
    }

    private void StartRx()
    {
        if (_running) return;
        _running = true;
        _rxThread?.Start();
    }

    #endregion

    #region Send

    public void SendF(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;
        if (_isDisposed) throw new ObjectDisposedException(nameof(InprocParticle));
        var link = _link ?? throw new InvalidOperationException("Not connected.");

        var ring = _isServer ? link.ToClient : link.ToServer;
        int spinExp = 1;

        while (!ring.TryEnqueue(payload))
        {
            Thread.SpinWait(spinExp);
            spinExp = spinExp < 4096 ? (spinExp << 1) : 4096;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return;
        if (_isDisposed) throw new ObjectDisposedException(nameof(InprocParticle));
        var link = _link ?? throw new InvalidOperationException("Not connected.");

        var ring = _isServer ? link.ToClient : link.ToServer;

        // Fallback: rent a temporary array since ReadOnlySpan -> ReadOnlyMemory can't be zero-copy.
        byte[] rented = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            payload.CopyTo(rented);
            var memory = new ReadOnlyMemory<byte>(rented, 0, payload.Length);

            int spinExp = 1;
            while (!ring.TryEnqueue(memory))
            {
                Thread.SpinWait(spinExp);
                spinExp = spinExp < 4096 ? (spinExp << 1) : 4096;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
    {
        Send(payload.Span);
        return TaskCompat.CompletedValueTask;
    }

    #endregion

    #region RX loop

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
            if (link is null) return;

            var inbound = _isServer ? link.ToServer : link.ToClient;
            int spinExp = 1;

            while (_running)
            {
                int delivered = 0;

                while (delivered < _batchMax)
                {
                    var idx = _bufIndex;
                    _bufIndex = (idx + 1) % _bufferCount;
                    var buf = _buffers[idx];

                    if (!inbound.TryDequeue(out var payload))
                        break;

                    OnReceived?.Invoke(this, payload);
                    delivered++;
                }

                if (delivered > 0)
                {
                    spinExp = 1;
                    continue;
                }

                Thread.SpinWait(spinExp);
                if (spinExp < (1 << 12)) spinExp <<= 1;
            }
        }
        catch
        {
            // keep teardown minimal
        }
        finally
        {
            try { OnDisconnected?.Invoke(this); } catch { }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _running = false;

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