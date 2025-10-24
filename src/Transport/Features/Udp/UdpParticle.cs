using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Faster.Transport.Features.Udp
{
    /// <summary>
    /// High-performance UDP transport supporting unicast, multicast, and broadcast modes.
    /// 
    /// ✅ Key features:
    /// - Supports both sending and receiving on the same socket.
    /// - Optional multicast group join with loopback disable.
    /// - Zero-copy buffer management via ConcurrentBufferManager.
    /// - Non-blocking async receive loop.
    /// - Designed for ultra-low-latency telemetry, simulation, or real-time systems.
    /// </summary>
    public sealed class UdpParticle : IParticle
    {
        private readonly Socket _socket;
        private readonly EndPoint? _remoteEndPoint;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBufferManager _bufferManager;
        private readonly SocketAsyncEventArgs _recvArgs;
        private volatile bool _isDisposed;

        private readonly bool _isMulticast;
        private readonly bool _canSend;
        private readonly bool _canReceive;
        private readonly IPAddress? _multicastGroup;

        #region Events
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }
        public Action<IParticle>? OnConnected { get; set; }
        #endregion

        #region Constructors

        /// <summary>
        /// Creates a UDP particle that can send and/or receive datagrams.
        /// </summary>
        /// <param name="localEndPoint">Local endpoint to bind (for receiving, may be 0.0.0.0).</param>
        /// <param name="remoteEndPoint">Optional remote endpoint (for sending).</param>
        /// <param name="joinMulticast">Optional multicast group to join (null = unicast).</param>
        /// <param name="disableLoopback">Whether to disable multicast loopback.</param>
        /// <param name="allowBroadcast">Allow sending to broadcast addresses.</param>
        /// <param name="bufferSize">Per-operation buffer size.</param>
        /// <param name="maxDegreeOfParallelism">Parallelism for buffer pool.</param>
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

            _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (allowBroadcast)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            _socket.Bind(localEndPoint);

            if (_isMulticast && joinMulticast != null)
            {
                if (localEndPoint.AddressFamily == AddressFamily.InterNetwork)
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(joinMulticast, localEndPoint.Address));
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, !disableLoopback);
                }
                else if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(joinMulticast));
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, !disableLoopback);
                }
            }

            // Allocate buffers and start receive loop
            var total = bufferSize * maxDegreeOfParallelism;
            _bufferManager = new ConcurrentBufferManager(bufferSize, total);

            _recvArgs = new SocketAsyncEventArgs();
            _recvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            _bufferManager.TrySetBuffer(_recvArgs);
            _recvArgs.Completed += ReceiveIOCompleted;

            if (_canReceive)
                StartReceive();

            OnConnected?.Invoke(this);
        }

        #endregion

        #region Send

        /// <summary>
        /// Sends a UDP datagram to the configured remote endpoint.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            _socket.SendTo(payload, _remoteEndPoint!);
        }

        /// <summary>
        /// Sends a UDP datagram asynchronously.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            await _socket.SendToAsync(payload, SocketFlags.None, _remoteEndPoint!).ConfigureAwait(false);
        }

        #endregion

        #region Receive Loop

        private void StartReceive()
        {
            if (!_socket.ReceiveFromAsync(_recvArgs))
                ProcessReceive(_recvArgs);
        }

        private void ReceiveIOCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
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

        public void Dispose()
        {
            Close();
            _cts.Dispose();
        }

        #endregion
    }
}
