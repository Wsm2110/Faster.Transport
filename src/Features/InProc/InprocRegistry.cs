using System;
using System.Collections.Concurrent;

namespace Faster.Transport.Inproc
{
    /// <summary>
    /// Central registry that keeps track of active in-process reactors (servers)
    /// and allows clients to connect to them by name.
    /// 
    /// ✅ Think of this as a mini in-memory "DNS + router" for the in-process transport layer.
    /// 
    /// - When an <see cref="InprocReactor"/> starts, it registers itself here under a unique name.
    /// - When an <see cref="InprocParticle"/> client connects, it looks up that name to find the reactor.
    /// - The registry then creates a bidirectional <see cref="InprocLink"/> (a message ring pair)
    ///   and hands one side to the reactor so it can communicate with the client.
    /// 
    /// All lookups and registrations are thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
    /// </summary>
    internal static class InprocRegistry
    {
        /// <summary>
        /// Stores all active reactors (hubs) by name.
        /// Key: reactor name; Value: <see cref="InprocReactor"/> instance.
        /// </summary>
        private static readonly ConcurrentDictionary<string, InprocReactor> _hubs =
            new(StringComparer.Ordinal);

        #region Public API

        /// <summary>
        /// Registers a new in-process reactor under a given name.
        /// Clients can later connect to this reactor using <see cref="Connect"/>.
        /// </summary>
        /// <param name="name">Unique reactor name (case-sensitive).</param>
        /// <param name="hub">The reactor instance to register.</param>
        /// <exception cref="InvalidOperationException">Thrown if a reactor with the same name is already registered.</exception>
        public static void RegisterHub(string name, InprocReactor hub)
        {
            if (!_hubs.TryAdd(name, hub))
                throw new InvalidOperationException($"InprocReactor '{name}' is already registered.");
        }

        /// <summary>
        /// Removes a reactor from the registry.
        /// Once unregistered, clients can no longer connect to it.
        /// </summary>
        /// <param name="name">The name of the reactor to remove.</param>
        /// <param name="hub">The reactor instance to remove.</param>
        public static void UnregisterHub(string name, InprocReactor hub)
        {
            // Attempt to remove only if the key-value pair matches
            _hubs.TryRemove(new(name, hub));
        }

        /// <summary>
        /// Connects a new client to a named in-process reactor.
        /// 
        /// This creates a new <see cref="InprocLink"/> (which internally holds two concurrent rings:
        /// one for client→server and one for server→client).  
        /// The new link is then handed off to the reactor via <see cref="InprocReactor.EnqueueIncoming"/>.
        /// 
        /// The returned link is attached to the client particle so both ends can communicate.
        /// </summary>
        /// <param name="name">The target reactor name to connect to.</param>
        /// <param name="ringCapacity">The ring buffer size for the connection.</param>
        /// <returns>A new <see cref="InprocLink"/> ready to attach to the client.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no reactor with the specified name is registered.</exception>
        public static InprocLink Connect(string name, int ringCapacity)
        {
            if (!_hubs.TryGetValue(name, out var hub))
                throw new InvalidOperationException($"No InprocReactor named '{name}' is currently running.");

            // Create a new bidirectional message link (server ↔ client)
            var link = new InprocLink(ringCapacity);

            // Enqueue the link into the reactor’s connection queue
            hub.EnqueueIncoming(link);

            // Return the link to the connecting client
            return link;
        }

        #endregion
    }
}
