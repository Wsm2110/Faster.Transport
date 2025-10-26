using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Faster.Transport.Features.Tcp;

/// <summary>
/// High-performance framed TCP transport supporting both synchronous and asynchronous sending.
/// </summary>
/// <remarks>
/// Each message is sent with a 4-byte little-endian length prefix followed by the payload.
/// The class is designed for **ultra-low-latency communication**, suitable for telemetry,
/// trading, simulation, and real-time systems requiring efficient message framing.
/// </remarks>
public sealed class Particle : IParticle
{
    private readonly Socket _socket;
    private readonly FrameParser _parser;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBufferManager _bufferManager;
    private readonly SocketAsyncEventArgsPool _sendArgsPool;
    private readonly SocketAsyncEventArgs _recvArgs;
    private volatile bool _isDisposed;

    /// <summary>
    /// Message shown when the payload exceeds the configured buffer slice size.
    /// </summary>
    private const string PayloadTooLargeMessage =
        "Payload size exceeds the configured buffer slice size. " +
        "To fix this, either increase the buffer slice size in ConcurrentBufferManager " +
        "or implement payload chunking for large messages.";

    /// <summary>
    /// Event triggered when a complete frame is received.
    /// </summary>
    public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

    /// <summary>
    /// Event triggered when the socket disconnects or an unrecoverable error occurs.
    /// </summary>
    public Action<IParticle>? OnDisconnected { get; set; }

    /// <summary>
    /// Triggered once the server and client are connected and ready.
    /// </summary>
    public Action<IParticle>? OnConnected { get; set; }


    /// <summary>
    /// Initializes and connects a new <see cref="Particle"/> client to the specified remote endpoint.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="bufferSize">Per-operation buffer size in bytes (default 8192).</param>
    /// <param name="maxDegreeOfParallelism">Parallelism level for send buffer pooling (default 8).</param>
    public Particle(EndPoint remoteEndPoint, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
        : this(CreateAndConnect(remoteEndPoint), bufferSize, maxDegreeOfParallelism)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="Particle"/> instance using an already connected socket.
    /// </summary>
    /// <param name="connectedSocket">An active, connected <see cref="Socket"/>.</param>
    /// <param name="bufferSize">Per-operation buffer size in bytes.</param>
    /// <param name="maxDegreeOfParallelism">Parallelism level for send buffer pooling.</param>
    public Particle(Socket connectedSocket, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
    {
        _socket = connectedSocket ?? throw new ArgumentNullException(nameof(connectedSocket));
        _socket.NoDelay = true;
        _socket.ReceiveBufferSize = 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;

        var sweetspot = maxDegreeOfParallelism * 2;
        var length = bufferSize * sweetspot;

        _bufferManager = new ConcurrentBufferManager(bufferSize, length);

        // Frame parser handles message framing (4-byte length prefix)
        _parser = new(length)
        {
            OnFrame = payload => OnReceived?.Invoke(this, payload),
            OnError = ex => Close(ex)
        };

        _sendArgsPool = new SocketAsyncEventArgsPool(1024);

        // Preallocate a pool of reusable SocketAsyncEventArgs objects for sending
        for (int i = 0; i < sweetspot; i++)
        {
            var args = CreateSocketAsyncEventArgs(SendIOCompleted);
            _sendArgsPool.Add(args);
        }

        // Allocate receive args and start listening
        _recvArgs = CreateSocketAsyncEventArgs(ReceiveIOCompleted);
        StartReceive();        
        OnConnected?.Invoke(this);
    }

    /// <summary>
    /// Creates and connects a new TCP socket to the specified endpoint.
    /// </summary>
    /// <param name="ep">The target endpoint.</param>
    /// <returns>A connected <see cref="Socket"/>.</returns>
    private static Socket CreateAndConnect(EndPoint ep)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        s.Connect(ep);        
        s.NoDelay = true;
        return s;
    }

    /// <summary>
    /// Creates and initializes a <see cref="SocketAsyncEventArgs"/> with a preallocated buffer and completion handler.
    /// </summary>
    /// <param name="completed">The event handler for I/O completion.</param>
    private SocketAsyncEventArgs CreateSocketAsyncEventArgs(EventHandler<SocketAsyncEventArgs> completed)
    {
        var args = new SocketAsyncEventArgs();
        args.Completed += completed;
        _bufferManager.TrySetBuffer(args);
        return args;
    }

    // ============================================================
    //  SYNC SEND
    // ============================================================

    /// <summary>
    /// Sends a framed message synchronously (fire-and-forget).
    /// </summary>
    /// <param name="payload">The payload to send.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<byte> payload)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(Particle));

        if (payload.Length > _bufferManager.SliceSize)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"{PayloadTooLargeMessage} (Payload: {payload.Length} bytes, SliceSize: {_bufferManager.SliceSize} bytes)");

        _sendArgsPool.TryRent(out var sendArgs);

        int totalLen = 4 + payload.Length;
        var memory = sendArgs.MemoryBuffer.Slice(0, totalLen);

        // Write 4-byte length prefix
        BinaryPrimitives.WriteInt32LittleEndian(memory.Span[..4], payload.Length);
        payload.CopyTo(memory.Span[4..]);

        sendArgs.UserToken = sendArgs.MemoryBuffer;
        sendArgs.SetBuffer(memory);

        // Start async send; complete inline if already finished
        if (!_socket.SendAsync(sendArgs))
            ProcessSend(sendArgs, null);
    }

    // ============================================================
    //  ASYNC SEND
    // ============================================================

    /// <summary>
    /// Sends a framed message asynchronously, returning once the send operation completes.
    /// </summary>
    /// <param name="payload">The payload to send.</param>
    /// <returns>A task that completes when the send operation is finished.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(Particle));

        if (payload.Length > _bufferManager.SliceSize)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"{PayloadTooLargeMessage} (Payload: {payload.Length} bytes, SliceSize: {_bufferManager.SliceSize} bytes)");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendArgsPool.TryRent(out var sendArgs);

        int totalLen = 4 + payload.Length;
        var memory = sendArgs.MemoryBuffer.Slice(0, totalLen);

        // Write 4-byte length prefix
        BinaryPrimitives.WriteInt32LittleEndian(memory.Span[..4], payload.Length);
        payload.Span.CopyTo(memory.Span[4..]);

        sendArgs.UserToken = new SendToken(tcs, sendArgs.MemoryBuffer);
        sendArgs.SetBuffer(memory);

        if (!_socket.SendAsync(sendArgs))
            ProcessSend(sendArgs, tcs);

        await tcs.Task.ConfigureAwait(false);
    }

    // ============================================================
    //  RECEIVE LOOP
    // ============================================================

    /// <summary>
    /// Starts the asynchronous receive loop.
    /// </summary>
    private void StartReceive()
    {
        if (!_socket.ReceiveAsync(_recvArgs))
            ProcessReceive(_recvArgs);
    }

    /// <summary>
    /// Processes received data and feeds it into the frame parser.
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

            // Feed received bytes into the frame parser
            if (!_parser.Feed(e.MemoryBuffer.Slice(0, e.BytesTransferred).Span))
            {
                Close(new IOException("Parser overflow or backpressure limit reached."));
                return;
            }
        }
        while (!_socket.ReceiveAsync(e));
    }

    // ============================================================
    //  COMPLETION HANDLERS
    // ============================================================

    /// <summary>
    /// Handles send operation completion (both sync and async paths).
    /// </summary>
    private void SendIOCompleted(object? _, SocketAsyncEventArgs e)
    {
        var token = e.UserToken as SendToken;
        ProcessSend(e, token?.Tcs);
    }

    /// <summary>
    /// Handles receive operation completion.
    /// </summary>
    private void ReceiveIOCompleted(object? _, SocketAsyncEventArgs e)
    {
        ProcessReceive(e);
    }

    /// <summary>
    /// Processes the completion of a send operation, returning buffers and signaling completion.
    /// </summary>
    private void ProcessSend(SocketAsyncEventArgs e, TaskCompletionSource<bool>? tcs)
    {
        var originalBuffer = e.UserToken is SendToken token ? token.OriginalBuffer : (Memory<byte>)e.UserToken;
        e.SetBuffer(originalBuffer);

        if (e.SocketError != SocketError.Success)
        {
            var ex = new SocketException((int)e.SocketError);
            tcs?.TrySetException(ex);
            _sendArgsPool.Return(e);
            Close(ex);
            return;
        }

        _sendArgsPool.Return(e);
        tcs?.TrySetResult(true);
    }

    // ============================================================
    //  CLEANUP AND DISPOSAL
    // ============================================================

    /// <summary>
    /// Closes the connection and releases all resources.
    /// </summary>
    private void Close(Exception? ex = null)
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _recvArgs.Dispose(); } catch { }

        OnDisconnected?.Invoke(this);
    }

    /// <summary>
    /// Disposes the client and releases all associated resources.
    /// </summary>
    public void Dispose()
    {
        Close();
        _parser.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Internal metadata used to associate a TaskCompletionSource with a send buffer.
    /// </summary>
    private sealed record SendToken(TaskCompletionSource<bool> Tcs, Memory<byte> OriginalBuffer);
}
