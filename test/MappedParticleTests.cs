using Faster.Transport.Ipc;
using System.Text;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]

public class MappedParticleTests : IDisposable
{
    // Safety timeout for all waits in this test class
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
    public void Dispose() => _cts.Dispose();

    private static string Channel() => "ipc-" + Guid.NewGuid().ToString("N");

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> m) => Encoding.UTF8.GetString(m.Span);

    // ------------------------------------------------------------
    // 1) Basic round-trip: client -> server -> client
    // ------------------------------------------------------------
    [Fact(DisplayName = "Basic round-trip: client sends, server replies")]
    public async Task Basic_RoundTrip()
    {
        var name = Channel();
        using var server = new MappedParticle(name, isServer: true);
        using var client = new MappedParticle(name, isServer: false);

        var gotClientReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnReceived = (p, data) =>
        {
            // Echo back
            p.Send(Utf8("pong"));
        };

        client.OnReceived = (p, data) =>
        {
            gotClientReply.TrySetResult(Str(data));
        };

        // give OnConnected a moment to fire internally (async)
        await Task.Delay(50, _cts.Token);

        client.Send(Utf8("ping"));

        var reply = await gotClientReply.Task.WaitAsync(_cts.Token);
        Assert.Equal("pong", reply);
    }

    // ------------------------------------------------------------
    // 2) Large payload under MaxFrameBytes echoes correctly
    // ------------------------------------------------------------
    [Fact(DisplayName = "Large payload echoes correctly")]
    public async Task Large_Payload_Echoes()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        var rnd = new Random(123);
        var payload = new byte[256 * 1024]; // 256KB << default 1MiB ring << MaxFrameBytes
        rnd.NextBytes(payload);

        var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnReceived = (p, data) =>
        {
            // Echo back exactly
            p.Send(data.Span);
        };

        client.OnReceived = (p, data) =>
        {
            got.TrySetResult(data.ToArray());
        };

        await Task.Delay(50, _cts.Token);
        await client.SendAsync(payload);

        var echoed = await got.Task.WaitAsync(_cts.Token);
        Assert.Equal(payload.Length, echoed.Length);
        Assert.True(payload.SequenceEqual(echoed));
    }

    // ------------------------------------------------------------
    // 3) Zero-length payload is ignored (no OnReceived)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Zero-length payload is ignored")]
    public async Task Zero_Length_Is_Ignored()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        var gotAny = false;
        server.OnReceived = (p, data) => gotAny = true;

        await Task.Delay(50, _cts.Token);
        client.Send(ReadOnlySpan<byte>.Empty);

        await Task.Delay(200, _cts.Token);
        Assert.False(gotAny);
    }

    // ------------------------------------------------------------
    // 4) Oversize payload throws ArgumentOutOfRangeException
    // ------------------------------------------------------------
    [Fact(DisplayName = "Oversize payload throws")]
    public void Oversize_Throws()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        // MaxFrameBytes = 8 MiB in implementation; try larger
        var tooLarge = new byte[(8 * 1024 * 1024) + 1];

        Assert.Throws<ArgumentOutOfRangeException>(() => client.Send(tooLarge));
    }

    // ------------------------------------------------------------
    // 5) Dispose during use triggers OnDisconnected
    // ------------------------------------------------------------
    [Fact(DisplayName = "Dispose triggers OnDisconnected")]
    public async Task Dispose_Triggers_OnDisconnected()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        var client = new MappedParticle(name, false);

        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnected = p => disconnected.TrySetResult(true);

        await Task.Delay(50, _cts.Token);
        client.Dispose();

        var ok = await disconnected.Task.WaitAsync(_cts.Token);
        Assert.True(ok);
    }

    // ------------------------------------------------------------
    // 6) Reentrancy safe: reply inside OnReceived (no stack overflow)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Reentrancy safe: reply in OnReceived")]
    public async Task Reentrancy_Safe_Reply()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnReceived = (p, data) =>
        {
            // reply inside callback (tests reentrancy guard)
            p.Send(Utf8("ok"));
        };

        client.OnReceived = (p, data) => got.TrySetResult(Str(data));

        await Task.Delay(50, _cts.Token);
        client.Send(Utf8("test"));

        var reply = await got.Task.WaitAsync(_cts.Token);
        Assert.Equal("ok", reply);
    }

    // ------------------------------------------------------------
    // 7) Concurrent Send() calls are handled (MPSC staging)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Concurrent Send() calls handled via MPSC staging")]
    public async Task Concurrent_Sends_Are_Handled()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        int total = 2000; // keep moderate for CI stability
        int received = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnReceived = (p, data) =>
        {
            if (Interlocked.Increment(ref received) == total)
                done.TrySetResult(true);
        };

        await Task.Delay(50, _cts.Token);

        // Blast from multiple tasks concurrently
        var tasks = Enumerable.Range(0, 8).Select(async t =>
        {
            for (int i = 0; i < total / 8; i++)
            {
                client.Send(Utf8("x"));
            }
            await Task.Yield();
        });

        await Task.WhenAll(tasks);
        await done.Task.WaitAsync(_cts.Token);

        Assert.Equal(total, received);
    }

    // ------------------------------------------------------------
    // 8) Backpressure: small rings still deliver
    // ------------------------------------------------------------
    [Fact(DisplayName = "Backpressure with small rings still delivers")]
    public async Task Backpressure_Small_Ring_Works()
    {
        var name = Channel();
        // Very small ring to force backpressure behavior
        using var server = new MappedParticle(name, true, ringBytes: 64 * 1024);
        using var client = new MappedParticle(name, false, ringBytes: 64 * 1024);

        int count = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnReceived = (p, data) =>
        {
            if (Interlocked.Increment(ref count) == 500)
                done.TrySetResult(true);
        };

        await Task.Delay(50, _cts.Token);

        // 500 small messages quickly → ring should apply backpressure internally but finish
        for (int i = 0; i < 500; i++)
            client.Send(Utf8("bp"));

        await done.Task.WaitAsync(_cts.Token);
        Assert.Equal(500, count);
    }

    // ------------------------------------------------------------
    // 9) OnConnected fires (both sides)
    // ------------------------------------------------------------
    [Fact(DisplayName = "OnConnected fires for both server and client")]
    public async Task OnConnected_Fires_Both_Sides()
    {
        var name = Channel();
        var sConn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cConn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new MappedParticle(name, true);
        server.OnConnected = _ => sConn.TrySetResult(true);

        using var client = new MappedParticle(name, false);
        client.OnConnected = _ => cConn.TrySetResult(true);

        var s = await sConn.Task.WaitAsync(_cts.Token);
        var c = await cConn.Task.WaitAsync(_cts.Token);

        Assert.True(s);
        Assert.True(c);
    }

    // ------------------------------------------------------------
    // 10) Multiple small messages preserve ordering per-sender
    // (best-effort check: echo sequence numbers)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Multiple small messages preserve order")]
    public async Task Order_Preserved_Per_Sender()
    {
        var name = Channel();
        using var server = new MappedParticle(name, true);
        using var client = new MappedParticle(name, false);

        // Server echoes same bytes back
        server.OnReceived = (p, data) => p.Send(data.Span);

        int N = 1000;
        int next = 0;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnReceived = (p, data) =>
        {
            var s = Str(data);
            var expected = next.ToString();
            if (s == expected)
            {
                if (Interlocked.Increment(ref next) == N)
                    done.TrySetResult(true);
            }
        };

        await Task.Delay(50, _cts.Token);

        for (int i = 0; i < N; i++)
            client.Send(Utf8(i.ToString()));

        await done.Task.WaitAsync(_cts.Token);
        Assert.Equal(N, next);
    }

    // ------------------------------------------------------------
    // 11) Dispose() prevents further sends
    // ------------------------------------------------------------
    [Fact(DisplayName = "Dispose prevents further sends")]
    public void Dispose_Prevents_Sends()
    {
        var name = Channel();
        var client = new MappedParticle(name, false);
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Send(Utf8("nope")));
    }
}
