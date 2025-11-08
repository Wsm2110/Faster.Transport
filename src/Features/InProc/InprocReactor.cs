using Faster.Transport.Contracts;
using System.Collections.Concurrent;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// An in-process (same application) message hub that accepts and manages multiple in-process clients.
    /// </summary>
    /// <remarks>
    /// Think of this like a lightweight TCP or IPC server, but everything runs **inside one process** — 
    /// so it’s extremely fast and has no network or OS overhead.
    /// 
    /// Each client is represented by an <see cref="InprocParticle"/>, which handles
    /// message sending and receiving through shared in-memory channels.
    /// 
    /// ✅ Responsibilities:
    /// <list type="bullet">
    ///   <item>Registers itself in <see cref="InprocRegistry"/> so clients can find it by name.</item>
    ///   <item>Accepts new client connections and creates <see cref="InprocParticle"/> instances.</item>
    ///   <item>Handles message forwarding through the <see cref="OnReceived"/> event.</item>
    ///   <item>Cleans up automatically when clients disconnect or the reactor stops.</item>
    /// </list>
    /// </remarks>
    public sealed class InprocReactor : IReactor, IDisposable
    {
        #region Fields

        // Unique name of this reactor (clients must use the same name to connect)
        private readonly string _name;

        // Internal configuration
        private readonly int _bufferSize;
        private readonly int _ringCapacity;
        private readonly int _maxDop;

        // Used to cancel background loops when stopping
        private readonly CancellationTokenSource _cts = new();

        // Tracks all connected clients (key = client ID)
        private readonly ConcurrentDictionary<int, InprocParticle> _clients = new();

        // Queue for new incoming connections from clients
        private readonly ConcurrentQueue<InprocLink> _incoming = new();

        // Background task that runs the accept loop
        private Task? _acceptLoop;

        // Counter for assigning unique numeric client IDs
        private int _clientId;
        private bool _running;

        #endregion

        #region Events

        /// <summary>
        /// Called when a new client successfully connects to this reactor.
        /// </summary>
        public Action<IParticle>? OnConnected { get; set; }

        /// <summary>
        /// Called when a client disconnects or its connection is lost.
        /// </summary>
        public Action<IParticle>? OnDisconnected { get; set; }

        /// <summary>
        /// Called whenever a message is received from any connected client.
        /// </summary>
        public Action<IParticle, ReadOnlyMemory<byte>>? OnReceived { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new in-process reactor (server) that other in-process clients can connect to.
        /// </summary>
        /// <param name="name">The unique name of this reactor (used by clients to find it).</param>
        /// <param name="bufferSize">Buffer size per client (default: 8192 bytes).</param>
        /// <param name="ringCapacity">Capacity of each client’s internal ring buffer.</param>
        /// <param name="maxDegreeOfParallelism">Number of threads that can send messages concurrently.</param>
        public InprocReactor(string name, int bufferSize = 8192, int ringCapacity = 4096, int maxDegreeOfParallelism = 8)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _bufferSize = bufferSize;
            _ringCapacity = ringCapacity;
            _maxDop = maxDegreeOfParallelism;

            Start();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Starts the reactor and registers it globally so clients can discover it.
        /// </summary>
        /// <remarks>
        /// After calling <see cref="Start"/>, this reactor begins listening for in-process client connections.  
        /// Clients that call <c>InprocParticle.Connect("MyHub")</c> will automatically link to it.
        /// </remarks>
        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;

            // Make this reactor discoverable by name
            InprocRegistry.RegisterHub(_name, this);

            // Start the background task that accepts new client connections
            _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        /// <summary>
        /// Called by <see cref="InprocRegistry"/> when a client wants to connect to this reactor.
        /// </summary>
        /// <param name="link">The shared memory link representing the new client connection.</param>
        internal void EnqueueIncoming(InprocLink link) => _incoming.Enqueue(link);

        #endregion

        #region Connection Management

        /// <summary>
        /// Background task that continuously accepts and manages in-process connections.
        /// </summary>
        /// <remarks>
        /// This loop runs as long as the reactor is active.  
        /// Each time a new client link appears in the queue, it creates a new
        /// <see cref="InprocParticle"/> to represent that connection.
        /// </remarks>
        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // Process all pending incoming client connections
                    while (_incoming.TryDequeue(out var link))
                    {
                        // Assign a unique ID to this new client
                        var id = Interlocked.Increment(ref _clientId);

                        // Create a server-side particle to handle communication
                        var serverParticle = new InprocParticle(_name, isServer: true, _ringCapacity);

                        // Clean up when this client disconnects
                        serverParticle.OnDisconnected = _ => RemoveClient(id);

                        // Forward message events up to the reactor-level handler
                        serverParticle.OnReceived = OnReceived;

                        // Attach the link (connects client ↔ server memory buffers)
                        serverParticle.AttachLink(link);

                        // Add to our client list
                        if (_clients.TryAdd(id, serverParticle))
                            OnConnected?.Invoke(serverParticle);
                    }

                    // Avoid tight loop when idle (yield to scheduler)
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping the reactor
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ InprocReactor '{_name}' accept loop error: {ex}");
            }
        }

        /// <summary>
        /// Removes a disconnected client and disposes its resources.
        /// </summary>
        /// <param name="id">The client ID to remove.</param>
        private void RemoveClient(int id)
        {
            if (_clients.TryRemove(id, out var particle))
                particle.Dispose();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Stops the reactor and disconnects all active clients.
        /// </summary>
        public void Stop()
        {
            Dispose();
        }

        /// <summary>
        /// Releases all resources, disconnects clients, and unregisters this reactor.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();

            // Disconnect all connected clients
            foreach (var kv in _clients)
                kv.Value.Dispose();

            _clients.Clear();

            // Remove this reactor from the global registry so no new clients connect
            InprocRegistry.UnregisterHub(_name, this);

            _cts.Dispose();
        }

        #endregion
    }
}
