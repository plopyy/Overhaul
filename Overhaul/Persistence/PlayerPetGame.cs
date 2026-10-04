using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerPetGame
    {
        internal const string Feed = "pet.feed";
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var pet = target.GetComponent<Pet>();
            if (!pet || !pet.m_FeedItem || !pet.m_materialVariation || !pet.m_nview || !pet.m_nview.IsValid() || request.Action.Amount != 1 || !request.Gameplay.Alternate ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0))) throw new InvalidOperationException("Pet cannot accept food");
            int slot = request.Action.FromY*256+request.Action.FromX;
            var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
            if (item.m_shared.m_name != pet.m_FeedItem.m_itemData.m_shared.m_name || item.m_worldLevel < Game.m_worldLevel)
                throw new InvalidOperationException("Wrong pet food");
            var data = pet.m_nview.GetZDO(); var material = pet.m_materialVariation;
            int key = ("MatVar"+material.m_materialIndex.ToString(CultureInfo.InvariantCulture)).GetStableHashCode();
            int old = data.GetInt(key,material.GetMaterial());
            int next = old == 7 ? 2 : UnityEngine.Random.value < .02f ? 4 : old;
            if (next < 0 || next >= material.m_materials.Count) throw new InvalidOperationException("Pet appearance is unavailable");
            inventory.Remove(slot,1);
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(key,next);
                return world.Finish(inventory.Delta(request.Action.Operation,snapshot.Revision),() =>
                {
                    if (pet) { pet.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_UpdateMaterial",next); if (pet.m_randomSpeak) pet.m_randomSpeak.enabled = next != 7; }
                });
            }
        }
        [HarmonyPatch(typeof(Pet),nameof(Pet.UseItem))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Pet __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            {
                if (user != Player.m_localPlayer || !PlayerSessionGame.Managed || item == null || !__instance.m_FeedItem || item.m_shared.m_name != __instance.m_FeedItem.m_itemData.m_shared.m_name) return true;
                __result = false;
                if (!__instance.m_nview || !__instance.m_nview.IsValid() || user.IsTeleporting() || !user.GetInventory().ContainsItem(item)) return false;
                var id = __instance.m_nview.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Feed,TargetUser = id.UserID,TargetId = id.ID,Alternate = true },item.m_gridPos.x,item.m_gridPos.y,1);
                return false;
            }
        }
        [HarmonyPatch(typeof(MaterialVariation),nameof(MaterialVariation.SetMaterial))]
        private static class ReservedAppearance
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(MaterialVariation __instance) => !__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid);
        }
    }
}
