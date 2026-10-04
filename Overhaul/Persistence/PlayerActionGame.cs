using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerActionGame
    {
        internal static PlayerChange Row(ItemDrop.ItemData item, int x = 0, int y = 0) => new PlayerChange("inventory",false,"main",x,y,
            item.m_dropPrefab.name.GetStableHashCode(),item.m_stack,item.m_quality,item.m_durability,item.m_equipped,item.m_variant,
            item.m_crafterID,item.m_crafterName ?? "",item.m_worldLevel,item.m_pickedUp,item.m_cheated);

        internal static PlayerActionPlan Prepare(ZRpc rpc, PlayerAdmission.Session session, InventoryMoveRequest request, PlayerSnapshot snapshot)
        {
            var actor = PlayerSessionGame.Actor(rpc);
            if (actor == null || actor.GetLong(ZDOVars.s_playerID,0) != PlayerSessionGame.CharacterId(session))
                throw new InvalidOperationException("Player actor is unavailable");
            var command = request.Gameplay;
            var inventory = new PlayerActionInventory(snapshot.Rows,InventoryMoveGame.PlayerLayout(snapshot.Rows));
            int slot = request.Action.FromY * 256 + request.Action.FromX;
            switch (command.Kind)
            {
                case PlayerActionKind.Pickup: return Pickup(actor,request,snapshot,inventory);
                case PlayerActionKind.UseOn: return PlayerMachineGame.Prepare(actor,request,snapshot,inventory);
                case PlayerActionKind.Craft: return PlayerCraftGame.Prepare(actor,request,snapshot,inventory);
                case PlayerActionKind.Equip: case PlayerActionKind.Unequip: return PlayerEquipmentGame.Prepare(actor,request,snapshot,inventory);
                case PlayerActionKind.Consume: return command.Definition == PlayerFoodGame.Placed ? PlayerFoodGame.FromWorld(actor,request,snapshot) : command.TargetId == 0 ? PlayerFoodGame.Prepare(request,snapshot,inventory) : PlayerFoodGame.FromContainer(rpc,request,snapshot);
                case PlayerActionKind.Buy: case PlayerActionKind.Sell: return PlayerTradeGame.Prepare(actor,request,snapshot,inventory);
                case PlayerActionKind.Drop: case PlayerActionKind.Trash:
                    return command.TargetId == 0 ? PlayerDropGame.Prepare(actor,request,snapshot,inventory) : PlayerDropGame.FromContainer(rpc,actor,request,snapshot);
                case PlayerActionKind.Repair:
                {
                    var station = Target(command)?.GetComponent<CraftingStation>();
                    if (!station || Vector3.Distance(actor.GetPosition(),station.transform.position) >= station.m_useDistance ||
                        station.m_craftRequireFire && !station.m_haveFire) throw new InvalidOperationException("Repair station is unavailable");
                    var values = inventory.Item(slot); var item = PlayerInventoryView.ReadItem(values,null,true);
                    if (!item.m_shared.m_canBeReparied || item.m_durability >= item.GetMaxDurability()) throw new InvalidOperationException("Item does not need repair");
                    values[6] = item.GetMaxDurability(); inventory.Set(slot,values);
                    return new PlayerActionPlan(new PlayerWorldAction(inventory.Delta(request.Action.Operation,snapshot.Revision),new Dictionary<long,ObjectRecord>()), () => { });
                }
                default: throw new InvalidOperationException("This gameplay action is not yet connected to server authority");
            }
        }
        private static GameObject Target(PlayerActionCommand command) => command.TargetId == 0 ? null : ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
        private static PlayerActionPlan Pickup(ZDO actor, InventoryMoveRequest request, PlayerSnapshot snapshot, PlayerActionInventory inventory)
        {
            var target = Target(request.Gameplay); var drop = target ? target.GetComponent<ItemDrop>() : null;
            var view = drop ? drop.m_nview : null; var data = view && view.IsValid() ? view.GetZDO() : null;
            if (data == null || !data.Persistent || GamePersistence.ActionReserved(data.m_uid) ||
                Vector3.Distance(actor.GetPosition(),data.GetPosition()) > 5f || Time.time - drop.m_spawnTime < 0.5f || drop.InTar())
                throw new InvalidOperationException("Ground item is unavailable");
            var item = drop.m_itemData.Clone(); ItemDrop.LoadFromZDO(item,data);
            if (!item.m_dropPrefab) item.m_dropPrefab = ObjectDB.instance.GetItemPrefab(data.GetPrefab());
            if (!item.m_dropPrefab || item.m_shared.m_icons == null || item.m_variant < 0 || item.m_variant >= item.m_shared.m_icons.Length)
                throw new InvalidDataException("Invalid ground item definition");
            if (item.m_shared.m_questItem && snapshot.Rows.Any(r => r.Table == "knowledge" && (string)r.Values[0] == "uniques" && (string)r.Values[1] == item.m_shared.m_name))
                throw new InvalidOperationException("Quest item already acquired");
            item.m_equipped = false; item.m_pickedUp = true;
            inventory.Add(Row(item).Values,item.m_customData);
            var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            changes.Add(new PlayerChange("knowledge",false,"materials",item.m_shared.m_name,""));
            if (item.m_shared.m_questItem) changes.Add(new PlayerChange("knowledge",false,"uniques",item.m_shared.m_name,""));
            return RemoveWorldItem(drop,new PlayerBatch(request.Action.Operation,snapshot.Revision,changes));
        }
        internal static PlayerActionPlan RemoveWorldItem(ItemDrop drop,PlayerBatch player)
        {
            var view = drop.m_nview; var data = view.GetZDO(); var target = drop.gameObject;
            var uid = data.m_uid;
            var record = GamePersistence.ReserveAction(data);
            try
            {
                // Reserve before submitting; native inventory/world mutations happen only after the commit.
                data.SetOwner(ZNet.GetUID());
                return new PlayerActionPlan(new PlayerWorldAction(player,
                    new Dictionary<long,ObjectRecord> { [record.Id] = null }), () =>
                    {
                        if (!drop || !view.IsValid()) throw new IOException("Reserved ground item disappeared before publication");
                        ZNetScene.instance.Destroy(target);
                        // Native Destroy queues the network notification. Remove the local ZDO now so
                        // an intervening world snapshot cannot resurrect the committed pickup.
                        ZDOMan.instance.HandleDestroyedZDO(uid);
                        InventoryMoveReservations.DiscardMutations(uid);
                        GamePersistence.ReleaseAction(new[] { uid });
                    });
            }
            catch { GamePersistence.ReleaseAction(new[] { uid }); throw; }
        }
        private static bool Managed(Humanoid player) => player && player == Player.m_localPlayer && PlayerSessionGame.Managed;
        private static bool Held(ItemDrop drop) => drop && drop.m_nview && drop.m_nview.IsValid() && GamePersistence.ActionReserved(drop.m_nview.GetZDO().m_uid);
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.Pickup))]
        private static class PickupIntent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Humanoid __instance,GameObject go,ref bool __result)
            {
                if (!Managed(__instance)) return true;
                __result = false;
                var drop = go ? go.GetComponent<ItemDrop>() : null;
                if (!drop || !drop.m_nview || !drop.m_nview.IsValid() || __instance.IsTeleporting()) return false;
                var id = drop.m_nview.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.Pickup,TargetUser = id.UserID,TargetId = id.ID });
                return false;
            }
        }
        [HarmonyPatch(typeof(ItemDrop),nameof(ItemDrop.Pickup))]
        private static class ManualPickupIntent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(ItemDrop __instance,Humanoid character)
            {
                if (!Managed(character)) return true;
                character.Pickup(__instance.gameObject,true,true); return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui),"RepairOneItem")]
        private static class RepairIntent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix()
            {
                var player = Player.m_localPlayer; if (!Managed(player)) return true;
                var station = player.GetCurrentCraftingStation(); var view = station ? station.GetComponent<ZNetView>() : null;
                if (!view || !view.IsValid()) return false;
                var item = player.GetInventory().GetAllItems().FirstOrDefault(i => i.m_shared.m_canBeReparied && i.m_durability < i.GetMaxDurability());
                if (item == null) return false;
                var id = view.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.Repair,TargetUser = id.UserID,TargetId = id.ID },item.m_gridPos.x,item.m_gridPos.y);
                return false;
            }
        }
        [HarmonyPatch(typeof(ItemDrop),"AutoStackItems")]
        private static class StackGuard
        {
            // A neighboring drop can absorb the reserved drop, so pause this rare background
            // operation while a pickup transaction is outstanding.
            private static bool Prefix() => !GamePersistence.HasActionReservations;
        }
        [HarmonyPatch(typeof(ItemDrop),"SlowUpdate")]
        private static class SlowGuard { private static bool Prefix(ItemDrop __instance) => !Held(__instance); }
        [HarmonyPatch(typeof(ItemDrop),"RPC_RequestOwn")]
        private static class OwnerGuard { private static bool Prefix(ItemDrop __instance) => !Held(__instance); }
        [HarmonyPatch(typeof(ItemDrop),nameof(ItemDrop.RemoveOne))]
        private static class RemoveGuard { private static bool Prefix(ItemDrop __instance,ref bool __result) { if (!Held(__instance)) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(ItemDrop),nameof(ItemDrop.CanPickup))]
        private static class PickupGuard { private static void Postfix(ItemDrop __instance,ref bool __result) { if (Held(__instance)) __result = false; } }
    }
}


