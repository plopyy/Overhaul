using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerStandGame
    {
        internal const string Attach = "stand.attach", Drop = "stand.drop", Rotate = "stand.rotate", Power = "stand.power";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        private static bool applyingPower;
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var stand = target.GetComponent<ItemStand>();
            if (!stand || !stand.m_nview || !stand.m_nview.IsValid() || request.Action.Amount != 1 ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Item stand is unavailable");
            var data = stand.m_nview.GetZDO();
            if (request.Gameplay.Definition == Attach)
            {
                if (stand.HaveAttachment()) throw new InvalidOperationException("Item stand is occupied");
                int slot = request.Action.FromY*256+request.Action.FromX;
                if (!request.Gameplay.Alternate)
                {
                    if (!stand.m_autoAttach || stand.m_supportedItems.Count != 1 || !stand.m_supportedItems[0]) throw new InvalidOperationException("Stand requires a selected item");
                    int? found = inventory.Keys.Where(k => inventory.Available(k) && PlayerInventoryView.ReadItem(inventory.Item(k),null,true).m_shared.m_name == stand.m_supportedItems[0].m_itemData.m_shared.m_name).Select(k => (int?)k).FirstOrDefault();
                    if (!found.HasValue) throw new InvalidOperationException("Stand item is missing"); slot = found.Value;
                }
                var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true); item.m_customData = inventory.Data(slot);
                if (!stand.CanAttach(item)) throw new InvalidOperationException("Item cannot be attached to this stand");
                inventory.Remove(slot,1,true); if (inventory.Keys.Contains(slot)) inventory.Equip(slot,false);
                item.m_stack = 1; item.m_equipped = false; item.m_customData.Remove("eaqs_parked");
                var payload = new ZPackage(); payload.Write((byte)109); item.Save(payload);
                var rows = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
                rows.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.ItemStandUses).ToString(CultureInfo.InvariantCulture),1));
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(ZDOVars.s_item,item.m_dropPrefab.name.GetStableHashCode()); world.Set(ZDOVars.s_itemData,payload.GetArray());
                    world.Set(ZDOVars.s_quality,item.m_quality); world.Set(ZDOVars.s_variant,item.m_variant);
                    return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,rows),
                        () => stand.m_nview.InvokeRPC(ZNetView.Everybody,"SetVisualItem",item.m_dropPrefab.name.GetStableHashCode(),item.m_variant,item.m_quality,data.GetInt(ZDOVars.s_type,0)));
                }
            }
            if (!stand.HaveAttachment()) throw new InvalidOperationException("Item stand is empty");
            if (request.Gameplay.Definition == Power) return SelectPower(actor,stand,request,snapshot);
            var prefab = ObjectDB.instance.GetItemPrefab(data.GetInt(ZDOVars.s_item,0)); var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!drop) throw new InvalidOperationException("Attached item definition is unavailable");
            if (request.Gameplay.Definition == Rotate)
            {
                var orientations = new List<ItemStand.OrientationSettings>(); stand.GetOrientationSettings(drop.m_itemData,ref orientations);
                if (orientations.Count <= 1) throw new InvalidOperationException("Stand does not support orientation changes");
                int orientation = (Math.Max(0,data.GetInt(ZDOVars.s_type,0))+1)%orientations.Count;
                using (var world = new PlayerActionObjectGame(data))
                {
                    world.Set(ZDOVars.s_type,orientation);
                    return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),
                        () => stand.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_UpdateVisual"));
                }
            }
            if (request.Gameplay.Definition != Drop || !stand.m_canBeRemoved || !stand.m_dropSpawnPoint)
                throw new InvalidOperationException("Stand attachment cannot be removed by this action");
            var restored = drop.m_itemData.Clone(); restored.m_dropPrefab = prefab; ItemDrop.LoadFromZDO(restored,data);
            if (restored.m_stack != 1) throw new InvalidOperationException("Invalid stand attachment quantity");
            restored.m_equipped = false;
            var attach = prefab.transform.Find("attach");
            bool offset = prefab.transform.Find("attachobj") && attach;
            var output = PlayerDropGame.Ground(restored,stand.m_dropSpawnPoint.position+(offset ? attach.localPosition : Vector3.zero),
                stand.m_dropSpawnPoint.rotation*(offset ? attach.localRotation : Quaternion.identity),0,Vector3.up*4);
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_item,0); world.Set(ZDOVars.s_type,0); world.Set(ZDOVars.s_itemData,Array.Empty<byte>());
                return world.FinishWithObjects(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),new[] { output },() =>
                {
                    stand.m_nview.InvokeRPC(ZNetView.Everybody,"SetVisualItem",0,0,0,0);
                    stand.m_effects.Create(stand.m_dropSpawnPoint.position,Quaternion.identity,null,1,-1,default(ZDOID));
                });
            }
        }
        private static PlayerActionPlan SelectPower(ZDO actor,ItemStand stand,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            if (!stand.m_guardianPower || ObjectDB.instance.GetStatusEffect(stand.m_guardianPower.name.GetStableHashCode()) != stand.m_guardianPower ||
                float.IsNaN(stand.m_powerActivationDelay) || float.IsInfinity(stand.m_powerActivationDelay) || stand.m_powerActivationDelay < 0 || stand.m_powerActivationDelay > 20)
                throw new InvalidOperationException("Guardian power is unavailable");
            string power = stand.m_guardianPower.name;
            if (snapshot.Rows.Any(r => r.Table == "state" && (string)r.Values[0] == "guardian_power" && (string)r.Values[3] == power))
                throw new InvalidOperationException("Guardian power is already selected");
            var rows = new List<PlayerChange> { new PlayerChange("state",false,"guardian_power",null,null,power,null),
                new PlayerChange("knowledge",false,"uniques",power,""),
                PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.SetGuardianPower).ToString(CultureInfo.InvariantCulture),1) };
            string stat = power == "GP_TheElder" ? "SetPowerElder" : power.StartsWith("GP_",StringComparison.Ordinal) ? "SetPower"+power.Substring(3) : "";
            if (Enum.TryParse(stat,out PlayerStatType type) && type != PlayerStatType.None)
                rows.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)type).ToString(CultureInfo.InvariantCulture),1));
            float due = Time.time+stand.m_powerActivationDelay; int trophy = stand.m_nview.GetZDO().GetInt(ZDOVars.s_item,0);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,rows),new Dictionary<long,ObjectRecord>()),() =>
            {
                try { if (stand) stand.m_activatePowerEffects.Create(stand.transform.position,stand.transform.rotation,null,1,-1,default(ZDOID)); }
                catch (Exception error) { ZLog.LogWarning("[Overhaul guardian visual] " + error.Message); }
            })
            {
                Ready = () =>
                {
                    if (!stand || !stand.m_nview.IsValid() || stand.m_nview.GetZDO().GetInt(ZDOVars.s_item,0) != trophy || actor.GetBool(ZDOVars.s_dead,false) ||
                        Vector3.Distance(actor.GetPosition(),stand.transform.position) > 5 ||
                        !Storage.ChestAccess.WardAccessAt(stand.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                        throw new InvalidOperationException("Guardian selection interrupted");
                    return Time.time >= due;
                }
            };
        }
        internal static Action PowerPresentation(IEnumerable<PlayerChange> changes,Player player)
        {
            var rows = changes.ToArray(); if (rows.Length == 0) return () => { };
            if (!player || rows.Length != 1 || rows[0].Delete || rows[0].Table != "state" || (string)rows[0].Values[0] != "guardian_power")
                throw new System.IO.InvalidDataException("Invalid guardian power confirmation");
            string power = (string)rows[0].Values[3];
            if (string.IsNullOrEmpty(power) || !ObjectDB.instance.GetStatusEffect(power.GetStableHashCode()))
                throw new System.IO.InvalidDataException("Unknown confirmed guardian power");
            return () => { applyingPower = true; try { player.SetGuardianPower(power); } finally { applyingPower = false; } };
        }
        private static bool Managed(Humanoid user) => user && user == Player.m_localPlayer && PlayerSessionGame.Managed;
        private static void Send(ItemStand stand,Humanoid user,string action,ItemDrop.ItemData item = null)
        {
            if (!stand.m_nview || !stand.m_nview.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            var id = stand.m_nview.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = action,
                TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
        }
        [HarmonyPatch(typeof(ItemStand),nameof(ItemStand.UseItem))]
        private static class Item
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ItemStand __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            { if (!Managed(user)) return true; __result = false; if (item != null) Send(__instance,user,Attach,item); return false; }
        }
        [HarmonyPatch(typeof(ItemStand),nameof(ItemStand.Interact))]
        private static class Interact
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ItemStand __instance,Humanoid user,bool hold,bool alt,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false;
                if (!__instance.HaveAttachment()) Send(__instance,user,Attach);
                else if (alt) Send(__instance,user,Rotate);
                else if (hold && __instance.m_canBeRemoved) Send(__instance,user,Drop);
                else if (__instance.m_guardianPower) Send(__instance,user,Power);
                return false;
            }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "RPC_DropItem","RPC_RequestOwn","UpdateAttach","UpdateOrientation" }) yield return AccessTools.Method(typeof(ItemStand),name);
            }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ItemStand __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch]
        private static class Mutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(ItemStand),"OnDestroyed"); yield return AccessTools.Method(typeof(ItemStand),"RPC_DestroyAttachment"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(ItemStand __instance,MethodBase __originalMethod,object[] __args) => !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
        }
        [HarmonyPatch(typeof(Player),nameof(Player.SetGuardianPower))]
        private static class SelectPowerGuard
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Player __instance) => !Managed(__instance) || __instance.m_isLoading || applyingPower;
        }
    }
}
