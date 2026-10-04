using System;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerDoorGame
    {
        internal const string Interaction = "door.use";
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var door = target.GetComponent<Door>();
            if (!door || !door.m_nview || !door.m_nview.IsValid() || request.Action.Amount != 1 || !door.CanInteract() ||
                door.m_checkGuardStone && !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Door is unavailable");
            var data = door.m_nview.GetZDO(); int state = data.GetInt(ZDOVars.s_state,0);
            if (state != 0 && (door.m_keyItem || door.m_canNotBeClosed)) throw new InvalidOperationException("Door cannot be closed");
            if (door.m_keyItem)
            {
                int? selected = request.Gameplay.Alternate ? (int?)(request.Action.FromY*256+request.Action.FromX) : null;
                var candidates = inventory.Keys.Where(k => !selected.HasValue || k == selected.Value).Where(inventory.Available).Where(k =>
                {
                    var item = PlayerInventoryView.ReadItem(inventory.Item(k),null,true);
                    return item.m_shared.m_name == door.m_keyItem.m_itemData.m_shared.m_name && item.m_worldLevel >= Game.m_worldLevel;
                }).ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException("Required door key is missing");
                // One key unlocks the door, regardless of the selected stack size.
                if (door.m_consumeKey) inventory.Remove(candidates[0],1,true);
            }
            else if (request.Gameplay.Alternate) throw new InvalidOperationException("Door does not accept an item");
            int next = state == 0 ? Vector3.Dot(door.transform.forward,actor.GetPosition()-door.transform.position) < 0 ? 1 : -1 : 0;
            var rows = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            rows.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)(state == 0 ? PlayerStatType.DoorsOpened : PlayerStatType.DoorsClosed)).ToString(CultureInfo.InvariantCulture),1));
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_state,next);
                return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,rows),() => { if (door) door.UpdateState(); });
            }
        }
        private static bool Managed(Humanoid user) => user && user == Player.m_localPlayer && PlayerSessionGame.Managed;
        private static void Send(Door door,Humanoid user,ItemDrop.ItemData item)
        {
            if (!door.m_nview || !door.m_nview.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            var id = door.m_nview.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Interaction,TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
        }
        [HarmonyPatch(typeof(Door),nameof(Door.Interact))]
        private static class Interact
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Door __instance,Humanoid character,bool hold,ref bool __result)
            { if (!Managed(character)) return true; __result = false; if (!hold) Send(__instance,character,null); return false; }
        }
        [HarmonyPatch(typeof(Door),nameof(Door.UseItem))]
        private static class UseItem
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Door __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            { if (!Managed(user)) return true; __result = false; if (item != null) Send(__instance,user,item); return false; }
        }
        [HarmonyPatch(typeof(Door),"RPC_UseDoor")]
        private static class Legacy
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Door __instance) => PlayerPersistenceConfig.Enabled?.Value != true &&
                (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
    }
}
