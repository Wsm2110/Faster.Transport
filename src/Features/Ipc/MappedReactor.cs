using System;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using Faster.Transport.Contracts;

namespace Faster.Transport.Ipc
{
    /// <summary>
    /// Represents a high-performance, shared-memory (IPC) message reactor that listens for 
    /// connections from multiple clients using <see cref="MemoryMappedFile"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="MappedReactor"/> acts like a server for IPC communication:
    /// <list type="bullet">
    ///   <item>Clients register themselves in a shared "Registry" memory file.</item>
    ///   <item>The reactor detects new clients and attaches to their C2S (client-to-server) and S2C (server-to-client) channels.</item>
    ///   <item>Each client is represented by a lightweight <see cref="ClientParticle"/> that handles incoming and outgoing messages.</item>
    /// </list>
    /// This design avoids sockets and serialization overhead by using zero-copy shared memory rings.
    /// </remarks>
    public sealed class MappedReactor : IDisposable
    {
        private readonly string _ns;
        private readonly string _base;
        private readonly int _ringBytes;
        private readonly ConcurrentDictionary<ulong, ClientParticle> _clients = new();
        private readonly Thread _registryThread;
        private volatile bool _running;

        /// <summary>
        /// Triggered whenever a client sends data to the server.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        /// <summary>
        /// Triggered when a new client connects (detected via the registry).
        /// </summary>
        public Action<ulong>? OnConnected { get; set; }

        /// <summary>
        /// Triggered when a connected client disconnects or is disposed.
        /// </summary>
        public Action<ulong>? OnClientDisconnected { get; set; }

        /// <summary>
        /// Creates a new IPC reactor (server) instance.
        /// </summary>
        /// <param name="baseName">Base name used for all shared memory maps and events (e.g. "FasterIpc").</param>
        /// <param name="global">
        /// If true, shared memory objects are created under the "Global\\" namespace, 
        /// making them visible across Windows sessions. Otherwise, "Local\\" is used.
        /// </param>
        /// <param name="ringBytes">Size of each client’s shared memory ring buffer (default: ~1 MB).</param>
        public MappedReactor(string baseName, bool global = false, int ringBytes = (128 + (1 << 20)))
        {
            _ns = global ? "Global\\" : "Local\\";
            _base = baseName;
            _ringBytes = ringBytes;
            _registryThread = new Thread(RegistryLoop)
            {
                IsBackground = true,
                Name = "ipc-registry"
            };
        }

        /// <summary>
        /// Starts the reactor and begins watching for newly registered clients.
        /// </summary>
        public void Start()
        {
            _running = true;
            _registryThread.Start();
        }

        /// <summary>
        /// Continuously monitors the shared "Registry" MMF for new client entries.
        /// Each line in the registry represents a new client ID in hexadecimal format.
        /// </summary>
        private void RegistryLoop()
        {
            string regMap = _ns + $"{_base}.Registry.map";
            string regMtx = _ns + $"{_base}.Registry.mtx";

            // The registry map stores all connected client IDs as text lines.
            using var reg = MmfHelper.CreateWithSecurity(regMap, 64 * 1024);
            using var view = reg.CreateViewAccessor(0, 64 * 1024, MemoryMappedFileAccess.ReadWrite);
            using var mutex = new Mutex(false, regMtx);

            var buf = new byte[64 * 1024];
            var seen = new HashSet<ulong>();

            while (_running)
            {
                // Synchronize access to the registry file so multiple clients
                // don’t overwrite each other when writing their IDs.
                mutex.WaitOne();
                try { view.ReadArray(0, buf, 0, buf.Length); }
                finally { mutex.ReleaseMutex(); }

                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;

                var text = Encoding.ASCII.GetString(buf, 0, len);

                // Each line = one registered client
                foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ulong.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out var id))
                        continue;

                    // Only attach once per client
                    if (seen.Add(id))
                        TryAttachClient(id);
                }

                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Attempts to attach to a registered client by opening its shared memory channels.
        /// </summary>
        /// <param name="id">Unique 64-bit client ID (as hex string in registry).</param>
        private void TryAttachClient(ulong id)
        {
            try
            {
                string c2sMap = _ns + $"{_base}.C2S.{id:X16}.map"; // client → server
                string s2cMap = _ns + $"{_base}.S2C.{id:X16}.map"; // server → client

                // Each client has its own pair of shared memory rings.
                var rx = new DirectionalChannel(c2sMap, evName: null, totalBytes: _ringBytes,
                                                create: false, isReader: true,
                                                rxThreadName: $"srv-rx:{id:X16}", useEvent: false);

                var tx = new DirectionalChannel(s2cMap, evName: null, totalBytes: _ringBytes,
                                                create: false, isReader: false,
                                                rxThreadName: null, useEvent: false);

                var particle = new ClientParticle(rx, tx, id, this);
                _clients[id] = particle;

                particle.Start();
                FasterIpcTrace.Info($"[Server] Client {id:X16} connected");
                OnConnected?.Invoke(id);
            }
            catch (Exception ex)
            {
                FasterIpcTrace.Warn($"[Server] Attach failed for {id:X16}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends data to a specific connected client.
        /// </summary>
        public void Send(ulong id, ReadOnlySpan<byte> payload)
        {
            if (_clients.TryGetValue(id, out var p))
                p.Send(payload);
        }

        /// <summary>
        /// Sends the same data to all connected clients (broadcast).
        /// </summary>
        public void Broadcast(ReadOnlySpan<byte> payload)
        {
            foreach (var kv in _clients)
            {
                try { kv.Value.Send(payload); } catch { /* Ignore broken clients */ }
            }
        }

        /// <summary>
        /// Represents a single connected client’s communication channel within the reactor.
        /// </summary>
        private sealed class ClientParticle : MappedParticleBase
        {
            private readonly ulong _id;
            private readonly MappedReactor _owner;

            public ClientParticle(DirectionalChannel rx, DirectionalChannel tx, ulong id, MappedReactor owner)
                : base(rx, tx)
            {
                _id = id;
                _owner = owner;

                // Forward messages to the reactor’s OnReceived callback.
                OnReceived += (self, data) => _owner.OnReceived?.Invoke(self, data);
            }

            /// <summary>
            /// Cleans up resources and notifies the reactor that the client disconnected.
            /// </summary>
            public override void Dispose()
            {
                base.Dispose();
                _owner.OnClientDisconnected?.Invoke(_id);
            }
        }

        /// <summary>
        /// Stops the reactor and cleans up all client connections.
        /// </summary>
        public void Dispose()
        {
            _running = false;
            foreach (var c in _clients.Values)
                c.Dispose();
            _clients.Clear();
        }
    }
}
