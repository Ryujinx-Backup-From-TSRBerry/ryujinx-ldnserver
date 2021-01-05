using NetCoreServer;
using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanPlayServer
{
    abstract class AbstractLdnServer
    {
        public static readonly int InactivityPingFrequency = 10000;

        public ConcurrentDictionary<string, HostedGame> HostedGames = new ConcurrentDictionary<string, HostedGame>();
        public MacAddressMemory MacAddresses { get; } = new MacAddressMemory();
        public bool UseProxy => true;

        private CancellationTokenSource _cancel = new CancellationTokenSource();

        public AbstractLdnServer(IPAddress address, int port)
        { 
            //Task.Run(BackgroundPingTask);
        }

        public abstract void Start();
        public abstract void Restart();

        public HostedGame CreateGame(string id, NetworkInfo info, AddressList dhcpConfig, string oldOwnerID)
        {
            HostedGame game = new HostedGame(id, info, dhcpConfig);
            bool idTaken = false;

            HostedGames.AddOrUpdate(id, game, (id, oldGame) =>
            {
                if (oldGame.OwnerId == oldOwnerID)
                {
                    oldGame.Close();

                    return game;
                }
                else
                {
                    game.Close();
                    idTaken = true;

                    return oldGame;
                }
            });

            if (idTaken)
            {
                return null;
            }

            return game;
        }

        public HostedGame FindGame(string id)
        {
            HostedGames.TryGetValue(id, out HostedGame result);

            return result;
        }

        public KeyValuePair<string, HostedGame>[] All()
        {
            return HostedGames.ToArray();
        }

        public int Scan(ref NetworkInfo[] info, ScanFilter filter, string passphrase)
        {
            KeyValuePair<string, HostedGame>[] all = HostedGames.ToArray();

            int results = 0;

            for (int i = 0; i < all.Length; i++)
            {
                HostedGame game = all[i].Value;

                if (game.Passphrase != passphrase)
                {
                    continue;
                }

                NetworkInfo scanInfo = game.Info;

                if (scanInfo.Ldn.StationAcceptPolicy == 1)
                {
                    // Optimization: don't tell anyone about unjoinable networks.

                    continue;
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.LocalCommunicationId))
                {
                    if (scanInfo.NetworkId.IntentId.LocalCommunicationId != filter.NetworkId.IntentId.LocalCommunicationId)
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.SceneId))
                {
                    if (scanInfo.NetworkId.IntentId.SceneId != filter.NetworkId.IntentId.SceneId)
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.SessionId))
                {
                    if (!scanInfo.NetworkId.SessionId.SequenceEqual(filter.NetworkId.SessionId))
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.Ssid))
                {
                    IEnumerable<byte> gameSsid = scanInfo.Common.Ssid.Name.Take(scanInfo.Common.Ssid.Length);
                    IEnumerable<byte> scanSsid = filter.Ssid.Name.Take(filter.Ssid.Length);
                    if (!gameSsid.SequenceEqual(scanSsid))
                    {
                        continue;
                    }
                }

                if (filter.Flag.HasFlag(ScanFilterFlag.NetworkType))
                {
                    if (scanInfo.Common.NetworkType != (byte)filter.NetworkType)
                    {
                        continue;
                    }
                }

                // Mac address filter not implemented, since they are currently random.

                if (results >= info.Length)
                {
                    Array.Resize(ref info, info.Length + 1);
                }

                info[results++] = scanInfo;
            }

            return results;
        }

        public void CloseGame(string id)
        {
            HostedGames.Remove(id, out HostedGame removed);
            removed?.Close();
        }

        /*
        protected override TcpSession CreateSession()
        { 
            return new LdnSession(this);
        }
        */

        protected void OnError(SocketError error)
        {
            Console.WriteLine($"LDN TCP server caught an error with code {error}");
        }

        public virtual void Stop()
        {
            _cancel.Cancel();
        }

        /*
        private async Task BackgroundPingTask()
        {
            while (!IsDisposed)
            {
                foreach (KeyValuePair<Guid, TcpSession> session in Sessions)
                {
                    (session.Value as LdnSession).Ping();
                }

                try
                {
                    await Task.Delay(InactivityPingFrequency, _cancel.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
        */
    }
}