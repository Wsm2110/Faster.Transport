using Faster.Transport.Inproc;
using Faster.Transport.Ipc;
using System.Text;

await RunInprocDemo();

static async Task RunInprocDemo()
{
    Console.WriteLine("▶ Starting In-Proc demo...");

    var hub = new InprocReactor("faster-demo-inproc");
    hub.ClientConnected += serverParticle =>
    {
        Console.WriteLine("Inproc: client connected");
        serverParticle.OnReceived = (_,data) =>
        {
            Console.WriteLine($"Server got: {Encoding.UTF8.GetString(data.Span)}");
            serverParticle.SendAsync(Encoding.UTF8.GetBytes("pong")).AsTask().Wait();
        };
    };
    hub.Start();

    var client = new InprocParticle("faster-demo-inproc", isServer: false);
    client.OnReceived = (_,data) => Console.WriteLine($"Client got: {Encoding.UTF8.GetString(data.Span)}");

    await Task.Delay(200);

    await client.SendAsync(Encoding.UTF8.GetBytes("ping"));
    await Task.Delay(200);

    hub.Dispose();
    client.Dispose();

    Console.WriteLine("✔ In-Proc demo complete");
}