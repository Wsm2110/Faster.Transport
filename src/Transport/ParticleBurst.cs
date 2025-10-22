using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Faster.Transport;

/// <summary>
/// High-performance framed TCP client using <see cref="SocketAsyncEventArgs"/> and a lock-free MPSC queue for sending.
/// </summary>
/// <remarks>
/// Each message is sent with a 4-byte little-endian length prefix followed by the payload.
/// The class is optimized for ultra-low-latency systems (e.g., trading, telemetry, or gaming) with multiple producer threads.
/// </remarks>
public sealed class ParticleBurst : IParticleBurst
{
    private readonly Socket _socket;
    private readonly FrameParser _parser;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBufferManager _bufferManager;
    private readonly SocketAsyncEventArgsPool _sendArgsPool;
    private readonly SocketAsyncEventArgs _recvArgs;
    private volatile bool _isDisposed;

    /// <summary>
    /// Exception message shown when a payload exceeds the configured buffer slice size.
    /// </summary>
    private const string PayloadTooLargeMessage =
        "Payload size exceeds the configured buffer slice size. " +
        "To fix this, either increase the buffer slice size in ConcurrentBufferManager " +
        "or implement payload chunking for large messages.";

    /// <summary>
    /// Triggered when a complete frame is received.
    /// </summary>
    public Action<ReadOnlyMemory<byte>>? OnReceived { get; set; }

    /// <summary>
    /// Triggered when the socket disconnects or an error occurs.
    /// </summary>
    public Action<ParticleBurst, Exception?>? Disconnected { get; set; }

    /// <summary>
    /// Creates a new <see cref="FasterClient"/> and connects synchronously to the specified endpoint.
    /// </summary>
    public ParticleBurst(EndPoint remoteEndPoint, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
        : this(CreateAndConnect(remoteEndPoint), bufferSize, maxDegreeOfParallelism)
    {
    }

    /// <summary>
    /// Creates a new <see cref="FasterClient"/> using an already connected <see cref="Socket"/>.
    /// </summary>
    public ParticleBurst(Socket connectedSocket, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
    {
        _socket = connectedSocket ?? throw new ArgumentNullException(nameof(connectedSocket));
        _socket.NoDelay = true;
        _socket.ReceiveBufferSize = 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;

        var sweetspot = maxDegreeOfParallelism * 2;
        var length = (bufferSize * sweetspot);

        _bufferManager = new ConcurrentBufferManager(bufferSize, length);
        _parser = new(length);

        // Wire frame parser callbacks
        _parser.OnFrame = payload => OnReceived?.Invoke(payload);
        _parser.OnError = ex => Close(ex);

        _sendArgsPool = new SocketAsyncEventArgsPool(1024);

        for (int i = 0; i < sweetspot; i++)
        {
            var args = CreateSocketAsyncEventArgs(SendIOCompleted);
            _sendArgsPool.Add(args);
        }

        _recvArgs = CreateSocketAsyncEventArgs(RetrieveIOCompleted);

        StartReceive();
    }

    /// <summary>
    /// Creates and connects a new <see cref="Socket"/> to the provided endpoint.
    /// </summary>
    private static Socket CreateAndConnect(EndPoint ep)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        s.Connect(ep);
        s.NoDelay = true;
        return s;
    }

    /// <summary>
    /// Initializes and configures a <see cref="SocketAsyncEventArgs"/> with an allocated buffer.
    /// </summary>
    private SocketAsyncEventArgs CreateSocketAsyncEventArgs(EventHandler<SocketAsyncEventArgs> Completed)
    {
        var args = new SocketAsyncEventArgs();
        args.Completed += Completed;
        _bufferManager.TrySetBuffer(args);
        return args;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlyMemory<byte> payload)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(Particle));
        }
        // ✅ Guard: prevent payload larger than slice size
        if (payload.Length > _bufferManager.SliceSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"{PayloadTooLargeMessage} (Payload: {payload.Length} bytes, SliceSize: {_bufferManager.SliceSize} bytes)");
        }

        _sendArgsPool.TryRent(out SocketAsyncEventArgs sendArgs);

        int totalLen = 4 + payload.Length;
        var memory = sendArgs.MemoryBuffer.Slice(0, totalLen);

        // ✅ Write 4-byte little-endian length prefix
        BinaryPrimitives.WriteInt32LittleEndian(memory.Span.Slice(0, 4), payload.Length);

        // ✅ Copy payload bytes
        payload.Span.CopyTo(memory.Span.Slice(4));

        // Attach metadata (TCS + original buffer for restore)
        sendArgs.UserToken = sendArgs.MemoryBuffer;

        // ✅ Set current buffer window
        sendArgs.SetBuffer(memory);

        // Start async send; if completed sync, process inline
        if (!_socket.SendAsync(sendArgs))
            ProcessSend(sendArgs);
    }

    /// <summary>
    /// Handles send completion for both async and synchronous paths.
    /// Restores the buffer and completes the awaiting <see cref="TaskCompletionSource{TResult}"/>.
    /// </summary>
    private void ProcessSend(SocketAsyncEventArgs e)
    {
        var originalBuffer = (Memory<byte>)e.UserToken;

        // Restore full buffer for next send
        e.SetBuffer(originalBuffer);

        if (e.SocketError != SocketError.Success)
        {
            var ex = new SocketException((int)e.SocketError);
            _sendArgsPool.Return(e);
            Close(ex);
            return;
        }

        _sendArgsPool.Return(e);
    }

    /// <summary>
    /// Starts asynchronous receiving.
    /// </summary>
    private void StartReceive()
    {
        if (!_socket.ReceiveAsync(_recvArgs))
        {
            ProcessReceive(_recvArgs);
        }
    }

    /// <summary>
    /// Handles incoming data and passes complete frames to the parser.
    /// </summary>
    private void ProcessReceive(SocketAsyncEventArgs e)
    {
        do
        {
            if (e.SocketError != SocketError.Success || e.BytesTransferred == 0)
            {
                Close(e.SocketError == SocketError.Success ? null : new SocketException((int)e.SocketError));
                return;
            }

            // Feed data into frame parser; if buffer overflow, close gracefully
            if (!_parser.Feed(e.MemoryBuffer.Slice(0, e.BytesTransferred).Span))
            {
                Close(new IOException("Parser overflow or backpressure limit reached."));
                return;
            }
        }
        while (!_socket.ReceiveAsync(e)); // Loop until async pending
    }

    /// <summary>
    /// Unified I/O completion callback.
    /// </summary>
    private void SendIOCompleted(object? sender, SocketAsyncEventArgs e)
    {
        ProcessSend(e);
    }

    private void RetrieveIOCompleted(object? sender, SocketAsyncEventArgs e)
    {
        ProcessReceive(e);
    }

    /// <summary>
    /// Closes the client and cleans up resources.
    /// </summary>
    private void Close(Exception? ex = null)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _recvArgs.Dispose(); } catch { }

        Disconnected?.Invoke(this, ex);
    }

    /// <summary>
    /// Disposes the client and all its resources.
    /// </summary>
    public void Dispose()
    {
        Close();
        _parser.Dispose();
        _cts.Dispose();
    }
}
