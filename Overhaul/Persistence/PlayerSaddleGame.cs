using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerSaddleGame
    {
        internal const string Attach = "saddle.attach", Remove = "saddle.remove";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var tame = target.GetComponent<Tameable>();
            if (!tame || !tame.m_nview || !tame.m_nview.IsValid() || !tame.m_saddleItem || !tame.m_saddle || !tame.IsTamed() || request.Action.Amount != 1 ||
                tame.m_character && tame.m_character.IsDead() || !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Saddle interaction is unavailable");
            var data = tame.m_nview.GetZDO(); bool attached = data.GetBool(ZDOVars.s_haveSaddleHash,false);
            if (request.Gameplay.Definition == Attach)
            {
                if (attached || !request.Gameplay.Alternate) throw new InvalidOperationException("Saddle is already attached or no item was selected");
                int slot = request.Action.FromY*256+request.Action.FromX;
                var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
                if (item.m_shared.m_name != tame.m_saddleItem.m_itemData.m_shared.m_name || item.m_worldLevel < Game.m_worldLevel)
                    throw new InvalidOperationException("Wrong saddle type");
                inventory.Remove(slot,1);
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(ZDOVars.s_haveSaddleHash,1);
                    return world.Finish(inventory.Delta(request.Action.Operation,snapshot.Revision),() => tame.m_nview.InvokeRPC(ZNetView.Everybody,"SetSaddle",true));
                }
            }
            if (request.Gameplay.Definition != Remove || !attached || tame.m_saddle.HaveValidUser() ||
                Vector3.Distance(actor.GetPosition(),tame.m_saddle.m_attachPoint.position) > tame.m_saddle.m_maxUseRange)
                throw new InvalidOperationException("Saddle cannot be removed");
            var saddle = tame.m_saddleItem.m_itemData.Clone(); saddle.m_dropPrefab = tame.m_saddleItem.gameObject; saddle.m_stack = 1;
            saddle.m_worldLevel = Game.m_worldLevel; saddle.m_equipped = false;
            var direction = actor.GetPosition()-target.transform.position;
            bool horizontal = direction.magnitude > .1f; direction.y = 0;
            var velocity = (Vector3.up+(horizontal ? direction.normalized : Vector3.zero))*tame.m_dropItemVel;
            var output = PlayerDropGame.Ground(saddle,target.transform.TransformPoint(tame.m_dropSaddleOffset),Quaternion.identity,0,velocity);
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_haveSaddleHash,0);
                return world.FinishWithObjects(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),new[] { output },
                    () => tame.m_nview.InvokeRPC(ZNetView.Everybody,"SetSaddle",false));
            }
        }
        private static void Send(Tameable tame,Humanoid user,string action,ItemDrop.ItemData item = null)
        {
            if (!tame || !tame.m_nview || !tame.m_nview.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            var id = tame.m_nview.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = action,TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
        }
        [HarmonyPatch(typeof(Tameable),nameof(Tameable.UseItem))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Tameable __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            { if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true; __result = false; if (item != null) Send(__instance,user,Attach,item); return false; }
        }
        [HarmonyPatch(typeof(Sadle),nameof(Sadle.Interact))]
        private static class Detach
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Sadle __instance,Humanoid character,bool repeat,bool alt,ref bool __result)
            { if (character != Player.m_localPlayer || !PlayerSessionGame.Managed || !alt) return true; __result = false; if (!repeat) Send(__instance.m_tambable,character,Remove); return false; }
        }
        [HarmonyPatch(typeof(Tameable),"RPC_AddSaddle")]
        private static class LegacyAttach
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Tameable __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch(typeof(Sadle),"RPC_RemoveSaddle")]
        private static class LegacyRemove
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Sadle __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch(typeof(Sadle),"RPC_RequestControl")]
        private static class ReservedRiding
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Sadle __instance) => !__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid);
        }
        [HarmonyPatch]
        private static class PendingMutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(Tameable),"OnDeath"); yield return AccessTools.Method(typeof(Tameable),"DropSaddle"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Tameable __instance,MethodBase __originalMethod,object[] __args) => !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
        }
    }
}
