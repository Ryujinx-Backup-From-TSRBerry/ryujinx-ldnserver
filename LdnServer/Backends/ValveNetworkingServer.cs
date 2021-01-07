using System;
using System.Collections.Generic;
using System.Threading;
using Valve.Sockets;

namespace LanPlayServer.LdnServer.Backends
{
    class ValveNetworkingServer : AbstractLdnServer
	{
        private NetworkingSockets _server;
		private NetworkingUtils _utils;
        private uint _pollGroup;
		private uint _listenSocket;

		private Address _valveAddress;

		private bool _isAlive = true;
		private Thread _messageThread;

		private StatusCallback _statusDelegate;
		private MessageCallback _messageDelegate;

		static ValveNetworkingServer()
        {
			Valve.Sockets.Library.Initialize();
		}

		private Dictionary<uint, ValveNetworkingSession> SessionById = new Dictionary<uint, ValveNetworkingSession>();

		public ValveNetworkingServer(System.Net.IPAddress address, int port) : base(address, port)
        {
            _server = new NetworkingSockets();
			_utils = new NetworkingUtils();

			_statusDelegate = new StatusCallback(StatusCallback);
			_messageDelegate = new MessageCallback(MessageCallback);

			_pollGroup = _server.CreatePollGroup();

			_utils.SetStatusCallback(_statusDelegate);

			_valveAddress.SetAddress(address.ToString(), (ushort)port);
		}

		public override void Start()
		{
			_listenSocket = _server.CreateListenSocket(ref _valveAddress);

			if (_messageThread == null)
			{
				_messageThread = new Thread(ServerMessageLoop);
				_messageThread.Start();
			}
		}

		private void StatusCallback(ref StatusInfo info)
        {
			ValveNetworkingSession session;

			switch (info.connectionInfo.state)
			{
				case ConnectionState.None:
					break;

				case ConnectionState.Connecting:
					_server.AcceptConnection(info.connection);
					_server.SetConnectionPollGroup(_pollGroup, info.connection);

					session = new ValveNetworkingSession(this, _server, info.connection, info.connectionInfo);

					SessionById[info.connection] = session;

					break;

				case ConnectionState.Connected:
					Console.WriteLine("Client connected - ID: " + info.connection + ", IP: " + info.connectionInfo.address.GetIP());

					if (SessionById.TryGetValue(info.connection, out session))
					{
						session.OnConnected();
					}

					break;

				case ConnectionState.ClosedByPeer:
				case ConnectionState.ProblemDetectedLocally:
					_server.CloseConnection(info.connection);

					if (SessionById.TryGetValue(info.connection, out session))
                    {
						session.OnDisconnected();
						SessionById.Remove(info.connection);
                    }

					Console.WriteLine("Client disconnected - ID: " + info.connection + ", IP: " + info.connectionInfo.address.GetIP());
					break;
			}
		}

		private bool DidRecv = false;

		private void MessageCallback(in NetworkingMessage netMessage)
        {
			DidRecv = true;
			if (SessionById.TryGetValue(netMessage.connection, out ValveNetworkingSession session))
			{
				byte[] data = new byte[netMessage.length];

				netMessage.CopyTo(data);

				session.OnReceived(data, 0, netMessage.length);
			}
		}

		private void ServerMessageLoop()
        {
			while (_isAlive)
            {
				_server.RunCallbacks();
				var time1 = System.Diagnostics.Stopwatch.GetTimestamp();

				DidRecv = false;
				_server.ReceiveMessagesOnPollGroup(_pollGroup, _messageDelegate, 200);

				var time2 = System.Diagnostics.Stopwatch.GetTimestamp();

				long time = (time2 - time1) / (System.Diagnostics.Stopwatch.Frequency / 1000);

				if (DidRecv) Console.WriteLine($"Recv took {time}ms.");

				Thread.Sleep(1);
            }
        }

        public override void Restart()
        {
			_server.CloseListenSocket(_listenSocket);

			Start();
        }

        public override void Stop()
        {
			_server.CloseListenSocket(_listenSocket);

			_isAlive = false;
			_messageThread?.Join();
			_messageThread = null;

			base.Stop();
        }
    }
}
