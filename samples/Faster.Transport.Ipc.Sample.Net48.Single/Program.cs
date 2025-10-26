
using System;
using System.Text;
using Faster.Transport.SharedMemory.Particles;

namespace Demo
{
    class Program
    {
        static void Main()
        {
            const string Map = "Global\\FasterIpc.Single";
            var server = new SharedServerParticle(Map);
            server.OnReceived = (s, data) =>
            {
                var text = Encoding.UTF8.GetString(data.Span);
                Console.WriteLine("[Server] " + text);
                s.Send(Encoding.UTF8.GetBytes("echo:" + text));
            };
            server.Start();

            var client = new SharedClientParticle(Map);
            client.OnReceived = (c, data) => Console.WriteLine("[Client] " + Encoding.UTF8.GetString(data.Span));
            client.Start();
            client.Send(Encoding.UTF8.GetBytes("hello single"));

            Console.WriteLine("Press ENTER to exit...");
            Console.ReadLine();
            server.Dispose();
            client.Dispose();
        }
    }
}
