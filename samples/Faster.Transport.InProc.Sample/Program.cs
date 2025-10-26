using Faster.Transport.Inproc;
using System.Text;

// Entry point
await RunInprocMultiClientDemo();

static async Task RunInprocMultiClientDemo()
{
    Console.WriteLine("▶ Starting multi-client In-Proc demo...");

    // Create a shared in-process reactor (server hub)
    var hub = new InprocReactor("faster-demo-inproc");

    // Handle new client connections
    hub.OnReceived += (p, data) =>
    {
    
        var msg = Encoding.UTF8.GetString(data.Span);
        Console.WriteLine($"[SERVER] Received: {msg}");

        // Echo back a response
        p.Send(Encoding.UTF8.GetBytes($"pong: {msg}"));

    };

    hub.Start();

    // Simulate multiple concurrent clients
    const int clientCount = 4;
   
    for (int i = 0; i < clientCount; i++)
    {
        var client = new InprocParticle("faster-demo-inproc", isServer: false);
        var id = i;
        client.OnReceived = (_, data) =>
        {           
            var text = Encoding.UTF8.GetString(data.Span);
            Console.WriteLine($"[CLIENT {id}] Got: {text}");
        };

        client.Send(Encoding.UTF8.GetBytes($"ping: client-{id}"));
    }

    // Give time for connections to establish
    await Task.Delay(200);

  
    Console.ReadLine();


    hub.Dispose();

    Console.WriteLine("✔ Multi-client In-Proc demo complete");
}
