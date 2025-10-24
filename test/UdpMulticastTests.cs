using Faster.Transport.Features.Udp;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]

public class UdpParticleMulticastTests : IDisposable
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Text(ReadOnlyMemory<byte> mem) => Encoding.UTF8.GetString(mem.Span);
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public void Dispose() => _cts.Dispose();

    // ---------------------------------------------------------------------
    // Multicast - Multiple receivers
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Multicast: Sender → Multiple Receivers (cross-platform reliable)")]
    public async Task Multicast_MultipleReceivers_CrossPlatform()
    {
        var group = IPAddress.Parse("239.0.0.123");
        var port = 9100;

        // ✅ On Windows: loopback must be enabled for local tests
        var disableLoopback = !IsWindows; // keep disabled on Linux/mac

        var recv1 = new UdpParticle(
            new IPEndPoint(IPAddress.Any, port),
            joinMulticast: group,
            disableLoopback: disableLoopback);

        var recv2 = new UdpParticle(
            new IPEndPoint(IPAddress.Any, port),
            joinMulticast: group,
            disableLoopback: disableLoopback);

        var recv1Tcs = new TaskCompletionSource<string>();
        var recv2Tcs = new TaskCompletionSource<string>();

        recv1.OnReceived = (p, msg) => recv1Tcs.TrySetResult(Text(msg));
        recv2.OnReceived = (p, msg) => recv2Tcs.TrySetResult(Text(msg));

        var sender = new UdpParticle(
            new IPEndPoint(IPAddress.Any, 0),
            new IPEndPoint(group, port));

        await sender.SendAsync(Bytes("multicast-test"));

        var result1 = await recv1Tcs.Task.WaitAsync(_timeout);
        var result2 = await recv2Tcs.Task.WaitAsync(_timeout);

        Assert.Equal("multicast-test", result1);
        Assert.Equal("multicast-test", result2);

        sender.Dispose();
        recv1.Dispose();
        recv2.Dispose();
    }

    // ---------------------------------------------------------------------
    // Multicast loopback suppression
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Multicast: Loopback disabled suppresses sender’s own packets")]
    public async Task Multicast_LoopbackDisabled_SenderDoesNotReceive()
    {
        var group = IPAddress.Parse("239.0.0.99");
        var port = 9200;

        // ✅ Receiver with loopback enabled to ensure it gets data
        var recv = new UdpParticle(
            new IPEndPoint(IPAddress.Any, port),
            joinMulticast: group,
            disableLoopback: false);

        var recvTcs = new TaskCompletionSource<string>();
        recv.OnReceived = (p, msg) => recvTcs.TrySetResult(Text(msg));

        // ✅ Sender joins group but disables loopback (so it won't receive its own packets)
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
        Assert.False(senderGotOwn, "Sender should not receive its own packet when loopback is disabled");

        sender.Dispose();
        recv.Dispose();
    }

    // ---------------------------------------------------------------------
    // Windows-only corner case test
    // ---------------------------------------------------------------------

    [Fact(DisplayName = "Windows: Verify multiple multicast bindings work (ReuseAddress)")]
    public async Task Multicast_Windows_MultipleSocketsReuseAddress()
    {
        if (!IsWindows)
        {
            return; // Skip non-Windows platforms
        }

        var group = IPAddress.Parse("239.0.0.111");
        var port = 9300;

        var recv1 = new UdpParticle(new IPEndPoint(IPAddress.Any, port), joinMulticast: group, disableLoopback: false);
        var recv2 = new UdpParticle(new IPEndPoint(IPAddress.Any, port), joinMulticast: group, disableLoopback: false);

        var recv1Tcs = new TaskCompletionSource<string>();
        var recv2Tcs = new TaskCompletionSource<string>();

        recv1.OnReceived = (p, msg) => recv1Tcs.TrySetResult(Text(msg));
        recv2.OnReceived = (p, msg) => recv2Tcs.TrySetResult(Text(msg));

        var sender = new UdpParticle(
            new IPEndPoint(IPAddress.Any, 0),
            new IPEndPoint(group, port));

        await sender.SendAsync(Bytes("reuse-test"));

        var r1 = await recv1Tcs.Task.WaitAsync(_timeout);
        var r2 = await recv2Tcs.Task.WaitAsync(_timeout);

        Assert.Equal("reuse-test", r1);
        Assert.Equal("reuse-test", r2);

        sender.Dispose();
        recv1.Dispose();
        recv2.Dispose();
    }
}