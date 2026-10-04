using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerDropGame
    {
        private static readonly int Dropper = "Overhaul.Dropper".GetStableHashCode();
        internal static ObjectRecord Ground(ItemDrop.ItemData item,Vector3 position,Quaternion rotation,long dropper = 0,Vector3 velocity = default(Vector3))
        {
            if (!item.m_dropPrefab || item.m_stack < 1 || item.m_stack > item.m_shared.m_maxStackSize || item.m_equipped)
                throw new InvalidOperationException("Invalid ground item");
            var record = GamePersistence.AllocateActionObject(item.m_dropPrefab,position,rotation);
            var package = new ZPackage(); package.Write(109); item.Save(package);
            void Add(int key,string type,object value) => record.Properties.Add(new PropertyRecord { Key = key,Type = type,Name = NameCatalog.Key(key),Value = value });
            Add(ZDOVars.s_itemData,"bytes",package.GetArray()); Add(ZDOVars.s_quality,"int",item.m_quality); Add(ZDOVars.s_variant,"int",item.m_variant);
            Add(ZDOVars.s_spawnTime,"long",ZNet.instance.GetTime().Ticks);
            if (dropper != 0) Add(Dropper,"long",dropper);
            if (velocity != Vector3.zero) Add(ZDOVars.s_bodyVelHash,"vector3",GameSnapshot.Components(velocity));
            return record;
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            int slot = request.Action.FromY*256+request.Action.FromX;
            var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true); item.m_customData = inventory.Data(slot);
            int amount = request.Action.Amount;
            if (amount > item.m_stack) throw new InvalidOperationException("Drop amount exceeds source stack");
            int originalAmount = item.m_stack;
            inventory.Remove(slot,amount); item.m_stack = amount; item.m_equipped = false; item.m_customData.Remove("eaqs_parked");
            if (amount < originalAmount) { var remainder = inventory.Item(slot); remainder[7] = false; inventory.Set(slot,remainder); }
            var rotation = actor.GetRotation(); var forward = rotation*Vector3.forward;
            var record = Ground(item,actor.GetPosition()+forward+Vector3.up,rotation,actor.GetLong(ZDOVars.s_playerID,0),
                (forward+Vector3.up)*(item.GetWeight(-1) >= 300f ? 0.5f : 5f));
            return new PlayerActionPlan(new PlayerWorldAction(inventory.Delta(request.Action.Operation,snapshot.Revision),
                new Dictionary<long,ObjectRecord> { [record.Id] = record }),() => GamePersistence.PublishActionObject(record));
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.DropItem))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Humanoid __instance,Inventory inventory,ItemDrop.ItemData item,int amount,ref bool __result)
            {
                if (__instance != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
                __result = false;
                if (item == null || amount <= 0 || inventory != null && inventory != __instance.GetInventory() ||
                    !__instance.GetInventory().ContainsItem(item) || __instance.IsTeleporting()) return false;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.Drop },item.m_gridPos.x,item.m_gridPos.y,Math.Min(amount,item.m_stack));
                return false;
            }
        }
        [HarmonyPatch(typeof(ItemDrop),"Awake")]
        private static class DroppedItem
        {
            private static void Postfix(ItemDrop __instance)
            {
                var view = __instance.m_nview; var player = Player.m_localPlayer;
                if (player && view && view.IsValid() && view.GetZDO().GetLong(Dropper,0) == player.GetPlayerID()) __instance.OnPlayerDrop();
            }
        }
    }
}
