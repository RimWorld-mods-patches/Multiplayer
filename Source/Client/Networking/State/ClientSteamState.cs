using Multiplayer.Client.Networking;
using Multiplayer.Common;

namespace Multiplayer.Client
{
    public class ClientSteamState : MpConnectionState
    {
        private readonly string username;

        public ClientSteamState(SteamSocketClientConn conn, string username) : base(conn)
        {
            this.username = username;
            // With ISteamNetworkingSockets the connection request is the handshake; we just wait for the host's
            // Server_SteamAccept, which it sends right after AcceptConnection.
        }

        [PacketHandler(Packets.Server_SteamAccept)]
        public void HandleSteamAccept(ByteReader data)
        {
            connection.ChangeState(new ClientJoiningState(connection, username));
        }
    }

}
