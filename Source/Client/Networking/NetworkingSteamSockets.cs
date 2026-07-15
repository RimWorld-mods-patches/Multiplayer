using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Steamworks;
using Verse;
using Verse.Steam;

namespace Multiplayer.Client.Networking
{
    public abstract class SteamSocketBaseConn : ConnectionBase
    {
        public HSteamNetConnection conn;
        public readonly CSteamID remoteId;

        protected SteamSocketBaseConn(HSteamNetConnection conn, CSteamID remoteId)
        {
            this.conn = conn;
            this.remoteId = remoteId;
        }

        protected override void SendRaw(byte[] raw, bool reliable = true)
        {
            if (conn == HSteamNetConnection.Invalid) return;

            // SendMessageToConnection copies the buffer synchronously, so pinning for the call is enough.
            var pin = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                int flags = reliable
                    ? Constants.k_nSteamNetworkingSend_Reliable
                    : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;

                var result = SteamNetworkingSockets.SendMessageToConnection(
                    conn, pin.AddrOfPinnedObject(), (uint)raw.Length, flags, out _);

                if (result != EResult.k_EResultOK)
                {
                    ServerLog.Error($"Failed to send Steam message ({result}, len {raw.Length}) to {remoteId}");

                    // A lost reliable message leaves a permanent hole in the packet stream (e.g. a dropped
                    // fragment) that the peer can't recover from, so surface it as a disconnect instead of
                    // limping along until deserialization breaks.
                    if (reliable)
                        OnReliableSendFailed();
                }
            }
            finally
            {
                pin.Free();
            }
        }

        protected abstract void OnReliableSendFailed();

        // A goodbye is only ever non-null server-side. CloseConnection with linger flushes queued reliable data
        // before tearing the connection down, so the reason reaches the peer without the deferred-close hack the
        // old ISteamNetworking path needed. The end reason mirrors the app reason (App range) as a fallback the
        // client can surface if the goodbye packet itself is lost.
        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            int endReason = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Min;

            if (goodbye.HasValue)
            {
                Send(goodbye.Value);
                endReason = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Min + (int)goodbye.Value.reason;
            }

            SteamNetworkingSockets.CloseConnection(conn, endReason, "", bEnableLinger: true);
            conn = HSteamNetConnection.Invalid;
        }

        public override string ToString() => $"SteamSockets ({remoteId}:{username})";
    }

    public class SteamSocketClientConn : SteamSocketBaseConn, ITickableConnection
    {
        private readonly IntPtr[] receiveBuffer = new IntPtr[SteamP2PIntegration.MaxMessagesPerReceive];

        public SteamSocketClientConn(CSteamID remoteId, string username)
            : base(HSteamNetConnection.Invalid, remoteId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(remoteId);

            conn = SteamNetworkingSockets.ConnectP2P(
                ref identity, 0, SteamP2PIntegration.ConfigOptions.Length, SteamP2PIntegration.ConfigOptions);

            if (conn == HSteamNetConnection.Invalid)
                ServerLog.Error($"Failed to start Steam connection to {remoteId}");

            // Keep the Steam accept state: the host sends Server_SteamAccept right after AcceptConnection,
            // which advances the client into ClientJoiningState.
            ChangeState(new ClientSteamState(this, username));
        }

        public void Tick()
        {
            if (conn == HSteamNetConnection.Invalid) return;

            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, receiveBuffer, receiveBuffer.Length);
            if (count <= 0) return;

            SteamP2PIntegration.ReceiveInto(receiveBuffer, count, (_, data, reliable) =>
            {
                // Receiving a packet can trigger a disconnection.
                if (State == ConnectionStateEnum.Disconnected) return;
                HandleReceiveRaw(data, reliable);
            });
        }

        // A send can fail from inside a packet handler; defer so the session teardown isn't reentrant.
        // OnClosed also closes our connection handle via StopMultiplayer -> Close -> OnClose.
        protected override void OnReliableSendFailed() =>
            OnMainThread.Enqueue(() => OnClosed(0));

        // Fallback shown only when the app-level goodbye never arrived. If the connection carried an app-range
        // end reason we recover the MpDisconnectReason from it; otherwise we show a timeout/generic message.
        public void OnClosed(int endReason)
        {
            if (State == ConnectionStateEnum.Disconnected) return;

            int appMin = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Min;
            int appMax = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Max;

            SessionDisconnectInfo info;
            if (endReason >= appMin && endReason <= appMax)
            {
                try
                {
                    info = SessionDisconnectInfo.From(new ServerDisconnectPacket
                    {
                        reason = (MpDisconnectReason)(endReason - appMin), data = []
                    });
                }
                catch
                {
                    info = new SessionDisconnectInfo { titleTranslated = "MpSteamGenericError".Translate() };
                }
            }
            else
            {
                bool timedOut =
                    endReason == (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Remote_Timeout ||
                    endReason == (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_Timeout;
                info = new SessionDisconnectInfo
                {
                    titleTranslated = (timedOut ? "MpSteamTimedOut" : "MpSteamGenericError").Translate()
                };
            }

            ConnectionStatusListeners.TryNotifyAll_Disconnected(info);
            Multiplayer.StopMultiplayer();
        }
    }

    public class SteamSocketServerConn : SteamSocketBaseConn
    {
        public SteamSocketServerConn(HSteamNetConnection conn, CSteamID remoteId) : base(conn, remoteId)
        {
        }

        public override void OnKeepAliveArrived(bool idMatched)
        {
            if (!idMatched) return;

            // Use the connection's own ping instead of the old keepalive-timer estimate.
            if (SteamP2PIntegration.TryGetRealTimeStatus(conn, out var status) && status.m_nPing >= 0)
                Latency = status.m_nPing;
        }

        // Sends can happen mid-iteration over playerManager.Players (e.g. SendToPlaying), so the teardown
        // is deferred to the queue. SetDisconnected's Disconnected-state guard makes this idempotent; no
        // goodbye is sent since the reliable stream is already broken.
        protected override void OnReliableSendFailed()
        {
            var server = serverPlayer.Server;
            server.Enqueue(() =>
            {
                if (State == ConnectionStateEnum.Disconnected) return;

                var handle = conn;
                server.playerManager.SetDisconnected(this, MpDisconnectReason.ClientLeft);

                if (handle != HSteamNetConnection.Invalid)
                    SteamNetworkingSockets.CloseConnection(handle, 0, "", false);
                conn = HSteamNetConnection.Invalid;
            });
        }
    }

    public class SteamSocketsNetManager : INetManager
    {
        // Set while a host's Steam listen socket is open; used by the accept path to assign the poll group.
        public static SteamSocketsNetManager Instance;

        private readonly MultiplayerServer server;
        private HSteamListenSocket listenSocket;
        private HSteamNetPollGroup pollGroup;
        private readonly IntPtr[] receiveBuffer = new IntPtr[SteamP2PIntegration.MaxMessagesPerReceive];

        private SteamSocketsNetManager(MultiplayerServer server) => this.server = server;

        public static SteamSocketsNetManager Create(MultiplayerServer server)
        {
            if (!SteamManager.Initialized) return null;

            var man = new SteamSocketsNetManager(server)
            {
                listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
                    0, SteamP2PIntegration.ConfigOptions.Length, SteamP2PIntegration.ConfigOptions),
                pollGroup = SteamNetworkingSockets.CreatePollGroup()
            };

            if (man.listenSocket == HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.DestroyPollGroup(man.pollGroup);
                return null;
            }

            Instance = man;
            return man;
        }

        public void RegisterConnection(HSteamNetConnection conn) =>
            SteamNetworkingSockets.SetConnectionPollGroup(conn, pollGroup);

        public void Tick()
        {
            int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, receiveBuffer, receiveBuffer.Length);
            if (count <= 0) return;

            SteamP2PIntegration.ReceiveInto(receiveBuffer, count, (from, data, reliable) =>
            {
                var player = server.playerManager.Players
                    .FirstOrDefault(p => p.conn is SteamSocketServerConn s && s.conn == from);

                if (player != null)
                    player.HandleReceive(data, reliable);
                else
                    ServerLog.Error($"Received Steam message from unknown connection {from}");
            });
        }

        public void Stop()
        {
            // Player connections are already closed by playerManager.OnServerStop (which runs first).
            SteamNetworkingSockets.CloseListenSocket(listenSocket);
            SteamNetworkingSockets.DestroyPollGroup(pollGroup);

            if (Instance == this) Instance = null;
        }

        public string GetDiagnosticsName() => "SteamSockets";

        public string GetDiagnosticsInfo() =>
            $"Listen socket: {listenSocket}\n" +
            $"Connections: {server.playerManager.Players.Count(p => p.conn is SteamSocketServerConn)}";
    }

    public static class SteamP2PIntegration
    {
        public const int MaxMessagesPerReceive = 256;

        // Raise the initial connection timeout well above Steam's ~10s default so a manual host accept prompt
        // isn't killed before the host answers. Applied to both the listen socket and outgoing connections.
        private const int AcceptPromptTimeoutMs = 120_000;

        public static readonly SteamNetworkingConfigValue_t[] ConfigOptions =
        {
            Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, AcceptPromptTimeoutMs),
            // The world download is sent as one synchronous burst of reliable fragments (up to
            // MaxFragmentPacketTotalSize). The default send buffer is only 512KB, and once it fills
            // SendMessageToConnection fails with LimitExceeded, silently dropping fragments and corrupting
            // the packet stream (the client then hangs mid-download and chokes on the next packet).
            // Size the buffer so a full burst always fits.
            Int32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                2 * ConnectionBase.MaxFragmentPacketTotalSize),
            // TEST: leave SendRateMax at Steam's default (~1MB/s) to measure download behavior
            // without the raised rate cap.
        };

        private static SteamNetworkingConfigValue_t Int32Option(ESteamNetworkingConfigValue key, int value) => new()
        {
            m_eValue = key,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = value }
        };

        private static Callback<SteamNetConnectionStatusChangedCallback_t> connStatusChanged;

        public static void InitCallbacks()
        {
            // Relay (SDR) route warmup is async; kick it off at startup rather than at connect time.
            SteamNetworkingUtils.InitRelayNetworkAccess();

            connStatusChanged =
                Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
        }

        // Fires on the Unity main thread. Server-side mutation is marshaled through server.Enqueue.
        private static void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
        {
            var conn = cb.m_hConn;
            var info = cb.m_info;
            var remoteId = info.m_identityRemote.GetSteamID();

            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Only incoming connections carry a listen socket handle; our own outgoing client
                    // connection does not, so this cleanly distinguishes the host accept path.
                    if (info.m_hListenSocket != HSteamListenSocket.Invalid)
                        SteamIntegration.OnIncomingConnection(conn, remoteId);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    // Client: the host's Server_SteamAccept packet drives the state machine.
                    // Server: the player was already set up on accept. Nothing to do.
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    HandleConnectionClosed(conn, info.m_eEndReason);
                    break;
            }
        }

        private static void HandleConnectionClosed(HSteamNetConnection conn, int endReason)
        {
            var session = Multiplayer.session;
            bool isClient = session?.client is SteamSocketClientConn c && c.conn == conn;
            if (isClient)
                ((SteamSocketClientConn)session.client).OnClosed(endReason);

            // Drop an unanswered prompt whose connection just died (e.g. it timed out before the host accepted).
            if (session != null)
                foreach (var remote in session.pendingSteam.Where(kv => kv.Value == conn).Select(kv => kv.Key).ToList())
                    session.pendingSteam.Remove(remote);

            var server = Multiplayer.LocalServer;
            if (server != null)
            {
                server.Enqueue(() =>
                {
                    var player = server.playerManager.Players
                        .FirstOrDefault(p => p.conn is SteamSocketServerConn s && s.conn == conn);
                    if (player != null)
                        server.playerManager.SetDisconnected(player.conn, MpDisconnectReason.ClientLeft);

                    // A peer-initiated close still requires us to close our handle to release it.
                    SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
                });
            }
            else if (!isClient)
            {
                // Orphan (e.g. a connection we already rejected) — release the native handle.
                SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
            }
        }

        // Creates the host-side player for an already-accepted connection. Runs the join bookkeeping on the
        // server thread since it mutates playerManager.
        public static void CreateServerPlayer(HSteamNetConnection conn, CSteamID remoteId)
        {
            var server = Multiplayer.LocalServer;
            if (server == null)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
                return;
            }

            server.Enqueue(() =>
            {
                var playerManager = server.playerManager;

                // Fast-rejoin policy: a new connection whose Steam id matches an existing player means the old
                // one is stale (a rejoin is simply a new connection now). Drop the stale player first.
                var stale = playerManager.Players
                    .FirstOrDefault(p => p.conn is SteamSocketServerConn s && s.remoteId == remoteId);
                if (stale != null)
                {
                    ServerLog.Log($"Reconnect from {remoteId}; dropping stale connection {stale.conn}");
                    stale.Disconnect(MpDisconnectReason.ClientLeft);
                }

                ConnectionBase newConn = new SteamSocketServerConn(conn, remoteId);

                var preConnect = playerManager.OnPreConnect(remoteId);
                if (preConnect != null)
                {
                    ServerLog.Log($"Rejected incoming connection from {remoteId}: {preConnect}");
                    newConn.Close(preConnect.Value);
                    return;
                }

                newConn.ChangeState(ConnectionStateEnum.ServerJoining);
                var player = playerManager.OnConnected(newConn);
                player.type = PlayerType.Steam;

                player.steamId = (ulong)remoteId;
                player.steamPersonaName = SteamFriends.GetFriendPersonaName(remoteId);
                if (player.steamPersonaName.Length == 0)
                    player.steamPersonaName = "[unknown]";

                SteamSocketsNetManager.Instance?.RegisterConnection(conn);
                newConn.Send(Packets.Server_SteamAccept);
            });
        }

        // Queries the connection's real-time status. The inline reserved arrays are allocated so struct
        // marshaling is well-defined; lanes are requested with nLanes 0 (aggregate status only).
        public static bool TryGetRealTimeStatus(HSteamNetConnection conn, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
            return SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) == EResult.k_EResultOK;
        }

        // Centralized receive-and-copy: copies each native message's payload out and frees it via Release().
        public static void ReceiveInto(IntPtr[] buffer, int count,
            Action<HSteamNetConnection, ByteReader, bool> handle)
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr msgPtr = buffer[i];
                var msg = SteamNetworkingMessage_t.FromIntPtr(msgPtr);

                byte[] data = new byte[msg.m_cbSize];
                if (msg.m_cbSize > 0)
                    Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);

                bool reliable = (msg.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
                var from = msg.m_conn;

                SteamNetworkingMessage_t.Release(msgPtr);

                handle(from, new ByteReader(data), reliable);
            }
        }
    }
}
