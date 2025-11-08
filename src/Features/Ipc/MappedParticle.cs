using Faster.Transport.Contracts;
using Faster.Transport.Primitives;
using System;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Base class for memory-mapped IPC transport participants (<see cref="MappedParticle"/> or server-side handlers).
    /// Provides common send/receive logic over two <see cref="MappedChannel"/> instances (RX/TX).
    /// </summary>
    /// <remarks>
    /// The base class handles:
    /// <list type="bullet">
    ///   <item>Thread-safe sending through the TX channel.</item>
    ///   <item>Frame forwarding via the RX channel using <see cref="OnReceived"/>.</item>
    ///   <item>Connection lifecycle events (<see cref="OnConnected"/> / <see cref="OnDisconnected"/>).</item>
    /// </list>
    /// </remarks>
    public abstract class MappedParticleBase : IParticle
    {
        /// <summary>
        /// The receive channel (server → client direction).
        /// </summary>
        protected readonly MappedChannel _rx;

        /// <summary>
        /// The transmit channel (client → server direction).
        /// </summary>
        protected readonly MappedChannel _tx;

        /// <inheritdoc/>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <inheritdoc/>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <inheritdoc/>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Initializes a new base IPC participant using the provided RX/TX channels.
        /// </summary>
        /// <param name="rx">The inbound (read) channel.</param>
        /// <param name="tx">The outbound (write) channel.</param>
        protected MappedParticleBase(MappedChannel rx, MappedChannel tx)
        {
            _rx = rx;
            _tx = tx;
        }

        private void OnRxFrame(ReadOnlyMemory<byte> mem)
            => OnReceived?.Invoke(this, mem);

        /// <summary>
        /// Starts the receive loop and triggers the <see cref="OnConnected"/> event.
        /// </summary>
        public virtual void Start()
        {
            _rx.Start();
            // Wire RX frames directly to OnReceived to minimize allocations
            _rx.OnFrame += payload => OnReceived?.Invoke(this, payload);
            OnConnected?.Invoke(this);
        }

        /// <inheritdoc/>
        public void Send(ReadOnlySpan<byte> payload)
        {
            if (payload.Length == 0) 
            {
                return;
            }

            _tx.Send(payload);
        }

        /// <inheritdoc/>
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length == 0)
            {
                return TaskCompat.CompletedValueTask;
            }

            _tx.Send(payload.Span);
            return TaskCompat.CompletedValueTask;
        }

        /// <inheritdoc/>
        public virtual void Dispose()
        {
            _rx.Dispose();
            _tx.Dispose();
            OnDisconnected?.Invoke(this);
        }
    }

    /// <summary>
    /// Represents a single IPC (Inter-Process Communication) client that connects
    /// to a <see cref="MappedReactor"/> (server) using shared memory.
    /// </summary>
    /// <remarks>
    /// Each <see cref="MappedParticle"/> instance:
    /// <list type="bullet">
    ///   <item>Creates its own pair of memory-mapped files for <b>C2S</b> (client-to-server) and <b>S2C</b> (server-to-client) communication.</item>
    ///   <item>Registers itself in a shared registry file so that the server can detect and attach to it.</item>
    ///   <item>Uses <see cref="MappedChannel"/> to handle message sending and receiving.</item>
    /// </list>
    /// The design allows multiple clients to connect to the same server process with near-zero latency.
    /// </remarks>
    public sealed class MappedParticle : MappedParticleBase
    {
        private readonly ulong _clientId;
        private readonly string _ns;
        private readonly string _base;

        /// <summary>
        /// Triggered when the client detects that the server has disconnected
        /// or the shared memory region is no longer accessible.
        /// </summary>
        public event Action? OnServerDisconnected;

        /// <summary>
        /// Initializes a new IPC client using memory-mapped files.
        /// </summary>
        /// <param name="baseName">
        /// Base name for all shared memory objects. The same base must be used by the server’s <see cref="MappedReactor"/>.
        /// </param>
        /// <param name="clientId">Unique 64-bit ID identifying this client (usually a random number or hash).</param>
        /// <param name="global">
        /// If true, uses the global namespace ("Global\\") so the IPC connection works across user sessions.
        /// Otherwise, uses "Local\\" for same-session communication only.
        /// </param>
        /// <param name="ringBytes">Size of each memory-mapped ring buffer (default ≈1MB).</param>
        /// <remarks>
        /// The client always creates its own shared memory maps for both directions:
        /// <list type="bullet">
        ///   <item><b>C2S:</b> Client → Server messages.</item>
        ///   <item><b>S2C:</b> Server → Client messages.</item>
        /// </list>
        /// The server discovers these maps via the registry file.
        /// </remarks>
        public MappedParticle(string baseName, ulong clientId, bool global = false, int ringBytes = 128 + (1 << 20))
            : base(
                // Inbound (server → client)
                rx: new MappedChannel($"{(global ? "Global" : "Local")}\\{baseName}.S2C.{clientId:X16}.map",
                                           evName: null, totalBytes: ringBytes,
                                           create: true, isReader: true,
                                           rxThreadName: $"cli-rx:{clientId:X16}", useEvent: false),

                // Outbound (client → server)
                tx: new MappedChannel($"{(global ? "Global" : "Local")}\\{baseName}.C2S.{clientId:X16}.map",
                                           evName: null, totalBytes: ringBytes,
                                           create: true, isReader: false,
                                           rxThreadName: null, useEvent: false))
        {
            _clientId = clientId;
            _ns = global ? "Global\\" : "Local\\";
            _base = baseName;
        }

        /// <summary>
        /// Starts the client’s receive loop and registers it in the shared registry
        /// so that the <see cref="MappedReactor"/> can discover it.
        /// </summary>
        public override void Start()
        {
            base.Start();
            Register();
        }

        /// <summary>
        /// Registers this client’s unique ID in the shared registry memory map.
        /// </summary>
        /// <remarks>
        /// The registry acts like a small shared “directory” of connected clients:
        /// <list type="bullet">
        ///   <item>Each client appends its 16-character hexadecimal ID followed by a newline.</item>
        ///   <item>The server polls this registry every few milliseconds to find new clients.</item>
        ///   <item>A <see cref="Mutex"/> ensures thread-safe and process-safe access to the registry.</item>
        /// </list>
        /// </remarks>
        private void Register()
        {
            string regMap = _ns + $"{_base}.Registry.map";
            string regMtx = _ns + $"{_base}.Registry.mtx";

            // Open or create the shared registry memory map
            using var reg = MmfHelper.CreateWithSecurity(regMap, 64 * 1024);
            using var view = reg.CreateViewAccessor(0, 64 * 1024, MemoryMappedFileAccess.ReadWrite);
            using var mutex = new Mutex(false, regMtx);

            // Lock registry during write
            mutex.WaitOne();
            try
            {
                var buf = new byte[64 * 1024];
                view.ReadArray(0, buf, 0, buf.Length);

                // Find the first zero byte (end of written data)
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;

                // Append new client entry (hex ID + newline)
                var add = Encoding.ASCII.GetBytes(_clientId.ToString("X16") + "\n");

                if (len + add.Length <= buf.Length)
                {
                    Array.Copy(add, 0, buf, len, add.Length);
                    view.WriteArray(0, buf, 0, buf.Length);
                }
            }
            finally
            {
                // Always release the mutex
                mutex.ReleaseMutex();
            }
        }
    }
}
