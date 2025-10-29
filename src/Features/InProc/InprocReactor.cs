using Faster.Transport.Contracts;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// In-process (same application) message hub that manages multiple in-process client connections.
    /// 
    /// Think of this as a local "server" that multiple in-process clients connect to.
    /// Each client is represented by an <see cref="InprocParticle"/> instance.
    /// 
    /// ✅ Key responsibilities:
    /// - Accept new in-process client links from <see cref="InprocRegistry"/>.
    /// - Create a dedicated <see cref="InprocParticle"/> for each client.
    /// - Forward messages from clients to the central <see cref="OnReceived"/> handler.
    /// - Handle disconnects and cleanup automatically.
    /// 
    /// This class is the in-memory equivalent of an IPC or TCP server — 
    /// but everything runs inside the same process for **maximum performance**.
    /// </summary>
    public sealed class InprocReactor: IDisposable
    {
        #region Fields

        private readonly string _name;
        private readonly int _bufferSize;
        private readonly int _ringCapacity;
        private readonly int _maxDop;

        private readonly CancellationTokenSource _cts = new();

        // Keeps track of active connected clients (each client has an ID)
        private readonly ConcurrentDictionary<int, InprocParticle> _clients = new();

        // Queue for pending incoming connections
        private readonly ConcurrentQueue<InprocLink> _incoming = new();

        private Task? _acceptLoop;
        private int _clientId;

        #endregion

        #region Events

        /// <summary>
        /// Triggered when a new client connects successfully.
        /// </summary>
        public Action<IParticle>? ClientConnected;

        /// <summary>
        /// Triggered when a client disconnects or an error occurs.
        /// </summary>
        public Action<IParticle>? ClientDisconnected;

        /// <summary>
        /// Triggered whenever any connected client sends a message to this reactor.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new in-process message hub.
        /// </summary>
        /// <param name="name">The unique name of the hub (must match clients).</param>
        /// <param name="bufferSize">Buffer size for each client (default 8192 bytes).</param>
        /// <param name="ringCapacity">Capacity of each client's ring buffer.</param>
        /// <param name="maxDegreeOfParallelism">How many threads can send messages concurrently.</param>
        public InprocReactor(string name, int bufferSize = 8192, int ringCapacity = 4096, int maxDegreeOfParallelism = 8)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _bufferSize = bufferSize;
            _ringCapacity = ringCapacity;
            _maxDop = maxDegreeOfParallelism;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Starts the reactor, registers it in the <see cref="InprocRegistry"/>, 
        /// and begins listening for incoming connections.
        /// </summary>
        public void Start()
        {
            // Register this hub globally so that clients can find it
            InprocRegistry.RegisterHub(_name, this);

            // Run the accept loop in a background task
            _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        /// <summary>
        /// Called internally by <see cref="InprocRegistry"/> when a client connects.
        /// Adds the new connection (link) to the queue to be processed by the accept loop.
        /// </summary>
        internal void EnqueueIncoming(InprocLink link) => _incoming.Enqueue(link);

        #endregion

        #region Connection management

        /// <summary>
        /// Continuously accepts new in-process connections and attaches a new
        /// <see cref="InprocParticle"/> to handle each one.
        /// 
        /// Runs in a background task until the reactor is disposed.
        /// </summary>
        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // Handle all pending connections
                    while (_incoming.TryDequeue(out var link))
                    {
                        // Assign a unique numeric ID to the new client
                        var id = Interlocked.Increment(ref _clientId);

                        // Create a server-side particle to represent this client connection
                        var serverParticle = new InprocParticle(_name, isServer: true, _ringCapacity);

                        // When the client disconnects, clean up
                        serverParticle.OnDisconnected = _ => RemoveClient(id);

                        // Also notify external subscribers (if any)
                        serverParticle.OnDisconnected = ClientDisconnected;

                        // Wire up message forwarding — when a client sends data, 
                        // bubble it up to the reactor’s OnReceived event.
                        serverParticle.OnReceived = OnReceived;

                        // Attach the established link (shared ring buffers)
                        serverParticle.AttachLink(link);

                        // Store it in our client list and fire connected event
                        if (_clients.TryAdd(id, serverParticle))
                            ClientConnected?.Invoke(serverParticle);
                    }

                    // Yield control briefly to avoid CPU spinning when idle
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ InprocReactor '{_name}' accept loop error: {ex}");
            }
        }

        /// <summary>
        /// Removes a client from the active list and disposes its resources.
        /// </summary>
        private void RemoveClient(int id)
        {
            if (_clients.TryRemove(id, out var p))
                p.Dispose();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Stops the reactor, disconnects all clients, and releases resources.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();

            // Dispose all active client connections
            foreach (var kv in _clients)
                kv.Value.Dispose();

            _clients.Clear();

            // Unregister this reactor so no new clients can connect
            InprocRegistry.UnregisterHub(_name, this);

            _cts.Dispose();
        }

        #endregion
    }
}
