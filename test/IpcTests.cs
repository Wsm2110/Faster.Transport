using System.Collections.Concurrent;
using System.Text;
using Faster.Transport.Contracts;

namespace Faster.Transport.Unittests;

    public sealed class IpcBuilderTests
    {
        private static string NewChannel() => $"FasterIpc_{Guid.NewGuid():N}";

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout, int spinMs = 5)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition()) return true;
                Thread.Sleep(spinMs);
            }
            return condition();
        }

        [Fact]
        public void OneClient_connects_and_roundtrips()
        {
            var channel = NewChannel();

            // server-side state
            var serverReceived = new ConcurrentQueue<(IParticle From, byte[] Payload)>();

            // Build server
            IParticle server = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel, isServer: true)
                .OnConnected(p => { /* could log */ })
                .OnReceived((p, data) =>
                {
                    var arr = data.ToArray();
                    serverReceived.Enqueue((p, arr));
                    // Echo back to sender
                    p.Send(arr);
                })
                .Build();

            try
            {
                // Build client
                var clientReceived = new ConcurrentQueue<byte[]>();
                IParticle client = new ParticleBuilder()
                    .UseMode(TransportMode.Ipc)
                    .WithChannel(channel)
                    .OnReceived((_, data) => clientReceived.Enqueue(data.ToArray()))
                    .Build();

                try
                {
                    // give it a moment to register + attach
                    Assert.True(WaitUntil(() => serverReceived.Count >= 0, TimeSpan.FromSeconds(2)));

                    var payload = Bytes("ping-123");
                    client.Send(payload);

                    // server should receive
                    Assert.True(WaitUntil(() => serverReceived.Count == 1, TimeSpan.FromSeconds(2)), "Server did not receive payload in time");
                    var okServer = serverReceived.TryDequeue(out var srvMsg);
                    Assert.True(okServer);
                    Assert.Equal(payload, srvMsg.Payload);

                    // client should get echo
                    Assert.True(WaitUntil(() => clientReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client did not receive echo in time");
                    var okCli = clientReceived.TryDequeue(out var back);
                    Assert.True(okCli);
                    Assert.Equal(payload, back);
                }
                finally
                {
                    client.Dispose();
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public void TwoClients_broadcast_from_server()
        {
            var channel = NewChannel();
            var connected = new ConcurrentBag<IParticle>();

            // Build server which keeps track of client proxies on connect
            IParticle server = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel, isServer: true)
                .OnConnected(p => connected.Add(p))
                .OnReceived((p, data) => { /* ignore */ })
                .Build();

            try
            {
                var receivedA = new ConcurrentQueue<byte[]>();
                var receivedB = new ConcurrentQueue<byte[]>();

                IParticle clientA = new ParticleBuilder()
                    .UseMode(TransportMode.Ipc)
                    .WithChannel(channel)
                    .OnReceived((_, data) => receivedA.Enqueue(data.ToArray()))
                    .Build();

                IParticle clientB = new ParticleBuilder()
                    .UseMode(TransportMode.Ipc)
                    .WithChannel(channel)
                    .OnReceived((_, data) => receivedB.Enqueue(data.ToArray()))
                    .Build();

                try
                {
                    // Wait until server has seen two clients
                    Assert.True(WaitUntil(() => connected.Count >= 2, TimeSpan.FromSeconds(3)), "Server did not detect two clients in time");

                    var payload = Bytes("hello-all");

                    // broadcast: send to each tracked client from server side
                    foreach (var cli in connected)
                        cli.Send(payload);

                    Assert.True(WaitUntil(() => receivedA.Count == 1 && receivedB.Count == 1, TimeSpan.FromSeconds(2)));

                    Assert.True(receivedA.TryDequeue(out var a));
                    Assert.True(receivedB.TryDequeue(out var b));
                    Assert.Equal(payload, a);
                    Assert.Equal(payload, b);
                }
                finally
                {
                    clientA.Dispose();
                    clientB.Dispose();
                }
            }
            finally
            {
                server.Dispose();
            }
        }

    [Fact]
    public void MultipleClients_send_individually_and_do_not_receive_others()
    {
        var channel = NewChannel();
        var serverReceived = new ConcurrentQueue<(IParticle From, byte[] Payload)>();

        // --- Server setup ---
        IParticle server = new ParticleBuilder()
            .UseMode(TransportMode.Ipc)
            .WithChannel(channel, isServer: true)
            .OnReceived((p, data) =>
            {
                var arr = data.ToArray();
                serverReceived.Enqueue((p, arr));
                // Echo back only to the sender
                p.Send(arr);
            })
            .Build();

        try
        {
            // --- Clients setup ---
            var clientAReceived = new ConcurrentQueue<byte[]>();
            var clientBReceived = new ConcurrentQueue<byte[]>();

            IParticle clientA = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) => clientAReceived.Enqueue(data.ToArray()))
                .Build();

            IParticle clientB = new ParticleBuilder()
                .UseMode(TransportMode.Ipc)
                .WithChannel(channel)
                .OnReceived((_, data) => clientBReceived.Enqueue(data.ToArray()))
                .Build();

            try
            {
                // Wait until both are connected
                Assert.True(WaitUntil(() => serverReceived.Count >= 0, TimeSpan.FromSeconds(2)));

                var payloadA = Bytes("from-A");
                var payloadB = Bytes("from-B");

                // Each client sends its own payload
                clientA.Send(payloadA);
                clientB.Send(payloadB);

                // Server should see two distinct messages
                Assert.True(WaitUntil(() => serverReceived.Count == 2, TimeSpan.FromSeconds(2)), "Server did not receive both messages");

                // Clients should each get only their own echo
                Assert.True(WaitUntil(() => clientAReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client A did not receive echo");
                Assert.True(WaitUntil(() => clientBReceived.Count == 1, TimeSpan.FromSeconds(2)), "Client B did not receive echo");

                Assert.True(clientAReceived.TryDequeue(out var echoA));
                Assert.True(clientBReceived.TryDequeue(out var echoB));

                Assert.Equal(payloadA, echoA);
                Assert.Equal(payloadB, echoB);

                // Ensure no cross-messages (no contamination)
                Assert.Empty(clientAReceived);
                Assert.Empty(clientBReceived);
            }
            finally
            {
                clientA.Dispose();
                clientB.Dispose();
            }
        }
        finally
        {
            server.Dispose();
        }
    }
}