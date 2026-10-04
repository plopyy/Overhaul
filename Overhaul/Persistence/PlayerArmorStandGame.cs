using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerArmorStandGame
    {
        internal const string Attach = "armor.attach", Drop = "armor.drop", Pose = "armor.pose";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        private static bool Managed(Humanoid user) => user && user == Player.m_localPlayer && PlayerSessionGame.Managed;
        private static int Key(int index,string suffix) => (index.ToString(CultureInfo.InvariantCulture)+"_"+suffix).GetStableHashCode();
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var stand = target.GetComponent<ArmorStand>();
            if (!stand || !stand.m_nview || !stand.m_nview.IsValid() || request.Action.Amount != 1 ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Armor stand is unavailable");
            var data = stand.m_nview.GetZDO();
            var empty = new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>());
            if (request.Gameplay.Definition == Pose)
            {
                if (!stand.m_changePoseSwitch || !stand.m_changePoseSwitch.gameObject.activeInHierarchy || stand.m_poseCount < 1)
                    throw new InvalidOperationException("Armor stand has no pose control");
                int pose = (Math.Max(0,data.GetInt(ZDOVars.s_pose,stand.m_pose))+1)%stand.m_poseCount;
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(ZDOVars.s_pose,pose);
                    return world.Finish(empty,() => stand.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_SetPose",pose));
                }
            }
            int selected = request.Gameplay.Variant;
            if (selected < 0 || selected >= stand.m_slots.Count || !stand.m_slots[selected].m_switch)
                throw new InvalidOperationException("Unknown armor stand slot");
            var control = stand.m_slots[selected].m_switch;
            if (request.Gameplay.Definition == Attach)
            {
                int source = request.Action.FromY*256+request.Action.FromX;
                var item = PlayerInventoryView.ReadItem(inventory.Item(source),null,true); item.m_customData = inventory.Data(source);
                int index = stand.m_slots.FindIndex(s => s.m_switch == control && stand.CanAttach(s,item));
                if (index < 0 || data.GetInt(Key(index,"item"),0) != 0 ||
                    item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Legs && item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Chest &&
                    !item.m_dropPrefab.transform.Find("attach") && !item.m_dropPrefab.transform.Find("attach_skin"))
                    throw new InvalidOperationException("Armor stand slot cannot accept this item");
                inventory.Remove(source,1,true); if (inventory.Keys.Contains(source)) inventory.Equip(source,false);
                item.m_stack = 1; item.m_equipped = false; item.m_customData.Remove("eaqs_parked");
                var payload = new ZPackage(); payload.Write((byte)109); item.Save(payload);
                var rows = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
                rows.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.ArmorStandUses).ToString(CultureInfo.InvariantCulture),1));
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(Key(index,"item"),item.m_dropPrefab.name.GetStableHashCode()); world.Set(Key(index,"itemData"),payload.GetArray()); world.Set(Key(index,"variant"),item.m_variant);
                    return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,rows),() =>
                    {
                        stand.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_SetVisualItem",index,item.m_dropPrefab.name.GetStableHashCode(),item.m_variant);
                        stand.m_effects.Create(stand.transform.position,Quaternion.identity,null,1,-1,default(ZDOID));
                    });
                }
            }
            if (request.Gameplay.Definition != Drop || !stand.m_dropSpawnPoint) throw new InvalidOperationException("Unknown armor stand interaction");
            var indices = Enumerable.Range(0,stand.m_slots.Count).Where(i => stand.m_slots[i].m_switch && stand.m_slots[i].m_switch.name == control.name && data.GetInt(Key(i,"item"),0) != 0).ToArray();
            if (indices.Length == 0 || indices.Length > 127) throw new InvalidOperationException("Armor stand attachments unavailable");
            var outputs = new List<ObjectRecord>();
            foreach (int index in indices)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(data.GetInt(Key(index,"item"),0)); var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!drop) throw new InvalidOperationException("Armor stand item definition unavailable");
                var item = drop.m_itemData.Clone(); item.m_dropPrefab = prefab; ItemDrop.LoadFromZDO(item,data,index);
                if (item.m_stack != 1) throw new InvalidOperationException("Invalid armor stand attachment quantity");
                item.m_equipped = false;
                outputs.Add(PlayerDropGame.Ground(item,stand.m_dropSpawnPoint.position,stand.m_dropSpawnPoint.rotation,0,Vector3.up*4));
            }
            using (var world = new PlayerActionObjectGame(data))
            {
                foreach (int index in indices) { world.Set(Key(index,"item"),0); world.Set(Key(index,"variant"),0); world.Set(Key(index,"itemData"),Array.Empty<byte>()); }
                return world.FinishWithObjects(empty,outputs,() =>
                {
                    foreach (int index in indices) stand.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_SetVisualItem",index,0,0);
                    stand.m_destroyEffects.Create(stand.m_dropSpawnPoint.position,Quaternion.identity,null,1,-1,default(ZDOID));
                });
            }
        }
        private static void Send(ArmorStand stand,Humanoid user,string action,int index = 0,ItemDrop.ItemData item = null)
        {
            if (!stand.m_nview || !stand.m_nview.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            var id = stand.m_nview.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = action,
                TargetUser = id.UserID,TargetId = id.ID,Variant = index,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
        }
        [HarmonyPatch(typeof(ArmorStand),"UseItem")]
        private static class Item
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ArmorStand __instance,Switch caller,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            {
                if (!Managed(user)) return true; __result = false;
                int index = __instance.m_slots.FindIndex(s => s.m_switch == caller);
                if (index >= 0) Send(__instance,user,item == null ? Drop : Attach,index,item);
                return false;
            }
        }
        private static bool PoseControl(Switch control,Humanoid user)
        {
            if (!Managed(user)) return false;
            var stand = control.GetComponentInParent<ArmorStand>();
            if (!stand || stand.m_changePoseSwitch != control) return false;
            Send(stand,user,Pose); return true;
        }
        [HarmonyPatch(typeof(Switch),nameof(Switch.Interact))]
        private static class PoseInteract
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Switch __instance,Humanoid character,bool hold,ref bool __result)
            { if (hold && Managed(character)) { var stand = __instance.GetComponentInParent<ArmorStand>(); if (stand && stand.m_changePoseSwitch == __instance) { __result = false; return false; } } if (!PoseControl(__instance,character)) return true; __result = false; return false; }
        }
        [HarmonyPatch(typeof(Switch),nameof(Switch.UseItem))]
        private static class PoseUse
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Switch __instance,Humanoid user,ref bool __result)
            { if (!PoseControl(__instance,user)) return true; __result = false; return false; }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { foreach (string name in new[] { "RPC_DropItem","RPC_DropItemByName","RPC_RequestOwn","UpdateAttach" }) yield return AccessTools.Method(typeof(ArmorStand),name); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ArmorStand __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch]
        private static class Mutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(ArmorStand),"OnDestroyed"); yield return AccessTools.Method(typeof(ArmorStand),"RPC_DestroyAttachment"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ArmorStand __instance,MethodBase __originalMethod,object[] __args) => !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
        }
        [HarmonyPatch(typeof(ArmorStand),nameof(ArmorStand.RPC_SetPose))]
        private static class PoseAuthority
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ArmorStand __instance,long sender,int index) => !Enabled ||
                index >= 0 && index < __instance.m_poseCount && ZNet.instance &&
                sender == (ZNet.instance.IsServer() ? ZNet.GetUID() : ZNet.instance.GetServerPeer()?.m_uid);
        }
    }
}


