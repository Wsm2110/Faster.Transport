
using System;
using System.Text;
using System.Threading;
using Faster.Transport.SharedMemory;
using Faster.Transport.SharedMemory.Server;
using Faster.Transport.Contracts;

namespace Demo
{
    class Program
    {
        static void Main()
        {
            var server = new SharedServerHost();
            server.OnClientConnected += (IParticle p) =>
            {
                Console.WriteLine("Server: client connected");
                p.OnReceived = (conn, bytes) =>
                {
                    var text = Encoding.UTF8.GetString(bytes.Span);
                    Console.WriteLine("Server <= " + text);
                    conn.Send(Encoding.UTF8.GetBytes("echo:" + text));
                };
            };
            server.Start();

            for (int i = 0; i < 3; i++)
            {
                new Thread(() =>
                {
                    var client = SharedClientFactory.CreateAndRegister();
                    client.OnReceived = (c, bytes) => Console.WriteLine("Client => " + Encoding.UTF8.GetString(bytes.Span));
                    client.Start();
                    client.Send(Encoding.UTF8.GetBytes("hello from client"));
                }) { IsBackground = true }.Start();
            }

            Console.WriteLine("Press ENTER to exit...");
            Console.ReadLine();
            server.Dispose();
        }
    }
}
