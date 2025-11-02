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
    public sealed class UdpParticle : IParticle
    {
        private readonly Socket _socket;
        private readonly EndPoint _remoteEndPoint;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentBufferManager _bufferManager;
        private readonly SocketAsyncEventArgsPool _sendArgsPool;
        private volatile bool _isDisposed;

        private readonly bool _isMulticast;
        private readonly bool _canSend;
        private readonly bool _canReceive;
        private readonly IPAddress _multicastGroup;

        #region Events
        public Action<IParticle, ReadOnlyMemory<byte>> OnReceived { get; set; }
        public Action<IParticle> OnDisconnected { get; set; }
        public Action<IParticle> OnConnected { get; set; }
        #endregion

        #region Constructor

        public UdpParticle(
            IPEndPoint localEndPoint,
            IPEndPoint remoteEndPoint = null,
            IPAddress joinMulticast = null,
            bool disableLoopback = true,
            bool allowBroadcast = false,
            int bufferSize = 8192,
            int maxDegreeOfParallelism = 8)
        {
            _canReceive = true;
            _canSend = remoteEndPoint != null;
            _multicastGroup = joinMulticast;
            _isMulticast = joinMulticast != null;
            _remoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);

            var af = localEndPoint != null ? localEndPoint.AddressFamily : AddressFamily.InterNetwork;
            _socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (allowBroadcast)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            _socket.ReceiveBufferSize = 4 * 1024 * 1024;
            _socket.SendBufferSize = 4 * 1024 * 1024;

            const int SIO_UDP_CONNRESET = -1744830452;
            try { _socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, BitConverter.GetBytes(false), null); } catch { }

            if (_socket.AddressFamily == AddressFamily.InterNetworkV6)
                _socket.DualMode = true;

            _socket.Bind(localEndPoint ?? new IPEndPoint(
                _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

            // Multicast setup
            if (_isMulticast && joinMulticast != null)
            {
                if (_socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    _socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(joinMulticast, localEndPoint != null ? localEndPoint.Address : IPAddress.Any));
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 16);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                }
                else
                {
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(joinMulticast));
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, !disableLoopback);
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 16);
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
                }
            }

            // Buffer manager
            int total = bufferSize * 64;
            _bufferManager = new ConcurrentBufferManager(bufferSize, total);
            _sendArgsPool = new SocketAsyncEventArgsPool();

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            SocketAsyncEventArgs args;
            _sendArgsPool.Rent(out args);
              
            byte[] buffer = args.Buffer;
            int offset = args.Offset;

            payload.CopyTo(buffer.AsSpan(offset, payload.Length));
            args.SetBuffer(offset, payload.Length);
            args.UserToken = new SendToken(null, buffer, offset, payload.Length);
            args.RemoteEndPoint = _remoteEndPoint;

            if (!_socket.SendToAsync(args))
                ProcessSend(args, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UdpParticle));
            if (!_canSend)
                throw new InvalidOperationException("No remote endpoint configured for sending.");

            SocketAsyncEventArgs args;
            _sendArgsPool.Rent(out args);
        
            byte[] buffer = args.Buffer;
            int offset = args.Offset;

            payload.Span.CopyTo(buffer.AsSpan(offset, payload.Length));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            args.SetBuffer(offset, payload.Length);
            args.UserToken = new SendToken(tcs, buffer, offset, payload.Length);
            args.RemoteEndPoint = _remoteEndPoint;

            if (!_socket.SendToAsync(args))
                ProcessSend(args, tcs);

            await tcs.Task.ConfigureAwait(false);
        }

        private void SendIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            var token = e.UserToken as SendToken;
            ProcessSend(e, token != null ? token.Tcs : null);
        }

        private void ProcessSend(SocketAsyncEventArgs e, TaskCompletionSource<bool> tcs)
        {
            var token = e.UserToken as SendToken;
            if (token != null)
                e.SetBuffer(token.Offset, _bufferManager.SliceSize);

            if (e.SocketError != SocketError.Success)
            {
                var ex = new SocketException((int)e.SocketError);
                if (tcs != null) tcs.TrySetException(ex);
                _sendArgsPool.Return(e);
                Close(ex);
                return;
            }

            _sendArgsPool.Return(e);
            if (tcs != null) tcs.TrySetResult(true);
        }

        #endregion

        #region Receive

        private void StartReceive()
        {
            var recvArgs = new SocketAsyncEventArgs();
            _bufferManager.TrySetBuffer(recvArgs);
            recvArgs.Completed += ReceiveIOCompleted;
            recvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            if (!_socket.ReceiveFromAsync(recvArgs))
                ProcessReceive(recvArgs);
        }

        private void ReceiveIOCompleted(object sender, SocketAsyncEventArgs e)
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
                        var span = new ReadOnlySpan<byte>(e.Buffer, e.Offset, e.BytesTransferred);
                        OnReceived?.Invoke(this, span.ToArray());
                    }
                }
                while (!_socket.ReceiveFromAsync(e));
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Close(ex); }
        }

        #endregion

        #region Cleanup

        private void Close(Exception ex = null)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            try { _cts.Cancel(); } catch { }
            try { _socket.Dispose(); } catch { }

            OnDisconnected?.Invoke(this);
        }

        public void Dispose()
        {
            Close();
            _cts.Dispose();
        }

        #endregion

        private sealed class SendToken
        {
            public readonly TaskCompletionSource<bool> Tcs;
            public readonly byte[] Buffer;
            public readonly int Offset;
            public readonly int Count;

            public SendToken(TaskCompletionSource<bool> tcs, byte[] buffer, int offset, int count)
            {
                Tcs = tcs;
                Buffer = buffer;
                Offset = offset;
                Count = count;
            }
        }
    }
}
