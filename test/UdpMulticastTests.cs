using Faster.Transport.Contracts;
using Faster.Transport;
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
    // Multicast: Sender → Multiple Receivers
    // ---------------------------------------------------------------------
    [Fact(DisplayName = "Multicast: Sender → Multiple Receivers (builder-based)")]
    public async Task Multicast_MultipleReceivers_CrossPlatform()
    {
        var group = IPAddress.Parse("239.0.0.123");
        var port = 9100;
        var disableLoopback = !IsWindows; // disable loopback on Linux/mac

        // ✅ Two receivers joining multicast group
        var recv1Tcs = new TaskCompletionSource<string>();
        var recv2Tcs = new TaskCompletionSource<string>();

        var recv1 = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: disableLoopback)
            .OnReceived((p, msg) => recv1Tcs.TrySetResult(Text(msg)))
            .Build();

        var recv2 = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: disableLoopback)
            .OnReceived((p, msg) => recv2Tcs.TrySetResult(Text(msg)))
            .Build();

        // ✅ Sender (unicast to group)
        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: disableLoopback)
            .Build();

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
    // Multicast: Loopback suppression
    // ---------------------------------------------------------------------
    [Fact(DisplayName = "Multicast: Loopback disabled suppresses sender’s own packets (builder-based)")]
    public async Task Multicast_LoopbackDisabled_SenderDoesNotReceive()
    {
        var group = IPAddress.Parse("239.0.0.99");
        var port = 9200;

        // ✅ Receiver with loopback enabled
        var recvTcs = new TaskCompletionSource<string>();
        var recv = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((p, msg) => recvTcs.TrySetResult(Text(msg)))
            .Build();

        // ✅ Sender joins group but disables loopback
        bool senderGotOwn = false;
        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: true)
            .OnReceived((p, msg) => senderGotOwn = true)
            .Build();

        await sender.SendAsync(Bytes("loopback-test"));

        var received = await recvTcs.Task.WaitAsync(_timeout);

        Assert.Equal("loopback-test", received);
        await Task.Delay(500);
        Assert.False(senderGotOwn, "Sender should not receive its own packet when loopback is disabled");

        sender.Dispose();
        recv.Dispose();
    }

    // ---------------------------------------------------------------------
    // Windows-only test: Multiple multicast bindings (SO_REUSEADDR)
    // ---------------------------------------------------------------------
    [Fact(DisplayName = "Windows: Multiple multicast bindings work (ReuseAddress, builder-based)")]
    public async Task Multicast_Windows_MultipleSocketsReuseAddress()
    {
        if (!IsWindows)
            return; // Skip non-Windows platforms

        var group = IPAddress.Parse("239.0.0.111");
        var port = 9300;

        var recv1Tcs = new TaskCompletionSource<string>();
        var recv2Tcs = new TaskCompletionSource<string>();

        var recv1 = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((p, msg) => recv1Tcs.TrySetResult(Text(msg)))
            .Build();

        var recv2 = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .OnReceived((p, msg) => recv2Tcs.TrySetResult(Text(msg)))
            .Build();

        var sender = new ParticleBuilder()
            .UseMode(TransportMode.Udp)
            .WithMulticast(group, port, disableLoopback: false)
            .Build();

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
