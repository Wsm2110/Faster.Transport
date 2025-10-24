using Faster.Transport;
using Faster.Transport.Ipc;

await IpcDemo();

await IpcBuilderDemo();

async Task IpcDemo()
{
    // Create the server
    var server = new MappedParticle("MyChannel", true);

    var mre = new ManualResetEventSlim(false);

    server.OnReceived = (client, payload) =>
    {
        Console.WriteLine($"Server received: {System.Text.Encoding.UTF8.GetString(payload.Span)}");
        client.Send(System.Text.Encoding.UTF8.GetBytes("Hello from Server!"));
    };

    server.OnConnected = client =>
    {
        mre.Set();
    };

    Console.WriteLine("Server ready. Waiting for client...");

    // give server a bit of time to start

    // Connect to same channel
    var client = new MappedParticle("MyChannel", isServer: false);

    mre.Wait();

    client.OnReceived = (_, payload) =>
    {
        Console.WriteLine($"Client received: {System.Text.Encoding.UTF8.GetString(payload.Span)}");
    };


    await client.SendAsync(System.Text.Encoding.UTF8.GetBytes("Hello from client!"));

    Console.ReadLine();
}

async Task IpcBuilderDemo()
{
    var mre = new ManualResetEventSlim(false);

    var ipcServer = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("MyIpcChannel", isServer: true)
    .OnReceived((particle, data) =>
    {
        Console.WriteLine("IPC server received.");

        // return to client
        particle.Send(System.Text.Encoding.UTF8.GetBytes("Hello from IPC server!"));
    }).OnConnected(client =>
    {
        mre.Set();
    })
    .Build();

    var ipcClient = new ParticleBuilder()
    .UseMode(TransportMode.Ipc)
    .WithChannel("MyIpcChannel", isServer: false)
    .OnReceived((_, data) => Console.WriteLine("IPC client received."))
    .Build();

    mre.Wait();

    await ipcClient.SendAsync(System.Text.Encoding.UTF8.GetBytes("Hello from IPC client!"));


    Console.ReadLine();
}