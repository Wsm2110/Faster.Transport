using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faster.Transport.Ipc;
using Xunit;

namespace Faster.Transport.Tests.Ipc
{
    public class MappedIpcTests : IDisposable
    {
        private readonly string _baseName;
        private readonly MappedReactor _server;
        private readonly MappedParticle _client;
        private readonly ManualResetEventSlim _serverReceived = new(false);
        private readonly ManualResetEventSlim _clientReceived = new(false);
        private string? _lastServerMsg;
        private string? _lastClientMsg;

        public MappedIpcTests()
        {
            _baseName = "IpcTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            _server = new MappedReactor(_baseName, global: false);
            _client = new MappedParticle(_baseName, (ulong)Random.Shared.NextInt64(), global: false);

            // server setup
            _server.OnConnected += id =>
            {
                // send welcome message once client registered
                _server.Send(id, Encoding.UTF8.GetBytes("WELCOME"));
            };

            _server.OnReceived = (p, mem) =>
            {
                _lastServerMsg = Encoding.UTF8.GetString(mem.Span);
                _serverReceived.Set();

                // echo back to all
                _server.Broadcast(mem.Span);
            };

            // client setup
            _client.OnReceived = (p, mem) =>
            {
                _lastClientMsg = Encoding.UTF8.GetString(mem.Span);
                _clientReceived.Set();
            };

            _server.Start();
            _client.Start();

            // give time for registry thread to detect client
            Thread.Sleep(200);
        }

        [Fact(DisplayName = "Client registers and receives welcome message")]
        public void Client_Should_Register_And_Receive_Welcome()
        {
            Assert.True(SpinWait.SpinUntil(() => _lastClientMsg == "WELCOME", 2000),
                "Client did not receive welcome message from server");
        }

        [Fact(DisplayName = "Server receives message from client")]
        public void Server_Should_Receive_Client_Message()
        {
            string message = "PING-" + Guid.NewGuid().ToString("N")[..6];
            var data = Encoding.UTF8.GetBytes(message);
            _client.Send(data);

            Assert.True(_serverReceived.Wait(2000), "Server did not receive any message");
            Assert.Equal(message, _lastServerMsg);
        }

        [Fact(DisplayName = "Client receives echo from server")]
        public void Client_Should_Receive_Echo()
        {
            string message = "HELLO";
            var data = Encoding.UTF8.GetBytes(message);
            _client.Send(data);

            Assert.True(_clientReceived.Wait(2000), "Client did not receive echo");
            Assert.Equal(message, _lastClientMsg);
        }

        [Fact(DisplayName = "Server can broadcast to multiple clients")]
        public void Server_Should_Broadcast_To_All()
        {
            // create a second client
            var client2 = new MappedParticle(_baseName, (ulong)Random.Shared.NextInt64(), global: false);
            string? received2 = null;
            var ev2 = new ManualResetEventSlim();

            client2.OnReceived = (_, mem) =>
            {
                received2 = Encoding.UTF8.GetString(mem.Span);
                ev2.Set();
            };

            client2.Start();
            Thread.Sleep(200);

            string broadcastMsg = "BROADCAST-" + Guid.NewGuid().ToString("N")[..4];
            _server.Broadcast(Encoding.UTF8.GetBytes(broadcastMsg));

            Assert.True(_clientReceived.Wait(2000), "Client1 did not receive broadcast");
            Assert.True(ev2.Wait(2000), "Client2 did not receive broadcast");

            Assert.Equal(broadcastMsg, _lastClientMsg);
            Assert.Equal(broadcastMsg, received2);

            client2.Dispose();
        }

        [Fact(DisplayName = "Client disconnect triggers server cleanup")]
        public void Client_Disconnect_Should_Remove_From_Server()
        {
            bool disconnected = false;
            ulong? disconnectedId = null;
            _server.OnClientDisconnected = id =>
            {
                disconnectedId = id;
                disconnected = true;
            };

            ulong clientId = (ulong)Random.Shared.NextInt64();
            var client = new MappedParticle(_baseName, clientId, global: false);
            client.Start();
            Thread.Sleep(300);

            client.Dispose(); // simulate disconnection
            Thread.Sleep(200);

            Assert.True(disconnected, "Server did not detect client disconnect");
            Assert.NotNull(disconnectedId);
        }

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
            _serverReceived.Dispose();
            _clientReceived.Dispose();
        }
    }
}
