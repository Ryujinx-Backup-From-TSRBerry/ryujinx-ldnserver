using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Valve.Sockets;

namespace LanPlayServer.LdnServer.Backends
{
    class ValveNetworkingSession : LdnSession
    {
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
            _sockets.SendMessageToConnection(_connection, data, (reliable ? SendFlags.Reliable : SendFlags.Unreliable) | SendFlags.NoDelay | SendFlags.NoNagle);
        }
    }
}
