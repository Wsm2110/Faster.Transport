using Faster.Transport.Inproc;
using Faster.Transport.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Faster.Transport.Tests.Inproc
{

    [CollectionDefinition("SequentialTests", DisableParallelization = true)]

    public class InprocParticleTests : IDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));
        public void Dispose() => _cts.Dispose();

        private static string Channel() => "inproc-" + Guid.NewGuid().ToString("N");
        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        private static string Str(ReadOnlyMemory<byte> m) => Encoding.UTF8.GetString(m.Span);

        // ------------------------------------------------------------
        // 1. Basic round-trip (client → server → client)
        // ------------------------------------------------------------
        [Fact(DisplayName = "Basic round-trip message exchange")]
        public async Task Basic_RoundTrip()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, isServer: false);
            string? serverReceived = null;
            string? clientReceived = null;

            reactor.OnReceived = (p, data) =>
            {
                serverReceived = Str(data);
                p.Send(Utf8("pong"));
            };

            client.OnReceived = (p, data) =>
            {
                clientReceived = Str(data);
            };

            await Task.Delay(100, _cts.Token);
            client.Send(Utf8("ping"));
            await Task.Delay(200, _cts.Token);

            Assert.Equal("ping", serverReceived);
            Assert.Equal("pong", clientReceived);
        }

        // ------------------------------------------------------------
        // 2. Large payload delivery
        // ------------------------------------------------------------
        [Fact(DisplayName = "Large payload is transferred correctly")]
        public async Task Large_Payload_Transferred()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, isServer: false);
            byte[] sent = new byte[128 * 1024];
            new Random(123).NextBytes(sent);
            byte[]? received = null;

            reactor.OnReceived = (p, data) => p.Send(data.Span);
            client.OnReceived = (p, data) => received = data.ToArray();

            await Task.Delay(100, _cts.Token);
            client.Send(sent);
            await Task.Delay(200, _cts.Token);

            Assert.NotNull(received);
            Assert.True(received!.SequenceEqual(sent));
        }

        // ------------------------------------------------------------
        // 3. Zero-length messages are ignored (no OnReceived)
        // ------------------------------------------------------------
        [Fact(DisplayName = "Zero-length payload is ignored")]
        public async Task Zero_Length_Ignored()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            bool got = false;

            reactor.OnReceived = (p, data) => got = true;

            await Task.Delay(100, _cts.Token);
            client.Send(ReadOnlySpan<byte>.Empty);
            await Task.Delay(100, _cts.Token);

            Assert.False(got);
        }

        // ------------------------------------------------------------
        // 4. Multiple messages preserve order
        // ------------------------------------------------------------
        [Fact(DisplayName = "Multiple messages preserve order")]
        public async Task Messages_Preserve_Order()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            var received = new List<int>();

            reactor.OnReceived = (p, data) =>
            {
                var num = int.Parse(Str(data));
                received.Add(num);
            };

            await Task.Delay(100, _cts.Token);
            for (int i = 0; i < 50; i++)
                client.Send(Utf8(i.ToString()));

            await Task.Delay(300, _cts.Token);

            Assert.Equal(Enumerable.Range(0, 50), received);
        }

        // ------------------------------------------------------------
        // 5. Concurrent Send() calls (backpressure & MPSC safety)
        // ------------------------------------------------------------
        [Fact(DisplayName = "Concurrent Send() calls are safe")]
        public async Task Concurrent_Sends_Are_Safe()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            int count = 0;
            reactor.OnReceived = (p, data) => Interlocked.Increment(ref count);

            await Task.Delay(100, _cts.Token);

            var tasks = Enumerable.Range(0, 4).Select(async _ =>
            {
                for (int i = 0; i < 100; i++)
                    client.Send(Utf8("msg"));
                await Task.Yield();
            });

            await Task.WhenAll(tasks);
            await Task.Delay(300, _cts.Token);

            Assert.Equal(400, count);
        }

        // ------------------------------------------------------------
        // 6. OnDisconnected fires when closed
        // ------------------------------------------------------------
        [Fact(DisplayName = "OnDisconnected is triggered on close")]
        public async Task OnDisconnected_Fires()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnDisconnected = _ => disconnected.TrySetResult(true);

            await Task.Delay(100, _cts.Token);
            client.Dispose();

            var result = await disconnected.Task.WaitAsync(_cts.Token);
            Assert.True(result);
        }

        // ------------------------------------------------------------
        // 7. OnConnected triggers for client automatically
        // ------------------------------------------------------------
        [Fact(DisplayName = "OnConnected triggers for client")]
        public async Task OnConnected_Fires_For_Client()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new InprocParticle(name, false);
            client.OnConnected = _ => connected.TrySetResult(true);

            var ok = await connected.Task.WaitAsync(_cts.Token);
            Assert.True(ok);
        }

        // ------------------------------------------------------------
        // 8. Dispose prevents future sends
        // ------------------------------------------------------------
        [Fact(DisplayName = "Dispose prevents sending")]
        public void Dispose_Prevents_Send()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            client.Dispose();

            Assert.Throws<ObjectDisposedException>(() => client.Send(Utf8("x")));
        }

        // ------------------------------------------------------------
        // 9. Multiple clients can connect to one reactor
        // ------------------------------------------------------------
        [Fact(DisplayName = "Multiple clients connect to same reactor")]
        public async Task Multiple_Clients_Work()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            int received = 0;
            reactor.OnReceived = (p, data) => Interlocked.Increment(ref received);

            var clients = Enumerable.Range(0, 3)
                .Select(_ => new InprocParticle(name, false))
                .ToList();

            await Task.Delay(100, _cts.Token);

            foreach (var c in clients)
                c.Send(Utf8("hello"));

            await Task.Delay(200, _cts.Token);
            Assert.Equal(3, received);

            foreach (var c in clients)
                c.Dispose();
        }

        // ------------------------------------------------------------
        // 10. Backpressure test: ring fills and drains under load
        // ------------------------------------------------------------
        [Fact(DisplayName = "Backpressure ring behavior under load")]
        public async Task Backpressure_Ring_Behavior()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            int count = 0;
            reactor.OnReceived = (p, data) => Interlocked.Increment(ref count);

            await Task.Delay(100, _cts.Token);

            // 2000 small messages should fill the ring but eventually complete
            for (int i = 0; i < 2000; i++)
                client.Send(Utf8("bp"));

            await Task.Delay(500, _cts.Token);
            Assert.Equal(2000, count);
        }

        // ------------------------------------------------------------
        // 11. Reader loop handles exceptions gracefully
        // ------------------------------------------------------------
        [Fact(DisplayName = "Reader loop handles exceptions gracefully")]
        public async Task Reader_Loop_Graceful_Exit()
        {
            string name = Channel();
            using var reactor = new InprocReactor(name);
            reactor.Start();

            var client = new InprocParticle(name, false);
            var disconnected = false;
            client.OnDisconnected = _ => disconnected = true;

            await Task.Delay(100, _cts.Token);

            // Simulate unexpected disposal mid-loop
            client.Dispose();

            await Task.Delay(100, _cts.Token);
            Assert.True(disconnected);
        }
    }
}
