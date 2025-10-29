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
        private readonly EndPoint? _remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0); 
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBufferManager _bufferManager;    
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
            IPEndPoint? localEndPoint,
            IPEndPoint? remoteEndPoint,
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

            // --- Socket creation ---
            var af = localEndPoint?.AddressFamily ?? AddressFamily.InterNetwork;
            _socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp);

            // ✅ Allow multiple sockets on same port (multicast / multiple listeners)
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // ✅ Enable broadcast if requested
            if (allowBroadcast)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            // ✅ Big kernel buffers (tune if needed)
            _socket.ReceiveBufferSize = 4 * 1024 * 1024; // 4 MB
            _socket.SendBufferSize = 4 * 1024 * 1024; // 4 MB

            // ✅ Ignore ICMP “Port Unreachable” (Windows) to avoid spurious SocketException
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            _socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, BitConverter.GetBytes(false), null);

            // ✅ IPv6 dual mode (must be set BEFORE Bind when AF=InterNetworkV6)
            if (_socket.AddressFamily == AddressFamily.InterNetworkV6)
                _socket.DualMode = true;

            // ✅ Bind after options are set
            _socket.Bind(localEndPoint ?? new IPEndPoint(
                _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

            // --- Multicast Configuration ---
            if (_isMulticast && joinMulticast != null)
            {
                if (_socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    // IPv4 multicast join (use provided local address or Any)
                    _socket.SetSocketOption(
                        SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(joinMulticast, localEndPoint?.Address ?? IPAddress.Any));

                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 16);

                    // Helpful when you care about source interface/addr
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                }
                else if (_socket.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // IPv6 multicast join
                    _socket.SetSocketOption(
                        SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(joinMulticast));

                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 16);

                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
                }
            }

            // --- Buffer Management ---
            var total = bufferSize * 64;
            _bufferManager = new ConcurrentBufferManager(bufferSize, total);
            _sendArgsPool = new SocketAsyncEventArgsPool();

            // Preallocate pooled SAEA for sends
            for (int i = 0; i < 64; i++)
            {
                var args = new SocketAsyncEventArgs();
                _bufferManager.TrySetBuffer(args);
                args.Completed += SendIOCompleted;
                args.RemoteEndPoint = _remoteEndPoint;
                _sendArgsPool.Add(args);
            }     

            if (_canReceive)
                StartReceive();

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

            args.UserToken = args.MemoryBuffer;
            args.SetBuffer(memory);

            // Fire async send (fire-and-forget)
            if (!_socket.SendToAsync(args))
                ProcessSend(args, null);
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

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            args.UserToken = new SendToken(tcs, args.MemoryBuffer);
            args.SetBuffer(memory);
            args.RemoteEndPoint = _remoteEndPoint;

            if (!_socket.SendToAsync(args))
                ProcessSend(args, tcs);

            await tcs.Task.ConfigureAwait(false);
        }

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

        #endregion

        #region Receive Loop

        /// <summary>
        /// Begins the continuous async receive loop.
        /// </summary>
        private void StartReceive()
        {
            var recvArgs = new SocketAsyncEventArgs();
            _bufferManager.TrySetBuffer(recvArgs);
            recvArgs.Completed += ReceiveIOCompleted;
            recvArgs.RemoteEndPoint = _remoteEndPoint;
         
            if (!_socket.ReceiveFromAsync(recvArgs))
                ProcessReceive(recvArgs);
        }

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

    /// <summary>
    /// Internal metadata used to associate a TaskCompletionSource with a send buffer.
    /// </summary>
    public sealed record SendToken(TaskCompletionSource<bool> Tcs, Memory<byte> OriginalBuffer);
}