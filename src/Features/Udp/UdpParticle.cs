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
    /// 
    /// ✅ Key features:
    /// - Full duplex (send + receive) using a single socket.
    /// - Optional multicast join and loopback disable.
    /// - Zero-copy buffer management via ConcurrentBufferManager.
    /// - SocketAsyncEventArgs pooling for ultra-fast async sends.
    /// - Designed for ultra-low-latency telemetry, simulation, or real-time systems.
    /// </summary>
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
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }
        public Action<IParticle>? OnDisconnected { get; set; }
        public Action<IParticle>? OnConnected { get; set; }
        #endregion

        #region Constructor

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

            _socket = new Socket(localEndPoint?.AddressFamily ?? AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (allowBroadcast)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            _socket.Bind(localEndPoint ?? new IPEndPoint(IPAddress.Any, 0));

            // Multicast setup
            if (_isMulticast && joinMulticast != null)
            {
                // Configure multicast interface
                if (localEndPoint.AddressFamily == AddressFamily.InterNetwork)
                {
                    // Set default outgoing interface
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        (int)BitConverter.ToUInt32(localEndPoint.Address.GetAddressBytes(), 0));

                    // Join group and set loopback & TTL
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(joinMulticast, localEndPoint.Address));
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 16);
                }
                else if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(joinMulticast));
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 16);
                }
            }

            // Buffer and event args pool
            var total = bufferSize * maxDegreeOfParallelism;
            _bufferManager = new ConcurrentBufferManager(bufferSize, total);
            _sendArgsPool = new SocketAsyncEventArgsPool(maxDegreeOfParallelism);

            for (int i = 0; i < maxDegreeOfParallelism; i++)
            {
                var args = new SocketAsyncEventArgs();
                _bufferManager.TrySetBuffer(args);
                args.Completed += SendIOCompleted;
                _sendArgsPool.Add(args);
            }

            // Receive setup
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
        /// Sends a UDP datagram synchronously using a pooled buffer.
        /// </summary>
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

            // Fire async send (UDP is connectionless, safe to fire-and-forget)
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
