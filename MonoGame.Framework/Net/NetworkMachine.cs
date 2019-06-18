using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.GamerServices;

namespace Microsoft.Xna.Framework.Net
{
    public sealed class NetworkMachine
    {
        internal readonly NetworkSession session;
        internal readonly bool isLocal;
        internal bool isHost;
        internal readonly byte id;
        internal readonly List<NetworkGamer> gamers = new List<NetworkGamer>();
        private bool beingRemoved = false;
        internal TimeSpan roundtripTime = TimeSpan.Zero;

        internal NetworkMachine(NetworkSession session, bool isLocal, bool isHost, byte id)
        {
            this.session = session;
            this.isLocal = isLocal;
            this.isHost = isHost;
            this.id = id;
            this.Gamers = new GamerCollection<NetworkGamer>(new List<NetworkGamer>(), gamers);
        }

        public GamerCollection<NetworkGamer> Gamers { get; private set; }

        internal static NetworkMachine SetNewHost(NetworkSession session)
        {
            NetworkGamer newHost = null;

            // simple approach, just choose the gamer with the lowest id.
            foreach (NetworkGamer gamer in session.AllGamers)
            {
                if (newHost == null || newHost.Id < gamer.Id)
                {
                    newHost = gamer;
                }
            }

            // Todo assign ishost = true to this gamer/machine both?
            newHost.machine.isHost = true;

            // TODO. if we are the new host, register with master server (somehow!?!?!?)
            throw new NotImplementedException();

            return newHost.machine;
        }

        public void RemoveFromSession()
        {
            if (session.IsDisposed) throw new ObjectDisposedException("NetworkSession");
            if (beingRemoved) throw new ObjectDisposedException("NetworkMachine");
            if (!isLocal && !session.IsHost)
            {
                throw new InvalidOperationException("Can only be called by the host or the owner of the machine");
            }

            if (isLocal)
            {
                session.End(NetworkSessionEndReason.Disconnected);
            }
            else
            {
                session.DisconnectMachine(this, NetworkSessionEndReason.RemovedByHost);
            }
            beingRemoved = true;
        }
    }
}
