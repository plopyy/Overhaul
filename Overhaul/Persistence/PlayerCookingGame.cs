using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerCookingGame
    {
        internal const string Interaction = "cooking.food";
        private static void Slot(PlayerActionObjectGame world,int slot,string name,float time,CookingStation.Status status,bool cheated)
        {
            world.Set(("slot"+slot).GetStableHashCode(),name); world.Set(("slot"+slot).GetStableHashCode(),time);
            world.Set(("slotstatus"+slot).GetStableHashCode(),(int)status); world.Set(ZDOVars.s_cheatedQueued+slot,cheated ? 1 : 0);
        }
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var station = target.GetComponent<CookingStation>();
            if (!station || request.Action.Amount != 1) throw new InvalidOperationException("Invalid cooking interaction");
            if (!request.Gameplay.Alternate)
                for (int i = 0; i < station.m_slots.Length; i++)
                {
                    station.GetSlot(i,out string name,out _,out var status,out bool cheated);
                    if (!string.IsNullOrEmpty(name) && station.IsItemDone(name)) return Take(actor,station,i,name,status,cheated,request,snapshot);
                }
            if (station.m_requireFire && !station.IsFireLit()) throw new InvalidOperationException("Cooking station needs fire");
            int free = station.GetFreeSlot(); if (free < 0) throw new InvalidOperationException("Cooking station is full");
            var allowed = station.m_conversion.Where(c => c.m_from && !station.m_incompatibleItems.Any(i => i.m_item && i.m_item.m_itemData.m_shared.m_name == c.m_from.m_itemData.m_shared.m_name))
                .Select(c => c.m_from.name.GetStableHashCode());
            int? selected = request.Gameplay.Alternate ? (int?)(request.Action.FromY*256+request.Action.FromX) : null;
            var used = inventory.ConsumeAvailable(allowed,1,0,selected,true).Single();
            var prefab = ObjectDB.instance.GetItemPrefab(used.Prefab); var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            if (!selected.HasValue && station.m_skill != Skills.SkillType.None) changes.AddRange(PlayerCraftProgressGame.Raise(snapshot,station.m_skill,0.4f));
            using (var world = new PlayerActionObjectGame(station.m_nview.GetZDO()))
            {
                Slot(world,free,prefab.name,0,CookingStation.Status.NotDone,used.Cheated);
                return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),() =>
                {
                    station.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_SetSlotVisual",free,prefab.name);
                    station.m_addEffect.Create(station.m_slots[free].position,Quaternion.identity,null,1,-1,default(ZDOID));
                });
            }
        }
        private static PlayerActionPlan Take(ZDO actor,CookingStation station,int slot,string name,CookingStation.Status status,bool cheated,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(name); var template = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!template) throw new InvalidOperationException("Cooked item is unavailable");
            var changes = new List<PlayerChange>();
            if (station.m_skill != Skills.SkillType.None) changes.AddRange(PlayerCraftProgressGame.Raise(snapshot,station.m_skill,0.6f));
            var skills = new HashSet<int>(changes.Where(r => r.Table == "skills").Select(r => Convert.ToInt32(r.Values[0])));
            var afterSkill = new PlayerSnapshot(snapshot.Revision,snapshot.Rows.Where(r => r.Table != "skills" || !skills.Contains(Convert.ToInt32(r.Values[0]))).Concat(changes));
            var gui = PlayerCraftGame.CraftGui(); int count = 1; bool bonus = station.m_canGiveBonusYield &&
                UnityEngine.Random.value < PlayerCraftProgressGame.Factor(afterSkill,station.m_skill)*(gui ? gui.m_craftBonusChance : 0.25f);
            if (bonus) count += gui ? gui.m_craftBonusAmount : 1;
            if (count < 1 || count > 127) throw new InvalidOperationException("Cooking yield is out of range");
            var stats = new Dictionary<PlayerStatType,int>();
            void Stat(PlayerStatType type) { if (type != PlayerStatType.None) stats[type] = stats.TryGetValue(type,out int n) ? n+1 : 1; }
            if (bonus) Stat(station.m_bonusStat);
            Stat(status == CookingStation.Status.Done ? station.m_cookStat : station.m_burntStat);
            foreach (var pair in stats) changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)pair.Key).ToString(CultureInfo.InvariantCulture),pair.Value));
            changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:craft",template.m_itemData.m_shared.m_name,count));
            Vector3 direction,position;
            if (station.m_spawnPoint) { position = station.m_spawnPoint.position; direction = station.m_spawnPoint.forward; }
            else { position = station.m_slots[slot].position; direction = actor.GetPosition()-position; direction.y = 0; direction.Normalize(); position += direction*0.5f; }
            var spawned = new List<ObjectRecord>();
            for (int i = 0; i < count; i++)
            {
                var item = template.m_itemData.Clone(); item.m_dropPrefab = prefab; item.m_equipped = false; item.m_cheated = cheated; item.m_worldLevel = Game.m_worldLevel;
                if (station.m_recordCrafter)
                {
                    item.m_crafterID = actor.GetLong(ZDOVars.s_playerID,0);
                    item.m_crafterName = (string)snapshot.Rows.FirstOrDefault(r => r.Table == "state" && (string)r.Values[0] == "player_name")?.Values[3] ?? "";
                }
                spawned.Add(PlayerDropGame.Ground(item,position,Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),0,direction*station.m_spawnForce));
            }
            using (var world = new PlayerActionObjectGame(station.m_nview.GetZDO()))
            {
                Slot(world,slot,"",0,CookingStation.Status.NotDone,false);
                return world.FinishWithObjects(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),spawned,() =>
                {
                    station.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_SetSlotVisual",slot,"");
                    station.m_pickEffector.Create(position,Quaternion.identity,null,1,-1,default(ZDOID));
                    if (bonus && gui) gui.m_craftBonusEffect.Create(position,Quaternion.identity,null,1,-1,default(ZDOID));
                });
            }
        }
        private static bool Send(CookingStation station,Humanoid user,ItemDrop.ItemData item)
        {
            if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
            if (!station.m_nview || !station.m_nview.IsValid()) return false;
            var id = station.m_nview.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Interaction,
                TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
            return false;
        }
        [HarmonyPatch(typeof(CookingStation),"OnInteract")]
        private static class Automatic { [HarmonyPriority(Priority.First+200)] private static bool Prefix(CookingStation __instance,Humanoid user,ref bool __result) { __result = false; return Send(__instance,user,null); } }
        [HarmonyPatch(typeof(CookingStation),"OnUseItem")]
        private static class Selected { [HarmonyPriority(Priority.First+200)] private static bool Prefix(CookingStation __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result) { __result = false; return Send(__instance,user,item); } }
    }
}
