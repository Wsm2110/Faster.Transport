using Faster.Transport.Ipc;
using System;
using System.Text;
using System.Threading;

class Program
{
    static void Main()
    {
        const string Base = "FasterIpcDemo";
        var server = new MappedReactor(Base);
        server.OnConnected += id => Console.WriteLine($"[SERVER] Client {id:X16} connected");
        server.OnReceived += (particle, mem) =>
        {
            var msg = Encoding.UTF8.GetString(mem.Span);
            // Console.WriteLine($"[SERVER] <- {id:X16}: {msg}");
            particle.Send(Encoding.UTF8.GetBytes("echo:" + msg));
        };
        server.Start();

        var c1 = new MappedParticle(Base, 0xA1UL);
        var c2 = new MappedParticle(Base, 0xB2UL);

        c1.OnReceived += (particale, mem) => Console.WriteLine($"[C1] <- {Encoding.UTF8.GetString(mem.Span)}");

        c1.Start();
        c2.Start();

        Console.WriteLine("Type '1 hi' or '2 hi' to send from client1/client2. Empty to quit.");
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            if (line.Length > 2 && (line[0] == '1' || line[0] == '2') && line[1] == ' ')
            {
                var text = line[2..];
                var bytes = Encoding.UTF8.GetBytes(text);
                if (line[0] == '1') c1.Send(bytes);
                else c2.Send(bytes);
            }
        }

        c1.Dispose(); c2.Dispose(); server.Dispose();
    }
}
