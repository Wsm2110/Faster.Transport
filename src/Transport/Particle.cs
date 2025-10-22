using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// High-performance framed TCP client using <see cref="SocketAsyncEventArgs"/> for fully asynchronous,
/// zero-copy I/O and a concurrent buffer pool for memory reuse.
/// </summary>
/// <remarks>
/// <para>
/// Each message is sent as a 4-byte little-endian length prefix followed by the payload bytes:
/// <code>[length:int32][payload]</code>.
/// </para>
/// <para>
/// This client is designed for ultra-low latency and high throughput in real-time applications
/// such as telemetry, distributed simulations, or trading engines.
/// It is thread-safe for concurrent sends from multiple threads.
/// </para>
/// </remarks>
public sealed class Particle : IParticle
{
    private readonly Socket _socket;
    private readonly FrameParser _parser;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBufferManager _bufferManager;
    private readonly SocketAsyncEventArgs _recvArgs;
    private readonly SocketAsyncEventArgs _sendArgs;
    private volatile bool _isDisposed;

    /// <summary>
    /// Exception message shown when a payload exceeds the configured buffer slice size.
    /// </summary>
    private const string PayloadTooLargeMessage =
        "Payload size exceeds the configured buffer slice size. " +
        "To fix this, either increase the buffer slice size in ConcurrentBufferManager " +
        "or implement payload chunking for large messages.";

    /// <summary>
    /// Triggered when a complete frame (payload only, without the 4-byte header) is received.
    /// </summary>
    public Action<ReadOnlyMemory<byte>>? OnReceived { get; set; }

    /// <summary>
    /// Triggered when the socket disconnects or an unrecoverable error occurs.
    /// </summary>
    public Action<IParticle, Exception?>? Disconnected { get; set; }

    /// <summary>
    /// Optional event triggered after disconnection; allows Reactor to remove this client.
    /// </summary>
    public Action<Particle>? OnDisconnected { get; set; }

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="Particle"/> and connects synchronously to the specified endpoint.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="bufferSize">Size of the per-operation buffer (default 8192 bytes).</param>
    /// <param name="maxDegreeOfParallelism">Degree of concurrency for buffer pool sizing.</param>
    public Particle(EndPoint remoteEndPoint, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
        : this(CreateAndConnect(remoteEndPoint), bufferSize, maxDegreeOfParallelism)
    {
    }

    /// <summary>
    /// Creates a new <see cref="Particle"/> using an existing connected <see cref="Socket"/>.
    /// </summary>
    public Particle(Socket connectedSocket, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
    {
        _socket = connectedSocket ?? throw new ArgumentNullException(nameof(connectedSocket));
        _socket.NoDelay = true;
        _socket.ReceiveBufferSize = 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;

        var sliceSize = bufferSize;
        var sweetSpot = maxDegreeOfParallelism;
        var totalBufferLength = sliceSize * sweetSpot;

        _bufferManager = new ConcurrentBufferManager(sliceSize, totalBufferLength);
        _parser = new FrameParser(totalBufferLength);

        // Wire frame parser callbacks
        _parser.OnFrame = payload => OnReceived?.Invoke(payload);
        _parser.OnError = ex => Close(ex);

        // Create async event args for send/receive
        _sendArgs = CreateSocketAsyncEventArgs();
        _recvArgs = CreateSocketAsyncEventArgs();

        // Begin receive loop
        StartReceive();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Creates and connects a new <see cref="Socket"/> to the specified endpoint.
    /// </summary>
    private static Socket CreateAndConnect(EndPoint ep)
    {
        var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(ep);
        socket.NoDelay = true;
        return socket;
    }

    /// <summary>
    /// Initializes and configures a <see cref="SocketAsyncEventArgs"/> with an allocated buffer.
    /// </summary>
    private SocketAsyncEventArgs CreateSocketAsyncEventArgs()
    {
        var args = new SocketAsyncEventArgs();
        args.Completed += IOCompleted;
        _bufferManager.TrySetBuffer(args);
        args.UserToken = args.MemoryBuffer;
        return args;
    }

    #endregion

    #region Sending

    /// <summary>
    /// Sends a framed payload asynchronously.
    /// The frame layout is: [4-byte length prefix][payload].
    /// </summary>
    /// <param name="payload">The payload to send (payload length must fit in an Int32).</param>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
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

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        int totalLen = 4 + payload.Length;
        var memory = _sendArgs.MemoryBuffer.Slice(0, totalLen);

        // ✅ Write 4-byte little-endian length prefix
        BinaryPrimitives.WriteInt32LittleEndian(memory.Span.Slice(0, 4), payload.Length);

        // ✅ Copy payload bytes
        payload.Span.CopyTo(memory.Span.Slice(4));
         
        // ✅ Set current buffer window
        _sendArgs.SetBuffer(memory);

        // Start async send; if completed sync, process inline
        if (!_socket.SendAsync(_sendArgs))
            ProcessSend(_sendArgs);

        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Handles send completion for both async and synchronous paths.
    /// Restores the buffer and completes the awaiting <see cref="TaskCompletionSource{TResult}"/>.
    /// </summary>
    private void ProcessSend(SocketAsyncEventArgs e)
    {
        var token = (SendToken)e.UserToken;

        // Restore full buffer for next send
        e.SetBuffer(token.OriginalBuffer);

        if (e.SocketError != SocketError.Success)
        {
            var ex = new SocketException((int)e.SocketError);
            token.Tcs.TrySetException(ex);
            Close(ex);
            return;
        }

        token.Tcs.TrySetResult(true);
    }

    #endregion

    #region Receiving

    /// <summary>
    /// Starts the continuous receive loop using async event args.
    /// </summary>
    private void StartReceive()
    {
        if (!_socket.ReceiveAsync(_recvArgs))
            ProcessReceive(_recvArgs);
    }

    /// <summary>
    /// Handles received data and feeds it into the <see cref="FrameParser"/>.
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
    /// Shared I/O completion handler for send/receive operations.
    /// </summary>
    private void IOCompleted(object? sender, SocketAsyncEventArgs e)
    {
        switch (e.LastOperation)
        {
            case SocketAsyncOperation.Send:
                ProcessSend(e);
                break;
            case SocketAsyncOperation.Receive:
                ProcessReceive(e);
                break;
        }
    }

    #endregion

    #region Teardown

    /// <summary>
    /// Closes the connection and releases all associated resources.
    /// </summary>
    private void Close(Exception? ex = null)
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try { _cts.Cancel(); } catch { }

        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch { /* ignored */ }

        try { _socket?.Dispose(); } catch { }
        try { _recvArgs.Dispose(); } catch { }

        try { Disconnected?.Invoke(ex); } catch { }
        try { OnDisconnected?.Invoke(this); } catch { }
    }

    /// <summary>
    /// Disposes the client and all underlying unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        Close();
        _parser.Dispose();
        _cts.Dispose();
    }

    #endregion

    #region Internal Helper Types

    /// <summary>
    /// Token structure that links an in-flight send operation to its <see cref="TaskCompletionSource{TResult}"/>.
    /// </summary>
    public sealed class SendToken
    {
        public TaskCompletionSource<bool> Tcs { get; }
        public Memory<byte> OriginalBuffer { get; }

        public SendToken(TaskCompletionSource<bool> tcs, Memory<byte> originalBuffer)
        {
            Tcs = tcs;
            OriginalBuffer = originalBuffer;
        }
    }

    #endregion
}
