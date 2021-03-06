﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;
using Microsoft.Xna.Framework.GamerServices;

namespace Microsoft.Xna.Framework.Net
{
    public sealed partial class NetworkSession : IDisposable
    {
        private static readonly TimeSpan NoMessageSleep = TimeSpan.FromSeconds(0.1);
        private static readonly TimeSpan DiscoveryTimeOut = TimeSpan.FromSeconds(4.0);
        private static readonly TimeSpan JoinTimeOut
            = TimeSpan.FromSeconds(15.0);

        private static NetworkSession InternalCreate(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties)
        {
            var config = new NetPeerConfiguration(NetworkSettings.GameAppId)
            {
                Port = NetworkSettings.Port,
                AcceptIncomingConnections = true,
                AutoFlushSendQueue = false,
            };
//#if DEBUG
//            config.ConnectionTimeout = 120f;
//#endif
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            if (sessionType == NetworkSessionType.SystemLink)
            {
                config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
            }
            if (sessionType == NetworkSessionType.PlayerMatch || sessionType == NetworkSessionType.Ranked)
            {
                config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            }

            var serverPeer = new NetPeer(config);
            try
            {
                serverPeer.Start();
            }
            catch (Exception e)
            {
                throw new NetworkException("Could not start server peer", e);
            }
            Debug.WriteLine("Server peer started.");

            var firstGamer = localGamers.First();

            return new NetworkSession(serverPeer,
                true,
                0,
                sessionType,
                sessionProperties,
                maxGamers,
                privateGamerSlots,
                localGamers,
                firstGamer.DisplayName,
                firstGamer.Gamertag);
        }

        private static IPEndPoint GetInternalIp(NetPeer peer)
        {
            IPAddress mask;
            IPAddress address = NetUtility.GetMyAddress(out mask);
            return new IPEndPoint(address, peer.Port);
        }

        private static NetworkSession InternalJoin(AvailableNetworkSession availableSession)
        {
            var config = new NetPeerConfiguration(NetworkSettings.GameAppId)
            {
                Port = 0, // Use any port
                AcceptIncomingConnections = false,
                AutoFlushSendQueue = true,
            };
//#if DEBUG
//            config.ConnectionTimeout = 120f;
//#endif
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);

            var clientPeer = new NetPeer(config);
            try
            {
                clientPeer.Start();
            }
            catch (Exception e)
            {
                throw new NetworkException("Could not start client peer", e);
            }
            Debug.WriteLine("Client peer started.");

            if (availableSession.SessionType == NetworkSessionType.SystemLink)
            {
                clientPeer.Connect((IPEndPoint)availableSession.Tag);
            }
            else if (availableSession.SessionType == NetworkSessionType.PlayerMatch || availableSession.SessionType == NetworkSessionType.Ranked)
            {
                clientPeer.Configuration.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
                NetworkMasterServer.RequestIntroduction(clientPeer, availableSession.HostGuid, GetInternalIp(clientPeer));
            }
            else
            {
                clientPeer.Shutdown("Failed to connect");
                throw new InvalidOperationException();
            }

            bool triedConnect = false;
            bool success = false;
            byte machineId = 255;
            var joinError = NetworkSessionJoinError.SessionNotFound;
            var startTime = DateTime.Now;
            while (DateTime.Now - startTime <= JoinTimeOut)
            {
                var msg = clientPeer.ReadMessage();
                if (msg == null)
                {
                    Thread.Sleep((int)NoMessageSleep.TotalMilliseconds);
                    continue;
                }

                if (msg.MessageType == NetIncomingMessageType.NatIntroductionSuccess)
                {
                    if (!triedConnect)
                    {
                        clientPeer.Connect(msg.SenderEndPoint);
                        triedConnect = true;
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = msg.ReadString();

                    if (status == NetConnectionStatus.Connected)
                    {
                        if (msg.SenderConnection.RemoteHailMessage != null &&
                            msg.SenderConnection.RemoteHailMessage.LengthBytes >= 1)
                        {
                            success = true;
                            machineId = msg.SenderConnection.RemoteHailMessage.ReadByte();
                        }
                        break;
                    }
                    else if (status == NetConnectionStatus.Disconnected)
                    {
                        NetworkSessionJoinError error;
                        if (Enum.TryParse(reason, out error))
                        {
                            joinError = error;
                        }
                    }
                }
                else
                {
                    HandleLidgrenMessage(msg);
                }
                clientPeer.Recycle(msg);
            }
            if (!success || machineId == 255)
            {
                clientPeer.Shutdown("Failed to connect");
                throw new NetworkSessionJoinException("Could not connect to host", joinError);
            }

            // Setup client
            if (availableSession.SessionType == NetworkSessionType.PlayerMatch || availableSession.SessionType == NetworkSessionType.Ranked)
            {
                clientPeer.Configuration.DisableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            }
            clientPeer.Configuration.AutoFlushSendQueue = false;

            return new NetworkSession(clientPeer,
                false,
                machineId,
                availableSession.SessionType,
                availableSession.SessionProperties,
                availableSession.MaxGamers,
                availableSession.PrivateGamerSlots,
                availableSession.LocalGamers,
                availableSession.HostGamertag, // TODO: Store real display name here instead of gamertag
                availableSession.HostGamertag);
        }

        private static void AddAvailableNetworkSession(Guid hostGuid, NetworkSessionPublicInfo publicInfo, IEnumerable<SignedInGamer> localGamers, NetworkSessionType searchType, NetworkSessionProperties searchProperties, IList<AvailableNetworkSession> availableSessions, object tag = null)
        {
            if (searchType == publicInfo.sessionType && searchProperties.SearchMatch(publicInfo.sessionProperties))
            {
                var availableSession = new AvailableNetworkSession(
                    hostGuid,
                    localGamers,
                    publicInfo.maxGamers,
                    publicInfo.privateGamerSlots,
                    publicInfo.sessionType,
                    publicInfo.currentGamerCount,
                    publicInfo.hostGamertag,
                    publicInfo.openPrivateGamerSlots,
                    publicInfo.openPublicGamerSlots,
                    publicInfo.sessionProperties)
                {
                    Tag = tag,
                };
                availableSessions.Add(availableSession);
            }
        }

        private static AvailableNetworkSessionCollection InternalFind(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties searchProperties)
        {
            var config = new NetPeerConfiguration(NetworkSettings.GameAppId)
            {
                Port = 0, // Use any port
                AcceptIncomingConnections = false,
            };
//#if DEBUG
//            config.ConnectionTimeout = 120f;
//#endif
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);

            var discoverPeer = new NetPeer(config);
            try
            {
                discoverPeer.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Could not start discover peer", e);
            }
            Debug.WriteLine("Discover peer started.");

            // Discover hosts
            if (sessionType == NetworkSessionType.SystemLink)
            {
                Debug.WriteLine("Sending local discovery request...");
                discoverPeer.DiscoverLocalPeers(NetworkSettings.Port);
            }
            else if (sessionType == NetworkSessionType.PlayerMatch || sessionType == NetworkSessionType.Ranked)
            {
                Debug.WriteLine("Sending discovery request to master server...");
                NetworkMasterServer.RequestHosts(discoverPeer);
            }
            else
            {
                throw new InvalidOperationException();
            }

            // Get list of answers
            var availableSessions = new List<AvailableNetworkSession>();

            var startTime = DateTime.Now;
            while (DateTime.Now - startTime <= DiscoveryTimeOut)
            {
                var msg = discoverPeer.ReadMessage();
                if (msg == null)
                {
                    Thread.Sleep((int)NoMessageSleep.TotalMilliseconds);
                    continue;
                }

                if (msg.MessageType == NetIncomingMessageType.DiscoveryResponse)
                {
                    Guid guid;
                    NetworkSessionPublicInfo publicInfo;
                    if (NetworkMasterServer.ParseExpectedResponseHeader(msg, MasterServerMessageType.RequestHosts) &&
                        NetworkMasterServer.ParseRequestHostsResponse(msg, out guid, out publicInfo))
                    {
                        AddAvailableNetworkSession(guid, publicInfo, localGamers, sessionType, searchProperties, availableSessions, tag: msg.SenderEndPoint);
                    }
                    else
                    {
                        Debug.WriteLine("Failed to parse local discovery response from " + msg.SenderEndPoint + ", ignoring...");
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.UnconnectedData)
                {
                    if (!msg.SenderEndPoint.Equals(NetworkMasterServer.ResolveEndPoint()))
                    {
                        Debug.WriteLine("Unconnected data not from master server recieved from " + msg.SenderEndPoint + ", ignoring...");
                    }
                    else
                    {
                        Guid guid;
                        NetworkSessionPublicInfo publicInfo;
                        if (NetworkMasterServer.ParseExpectedResponseHeader(msg, MasterServerMessageType.RequestHosts) &&
                            NetworkMasterServer.ParseRequestHostsResponse(msg, out guid, out publicInfo))
                        {
                            AddAvailableNetworkSession(guid, publicInfo, localGamers, sessionType, searchProperties, availableSessions);
                        }
                        else
                        {
                            Debug.WriteLine("Failed to parse master server discovery response from " + msg.SenderEndPoint + ", ignoring...");
                        }
                    }
                }
                else
                {
                    HandleLidgrenMessage(msg);
                }
                discoverPeer.Recycle(msg);
            }
            discoverPeer.Shutdown(string.Empty);
            Debug.WriteLine("Discovery peer shut down.");

            return new AvailableNetworkSessionCollection(availableSessions);
        }
    }
}
