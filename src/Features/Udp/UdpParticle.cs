using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport.Features.Udp
{
    /// <summary>
    /// High-performance UDP transport supporting unicast, multicast, and broadcast modes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="UdpParticle"/> class implements the <see cref="IParticle"/> interface using UDP sockets.
    /// It supports **send** and **receive** operations on the same socket, minimizing memory allocations.
    /// </para>
    /// <para>
    /// ✅ **Key Features:**
    /// <list type="bullet">
    /// <item><description>Full-duplex I/O using a single socket.</description></item>
    /// <item><description>Supports unicast, multicast, and broadcast modes.</description></item>
    /// <item><description>Uses pooled <see cref="SocketAsyncEventArgs"/> for zero-GC async sends.</description></item>
    /// <item><description>Employs <see cref="ConcurrentBufferManager"/> to reuse buffers efficiently.</description></item>
    /// <item><description>Ideal for real-time telemetry, simulation, or streaming systems.</description></item>
    /// </list>
    /// </para>
    /// <example>
    /// Example: Basic UDP sender and receiver
    /// <code>
    /// var receiver = new UdpParticle(
    ///     localEndPoint: new IPEndPoint(IPAddress.Any, 5000),
    ///     allowBroadcast: true);
    /// receiver.OnReceived = (p, data) =&gt;
    ///     Console.WriteLine($"Received: {data.Length} bytes");
    ///
    /// var sender = new UdpParticle(
    ///     remoteEndPoint: new IPEndPoint(IPAddress.Broadcast, 5000),
    ///     allowBroadcast: true);
    ///
    /// sender.Send("hello"u8.ToArray());
    /// </code>
    /// </example>
    /// </remarks>
    public sealed class UdpParticle : IParticle
    {
        private readonly Socket _socket;
        private readonly EndPoint? _remoteEndPoint;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBufferManager _bufferManager;
        private readonly SocketAsyncEventArgs _recvArgs;
        private readonly SocketAsyncEventArgsPool _sendArgsPool;
        private volatile bool _isDisposed;

        private readonly bool _isMulticast;
        private readonly bool _canSend;
        private readonly bool _canReceive;
        private readonly IPAddress? _multicastGroup;

        #region Events
        /// <inheritdoc/>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <inheritdoc/>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <inheritdoc/>
        public Action<IParticle>? OnConnected { get; set; }
        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new UDP transport instance.
        /// </summary>
        /// <param name="localEndPoint">Local socket bind address (or <see langword="null"/> for ephemeral).</param>
        /// <param name="remoteEndPoint">Remote destination for sending packets.</param>
        /// <param name="joinMulticast">Optional multicast group to join.</param>
        /// <param name="disableLoopback">If true, disables receiving multicast packets from the same host.</param>
        /// <param name="allowBroadcast">If true, allows sending to broadcast addresses.</param>
        /// <param name="bufferSize">Size of each internal buffer in bytes.</param>
        /// <param name="maxDegreeOfParallelism">Number of pooled send buffers.</param>
        /// <remarks>
        /// This constructor sets up the UDP socket, joins multicast groups if specified,
        /// and starts the background receive loop automatically.
        /// </remarks>
        public UdpParticle(
            IPEndPoint? localEndPoint = null,
            IPEndPoint? remoteEndPoint = null,
            IPAddress? joinMulticast = null,
            bool disableLoopback = true,
            bool allowBroadcast = false,
            int bufferSize = 8192,
            int maxDegreeOfParallelism = 8)
        {
            _canReceive = true;
            _canSend = remoteEndPoint != null;
            _multicastGroup = joinMulticast;
            _isMulticast = joinMulticast != null;
            _remoteEndPoint = remoteEndPoint;

            // Create UDP socket
            _socket = new Socket(localEndPoint?.AddressFamily ?? AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (allowBroadcast)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            _socket.Bind(localEndPoint ?? new IPEndPoint(IPAddress.Any, 0));

            // --- Multicast Configuration ---
            if (_isMulticast && joinMulticast != null)
            {
                if (localEndPoint.AddressFamily == AddressFamily.InterNetwork)
                {
                    // IPv4 multicast join
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(joinMulticast, localEndPoint.Address));
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 16);
                }
                else if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // IPv6 multicast join
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(joinMulticast));
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 16);
                }
            }

            // --- Buffer Management ---
            var total = bufferSize * maxDegreeOfParallelism;
            _bufferManager = new ConcurrentBufferManager(bufferSize, total);
            _sendArgsPool = new SocketAsyncEventArgsPool(maxDegreeOfParallelism);

            // Prepare a pool of preallocated SocketAsyncEventArgs for sending
            for (int i = 0; i < maxDegreeOfParallelism; i++)
            {
                var args = new SocketAsyncEventArgs();
                _bufferManager.TrySetBuffer(args);
                args.Completed += SendIOCompleted;
                _sendArgsPool.Add(args);
            }

            // --- Receiving Setup ---
            _recvArgs = new SocketAsyncEventArgs
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0)
            };
            _bufferManager.TrySetBuffer(_recvArgs);
            _recvArgs.Completed += ReceiveIOCompleted;

            if (_canReceive)
                StartReceive();

            // Signal ready state
            OnConnected?.Invoke(this);
        }

        #endregion

        #region Send

        /// <summary>
        /// Sends a UDP datagram synchronously using a pooled buffer.
        /// </summary>
        /// <param name="payload">The data to send.</param>
        /// <exception cref="ObjectDisposedException">Thrown if this particle is disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if no remote endpoint is configured.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            _sendArgsPool.TryRent(out var args);
            var memory = args.MemoryBuffer.Slice(0, payload.Length);
            payload.CopyTo(memory.Span);
            args.SetBuffer(memory);
            args.RemoteEndPoint = _remoteEndPoint;

            // Fire async send (fire-and-forget)
            if (!_socket.SendToAsync(args))
                ProcessSend(args);
        }

        /// <summary>
        /// Sends a UDP datagram asynchronously using a pooled buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            _sendArgsPool.TryRent(out var args);
            var memory = args.MemoryBuffer.Slice(0, payload.Length);
            payload.CopyTo(memory);
            args.SetBuffer(memory);
            args.RemoteEndPoint = _remoteEndPoint;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            args.UserToken = tcs;

            if (!_socket.SendToAsync(args))
                ProcessSend(args);

            await tcs.Task.ConfigureAwait(false);
        }

        private void SendIOCompleted(object? sender, SocketAsyncEventArgs e) => ProcessSend(e);

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            try
            {
                var tcs = e.UserToken as TaskCompletionSource<bool>;

                if (e.SocketError != SocketError.Success)
                {
                    tcs?.TrySetException(new SocketException((int)e.SocketError));
                    _sendArgsPool.Return(e);
                    Close(new SocketException((int)e.SocketError));
                    return;
                }

                tcs?.TrySetResult(true);
            }
            finally
            {
                e.UserToken = null;
                _sendArgsPool.Return(e);
            }
        }

        #endregion

        #region Receive Loop

        /// <summary>
        /// Begins the continuous async receive loop.
        /// </summary>
        private void StartReceive()
        {
            if (!_socket.ReceiveFromAsync(_recvArgs))
                ProcessReceive(_recvArgs);
        }

        private void ReceiveIOCompleted(object? sender, SocketAsyncEventArgs e) => ProcessReceive(e);

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            try
            {
                do
                {
                    if (e.SocketError != SocketError.Success)
                    {
                        Close(new SocketException((int)e.SocketError));
                        return;
                    }

                    if (e.BytesTransferred > 0)
                    {
                        var msg = e.MemoryBuffer.Slice(0, e.BytesTransferred);
                        OnReceived?.Invoke(this, msg);
                    }
                }
                while (!_socket.ReceiveFromAsync(e));
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Close(ex); }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Closes the socket and releases resources.
        /// </summary>
        private void Close(Exception? ex = null)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            try { _cts.Cancel(); } catch { }
            try { _socket.Dispose(); } catch { }
            try { _recvArgs.Dispose(); } catch { }

            OnDisconnected?.Invoke(this);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Close();
            _cts.Dispose();
        }

        #endregion
    }
}
