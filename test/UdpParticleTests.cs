using System.Net;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]
public class UdpParticleTests : IDisposable
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));

    public void Dispose() => _cts.Dispose();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Text(ReadOnlyMemory<byte> mem) => Encoding.UTF8.GetString(mem.Span);

    // -------------------------------------------------------------------------------------
    //  UNICAST TESTS
    // -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Unicast: Client → Server basic send/receive (builder)")]
    public async Task Unicast_SendAndReceive_Works()
    {
        var port = 9000;
        var received = new TaskCompletionSource<string>();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, port))
            .OnReceived((p, msg) => received.TrySetResult(Text(msg)))
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, 0))
            .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
            .Build();

        await client.SendAsync(Bytes("ping"));
        var result = await received.Task.WaitAsync(_timeout);

        Assert.Equal("ping", result);

        server.Dispose();
        client.Dispose();
    }

    [Fact(DisplayName = "Unicast: Multiple sequential messages handled correctly (builder)")]
    public async Task Unicast_MultipleMessages_Success()
    {
        var port = 9001;
        var counter = 0;
        var tcs = new TaskCompletionSource();

        var server = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, port))
            .OnReceived((p, msg) =>
            {
                Interlocked.Increment(ref counter);
                if (counter == 3)
                    tcs.TrySetResult();
            })
            .Build();

        var client = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, 0))
            .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
            .Build();

        await client.SendAsync(Bytes("msg1"));
        await client.SendAsync(Bytes("msg2"));
        await client.SendAsync(Bytes("msg3"));

        await tcs.Task.WaitAsync(_timeout);
        Assert.Equal(3, counter);

        server.Dispose();
        client.Dispose();
    }

    [Fact(DisplayName = "Multicast: Loopback disabled suppresses only sender’s own packets (builder)")]
    public async Task Multicast_LoopbackDisabled_SenderDoesNotReceive_ButReceiverDoes()
    {
        var group = IPAddress.Parse("239.0.0.88");
        var port = 9250;

        var recvTcs = new TaskCompletionSource<string>();
        var recv = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((p, msg) =>
            {
                recvTcs.TrySetResult(Text(msg));
            })
            .Build();

        bool senderGotOwn = false;

        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: true)
            .OnReceived((p, msg) =>
            {
                senderGotOwn = true;
            })
            .Build();

        await sender.SendAsync(Bytes("loopback-test"));
        var received = await recvTcs.Task.WaitAsync(_timeout);

        Assert.Equal("loopback-test", received);
        await Task.Delay(500);
        Assert.False(senderGotOwn, "Sender should not receive its own packet when loopback is disabled");

        sender.Dispose();
        recv.Dispose();
    }

    [Fact(DisplayName = "Broadcast: Sender → Receiver on local subnet (builder, inferred endpoints)")]
    public async Task Broadcast_SendAndReceive_Works()
    {
        var port = 9300;
        var tcs = new TaskCompletionSource<string>();

        // ✅ Receiver: bind to Any, allow broadcast reception
        var recv = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, port))  // loopback ensures local delivery
            .AllowBroadcast(true)
            .OnReceived((p, msg) =>
            {
                var text = Encoding.UTF8.GetString(msg.Span);
                Console.WriteLine($"[Receiver] Got: {text}");
                tcs.TrySetResult(text);
            })
            .Build();

        // ✅ Sender: builder will infer local endpoint automatically
        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
            .AllowBroadcast(true)
            .Build();

        await Task.Delay(100); // let OS bind before sending

        await sender.SendAsync(Encoding.UTF8.GetBytes("broadcast!"));
        var msg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("broadcast!", msg);

        recv.Dispose();
        sender.Dispose();
    }


    // -------------------------------------------------------------------------------------
    //  EDGE CASES & ERROR HANDLING
    // -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Gracefully disposes socket and stops receiving (builder)")]
    public async Task Dispose_StopsReceiving()
    {
        var recv = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, 9400))
            .OnReceived((_, _) => Assert.Fail("Should not receive after Dispose"))
            .Build();

        recv.Dispose();
        await Task.Delay(300);
    }

    [Fact(DisplayName = "Can send and receive concurrently (builder)")]
    public async Task ConcurrentSendReceive_Success()
    {
        var port = 9500;
        var count = 0;
        var tcs = new TaskCompletionSource();

        var recv = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, port))
            .OnReceived((p, msg) =>
            {
                if (Interlocked.Increment(ref count) == 10)
                {
                    tcs.TrySetResult();
                }
            })
            .Build();

        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithLocal(new IPEndPoint(IPAddress.Loopback, 0))
            .WithRemote(new IPEndPoint(IPAddress.Loopback, port))
            .Build();

        var tasks = Enumerable.Range(0, 10)
            .Select(i => sender.SendAsync(Bytes($"msg-{i}")).AsTask())
            .ToArray();

        await Task.WhenAll(tasks);
        await tcs.Task.WaitAsync(_timeout);

        Assert.Equal(10, count);

        recv.Dispose();
        sender.Dispose();
    }
}
