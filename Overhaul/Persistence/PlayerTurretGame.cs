using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerTurretGame
    {
        internal const string Interaction = "turret.use";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        private static bool Held(Turret turret) => turret.m_nview && turret.m_nview.IsValid() && GamePersistence.ActionReserved(turret.m_nview.GetZDO().m_uid);
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var turret = target.GetComponent<Turret>();
            if (!turret || !turret.m_nview || !turret.m_nview.IsValid() || request.Action.Amount != 1 ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0))) throw new InvalidOperationException("Turret is unavailable");
            var data = turret.m_nview.GetZDO(); int ammo = data.GetInt(ZDOVars.s_ammo,0); string loaded = data.GetString(ZDOVars.s_ammoType,"");
            int? slot = request.Gameplay.Alternate ? (int?)(request.Action.FromY*256+request.Action.FromX) : inventory.Keys.Where(inventory.Available).Where(k =>
            {
                var i = PlayerInventoryView.ReadItem(inventory.Item(k),null,true);
                return (i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable) &&
                    i.m_shared.m_ammoType == turret.m_ammoType && (ammo <= 0 || i.m_dropPrefab.name == loaded);
            }).Select(k => (int?)k).FirstOrDefault();
            if (!slot.HasValue || !inventory.Available(slot.Value)) throw new InvalidOperationException("Turret input is missing");
            var item = PlayerInventoryView.ReadItem(inventory.Item(slot.Value),null,true);
            if (item.m_worldLevel < Game.m_worldLevel) throw new InvalidOperationException("Turret item world level is too low");
            var trophy = turret.m_configTargets.FirstOrDefault(t => t.m_item && t.m_item.m_itemData.m_shared.m_name == item.m_shared.m_name);
            if (trophy.m_item)
            {
                int count = data.GetInt(ZDOVars.s_targets,0);
                if (turret.m_maxConfigTargets < 1 || turret.m_maxConfigTargets > 100 || count < 0 || count > 100)
                    throw new InvalidOperationException("Invalid turret target configuration");
                var names = Enumerable.Range(0,count).Select(i => data.GetString("target"+i.ToString(CultureInfo.InvariantCulture),"")).ToList();
                string name = trophy.m_item.m_itemData.m_shared.m_name;
                if (!names.Remove(name)) { while (names.Count >= turret.m_maxConfigTargets) names.RemoveAt(0); names.Add(name); }
                var rows = new[] { PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.TurretTrophySet).ToString(CultureInfo.InvariantCulture),1) };
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(ZDOVars.s_targets,names.Count);
                    for (int i = 0; i < Math.Max(count,names.Count); i++) world.Set(("target"+i.ToString(CultureInfo.InvariantCulture)).GetStableHashCode(),i < names.Count ? names[i] : "");
                    return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,rows),() =>
                    { turret.ReadTargets(); turret.m_setTargetEffect.Create(target.transform.position,target.transform.rotation,null,1,-1,default(ZDOID)); });
                }
            }
            if (!turret.IsItemAllowed(item.m_dropPrefab.name) || ammo < 0 || ammo >= turret.m_maxAmmo || ammo > 0 && loaded != item.m_dropPrefab.name)
                throw new InvalidOperationException("Turret cannot accept this ammunition");
            inventory.Remove(slot.Value,1);
            var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.TurretAmmoAdded).ToString(CultureInfo.InvariantCulture),1));
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_ammo,ammo+1); world.Set(ZDOVars.s_ammoType,item.m_dropPrefab.name);
                return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),() =>
                {
                    turret.UpdateVisualBolt(); var body = turret.m_turretBody ? turret.m_turretBody.transform : turret.transform;
                    turret.m_addAmmoEffect.Create(body.position,body.rotation,null,1,-1,default(ZDOID));
                });
            }
        }
        [HarmonyPatch(typeof(Turret),nameof(Turret.UseItem))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Turret __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            {
                if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
                __result = false;
                if (!__instance.m_nview || !__instance.m_nview.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return false;
                var id = __instance.m_nview.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Interaction,TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
                return false;
            }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(Turret),"RPC_AddAmmo"); yield return AccessTools.Method(typeof(Turret),"SetTargets"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Turret __instance) => !Enabled && !Held(__instance);
        }
        [HarmonyPatch]
        private static class PendingSimulation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(Turret),"FixedUpdate"); yield return AccessTools.Method(typeof(Turret),"ShootProjectile"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Turret __instance) => !Held(__instance);
        }
        [HarmonyPatch(typeof(Turret),"OnDestroyed")]
        private static class Destruction
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Turret __instance,MethodBase __originalMethod,object[] __args) => !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
        }
    }
}
