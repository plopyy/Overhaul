using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class ShipDismantle
    {
        const string Rpc = "Overhaul_DismantleShip";
        static readonly int TokenKey = "overhaul_ship_remove_token".GetStableHashCode();
        static readonly int UntilKey = "overhaul_ship_remove_until".GetStableHashCode();
        static Ship pending, destroying;
        static Player player;
        static string token;
        static float started;
        internal static void Register(Ship ship)
        {
            if (ship.m_nview && ship.m_nview.IsValid())
                ship.m_nview.Register<ZDOID,string>(Rpc, (sender,actor,key) => Request(ship,sender,actor,key));
        }
        static bool Access(Ship ship, long id)
        {
            if (!ship || !ship.m_nview || !ship.m_nview.IsValid() || !ship.GetComponent<Piece>() || !ship.GetComponent<WearNTear>()) return false;
            if (ship.m_nview.GetZDO().GetFloat(ZDOVars.s_health, 1) <= 0 || Location.IsInsideNoBuildLocation(ship.transform.position)) return false;
            bool ward = false, allowed = false;
            foreach (var area in PrivateArea.m_allAreas)
                if (area && area.IsEnabled() && area.IsInside(ship.transform.position, 0))
                { ward = true; allowed |= area.m_piece.GetCreator() == id || area.IsPermitted(id); }
            if (ward && !allowed) return false;
            foreach (var c in ship.GetComponentsInChildren<Container>())
                if (!ChestAccess.Allows(c,id) || !c.CheckAccess(id) || c.m_inUse || ChestAccess.Leased(c) ||
                    (ChestAccess.Data(c)?.GetInt(ZDOVars.s_inUse,0) ?? 0) != 0) return false;
            return true;
        }
        static bool Near(Ship ship, Vector3 position, float distance) => ship.GetComponentsInChildren<Collider>()
            .Any(c => c.enabled && !c.isTrigger && Vector3.Distance(c.bounds.ClosestPoint(position),position) <= distance);
        internal static bool HasWorkbench(Vector3 position) => CraftingStation.HaveBuildStationInRange("$piece_workbench", position);
        internal static void Request(Ship ship, long sender, ZDOID actorId, string key)
        {
            if(Persistence.GameCreatureAuthority.Enabled)return; // Managed dismantling uses PlayerBuildGame.
            if (!ship.m_nview.IsOwner() || string.IsNullOrEmpty(key) || key.Length > 64) return;
            var actor = ChestAccess.Actor(sender,actorId);
            if (actor == null || !HasWorkbench(actor.GetPosition()) || !Access(ship,actor.GetLong(ZDOVars.s_playerID,0)) || !Near(ship,actor.GetPosition(),8)) return;
            var z = ship.m_nview.GetZDO();
            if (z.GetLong(UntilKey,0) > DateTime.UtcNow.Ticks && z.GetString(TokenKey,"") != key) return;
            foreach (var c in ship.GetComponentsInChildren<Container>()) { c.Load(); c.Save(); }
            z.Set(TokenKey,key); z.Set(UntilKey,DateTime.UtcNow.AddSeconds(10).Ticks); z.SetOwner(sender);
            if (sender != ZNet.GetUID()) ZDOMan.instance.ForceSendZDO(sender,z.m_uid);
        }
        internal static bool Begin(Player p, out bool result)
        {
            result = false;
            if (p != Player.m_localPlayer || !GameCamera.instance || !p.InPlaceMode()) return true;
            RaycastHit hit;
            if (!Physics.Raycast(GameCamera.instance.transform.position,GameCamera.instance.transform.forward,out hit,50,p.m_removeRayMask) ||
                Vector3.Distance(hit.point,p.m_eye.position) >= p.m_maxPlaceDistance) return true;
            var ship = hit.collider.GetComponentInParent<Ship>();
            if (!ship) return true;
            if (!HasWorkbench(p.transform.position))
            { p.Message(MessageHud.MessageType.Center,"$msg_missingstation"); return false; }
            if (pending || !Access(ship,p.GetPlayerID())) return false;
            pending = ship; player = p; token = Guid.NewGuid().ToString("N"); started = Time.realtimeSinceStartup;
            if (ship.m_nview.IsOwner()) Request(ship,ZNet.GetUID(),p.GetZDOID(),token);
            else ship.m_nview.InvokeRPC(Rpc,p.GetZDOID(),token);
            result = true;
            return false;
        }
        internal static List<ItemDrop.ItemData> Resources(Piece piece)
        {
            var items = new List<ItemDrop.ItemData>();
            if (ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return items;
            foreach (var r in piece.m_resources)
            {
                if (!r.m_resItem || !r.m_recover || r.m_amount <= 0) continue;
                int count = piece.IsPlacedByPlayer() ? r.m_amount : Mathf.Max(1,r.m_amount / 3);
                var prefab = ObjectDB.instance.GetItemPrefab(Utils.GetPrefabName(r.m_resItem.name));
                if (!prefab) throw new InvalidOperationException("Missing boat refund prefab: " + r.m_resItem.name);
                while (count > 0)
                {
                    var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
                    item.m_stack = Mathf.Min(count,item.m_shared.m_maxStackSize); count -= item.m_stack;
                    item.m_worldLevel = (byte)Game.m_worldLevel;
                    item.m_cheated |= piece.m_nview.GetZDO().GetBool(ZDOVars.s_cheated,false) && !PlayerProfile.s_bypassCheatChecks;
                    items.Add(item);
                }
            }
            return items;
        }
        internal static int Store(Inventory inventory, ItemDrop.ItemData item)
        {
            // Native CanAddItem includes existing stacks. Add one unit at a time to avoid
            // partial AddItem failures and preserve exactly the unaccepted remainder.
            int left = item.m_stack;
            while (left > 0 && inventory.CanAddItem(item,1))
            {
                var unit = item.Clone(); unit.m_stack = 1;
                if (!inventory.AddItem(unit)) break;
                left--;
            }
            return left;
        }
        internal static void Tick()
        {
            if (!pending) return;
            if (!player || player.IsDead() || !player.InPlaceMode() || Time.realtimeSinceStartup-started > 8 ||
                !Near(pending,player.transform.position,player.m_maxPlaceDistance+1)) { pending = null; return; }
            var view = pending.m_nview; var z = view.GetZDO();
            if (!view.IsOwner() || z.GetString(TokenKey,"") != token) return;
            var ship = pending; pending = null;
            if (!HasWorkbench(player.transform.position) || !Access(ship,player.GetPlayerID())) return;
            var items = Resources(ship.GetComponent<Piece>());
            var cargo = ship.GetComponentsInChildren<Container>();
            foreach (var c in cargo) { c.Load(); items.AddRange(c.GetInventory().GetAllItems().Select(i=>i.Clone())); }
            destroying = ship;
            try { ship.GetComponent<WearNTear>().Destroy(null,true); }
            finally { destroying = null; }
            foreach (var item in items)
            {
                item.m_stack = Store(player.GetInventory(),item);
                if (item.m_stack > 0) ItemDrop.DropItem(item,0,player.transform.position+Vector3.up*.3f,Quaternion.identity);
            }
        }
        internal static bool NativeCargo(Container c) => !destroying || c.GetComponentInParent<Ship>() != destroying;
    }
    [HarmonyPatch(typeof(Ship),"Awake")]
    internal static class ShipDismantleRegister { static void Postfix(Ship __instance) => ShipDismantle.Register(__instance); }
    [HarmonyPatch(typeof(Player),"RemovePiece")]
    internal static class ShipDismantleInput { static bool Prefix(Player __instance, ref bool __result) { bool value; bool run=ShipDismantle.Begin(__instance,out value); if(!run)__result=value; return run; } }
    [HarmonyPatch(typeof(Game),"Update")]
    internal static class ShipDismantleUpdate { static void Postfix() => ShipDismantle.Tick(); }
    [HarmonyPatch(typeof(Container),"OnDestroyed")]
    internal static class ShipDismantleCargo { static bool Prefix(Container __instance) => ShipDismantle.NativeCargo(__instance); }
}
