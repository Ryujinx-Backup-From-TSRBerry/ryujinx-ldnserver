using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Valve.Sockets;

namespace LanPlayServer.LdnServer.Backends
{
    class ValveNetworkingSession : LdnSession
    {
        private const SendFlags ReliableFlags = SendFlags.Reliable | SendFlags.NoNagle;
        private const SendFlags UnreliableFlags = SendFlags.Unreliable | SendFlags.NoNagle;

        private ValveNetworkingServer _server;
        private NetworkingSockets _sockets;
        private uint _connection;
        private ConnectionInfo _info;

        public override string Id { get; }

        public override IPEndPoint RemoteEndPoint { get; }

        public ValveNetworkingSession(ValveNetworkingServer server, NetworkingSockets sockets, uint connection, ConnectionInfo info) : base(server)
        {
            _server = server;
            _sockets = sockets;
            _connection = connection;
            _info = info;

            Id = Guid.NewGuid().ToString();
            RemoteEndPoint = new IPEndPoint(new IPAddress(info.address.ip), info.address.port);
        }

        public override void Disconnect()
        {
            _sockets.CloseConnection(_connection);
        }

        public override void SendAsync(byte[] data, bool reliable)
        {
            Result result = _sockets.SendMessageToConnection(_connection, data, reliable ? ReliableFlags : UnreliableFlags);

            if (result != Result.OK)
            {
                Console.WriteLine($"Failed to send message: {result}");
            }

            //Console.WriteLine($"Send took {time}ms. reliable: {reliable}");
        }
    }
}
