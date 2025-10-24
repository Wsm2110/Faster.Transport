using Faster.Transport.Features.Udp;
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

    [Fact(DisplayName = "Unicast: Client → Server basic send/receive")]
    public async Task Unicast_SendAndReceive_Works()
    {
        var server = new UdpParticle(new IPEndPoint(IPAddress.Loopback, 9000));
        var received = new TaskCompletionSource<string>();

        server.OnReceived = (p, msg) => received.TrySetResult(Text(msg));

        var client = new UdpParticle(
            localEndPoint: new IPEndPoint(IPAddress.Loopback, 0),
            remoteEndPoint: new IPEndPoint(IPAddress.Loopback, 9000));

        await client.SendAsync(Bytes("ping"));
        var result = await received.Task.WaitAsync(_timeout);

        Assert.Equal("ping", result);

        server.Dispose();
        client.Dispose();
    }

    [Fact(DisplayName = "Unicast: Multiple sequential messages handled correctly")]
    public async Task Unicast_MultipleMessages_Success()
    {
        var server = new UdpParticle(new IPEndPoint(IPAddress.Loopback, 9001));
        var counter = 0;
        var tcs = new TaskCompletionSource();

        server.OnReceived = (p, msg) =>
        {
            Interlocked.Increment(ref counter);
            if (counter == 3)
                tcs.TrySetResult();
        };

        var client = new UdpParticle(
            new IPEndPoint(IPAddress.Loopback, 0),
            new IPEndPoint(IPAddress.Loopback, 9001));

        await client.SendAsync(Bytes("msg1"));
        await client.SendAsync(Bytes("msg2"));
        await client.SendAsync(Bytes("msg3"));

        await tcs.Task.WaitAsync(_timeout);
        Assert.Equal(3, counter);

        server.Dispose();
        client.Dispose();
    }

    // -------------------------------------------------------------------------------------
    //  MULTICAST TESTS
    // -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Multicast: Loopback disabled suppresses only sender’s own packets")]
    public async Task Multicast_LoopbackDisabled_SenderDoesNotReceive_ButReceiverDoes()
    {
        var group = IPAddress.Parse("239.0.0.88");
        var port = 9250;

        var recv = new UdpParticle(new IPEndPoint(IPAddress.Any, port), joinMulticast: group, disableLoopback: false);
        var recvTcs = new TaskCompletionSource<string>();
        recv.OnReceived = (p, msg) => recvTcs.TrySetResult(Text(msg));

        // Sender joins group, but loopback disabled
        var sender = new UdpParticle(
            localEndPoint: new IPEndPoint(IPAddress.Any, 0),
            remoteEndPoint: new IPEndPoint(group, port),
            joinMulticast: group,
            disableLoopback: true);

        bool senderGotOwn = false;
        sender.OnReceived = (p, msg) => senderGotOwn = true;

        await sender.SendAsync(Bytes("loopback-test"));
        var received = await recvTcs.Task.WaitAsync(_timeout);

        Assert.Equal("loopback-test", received);
        await Task.Delay(500);
        Assert.False(senderGotOwn);

        sender.Dispose();
        recv.Dispose();
    }

    // -------------------------------------------------------------------------------------
    //  BROADCAST TESTS
    // -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Broadcast: Sender → Receiver on local subnet")]
    public async Task Broadcast_SendAndReceive_Works()
    {
        var port = 9300;
        var recv = new UdpParticle(new IPEndPoint(IPAddress.Any, port), allowBroadcast: true);
        var tcs = new TaskCompletionSource<string>();
        recv.OnReceived = (p, msg) => tcs.TrySetResult(Text(msg));

        var sender = new UdpParticle(
            localEndPoint: new IPEndPoint(IPAddress.Any, 0),
            remoteEndPoint: new IPEndPoint(IPAddress.Broadcast, port),
            allowBroadcast: true);

        await sender.SendAsync(Bytes("broadcast!"));
        var msg = await tcs.Task.WaitAsync(_timeout);

        Assert.Equal("broadcast!", msg);

        recv.Dispose();
        sender.Dispose();
    }

    // -------------------------------------------------------------------------------------
    //  EDGE CASES & ERROR HANDLING
    // -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Gracefully disposes socket and stops receiving")]
    public async Task Dispose_StopsReceiving()
    {
        var recv = new UdpParticle(new IPEndPoint(IPAddress.Loopback, 9400));
        var called = false;
        recv.OnReceived = (_, _) => called = true;

        recv.Dispose();
        await Task.Delay(300);
        Assert.False(called);
    }

    [Fact(DisplayName = "Can send and receive concurrently")]
    public async Task ConcurrentSendReceive_Success()
    {
        var port = 9500;
        var recv = new UdpParticle(new IPEndPoint(IPAddress.Loopback, port));
        var sender = new UdpParticle(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, port));

        var count = 0;
        var tcs = new TaskCompletionSource();

        recv.OnReceived = (p, msg) =>
        {
            if (Interlocked.Increment(ref count) == 10)
                tcs.TrySetResult();
        };

        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
            tasks[i] = sender.SendAsync(Bytes($"msg-{i}")).AsTask();

        await Task.WhenAll(tasks);
        await tcs.Task.WaitAsync(_timeout);

        Assert.Equal(10, count);

        recv.Dispose();
        sender.Dispose();
    }
}
