using Faster.Transport.Inproc;
using Faster.Transport.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Faster.Transport.Unittests;

[CollectionDefinition("SequentialTests", DisableParallelization = true)]

public class InprocReactorTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));
    public void Dispose() => _cts.Dispose();

    private static string Channel() => "reactor-" + Guid.NewGuid().ToString("N");
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> m) => Encoding.UTF8.GetString(m.Span);

    // ------------------------------------------------------------
    // 1. Basic connection lifecycle: Start → Connect → Dispose
    // ------------------------------------------------------------
    [Fact(DisplayName = "Client can connect to started reactor")]
    public async Task Client_Can_Connect_To_Reactor()
    {
        string name = Channel();
        var connected = new TaskCompletionSource<IParticle>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reactor = new InprocReactor(name);
        reactor.ClientConnected += p => connected.TrySetResult(p);
        reactor.Start();

        using var client = new InprocParticle(name, isServer: false);

        var serverSide = await connected.Task.WaitAsync(_cts.Token);
        Assert.NotNull(serverSide);
    }

    // ------------------------------------------------------------
    // 2. Client → Reactor message forwarding (OnReceived)
    // ------------------------------------------------------------
    [Fact(DisplayName = "Messages from client trigger reactor OnReceived")]
    public async Task Client_Message_Triggers_OnReceived()
    {
        string name = Channel();
        var messageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reactor = new InprocReactor(name);
        reactor.OnReceived = (p, data) =>
        {
            messageReceived.TrySetResult(Str(data));
        };
        reactor.Start();

        using var client = new InprocParticle(name, isServer: false);

        await Task.Delay(50, _cts.Token);
        client.Send(Utf8("hello"));

        var result = await messageReceived.Task.WaitAsync(_cts.Token);
        Assert.Equal("hello", result);
    }

    // ------------------------------------------------------------
    // 3. Reactor fires ClientConnected and ClientDisconnected
    // ------------------------------------------------------------
    [Fact(DisplayName = "Reactor fires ClientConnected and ClientDisconnected")]
    public async Task Reactor_Fires_Connection_Events()
    {
        string name = Channel();
        var connected = new TaskCompletionSource<IParticle>();
        var disconnected = new TaskCompletionSource<IParticle>();

        using var reactor = new InprocReactor(name);
        reactor.ClientConnected += p => connected.TrySetResult(p);
        reactor.ClientDisconnected += p => disconnected.TrySetResult(p);
        reactor.Start();

        var client = new InprocParticle(name, isServer: false);
        var serverParticle = await connected.Task.WaitAsync(_cts.Token);
        Assert.NotNull(serverParticle);

        await Task.Delay(100, _cts.Token);
        client.Dispose();

        var disc = await disconnected.Task.WaitAsync(_cts.Token);
        Assert.NotNull(disc);
    }

    // ------------------------------------------------------------
    // 4. Multiple clients connect and all messages are received
    // ------------------------------------------------------------
    [Fact(DisplayName = "Multiple clients connect and messages received")]
    public async Task Multiple_Clients_Messages_Received()
    {
        string name = Channel();
        int totalMessages = 0;
        var connectedCount = 0;
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reactor = new InprocReactor(name);
        reactor.ClientConnected += _ =>
        {
            if (Interlocked.Increment(ref connectedCount) == 3)
                connected.TrySetResult(true);
        };
        reactor.OnReceived = (p, data) => Interlocked.Increment(ref totalMessages);
        reactor.Start();

        var clients = Enumerable.Range(0, 3)
            .Select(_ => new InprocParticle(name, false))
            .ToList();

        await connected.Task.WaitAsync(_cts.Token);

        foreach (var c in clients)
            c.Send(Utf8("ping"));

        await Task.Delay(200, _cts.Token);
        Assert.Equal(3, totalMessages);

        foreach (var c in clients)
            c.Dispose();
    }

    // ------------------------------------------------------------
    // 6. Stopping the reactor prevents new connections
    // ------------------------------------------------------------
    [Fact(DisplayName = "After Dispose, no new clients can connect")]
    public async Task Reactor_Cannot_Accept_After_Dispose()
    {
        string name = Channel();
        using var reactor = new InprocReactor(name);
        reactor.Start();

        var client1 = new InprocParticle(name, false);
        await Task.Delay(100, _cts.Token);

        reactor.Dispose();

        Assert.Throws<InvalidOperationException>(() =>
        {
            var lateClient = new InprocParticle(name, false);
        });

        client1.Dispose();
    }

    // ------------------------------------------------------------
    // 7. Reactor handles back-to-back rapid connects
    // ------------------------------------------------------------
    [Fact(DisplayName = "Reactor handles rapid connects")]
    public async Task Reactor_Handles_Rapid_Connects()
    {
        string name = Channel();
        int count = 0;
        using var reactor = new InprocReactor(name);
        reactor.ClientConnected += _ => Interlocked.Increment(ref count);
        reactor.Start();

        var clients = Enumerable.Range(0, 10)
            .Select(_ => new InprocParticle(name, false))
            .ToList();

        await Task.Delay(200, _cts.Token);
        Assert.Equal(10, count);

        foreach (var c in clients)
            c.Dispose();
    }

    // ------------------------------------------------------------
    // 8. OnReceived can reply back to client through server particle
    // ------------------------------------------------------------
    [Fact(DisplayName = "Reactor can reply to client via server particle")]
    public async Task Reactor_Can_Reply_To_Client()
    {
        string name = Channel();
        var replyReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reactor = new InprocReactor(name);
        reactor.OnReceived = (p, data) =>
        {
            // Echo message back
            p.Send(Utf8("pong"));
        };
        reactor.Start();

        using var client = new InprocParticle(name, false);
        client.OnReceived = (p, data) => replyReceived.TrySetResult(Str(data));

        await Task.Delay(100, _cts.Token);
        client.Send(Utf8("ping"));

        var result = await replyReceived.Task.WaitAsync(_cts.Token);
        Assert.Equal("pong", result);
    }

    // ------------------------------------------------------------
    // 10. Removing client cleans up properly
    // ------------------------------------------------------------
    [Fact(DisplayName = "Removing a client cleans it up")]
    public async Task Removing_Client_Cleans_Up()
    {
        string name = Channel();
        using var reactor = new InprocReactor(name);
        reactor.Start();

        var client = new InprocParticle(name, false);
        await Task.Delay(100, _cts.Token);

        // When client disconnects, should not crash or leak
        client.Dispose();
        await Task.Delay(100, _cts.Token);

        // Reactor should remain operational
        var anotherClient = new InprocParticle(name, false);
        await Task.Delay(50, _cts.Token);
        anotherClient.Dispose();
    }
}