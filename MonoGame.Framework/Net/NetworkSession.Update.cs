﻿using System;
using System.Diagnostics;
using Lidgren.Network;

namespace Microsoft.Xna.Framework.Net
{
    public sealed partial class NetworkSession : IDisposable
    {
        internal static void HandleLidgrenMessage(NetIncomingMessage msg)
        {
            switch (msg.MessageType)
            {
                case NetIncomingMessageType.VerboseDebugMessage:
                case NetIncomingMessageType.DebugMessage:
                    Debug.WriteLine("Lidgren: " + msg.ReadString());
                    break;
                case NetIncomingMessageType.WarningMessage:
                    Debug.WriteLine("Lidgren Warning: " + msg.ReadString());
                    break;
                case NetIncomingMessageType.ErrorMessage:
                    Debug.WriteLine("Lidgren Error: " + msg.ReadString());
                    break;
                default:
                    Debug.WriteLine("Unhandled message type: " + msg.MessageType);
                    break;
            }
        }

        private void ReceiveMessages()
        {
            NetIncomingMessage msg;
            while ((msg = peer.ReadMessage()) != null)
            {
                if (msg.MessageType == NetIncomingMessageType.DiscoveryRequest)
                {
                    Debug.WriteLine("Local discovery request received");

                    if (isHost)
                    {
                        UpdatePublicInfo();

                        NetworkMasterServer.SendRequestHostsResponse(peer, msg.SenderEndPoint, true, guid, publicInfo);
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.ConnectionApproval)
                {
                    if (!isHost)
                    {
                        throw new InvalidOperationException();
                    }

                    if (allowJoinInProgress || state == NetworkSessionState.Lobby)
                    {
                        byte machineId;
                        if (GetOpenPublicGamerSlots() > 0 && GetUniqueId(machineFromId, out machineId))
                        {
                            // Approved, create network machine
                            var machine = new NetworkMachine(this, false, false, machineId);
                            msg.SenderConnection.Tag = machine;
                            AddMachine(machine, msg.SenderConnection);

                            // Send approval to client containing unique machine id
                            var hailMsg = peer.CreateMessage();
                            hailMsg.Write(machineId);
                            msg.SenderConnection.Approve(hailMsg);
                        }
                        else
                        {
                            msg.SenderConnection.Deny(NetworkSessionJoinError.SessionFull.ToString());
                        }
                    }
                    else
                    {
                        msg.SenderConnection.Deny(NetworkSessionJoinError.SessionNotJoinable.ToString());
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)msg.ReadByte();
                    Debug.WriteLine("Connection status updated: " + status + " (Reason: " + msg.ReadString() + ")");

                    if (status == NetConnectionStatus.Connected)
                    {
                        if (!isHost)
                        {
                            throw new InvalidOperationException("A client cannot accept new connections");
                        }
                        if (msg.SenderConnection.Tag == null)
                        {
                            throw new InvalidOperationException();
                        }

                        var machine = (NetworkMachine)msg.SenderConnection.Tag;

                        SendMachineConnectedMessage(machine, null);
                    }
                    else if (status == NetConnectionStatus.Disconnected)
                    {
                        if (msg.SenderConnection != null)
                        {
                            if (msg.SenderConnection.Tag == null)
                            {
                                throw new InvalidOperationException();
                            }

                            var machineToRemove = (NetworkMachine)msg.SenderConnection.Tag;

                            bool removingHost = machineToRemove.isHost;

                            // Only remove the machine here if they are not the host/
                            // If they are th ehost, we will need to handle their removal after
                            // the new host has been assigned and the host changed message has been sent
                            if (!removingHost)
                            {
                                RemoveMachine(machineToRemove);
                            }

                            if (removingHost)
                            {
                                // am I the new host
                                isHost = NetworkMachine.AmNewHost(this, machineToRemove);
#if DEBUG
                                var not = isHost ? string.Empty : "NOT ";

                                Console.WriteLine($"I ({LocalGamers[0].Id}) am {not}the new host.");
#endif
                            }

                            if (removingHost && isHost)
                            {
#if DEBUG
                                Console.WriteLine($"Sending host changed message to {AllGamers.Count - 1} peers.");
#endif
                                // tell peers I am the new host
                                SendHostChanged(machineToRemove.id, localMachine.id);
                            }
                            
                            if (isHost)
                            {
#if DEBUG
                                Console.WriteLine($"I ({LocalGamers[0].Id}) am the host so notify clients that machine {machineToRemove.id} is disconnected.");
#endif
                                // since we are the host, tell all our clients that a machine/gamer(s)
                                // has been disconnected from the game...
                                SendMachineDisconnectedMessage(machineToRemove);
                            }
                            //else
                            //{
                            //    string reasonString;
                            //    NetworkSessionEndReason reason;
                            //    if (msg.ReadString(out reasonString) && Enum.TryParse(reasonString, out reason))
                            //    {
                            //        End(reason);
                            //    }
                            //    else
                            //    {
                            //        End(NetworkSessionEndReason.Disconnected);
                            //    }
                            //}
                        }
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.Data)
                {
                    if (msg.SenderConnection.Tag == null)
                    {
                        throw new InvalidOperationException();
                    }
                    ReceiveMessage(msg, msg.DeliveryMethod, (NetworkMachine)msg.SenderConnection.Tag);
                }
                else if (msg.MessageType == NetIncomingMessageType.UnconnectedData)
                {
                    if (msg.SenderEndPoint.Equals(NetworkMasterServer.ResolveEndPoint()))
                    {
                        MasterServerMessageType responseType;
                        MasterServerMessageResult responseResult;
                        if (NetworkMasterServer.ParseResponseHeader(msg, out responseType, out responseResult))
                        {
                            if (responseResult == MasterServerMessageResult.Ok)
                            {
                                if (responseType == MasterServerMessageType.RequestGeneralInfo)
                                {
                                    string generalInfo;
                                    if (NetworkMasterServer.ParseRequestGeneralInfoResponse(msg, out generalInfo))
                                    {
                                        masterServerGeneralInfo = generalInfo;
                                    }
                                }
                                else if (responseType == MasterServerMessageType.RegisterHost)
                                {
                                    isRegisteredAsHostWithMasterServer = true;
                                }
                                else if (responseType == MasterServerMessageType.UnregisterHost)
                                {
                                    isRegisteredAsHostWithMasterServer = false;
                                }
                            }
                            else
                            {
                                hasFailedMasterServerValidation = true;
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine("Unconnected data not from master server recieved from " + msg.SenderEndPoint + ", ignoring...");
                    }
                }
                else
                {
                    HandleLidgrenMessage(msg);
                }
                peer.Recycle(msg);

                if (IsDisposed)
                {
                    break;
                }
            }
        }

        private int GetOpenPrivateGamerSlots()
        {
            int usedPrivateSlots = 0;
            foreach (var gamer in gamerFromId.Values)
            {
                if (gamer.isPrivateSlot)
                {
                    usedPrivateSlots++;
                }
            }
            return privateGamerSlots - usedPrivateSlots;
        }

        private int GetOpenPublicGamerSlots()
        {
            int usedPublicSlots = 0;
            foreach (var gamer in gamerFromId.Values)
            {
                if (!gamer.isPrivateSlot)
                {
                    usedPublicSlots++;
                }
            }
            return maxGamers - privateGamerSlots - usedPublicSlots;
        }

        private int GetOpenSlotsForMachine(NetworkMachine machine)
        {
            int slots = GetOpenPublicGamerSlots();
            if (machine.isHost)
            {
                slots = Math.Max(slots, GetOpenPrivateGamerSlots());
            }
            return slots;
        }

        private void UpdatePublicInfo()
        {
            publicInfo.Set(type,
                            properties,
                            Host.Gamertag,
                            maxGamers,
                            PrivateGamerSlots,
                            allGamers.Count,
                            GetOpenPrivateGamerSlots(),
                            GetOpenPublicGamerSlots());
        }

        private void RegisterWithMasterServer()
        {
            if (!isHost || type == NetworkSessionType.Local || type == NetworkSessionType.SystemLink)
            {
                return;
            }
            var currentTime = DateTime.Now;
            if (currentTime - lastMasterServerReport < NetworkSettings.MasterServerRegistrationInterval)
            {
                return;
            }
            lastMasterServerReport = currentTime;

            UpdatePublicInfo();
            
            NetworkMasterServer.RegisterHost(peer, guid, GetInternalIp(peer), publicInfo);
        }

        private void UnregisterWithMasterServer()
        {
            if (!isHost || type == NetworkSessionType.Local || type == NetworkSessionType.SystemLink)
            {
                return;
            }

            NetworkMasterServer.UnregisterHost(peer, guid);
        }
    }
}
