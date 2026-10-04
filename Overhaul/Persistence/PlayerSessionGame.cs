using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerSessionGame
    {
        private static readonly Dictionary<ZRpc, PlayerAdmissionRpc> servers = new Dictionary<ZRpc, PlayerAdmissionRpc>();
        private static readonly Dictionary<ZRpc, PlayerAdmission.Session> sessions = new Dictionary<ZRpc, PlayerAdmission.Session>();
        private static PlayerAdmission admission;
        private static PlayerAdmissionRpc client;
        private static ZRpc localClient, localServer;
        private static PlayerProfile originalProfile;
        private static bool failed;
        private static bool awaitingMode;
        private static DateTime modeDeadline;
        private const string ModeRpc = "Overhaul_CharacterMode";
        internal static bool Managed => client != null;
        internal static bool Ready => !failed && client?.Client?.Ready == true;
        internal static bool IsLocal(ZRpc rpc) => rpc != null && ReferenceEquals(rpc, localServer);
        internal static PlayerAdmission.Session Session(ZRpc rpc) => sessions.TryGetValue(rpc, out var session) ? session : null;
        internal static long CharacterId(PlayerAdmission.Session session)
        {
            var row = session?.Snapshot?.Rows.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == "player_id");
            return row == null ? 0 : Convert.ToInt64(row.Values[1]);
        }
        internal static ZDO Actor(ZRpc rpc)
        {
            if (IsLocal(rpc)) return Player.m_localPlayer ? Player.m_localPlayer.m_nview.GetZDO() : null;
            var peer = ZNet.instance.GetPeer(rpc);
            return peer == null ? null : Storage.ChestAccess.Actor(peer.m_uid, peer.m_characterID);
        }
        internal static PlayerIdentity Identity(ZNetPeer peer)
        {
            if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected()) throw new InvalidOperationException("Native peer admission is incomplete");
            long world = ZNet.m_world.m_uid;
            if (peer.m_socket is ZSteamSocket steam)
                return new PlayerIdentity(world, "steam", steam.GetHostName());
            if (peer.m_socket is ZPlayFabSocket playfab && !string.IsNullOrEmpty(playfab.m_remotePlayerId))
                // Party supplies EntityKey.Id for the connected remote player. GetHostName is a
                // platform string received from the remote client and is not an account proof.
                return new PlayerIdentity(world, "playfab", playfab.m_remotePlayerId);
            throw new InvalidOperationException("Unsupported authenticated character transport");
        }
        private static PlayerIdentity LocalIdentity()
        {
            if (ZNet.m_onlineBackend == OnlineBackendType.PlayFab && PlayFabManager.IsLoggedIn)
                return new PlayerIdentity(ZNet.m_world.m_uid, "playfab", PlayFabManager.instance.Entity.Id);
            var id = Steamworks.SteamUser.GetSteamID().m_SteamID;
            if (id == 0) throw new InvalidOperationException("Local Steam account is unavailable");
            return new PlayerIdentity(ZNet.m_world.m_uid, "steam", id.ToString(CultureInfo.InvariantCulture));
        }
        internal static void BeginClient(ZRpc rpc)
        {
            if (client != null) throw new InvalidOperationException("Character connection is already bound");
            failed = false;
            client = new PlayerAdmissionRpc(rpc, CaptureImport, Load, ClientFailed);
        }
        internal static void BeginServer(ZRpc rpc, PlayerIdentity identity, string name)
        {
            if (!GamePersistence.Active || GamePersistence.Players == null) throw new InvalidOperationException("Progressive world storage is unavailable");
            if (servers.ContainsKey(rpc)) return;
            if (admission == null) admission = new PlayerAdmission(GamePersistence.Players);
            var prefab = Game.instance.m_playerPrefab.GetComponent<Player>();
            var fresh = PlayerLoginData.Fresh(name, prefab.m_baseHP, prefab.m_baseStamina);
            var session = admission.Begin(rpc, identity, PlayerPersistenceConfig.AllowClientCharacterMigration.Value, () => fresh);
            sessions.Add(rpc, session);
            servers.Add(rpc, new PlayerAdmissionRpc(rpc, admission, session,
                bytes => PlayerProfileBridge.DecodeImportedMap(PlayerLoginData.UnpackImport(bytes)),
                error => ZLog.LogError("[Overhaul character] " + error)));
        }
        private static PlayerProfile Selected()
        {
            if (originalProfile == null) originalProfile = Game.instance.GetPlayerProfile();
            return originalProfile ?? throw new InvalidOperationException("Selected local character is unavailable");
        }
        private static byte[] CaptureImport()
        {
            var profile = Selected();
            return PlayerLoginData.PackImport(profile.m_playerData, PlayerProfileBridge.Capture(profile, ZNet.m_world.m_uid));
        }
        private static byte[] Appearance()
        {
            var profile = Selected();
            if (profile.m_playerData != null) return PlayerNativeFormat.Decode(profile.m_playerData).Appearance;
            var prefab = Game.instance.m_playerPrefab.GetComponent<Player>();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(prefab.m_beardItem); writer.Write(prefab.m_hairItem);
                foreach (var color in new[] { prefab.m_skinColor, prefab.m_hairColor }) { writer.Write(color.x); writer.Write(color.y); writer.Write(color.z); }
                writer.Write(prefab.m_modelIndex); writer.Flush(); return stream.ToArray();
            }
        }
        private static void Load(PlayerSnapshot snapshot)
        {
            var rows = snapshot.Rows.ToArray();
            var profile = PlayerProfileBridge.Restore(rows, ZNet.m_world.m_uid,
                PlayerNativeFormat.Encode(rows, Appearance()), PlayerMapFormat.Encode(rows));
            // Swap only after complete reconstruction. The selected file-backed profile is never mutated.
            Game.instance.m_playerProfile = profile;
            PlayerPotionGame.Initial(rows);
            if (!ZNet.instance.IsServer()) ZNet.instance.GetServerRPC().Invoke("PlayerID", profile.m_playerID);
        }
        private static void ClientFailed(Exception error)
        {
            failed = true; ZLog.LogError("[Overhaul character] " + error);
            ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
        }
        internal static void Tick()
        {
            if (!ZNet.instance || !Game.instance) return;
            if (awaitingMode && ZNet.m_connectionStatus != ZNet.ConnectionStatus.Connected) modeDeadline = DateTime.UtcNow.AddSeconds(30);
            if (awaitingMode && DateTime.UtcNow >= modeDeadline)
            {
                awaitingMode = false;
                ClientFailed(new TimeoutException("Server character mode was not received"));
                ZNet.instance.GetServerRPC()?.GetSocket().Close();
            }
            if (PlayerPersistenceConfig.Enabled.Value && ZNet.instance.IsServer() && !ZNet.instance.IsDedicated() && client == null && !failed && GamePersistence.Active)
            {
                try
                {
                    PlayerLocalSocket.Pair(out localClient, out localServer);
                    BeginClient(localClient); BeginServer(localServer, LocalIdentity(), Selected().GetName());
                }
                catch (Exception error) { ClientFailed(error); }
            }
            localServer?.Update(0); localClient?.Update(0);
            admission?.Tick();
            foreach (var pair in servers.ToArray())
            {
                pair.Value.Tick();
                if (pair.Value.Closed) { servers.Remove(pair.Key); sessions.Remove(pair.Key); }
            }
            client?.Tick();
        }
        internal static void Stop()
        {
            client?.Dispose(); client = null;
            foreach (var endpoint in servers.Values) endpoint.Dispose(); servers.Clear(); sessions.Clear(); admission = null;
            localClient?.Dispose(); localServer?.Dispose(); localClient = localServer = null;
            if (Game.instance && originalProfile != null) Game.instance.m_playerProfile = originalProfile;
            originalProfile = null; failed = false; awaitingMode = false;
            PlayerPotionGame.Initial(null);
        }
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connect
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer() || !peer.m_server) return;
                awaitingMode = true; modeDeadline = DateTime.UtcNow.AddSeconds(30);
                peer.m_rpc.Register<bool>(ModeRpc, (sender, enabled) =>
                {
                    if (!ReferenceEquals(sender, peer.m_rpc) || !awaitingMode) return;
                    awaitingMode = false;
                    try { if (enabled) BeginClient(peer.m_rpc); }
                    catch (Exception error) { ClientFailed(error); peer.m_rpc.GetSocket().Close(); }
                });
            }
        }
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class Admit
        {
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!__instance.IsServer()) return;
                var peer = __instance.GetPeer(rpc); if (peer == null || !peer.IsReady()) return;
                try { rpc.Invoke(ModeRpc, PlayerPersistenceConfig.Enabled.Value); if (PlayerPersistenceConfig.Enabled.Value) BeginServer(rpc, Identity(peer), peer.m_playerName); }
                catch (Exception error) { ZLog.LogError("[Overhaul character] " + error); rpc.GetSocket().Close(); }
            }
        }
        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class Update
        {
            [HarmonyPriority(Priority.First)]
            private static void Postfix() => Tick();
        }
        [HarmonyPatch(typeof(Game), "UpdateRespawn")]
        private static class SpawnGate
        {
            private static bool Prefix() => !ZNet.instance || ZNet.instance.IsDedicated() ||
                !failed && !awaitingMode && (ZNet.instance.IsServer() && !PlayerPersistenceConfig.Enabled.Value || !ZNet.instance.IsServer() && !Managed || Ready);
        }
        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
        private static class Save
        {
            private static bool Prefix() => !Managed;
        }
        [HarmonyPatch(typeof(ZNet), "RPC_PlayerID")]
        private static class PlayerId
        {
            private static bool Prefix(ZRpc rpc, long playerID)
            {
                if (!PlayerPersistenceConfig.Enabled.Value) return true;
                var session = Session(rpc); if (session == null) return false;
                if (CharacterId(session) == playerID && playerID != 0) return true;
                rpc.GetSocket().Close(); return false;
            }
        }
        [HarmonyPatch(typeof(ZNet), "StopAll")]
        private static class Shutdown
        {
            [HarmonyPriority(Priority.First + 100)]
            private static void Prefix() => Stop();
        }
    }
}
