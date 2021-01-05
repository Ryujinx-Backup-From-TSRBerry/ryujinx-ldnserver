using NetCoreServer;
using System;
using System.Net;
using System.Net.Sockets;

namespace LanPlayServer
{
    class ApiServer : HttpServer
    {
        readonly AbstractLdnServer _ldnServer;

        public ApiServer(IPAddress address, int port, AbstractLdnServer ldnServer) : base(address, port)
        {
            _ldnServer = ldnServer;
        }

        protected override TcpSession CreateSession()
        { 
            return new ApiSession(this, _ldnServer);
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"HTTP session caught an error: {error}");
        }
    }
}