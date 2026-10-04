using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.Persistence
{
    // CharacterID is supplied by the peer. Keep the permitted avatar separately so
    // clearing that native field cannot authorize another player object.
    internal static class GameAvatarBinding
    {
        private sealed class Binding
        {
            internal ZDOID Current;
            internal uint Retired;
            internal bool Available = true;
        }
        private static readonly Dictionary<ZRpc, Binding> bindings = new Dictionary<ZRpc, Binding>();
        internal static ZDOID Current(ZRpc rpc) => bindings.TryGetValue(rpc, out var binding) ? binding.Current : ZDOID.None;
        internal static void Forget(ZRpc rpc) => bindings.Remove(rpc);
        internal static void Clear() => bindings.Clear();

        internal static bool Accept(ZNetPeer peer, ZDOID id)
        {
            if (peer == null || id.IsNone() || id.UserID != peer.m_uid || id.ID == 0) return false;
            var session = PlayerSessionGame.Session(peer.m_rpc);
            if (session == null || session.State != PlayerAdmission.Phase.Ready) return false;
            var existing = ZDOMan.instance.GetZDO(id);
            if (existing != null)
            {
                var prefab = ZNetScene.instance.GetPrefab(existing.GetPrefab());
                if (existing.GetOwner() != peer.m_uid || !prefab || !prefab.GetComponent<Player>()) return false;
            }
            if (!bindings.TryGetValue(peer.m_rpc, out var binding))
                bindings.Add(peer.m_rpc, binding = new Binding());
            if (binding.Current == id) return true;
            // Native ZDO identifiers increase within a connection. Retiring their
            // high-water mark also rejects delayed packets from earlier lives.
            if (!binding.Available || id.ID <= binding.Retired) return false;
            binding.Current = id;
            binding.Available = false;
            return true;
        }

        internal static void RespawnCommitted(ZRpc rpc, ZDOID previous)
        {
            if (PlayerSessionGame.IsLocal(rpc)) return;
            if (!bindings.TryGetValue(rpc, out var binding) || binding.Current != previous) return;
            binding.Retired = System.Math.Max(binding.Retired, previous.ID);
            binding.Current = ZDOID.None;
            binding.Available = true;
        }

        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        private static class CharacterId
        {
            private static bool Prefix(ZNet __instance, ZRpc rpc, ZDOID characterID)
            {
                if (!GameCreatureAuthority.Enabled) return true;
                var peer = __instance.GetPeer(rpc);
                if (!characterID.IsNone()) return Accept(peer, characterID);
                // Initial loading and a committed respawn may announce no avatar.
                // Never release a live binding in response to a client announcement.
                return peer != null && Current(rpc).IsNone();
            }
        }
    }
}
