using System;
using System.Collections.Generic;
using System.Diagnostics;
using LudeonTK;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Client.Windows;
using Multiplayer.Common;
using RimWorld;
using Steamworks;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public static class SteamIntegration
    {
        // Callbacks stored in static fields so they don't get garbage collected
        private static Callback<FriendRichPresenceUpdate_t> friendRchpUpdate;
        private static Callback<GameRichPresenceJoinRequested_t> gameJoinReq;
        private static Callback<PersonaStateChange_t> personaChange;
        private static Callback<AvatarImageLoaded_t> avatarLoaded;

        public static AppId_t RimWorldAppId;

        private const string SteamConnectStart = " -mpserver=";

        public static void InitCallbacks()
        {
            RimWorldAppId = SteamUtils.GetAppID();

            friendRchpUpdate = Callback<FriendRichPresenceUpdate_t>.Create(update =>
            {
            });

            gameJoinReq = Callback<GameRichPresenceJoinRequested_t>.Create(req =>
            {
                if (Current.Game == null)
                {
                    ClientUtil.TryConnectWithWindow(ConnectorRegistry.Steam(req.m_steamIDFriend), false);
                }
                else
                {
                    Messages.Message("MpQuitBeforeAcceptInvite".Translate(), MessageTypeDefOf.RejectInput,
                        historical: false);
                }
            });

            personaChange = Callback<PersonaStateChange_t>.Create(change =>
            {
                // When a persona's avatar changes, the avatar id changes too. It's not a problem for us because we
                // query the avatar id every frame. It'd be nice to remove the old avatar from the SteamImages cache,
                // but realistically it's not an issue. (Also, it'd require keeping track of the avatar's owner because
                // I don't think there's a way to query the old avatar id to easily remove it.)
            });

            avatarLoaded =
                Callback<AvatarImageLoaded_t>.Create(loaded => SteamImages.GetTexture(loaded.m_iImage, force: true));
        }

        // Entry point for an incoming ISteamNetworkingSockets connection (state Connecting on the host's listen
        // socket). Replaces the old P2PSessionRequest_t handler. Fires on the Unity main thread.
        public static void OnIncomingConnection(HSteamNetConnection conn, CSteamID remoteId)
        {
            ServerLog.Log($"Incoming Steam connection from {remoteId}");

            var server = Multiplayer.LocalServer;
            if (server?.settings.steam != true)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
                return;
            }

            // The listen socket is opened before hosting finishes, so peers can reach us while the world is still
            // being saved. PlayerManager.OnPreConnect would reject them, but it only runs on the server queue,
            // which nothing drains until the server thread starts — the peer would sit connected and never hear
            // back. Reject here, on the main thread, so they get told to try again instead.
            if (!server.AcceptingConnections)
            {
                RejectConnection(conn, MpDisconnectReason.ServerStarting);
                return;
            }

            var session = Multiplayer.session;
            if (session == null) return;

            if (Multiplayer.settings.autoAcceptSteam)
            {
                AcceptConnection(conn, remoteId);
            }
            else if (session.pendingSteam.TryGetValue(remoteId, out var pending))
            {
                // A prompt for this peer is already open. Point it at the newest connection (a distinct handle)
                // and close the superseded one; the timed-out old handle would otherwise linger.
                if (pending != conn)
                    SteamNetworkingSockets.CloseConnection(pending, 0, "", false);
                session.pendingSteam[remoteId] = conn;
            }
            else
            {
                session.pendingSteam[remoteId] = conn;
                PendingPlayerWindow.EnqueueJoinRequest(remoteId, (joinReq, accepted) =>
                {
                    if (!joinReq.steamId.HasValue) return;
                    if (accepted)
                        AcceptPlayerJoinRequest(joinReq.steamId.Value);
                    else
                        RejectPlayerJoinRequest(joinReq.steamId.Value);
                });
            }

            if (!session.knownUsers.Contains(remoteId))
                session.knownUsers.Add(remoteId);
            session.NotifyChat();

            SteamFriends.RequestUserInformation(remoteId, true);
        }

        public static void AcceptPlayerJoinRequest(CSteamID id)
        {
            var session = Multiplayer.session;
            if (session == null || !session.pendingSteam.TryGetValue(id, out var conn)) return;
            session.pendingSteam.Remove(id);

            AcceptConnection(conn, id);

            Messages.Message("MpSteamAccepted".Translate(), MessageTypeDefOf.PositiveEvent, false);
        }

        private static void RejectPlayerJoinRequest(CSteamID id)
        {
            var session = Multiplayer.session;
            if (session == null || !session.pendingSteam.TryGetValue(id, out var conn)) return;
            session.pendingSteam.Remove(id);

            SteamNetworkingSockets.CloseConnection(conn, 0, "", false);
        }

        // Turns down a connection we never accepted. There's no ConnectionBase to send a goodbye packet through
        // yet, so the reason travels only as an App-range end reason, which SteamSocketClientConn.OnClosed
        // decodes back into an MpDisconnectReason.
        private static void RejectConnection(HSteamNetConnection conn, MpDisconnectReason reason)
        {
            int endReason = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Min + (int)reason;
            SteamNetworkingSockets.CloseConnection(conn, endReason, "", bEnableLinger: true);
        }

        private static void AcceptConnection(HSteamNetConnection conn, CSteamID remoteId)
        {
            var result = SteamNetworkingSockets.AcceptConnection(conn);
            if (result != EResult.k_EResultOK)
            {
                ServerLog.Error($"Failed to accept Steam connection from {remoteId}: {result}");
                return;
            }

            SteamP2PIntegration.CreateServerPlayer(conn, remoteId);
        }

        private static Stopwatch lastSteamUpdate = Stopwatch.StartNew();
        private static bool lastLocalSteam; // running a server with steam networking
        private static CSteamID? lastRemoteSteam; // connected to a server with steam networking

        public static void UpdateRichPresence()
        {
            if (lastSteamUpdate.ElapsedMilliseconds < 1000) return;

            // Gated on readiness, not on the server merely existing: the server object and its listen socket are
            // created up front, but the world isn't saved until hosting finishes. Advertising before that puts a
            // join button in front of friends the host can't actually serve yet.
            var localSteam = Multiplayer.LocalServer is { settings.steam: true, AcceptingConnections: true };
            var remoteSteam = (Multiplayer.Client as SteamSocketClientConn)?.remoteId;
            if (localSteam != lastLocalSteam || remoteSteam != lastRemoteSteam)
            {
                string connect;
                if (localSteam) connect = SteamUser.GetSteamID().ToString();
                else if (remoteSteam != null) connect = remoteSteam.ToString();
                else connect = null;

                // Null and empty string mentioned in the docs doesn't seem to work
                SteamFriends.SetRichPresence("connect", connect != null ? $"{SteamConnectStart}{connect}" : "nil");

                lastLocalSteam = localSteam;
                lastRemoteSteam = remoteSteam;
            }

            lastSteamUpdate.Restart();
        }

        /// Gets the Steam ID of the user hosting the server that the friend is now playing on.
        public static CSteamID GetConnectHostId(CSteamID friend)
        {
            string connectValue = SteamFriends.GetFriendRichPresence(friend, "connect");
            if (connectValue?.StartsWith(SteamConnectStart, StringComparison.OrdinalIgnoreCase) == true &&
                ulong.TryParse(connectValue[SteamConnectStart.Length..], out ulong hostId))
            {
                return (CSteamID)hostId;
            }

            return CSteamID.Nil;
        }
    }

    public static class SteamImages
    {
        private static readonly Dictionary<int, Texture2D> Cache = new();

        [DebugAction(category = MpDebugActions.MultiplayerCategory, name = "Clear image cache",
            allowedGameStates = AllowedGameStates.Entry)]
        private static void ClearCache()
        {
            foreach (var tex in Cache.Values) UnityEngine.Object.Destroy(tex);
            Cache.Clear();
        }

        public static Texture2D GetTexture(int id, bool force = false)
        {
            if (Cache.TryGetValue(id, out Texture2D tex) && !force)
                return tex;

            if (!SteamUtils.GetImageSize(id, out uint width, out uint height))
            {
                Cache[id] = null;
                return null;
            }

            uint sizeInBytes = width * height * 4;
            byte[] data = new byte[sizeInBytes];

            if (!SteamUtils.GetImageRGBA(id, data, (int)sizeInBytes))
            {
                Cache[id] = null;
                return null;
            }

            tex = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(data);
            FlipVertically(tex);
            tex.Apply();

            if (Cache.TryGetValue(id, out var oldTex)) UnityEngine.Object.Destroy(oldTex);
            Cache[id] = tex;

            return tex;
        }

        private static void FlipVertically(Texture2D tex)
        {
            var pixels = tex.GetPixels32();
            var buf = new Color32[tex.width];

            for (int y = 0; y < tex.height / 2; y++)
            {
                var reversedY = tex.height - y - 1;
                Array.Copy(pixels, y * tex.width, buf, 0, tex.width);
                Array.Copy(pixels, reversedY * tex.width, pixels, y * tex.width, tex.width);
                Array.Copy(buf, 0, pixels, reversedY * tex.width, tex.width);
            }

            tex.SetPixels32(pixels);
        }
    }

}
