using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport.Features.Tcp
{
    /// <summary>
    /// High-performance framed TCP transport supporting both synchronous and asynchronous sending.
    /// </summary>
    public sealed class Particle : IParticle
    {
        private readonly Socket _socket;
        private readonly FrameParser _parser;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentBufferManager _bufferManager;
        private readonly SocketAsyncEventArgsPool _sendArgsPool;
        private readonly SocketAsyncEventArgs _recvArgs;
        private volatile bool _isDisposed;

        private const string PayloadTooLargeMessage =
            "Payload size exceeds the configured buffer slice size. " +
            "Increase the buffer slice size in ConcurrentBufferManager or chunk large messages.";

        public Action<IParticle, ReadOnlyMemory<byte>> OnReceived { get; set; }
        public Action<IParticle> OnDisconnected { get; set; }
        public Action<IParticle> OnConnected { get; set; }

        public Particle(EndPoint remoteEndPoint, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
            : this(CreateAndConnect(remoteEndPoint), bufferSize, maxDegreeOfParallelism) { }

        public Particle(Socket connectedSocket, int bufferSize = 8192, int maxDegreeOfParallelism = 8)
        {
            _socket = connectedSocket ?? throw new ArgumentNullException(nameof(connectedSocket));
            _socket.NoDelay = true;
            _socket.ReceiveBufferSize = 1024 * 1024;
            _socket.SendBufferSize = 1024 * 1024;

            int sweetspot = maxDegreeOfParallelism * 2;
            int length = bufferSize * sweetspot;

            _bufferManager = new ConcurrentBufferManager(bufferSize, length);

            _parser = new FrameParser(length)
            {
                OnFrame = payload => OnReceived?.Invoke(this, payload),
                OnError = ex => Close(ex)
            };

            _sendArgsPool = new SocketAsyncEventArgsPool(1024);

            for (int i = 0; i < sweetspot; i++)
            {
                var args = CreateSocketAsyncEventArgs(SendIOCompleted);
                _sendArgsPool.Add(args);
            }

            _recvArgs = CreateSocketAsyncEventArgs(ReceiveIOCompleted);
            StartReceive();
            OnConnected?.Invoke(this);
        }

        private static Socket CreateAndConnect(EndPoint ep)
        {
            var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(ep);
            s.NoDelay = true;
            return s;
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(Particle));

            if (payload.Length > _bufferManager.SliceSize)
                throw new ArgumentOutOfRangeException(nameof(payload),
                    string.Format("{0} (Payload: {1} bytes, SliceSize: {2} bytes)",
                        PayloadTooLargeMessage, payload.Length, _bufferManager.SliceSize));

            SocketAsyncEventArgs sendArgs;
            _sendArgsPool.Rent(out sendArgs);

            int totalLen = 4 + payload.Length;

#if NETSTANDARD2_1_OR_GREATER
            // Use MemoryBuffer + SetBuffer(ReadOnlyMemory<byte>)
            var mem = sendArgs.MemoryBuffer.Slice(0, totalLen);
            BinaryPrimitives.WriteInt32LittleEndian(mem.Span.Slice(0, 4), payload.Length);
            payload.CopyTo(mem.Span.Slice(4, payload.Length));

            sendArgs.UserToken = new SendToken(null, mem);
            sendArgs.SetBuffer(mem);
#else
            // netstandard2.0 path (byte[] + offset + count)
            byte[] buffer = sendArgs.Buffer;
            int offset = sendArgs.Offset;

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), payload.Length);
            payload.CopyTo(buffer.AsSpan(offset + 4, payload.Length));

            sendArgs.SetBuffer(offset, totalLen);
            sendArgs.UserToken = new SendToken(null, buffer, offset, totalLen);
#endif

            if (!_socket.SendAsync(sendArgs))
                ProcessSend(sendArgs, null);
        }

        // ============================================================
        //  ASYNC SEND
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(Particle));

            if (payload.Length > _bufferManager.SliceSize)
                throw new ArgumentOutOfRangeException(nameof(payload),
                    string.Format("{0} (Payload: {1} bytes, SliceSize: {2} bytes)",
                        PayloadTooLargeMessage, payload.Length, _bufferManager.SliceSize));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SocketAsyncEventArgs sendArgs;
            _sendArgsPool.Rent(out sendArgs);

            int totalLen = 4 + payload.Length;

#if NETSTANDARD2_1_OR_GREATER
            var mem = sendArgs.MemoryBuffer.Slice(0, totalLen);
            BinaryPrimitives.WriteInt32LittleEndian(mem.Span.Slice(0, 4), payload.Length);
            payload.Span.CopyTo(mem.Span.Slice(4, payload.Length));

            sendArgs.UserToken = new SendToken(tcs, mem);
            sendArgs.SetBuffer(mem);
#else
            byte[] buffer = sendArgs.Buffer;
            int offset = sendArgs.Offset;

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(offset + 4, payload.Length));

            sendArgs.SetBuffer(offset, totalLen);
            sendArgs.UserToken = new SendToken(tcs, buffer, offset, totalLen);
#endif

            if (!_socket.SendAsync(sendArgs))
                ProcessSend(sendArgs, tcs);

            await tcs.Task.ConfigureAwait(false);
        }

        // ============================================================
        //  RECEIVE LOOP
        // ============================================================

        private void StartReceive()
        {
            if (!_socket.ReceiveAsync(_recvArgs))
                ProcessReceive(_recvArgs);
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            do
            {
                if (e.SocketError != SocketError.Success || e.BytesTransferred == 0)
                {
                    Close(e.SocketError == SocketError.Success ? null : new SocketException((int)e.SocketError));
                    return;
                }

#if NETSTANDARD2_1_OR_GREATER
                var mem = e.MemoryBuffer.Slice(0, e.BytesTransferred);
                if (!_parser.Feed(mem.Span))
                {
                    Close(new IOException("Parser overflow or backpressure limit reached."));
                    return;
                }
#else
                var span = new ReadOnlySpan<byte>(e.Buffer, e.Offset, e.BytesTransferred);
                if (!_parser.Feed(span))
                {
                    Close(new IOException("Parser overflow or backpressure limit reached."));
                    return;
                }
#endif
            }
            while (!_socket.ReceiveAsync(e));
        }

        // ============================================================
        //  COMPLETION HANDLERS
        // ============================================================

        private void SendIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            var token = e.UserToken as SendToken;
            ProcessSend(e, token != null ? token.Tcs : null);
        }

        private void ReceiveIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
        }

        private void ProcessSend(SocketAsyncEventArgs e, TaskCompletionSource<bool> tcs)
        {
            var token = e.UserToken as SendToken;

#if NETSTANDARD2_1_OR_GREATER
            if (token != null)
            {
                // restore full slice for next send (we rented full slice size when assigning)
                // Note: our buffer manager set a fixed slice; re-expose entire slice to pool user.
                e.SetBuffer(token.OriginalMemory);
            }
#else
            if (token != null)
            {
                // restore to full slice (Count ignored; we reset to full slice size)
                e.SetBuffer(token.Offset, _bufferManager.SliceSize);
            }
#endif

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

        // ============================================================
        //  CLEANUP AND DISPOSAL
        // ============================================================

        private void Close(Exception ex = null)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try { _cts.Cancel(); } catch { }
            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            try { _socket.Dispose(); } catch { }
            try { _recvArgs.Dispose(); } catch { }

            OnDisconnected?.Invoke(this);
        }

        public void Dispose()
        {
            Close();
            _parser.Dispose();
            _cts.Dispose();
        }

#if NETSTANDARD2_1_OR_GREATER
        private sealed class SendToken
        {
            public readonly TaskCompletionSource<bool> Tcs;
            public readonly Memory<byte> OriginalMemory;

            public SendToken(TaskCompletionSource<bool> tcs, Memory<byte> originalMemory)
            {
                Tcs = tcs;
                OriginalMemory = originalMemory;
            }
        }
#else
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
#endif
    }
}
