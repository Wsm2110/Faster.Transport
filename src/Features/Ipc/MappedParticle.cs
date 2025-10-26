using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Represents a single IPC (Inter-Process Communication) client that connects
    /// to a <see cref="MappedReactor"/> (server) using shared memory.
    /// </summary>
    /// <remarks>
    /// Each <see cref="MappedParticle"/> instance:
    /// <list type="bullet">
    ///   <item>Creates its own pair of memory-mapped files for <b>C2S</b> (client-to-server) and <b>S2C</b> (server-to-client) communication.</item>
    ///   <item>Registers itself in a shared registry file so that the server can detect and attach to it.</item>
    ///   <item>Uses <see cref="DirectionalChannel"/> to handle message sending and receiving.</item>
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
        public MappedParticle(string baseName, ulong clientId, bool global = false, int ringBytes = (128 + (1 << 20)))
            : base(
                // Create inbound (server → client) channel
                rx: new DirectionalChannel($"{(global ? "Global" : "Local")}\\{baseName}.S2C.{clientId:X16}.map",
                                           evName: null, totalBytes: ringBytes,
                                           create: true, isReader: true,
                                           rxThreadName: $"cli-rx:{clientId:X16}", useEvent: false),

                // Create outbound (client → server) channel
                tx: new DirectionalChannel($"{(global ? "Global" : "Local")}\\{baseName}.C2S.{clientId:X16}.map",
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

            // Lock the registry so only one client writes at a time
            mutex.WaitOne();
            try
            {
                var buf = new byte[64 * 1024];
                view.ReadArray(0, buf, 0, buf.Length);

                // Find the first zero byte (end of text data)
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;

                // Prepare new entry (hex client ID + newline)
                var add = Encoding.ASCII.GetBytes(_clientId.ToString("X16") + "\n");

                // Append to the registry if space is available
                if (len + add.Length <= buf.Length)
                {
                    Array.Copy(add, 0, buf, len, add.Length);
                    view.WriteArray(0, buf, 0, buf.Length);
                }
            }
            finally
            {
                // Always release the mutex even if an exception occurs
                mutex.ReleaseMutex();
            }
        }
    }
}
