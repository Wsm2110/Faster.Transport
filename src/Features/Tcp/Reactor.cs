using Faster.Transport.Primitives;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Faster.Transport.Features.Tcp;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// A high-performance asynchronous TCP server that accepts multiple clients
/// and wraps each connection in a <see cref="Connection"/> for framed message I/O.
/// </summary>
/// <remarks>
/// This class uses <see cref="SocketAsyncEventArgs"/> for non-blocking accepts
/// and a thread-safe <see cref="ConcurrentDictionary{TKey, TValue}"/> to manage connected clients.
/// 
/// It is optimized for low-latency systems that require zero allocations per message
/// and efficient cleanup on disconnect. Typical applications include telemetry, 
/// multiplayer game servers, and real-time distributed systems.
/// </remarks>
public sealed class Reactor : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Connection> _clients = new();
    private readonly int _bufferSize;
    private readonly int _maxDegreeParralelism;
    private SocketAsyncEventArgs? _acceptArgs;
    private int _clientCounter;
    private bool _isRunning;

    /// <summary>
    /// Triggered when a complete frame is received from any connected client.
    /// </summary>
    public Action<Connection, ReadOnlyMemory<byte>>? OnReceived { get; set; }

    public Action<Connection>? OnConnected { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Reactor"/> class and binds it to the specified endpoint.
    /// </summary>
    /// <param name="bindEndPoint">The network endpoint to bind to.</param>
    /// <param name="backlog">The maximum number of pending connections (default = 1024).</param>
    /// <param name="maxDegreeParralelism">Optional parameter for future connection scaling logic.</param>
    public Reactor(EndPoint bindEndPoint, int backlog = 1024, int bufferSize = 8192, int maxDegreeParralelism = 8)
    {
        _listener = new Socket(bindEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true // Disable Nagle's algorithm for ultra-low-latency I/O
        };

        _listener.Bind(bindEndPoint);
        _listener.Listen(backlog);
        _bufferSize = bufferSize;
        _maxDegreeParralelism = maxDegreeParralelism;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Reactor"/> class and binds it to the specified endpoint.
    /// </summary>
    /// <param name="bindEndPoint">The network endpoint to bind to.</param>
    /// <param name="backlog">The maximum number of pending connections (default = 1024).</param>
    /// <param name="maxDegreeParralelism">Optional parameter for future connection scaling logic.</param>
    public Reactor(Socket socket, int backlog = 1024, int bufferSize = 8192, int maxDegreeParralelism = 8)
    {
        _listener = socket ?? throw new ArgumentNullException(nameof(socket));

        _listener.Bind(socket.LocalEndPoint!);

        _listener.Listen(backlog);
        _bufferSize = bufferSize;
        _maxDegreeParralelism = maxDegreeParralelism;
    }

    /// <summary>
    /// Starts the reactor and begins accepting incoming TCP connections asynchronously.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the reactor is already running.</exception>
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("Server is already running.");

        _isRunning = true;

        // Allocate reusable accept event args
        _acceptArgs = new SocketAsyncEventArgs();
        _acceptArgs.Completed += AcceptCompleted;

        // Kick off the first asynchronous accept operation
        AcceptNext(_acceptArgs);
    }

    /// <summary>
    /// Begins the next asynchronous accept operation using the reusable event args.
    /// </summary>
    private void AcceptNext(SocketAsyncEventArgs e)
    {
        while (_isRunning && !_cts.IsCancellationRequested)
        {
            e.AcceptSocket = null;

            try
            {
                // Returns true if pending, false if completed synchronously
                if (_listener.AcceptAsync(e))
                    return;

                ProcessAccept(e);
            }
            catch (ObjectDisposedException)
            {
                e.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Reactor] Accept failed: {ex.Message}");
                Thread.Sleep(100); // Avoid tight loop on transient errors
            }
        }

        e.Dispose();
    }

    /// <summary>
    /// Callback for completion of asynchronous accept operations.
    /// </summary>
    private void AcceptCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (!_isRunning || _cts.IsCancellationRequested)
        {
            e.Dispose();
            return;
        }

        ProcessAccept(e);
    }

    /// <summary>
    /// Processes a successfully accepted connection and attaches event handlers.
    /// </summary>
    private void ProcessAccept(SocketAsyncEventArgs e)
    {
        if (!_isRunning || _cts.IsCancellationRequested)
        {
            e.Dispose();
            return;
        }

        // Validate accept result
        if (e.SocketError != SocketError.Success || e.AcceptSocket == null)
        {
            try { e.AcceptSocket?.Close(); } catch { }
            AcceptNext(e);
            return;
        }

        var socket = e.AcceptSocket;
        var clientId = Interlocked.Increment(ref _clientCounter);
        var connection = new Connection(socket, bufferSize: _bufferSize, maxDegreeOfParallelism: _maxDegreeParralelism);

        // Wire up client events
        connection.OnReceived = OnReceived;
        connection.OnDisconnected = conn =>
        {
            _clients.TryRemove(clientId, out _);
        };

        // Add client to active connection dictionary
        _clients[clientId] = connection;

        // Notify subscribers of client
        OnConnected?.Invoke(connection);

        // Continue accepting more clients
        AcceptNext(e);
    }

    /// <summary>
    /// Stops the reactor gracefully, closes the listener socket,
    /// and disposes all connected clients.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts.Cancel();

        try { _listener.Close(); } catch { }
        try { _acceptArgs?.Dispose(); } catch { }

        foreach (var kv in _clients)
        {
            try { kv.Value.Dispose(); } catch { }
        }

        _clients.Clear();
    }

    /// <summary>
    /// Releases all resources including sockets, clients, and cancellation tokens.
    /// </summary>
    public void Dispose()
    {
        Stop();

        try { _listener.Dispose(); } catch { }
        try { _acceptArgs?.Dispose(); } catch { }

        _cts.Dispose();
    }
}

/// <summary>
/// Represents a single framed TCP connection managed by a <see cref="Reactor"/>.
/// Each connection supports asynchronous, zero-copy I/O using <see cref="SocketAsyncEventArgs"/>.
/// </summary>
/// <remarks>
/// Frames follow a simple binary protocol: 
///   [4-byte little-endian length prefix][payload bytes].
/// 
/// This class is designed for low-latency event-driven systems (trading, telemetry, etc.)
/// and supports multiple concurrent senders using a lock-free <see cref="SocketAsyncEventArgsPool"/>.
/// </remarks>
public sealed class Connection : IDisposable
{
    private readonly Socket _socket;
    private readonly FrameParser _parser;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBufferManager _bufferManager;
    private readonly SocketAsyncEventArgs _recvArgs;
    private readonly SocketAsyncEventArgsPool _sendArgsPool;
    private volatile bool _isDisposed;

    /// <summary>
    /// Triggered when a complete frame is received and parsed successfully.
    /// </summary>
    public Action<Connection, ReadOnlyMemory<byte>>? OnReceived { get; set; }

    /// <summary>
    /// Triggered when the connection is closed, either normally or due to an error.
    /// </summary>
    public Action<Connection>? OnDisconnected { get; set; }

    /// <summary>
    /// Creates a new <see cref="Connection"/> using an already connected <see cref="Socket"/>.
    /// </summary>
    /// <param name="connectedSocket">The connected socket instance.</param>
    /// <param name="bufferSize">Default buffer size for send/receive operations.</param>
    /// <param name="maxDegreeOfParallelism">Number of concurrent send operations supported.</param>
    public Connection(Socket connectedSocket, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
    {
        _socket = connectedSocket ?? throw new ArgumentNullException(nameof(connectedSocket));
        _socket.NoDelay = true;
        _socket.ReceiveBufferSize = 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;

        int sweetspot = maxDegreeOfParallelism * 2;
        int totalSize = bufferSize * sweetspot;

        _sendArgsPool = new SocketAsyncEventArgsPool(sweetspot);
        _bufferManager = new ConcurrentBufferManager(bufferSize, totalSize);

        _recvArgs = CreateSocketAsyncEventArgs();
        _parser = new FrameParser(totalSize);

        _parser.OnFrame = payload => OnReceived?.Invoke(this, payload);
        _parser.OnError = ex => Close(ex); // handle parser overflow, desync, or corruption

        // Pre-allocate reusable send event args
        for (int i = 0; i < sweetspot; i++)
        {
            SocketAsyncEventArgs args = new();
            _bufferManager.TrySetBuffer(args);
            args.Completed += IOCompleted;
            args.UserToken = args.MemoryBuffer;
            _sendArgsPool.Add(args);
        }

        StartReceive();
    }

    /// <summary>
    /// Creates and initializes a reusable <see cref="SocketAsyncEventArgs"/> for receive operations.
    /// </summary>
    private SocketAsyncEventArgs CreateSocketAsyncEventArgs()
    {
        var args = new SocketAsyncEventArgs();
        args.Completed += IOCompleted;
        _bufferManager.TrySetBuffer(args);
        args.UserToken = args.MemoryBuffer;
        return args;
    }

    /// <summary>
    /// Sends a payload asynchronously with a 4-byte little-endian length prefix.
    /// </summary>
    /// <param name="payload">The message to send.</param>
    /// <returns>True if successfully queued for sending, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Return(ReadOnlySpan<byte> payload)
    {
        if (_isDisposed || payload.IsEmpty)
            return false;

        var length = payload.Length;

        // Guard: check payload fits in available buffer slice
        if (length + 4 > _bufferManager.SliceSize)
        {
            Console.WriteLine(
                $"[WARN] Payload size {length} bytes exceeds slice size {_bufferManager.SliceSize}. " +
                $"Increase buffer size or implement chunking.");
            return false;
        }

        // Rent a SocketAsyncEventArgs instance for send
        _sendArgsPool.TryRent(out SocketAsyncEventArgs sendArgs);

        // Prepare buffer [0..4+len)
        var buffer = sendArgs.MemoryBuffer.Slice(0, 4 + length);

        // Write 4-byte header
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span.Slice(0, 4), length);

        // Copy payload
        payload.CopyTo(buffer.Span.Slice(4));

        // Set buffer and send
        sendArgs.SetBuffer(buffer);

        if (!_socket.SendAsync(sendArgs))
            ProcessSend(sendArgs);

        return true;
    }

    /// <summary>
    /// Handles send completion for both sync and async paths.
    /// </summary>
    private void ProcessSend(SocketAsyncEventArgs e)
    {
        // Restore original buffer memory
        e.SetBuffer((Memory<byte>)e.UserToken);

        if (e.SocketError != SocketError.Success)
        {
            _sendArgsPool.Return(e);
            Close(new SocketException((int)e.SocketError));
            return;
        }

        _sendArgsPool.Return(e);
    }

    /// <summary>
    /// Begins asynchronous receive loop.
    /// </summary>
    private void StartReceive()
    {
        if (!_socket.ReceiveAsync(_recvArgs))
        {
            ProcessReceive(_recvArgs);
        }
    }

    /// <summary>
    /// Processes incoming data, forwarding complete frames to the <see cref="FrameParser"/>.
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

            if (!_parser.Feed(e.MemoryBuffer.Slice(0, e.BytesTransferred).Span))
            {
                Close(new IOException("Parser buffer overflow or invalid frame sequence."));
                return;
            }
        }
        while (!_socket.ReceiveAsync(e));
    }

    /// <summary>
    /// Unified I/O completion event for both send and receive operations.
    /// </summary>
    private void IOCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (e.LastOperation == SocketAsyncOperation.Send)
            ProcessSend(e);
        else if (e.LastOperation == SocketAsyncOperation.Receive)
            ProcessReceive(e);
    }

    /// <summary>
    /// Closes the connection and releases all associated resources.
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

        try { OnDisconnected?.Invoke(this); } catch { }
    }

    /// <summary>
    /// Disposes the connection and all managed resources.
    /// </summary>
    public void Dispose()
    {
        Close();
        _cts.Dispose();
        _parser.Dispose();
    }
}

