using LanPlayServer.Network;
using LanPlayServer.Network.Types;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace LanPlayServer
{
    class HostedGame
    {
        private const uint NetworkBaseAddress = 0x0a720000; // 10.114.0.0 (our "virtual network")
        private const uint NetworkSubnetMask  = 0xffff0000; // 255.255.0.0

        private ReaderWriterLockSlim _lock;

        private List<LdnSession> _players;
        private VirtualDhcp      _dhcp;

        private RyuLdnProtocol _protocol;

        private bool                _closed;
        private ExternalProxyConfig _externalConfig;
        private ExternalProxyConfig _privateConfig;

        private NetworkInfo _info;
        public NetworkInfo Info
        {
            get
            {
                _lock.EnterReadLock();

                NetworkInfo result = _info;

                _lock.ExitReadLock();

                return result;
            }
        }

        public string Id { get; }

        public LdnSession Owner { get; private set; }
        public string OwnerId => Owner.StringId;

        private string _passphrase;
        public string Passphrase
        {
            get
            {
                _lock.EnterReadLock();

                string result = _passphrase;

                _lock.ExitReadLock();

                return result;
            }
        }

        private string _gameVersion;
        public string GameVersion
        {
            get
            {
                _lock.EnterReadLock();

                string result = _gameVersion;

                _lock.ExitReadLock();

                return result;
            }
        }

        public int Players
        {
            get
            {
                int result;

                _lock.EnterReadLock();

                result = _players.Count;

                _lock.ExitReadLock();

                return result;
            }
        }

        public bool IsP2P { get; private set; }

        public HostedGame(string id, NetworkInfo info, AddressList dhcpConfig)
        {
            Id = id;

            _lock    = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            _players = new List<LdnSession>();
            _dhcp    = new VirtualDhcp(NetworkBaseAddress, NetworkSubnetMask, dhcpConfig);

            _protocol = new RyuLdnProtocol();

            UpdateNetworkInfo(info);
        }

        public void UpdateNetworkInfo(NetworkInfo info)
        {
            _lock.EnterWriteLock();

            _info = info;

            _lock.ExitWriteLock();
        }

        private byte[] AddressTo16Byte(IPAddress address)
        {
            byte[] ipBytes  = new byte[16];
            byte[] srcBytes = address.GetAddressBytes();

            Array.Copy(srcBytes, 0, ipBytes, 0, srcBytes.Length);

            return ipBytes;
        }

        public void SetOwner(LdnSession session, RyuNetworkConfig request)
        {
            _lock.EnterWriteLock();

            Owner       = session;
            _passphrase = session.Passphrase;

            _gameVersion = Encoding.UTF8.GetString(request.GameVersion, 0, request.GameVersion.Length).Trim('\0');

            if (request.ExternalProxyPort != 0)
            {
                IsP2P = true;

                IPAddress address = session.RemoteEndPoint.Address;

                _externalConfig = new ExternalProxyConfig()
                {
                    ProxyIp       = AddressTo16Byte(address),
                    AddressFamily = address.AddressFamily,
                    ProxyPort     = request.ExternalProxyPort,
                    Token         = new byte[10]
                };

                _privateConfig = new ExternalProxyConfig()
                {
                    ProxyIp       = request.PrivateIp,
                    AddressFamily = request.AddressFamily,
                    ProxyPort     = request.InternalProxyPort,
                    Token         = new byte[10]
                };
            }

            _lock.ExitWriteLock();
        }

        private void InitExternalProxy(LdnSession session)
        {
            IPAddress address          = session.RemoteEndPoint.Address;
            byte[]    addressBytes     = AddressTo16Byte(address);
            bool      sessionIsPrivate = address.AddressFamily == _externalConfig.AddressFamily && addressBytes.SequenceEqual(_externalConfig.ProxyIp);
            byte[]    token            = LdnHelper.StringToByteArray(Guid.NewGuid().ToString().Replace("-", ""));

            // The proxy host needs to know about the new joiner.

            ExternalProxyToken tokenMsg = new ExternalProxyToken
            {
                VirtualIp     = session.IpAddress,
                PhysicalIp    = sessionIsPrivate ? new byte[16] : addressBytes,
                AddressFamily = address.AddressFamily,
                Token         = token
            };

            Owner.SendAsync(_protocol.Encode(PacketId.ExternalProxyToken, tokenMsg));

            // Tell the joiner about the new proxy host.

            ExternalProxyConfig configCopy = sessionIsPrivate ? _privateConfig : _externalConfig;

            configCopy.Token = token;

            session.SendAsync(_protocol.Encode(PacketId.ExternalProxy, configCopy));
        }

        public bool Connect(LdnSession session, NodeInfo node)
        {
            _lock.EnterWriteLock();

            if (_closed || _info.Ldn.NodeCount == _info.Ldn.NodeCountMax)
            {
                _lock.ExitWriteLock();

                return false;
            }

            uint ip = _dhcp.RequestIpV4(session.MacAddress);
            if (!session.SetIpV4(ip, NetworkSubnetMask, !IsP2P))
            {
                _dhcp.ReturnIpV4(ip);
            }

            node.Ipv4Address = session.IpAddress;

            // Add the client to the network info.
            int nodeId = LocateEmptyNode();
            _info.Ldn.NodeCount++;

            node.NodeId = (byte)nodeId;
            session.NodeId = nodeId;

            _info.Ldn.Nodes[nodeId] = node;

            if (IsP2P)
            {
                InitExternalProxy(session);
            }

            BroadcastNetworkInfoInLock();

            session.CurrentGame = this;

            _players.Add(session);

            session.SendAsync(_protocol.Encode(PacketId.Connected, _info));

            _lock.ExitWriteLock();

            return true;
        }

        public void RemoveFromInfo(uint ip)
        {
            ref LdnNetworkInfo ldn       = ref _info.Ldn;
            bool               removed   = false;
            int                nodeCount = ldn.NodeCount;

            for (int i = 0; i < nodeCount; i++)
            {
                if (ldn.Nodes[i].Ipv4Address == ip)
                {
                    ldn.NodeCount--;
                    ldn.Nodes[i].IsConnected = 0;
                    removed = true;
                }

                /*
                if (removed)
                {
                    // Move the next client into this space. Keep doing this repeatedly.
                    if (i + 1 < ldn.NodeCountMax)
                    {
                        ldn.Nodes[i] = ldn.Nodes[i + 1];
                    }
                    else
                    {
                        ldn.Nodes[i] = new NodeInfo();
                    }
                }
                */
            }
        }

        private int LocateEmptyNode()
        {
            NodeInfo[] nodes = Info.Ldn.Nodes;

            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].IsConnected == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        // Proxy handlers

        private void RouteMessage(LdnSession sender, ref ProxyInfo info, Action<LdnSession> action)
        {
            if (info.SourceIpV4 == 0)
            {
                // If they sent from a connection bound on 0.0.0.0, make others see it as them.
                info.SourceIpV4 = sender.IpAddress;
            } 
            else if (info.SourceIpV4 != sender.IpAddress)
            {
                // Can't pretend to be somebody else.
                return;
            }

            uint destIp = info.DestIpV4;

            if (destIp == 0xc0a800ff)
            {
                destIp = _dhcp.BroadcastAddress;
            }

            bool isBroadcast = destIp == _dhcp.BroadcastAddress;

            _lock.EnterReadLock();

            if (isBroadcast)
            {
                _players.ForEach(player =>
                {
                    action(player);
                });
            }
            else
            {
                LdnSession target = _players.FirstOrDefault(player => player.IpAddress == destIp);
                
                if (target != null)
                {
                    action(target);
                }
            }

            _lock.ExitReadLock();
        }

        public void HandleReject(LdnSession sender, LdnHeader header, RejectRequest reject)
        {
            if (sender == Owner)
            {
                _lock.EnterWriteLock();

                if (reject.NodeId >= _players.Count)
                {
                    _lock.ExitWriteLock();

                    sender.SendAsync(_protocol.Encode(PacketId.NetworkError, new NetworkErrorMessage() { Error = NetworkError.RejectFailed }));
                }
                else
                {
                    Disconnect(_players.FirstOrDefault(player => player.NodeId == reject.NodeId), false);

                    _lock.ExitWriteLock();
                }
            } 
            else
            {
                sender.SendAsync(_protocol.Encode(PacketId.NetworkError, new NetworkErrorMessage() { Error = NetworkError.RejectFailed }));
            }

            sender.SendAsync(_protocol.Encode(PacketId.RejectReply));
        }

        public void HandleSetAcceptPolicy(LdnSession sender, LdnHeader header, SetAcceptPolicyRequest policy)
        {
            if (sender == Owner)
            {
                _lock.EnterWriteLock();

                _info.Ldn.StationAcceptPolicy = policy.StationAcceptPolicy;

                BroadcastNetworkInfoInLock();

                _lock.ExitWriteLock();
            }
        }

        public void HandleSetAdvertiseData(LdnSession sender, LdnHeader header, byte[] data)
        {
            if (sender == Owner)
            {
                _lock.EnterWriteLock();

                Array.Resize(ref data, 0x180);

                _info.Ldn.AdvertiseData = data;

                BroadcastNetworkInfoInLock();

                _lock.ExitWriteLock();
            }
        }

        public void HandleExternalProxyState(LdnSession sender, LdnHeader header, ExternalProxyConnectionState state)
        {
            _lock.EnterWriteLock();

            if (sender != Owner)
            {
                // Only the owner can update external proxy state.
                _lock.ExitWriteLock();

                return;
            }

            LdnSession player = _players.FirstOrDefault(player => player.IpAddress == state.IpAddress);

            if (player != null)
            {
                if (!state.Connected)
                {
                    // Indicate that the player is no longer connected to the game.

                    player.SendAsync(_protocol.Encode(PacketId.Disconnect, new DisconnectMessage()));

                    Disconnect(player, true);
                }
            }

            _lock.ExitWriteLock();
        }

        public void HandleProxyDisconnect(LdnSession sender, LdnHeader header, ProxyDisconnectMessage message)
        {
            RouteMessage(sender, ref message.Info, (target) =>
            {
                target.SendAsync(_protocol.Encode(PacketId.ProxyDisconnect, message));
            });
        }

        public void HandleProxyData(LdnSession sender, LdnHeader header, ProxyDataHeader message, byte[] data)
        {
            RouteMessage(sender, ref message.Info, (target) =>
            {
                target.SendAsync(_protocol.Encode(PacketId.ProxyData, message, data), message.Info.Protocol == System.Net.Sockets.ProtocolType.Tcp);
            });
        }

        public void HandleProxyConnectReply(LdnSession sender, LdnHeader header, ProxyConnectResponse message)
        {
            RouteMessage(sender, ref message.Info, (target) =>
            {
                target.SendAsync(_protocol.Encode(PacketId.ProxyConnectReply, message));
            });
        }

        public void HandleProxyConnect(LdnSession sender, LdnHeader header, ProxyConnectRequest message)
        {
            RouteMessage(sender, ref message.Info, (target) =>
            {
                target.SendAsync(_protocol.Encode(PacketId.ProxyConnect, message));
            });
        }

        public void Disconnect(LdnSession session, bool expected)
        {
            if (session == null)
            {
                return;
            }

            _lock.EnterWriteLock();

            _players.Remove(session);

            session.CurrentGame = null;

            if (IsP2P && !expected)
            {
                Owner.SendAsync(_protocol.Encode(PacketId.ExternalProxyState, new ExternalProxyConnectionState
                {
                    IpAddress = session.IpAddress,
                    Connected = false
                }));
            }

            _dhcp.ReturnIpV4(session.IpAddress);

            // Remove the client from the network info.
            RemoveFromInfo(session.IpAddress);

            BroadcastNetworkInfoInLock();

            _lock.ExitWriteLock();
        }

        // NOTE: Unused.
        private void BroadcastNetworkInfo()
        {
            _lock.EnterReadLock();

            BroadcastNetworkInfoInLock();

            _lock.ExitReadLock();
        }

        private void BroadcastNetworkInfoInLock()
        {
            BroadcastInLock(_protocol.Encode(PacketId.SyncNetwork, _info));
        }

        private void BroadcastInLock(byte[] buffer)
        {
            foreach (LdnSession player in _players)
            {
                player.SendAsync(buffer);
            }
        }

        public void Close()
        {
            _lock.EnterWriteLock();

            _closed = true;

            BroadcastInLock(_protocol.Encode(PacketId.Disconnect, new DisconnectMessage()));

            _lock.ExitWriteLock();
        }
    }
}