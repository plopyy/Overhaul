using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerOfferingGame
    {
        internal const string Interaction = "altar.offer";
        private static readonly int Until = "overhaul_altar_until".GetStableHashCode();
        private static readonly int AltarPrefab = "overhaul_boss_altar".GetStableHashCode(), BowlIndex = "overhaul_boss_bowl".GetStableHashCode(), EffectsUntil = "overhaul_boss_effects_until".GetStableHashCode();
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var command = request.Gameplay; var target = ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            var view = target ? target.GetComponent<ZNetView>() : null; var data = view && view.IsValid() ? view.GetZDO() : null;
            var bowl = target ? target.GetComponentsInChildren<OfferingBowl>(true).ElementAtOrDefault(command.Variant) : null;
            if (data == null || !data.Persistent || !bowl || !bowl.m_nview || bowl.m_nview.GetZDO() != data || GamePersistence.ActionReserved(data.m_uid) ||
                request.Action.Amount != 1 || Vector3.Distance(actor.GetPosition(),bowl.transform.position) > 5 || bowl.IsBossSpawnQueued() ||
                data.GetLong(Until,0) > ZNet.instance.GetTime().Ticks || !Storage.ChestAccess.WardAccessAt(bowl.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Offering altar is unavailable");
            var stands = new List<ItemStand>();
            if (bowl.m_useItemStands)
            {
                if (command.Alternate || !bowl.m_bossPrefab) throw new InvalidOperationException("Altar requires its item stands");
                stands = bowl.FindItemStands();
                if (stands.Count == 0 || stands.Count > 126 || stands.Any(s => !s.m_nview || !s.m_nview.IsValid() || !s.HaveAttachment() ||
                    !Storage.ChestAccess.WardAccessAt(s.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))) throw new InvalidOperationException("Altar offerings are incomplete");
            }
            else
            {
                if (!command.Alternate || !bowl.m_bossItem || bowl.m_bossItems < 1 || bowl.m_bossItems > 4096) throw new InvalidOperationException("Altar ingredient is unavailable");
                int slot = request.Action.FromY*256+request.Action.FromX;
                var selected = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
                if (selected.m_shared.m_name != bowl.m_bossItem.m_itemData.m_shared.m_name) throw new InvalidOperationException("Wrong altar ingredient");
                var used = inventory.ConsumeAvailable(new[] { bowl.m_bossItem.gameObject.name.GetStableHashCode() },bowl.m_bossItems,Game.m_worldLevel,slot,false);
                if (used.Sum(i => i.Count) != bowl.m_bossItems) throw new InvalidOperationException("Not enough altar ingredients");
            }
            ObjectRecord output; Vector3 point; float delay = 0;
            if (bowl.m_bossPrefab)
            {
                if (!bowl.CanSpawnBoss(bowl.GetSpawnPosition(),out point)) throw new InvalidOperationException("No valid boss spawn location");
                delay = bowl.m_spawnBossDelay;
                if (float.IsNaN(delay) || float.IsInfinity(delay) || delay < 0 || delay > 120) throw new InvalidOperationException("Invalid boss spawn delay");
                var rotation = bowl.m_spawnPoints.Count > 0 ? bowl.m_spawnPoints[0].transform.rotation : Quaternion.identity;
                output = GamePersistence.AllocateActionObject(bowl.m_bossPrefab,point,rotation);
                var ai = bowl.m_bossPrefab.GetComponent<BaseAI>();
                if (ai)
                {
                    output.Properties.Add(new PropertyRecord { Key = ZDOVars.s_patrol,Type = "int",Value = 1 });
                    output.Properties.Add(new PropertyRecord { Key = ZDOVars.s_patrolPoint,Type = "vector3",Value = GameSnapshot.Components(point) });
                    if (bowl.m_alertOnSpawn && ai.m_canBeAlerted) output.Properties.Add(new PropertyRecord { Key = ZDOVars.s_alert,Type = "int",Value = 1 });
                }
                output.Properties.Add(new PropertyRecord { Key = AltarPrefab,Type = "int",Value = data.GetPrefab() });
                output.Properties.Add(new PropertyRecord { Key = BowlIndex,Type = "int",Value = command.Variant });
                output.Properties.Add(new PropertyRecord { Key = EffectsUntil,Type = "long",Value = ZNet.instance.GetTime().AddSeconds(delay+30).Ticks });
            }
            else
            {
                if (!bowl.m_itemPrefab || !bowl.m_itemSpawnPoint) throw new InvalidOperationException("Altar reward is unavailable");
                var item = bowl.m_itemPrefab.m_itemData.Clone(); item.m_dropPrefab = bowl.m_itemPrefab.gameObject;
                if (item.m_shared.m_questItem && snapshot.Rows.Any(r => r.Table == "knowledge" && (string)r.Values[0] == "uniques" && (string)r.Values[1] == item.m_shared.m_name))
                    throw new InvalidOperationException("Altar quest reward is already known");
                item.m_worldLevel = Game.m_worldLevel; item.m_equipped = false; point = bowl.m_itemSpawnPoint.position;
                output = PlayerDropGame.Ground(item,point,Quaternion.identity);
            }
            var targets = new Dictionary<ZDOID,PlayerActionObjectGame>();
            using (var key = !string.IsNullOrEmpty(bowl.m_setGlobalKey) && !ZoneSystem.instance.GetGlobalKey(bowl.m_setGlobalKey) ? new PlayerWorldKeyGame.Reservation(bowl.m_setGlobalKey) : null)
            {
                try
                {
                    PlayerActionObjectGame Reserve(ZDO value)
                    { if (!targets.TryGetValue(value.m_uid,out var world)) { world = new PlayerActionObjectGame(value); targets.Add(value.m_uid,world); } return world; }
                    var primary = Reserve(data);
                    if (delay > 0) primary.Set(Until,ZNet.instance.GetTime().AddSeconds(delay).Ticks);
                    foreach (var stand in stands)
                    {
                        var world = Reserve(stand.m_nview.GetZDO()); world.Set(ZDOVars.s_item,0); world.Set(ZDOVars.s_type,0); world.Set(ZDOVars.s_itemData,Array.Empty<byte>());
                    }
                    var player = inventory.Delta(request.Action.Operation,snapshot.Revision);
                    var plans = targets.Values.Where(w => w != primary).Select(w => w.Finish(player)).ToList();
                    plans.Add(primary.FinishWithDelayedObjects(player,new[] { output },delay,() =>
                    {
                        foreach (var stand in stands) if (stand) stand.m_nview.InvokeRPC(ZNetView.Everybody,"SetVisualItem",0,0,0,0);
                        if (!bowl) return;
                        if (bowl.m_bossPrefab) bowl.m_spawnBossStartEffects.Create(point,Quaternion.identity,null,1,-1,default(ZDOID));
                        else bowl.m_fuelAddedEffects.Create(point,bowl.transform.rotation,null,1,-1,default(ZDOID));
                    },null));
                    var combined = new PlayerActionPlan(new PlayerWorldAction(player,plans.SelectMany(p => p.Change.Objects).ToDictionary(p => p.Key,p => p.Value)),
                        () => { foreach (var plan in plans) plan.Publish(); });
                    return key == null ? combined : key.Finish(combined);
                }
                catch { GamePersistence.ReleaseAction(targets.Keys.ToArray()); throw; }
                finally { foreach (var world in targets.Values) world.Dispose(); }
            }
        }
        private static void Send(OfferingBowl bowl,Humanoid user,ItemDrop.ItemData item)
        {
            var view = bowl.m_nview;
            if (!view || !view.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            int index = Array.IndexOf(view.GetComponentsInChildren<OfferingBowl>(true),bowl); if (index < 0) return;
            var id = view.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Interaction,TargetUser = id.UserID,TargetId = id.ID,Variant = index,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,1);
        }
        [HarmonyPatch(typeof(OfferingBowl),nameof(OfferingBowl.UseItem))]
        private static class UseItem
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(OfferingBowl __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            { if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true; __result = false; if (item != null) Send(__instance,user,item); return false; }
        }
        [HarmonyPatch(typeof(OfferingBowl),nameof(OfferingBowl.Interact))]
        private static class Interact
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(OfferingBowl __instance,Humanoid user,bool hold,ref bool __result)
            { if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true; __result = false; if (!hold && __instance.m_useItemStands) Send(__instance,user,null); return false; }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { foreach (string name in new[] { "RPC_SpawnBoss","RPC_RemoveBossSpawnInventoryItems","RemoveAltarItems","DelayedSpawnBoss" }) yield return AccessTools.Method(typeof(OfferingBowl),name); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(OfferingBowl __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch(typeof(OfferingBowl),"IsBossSpawnQueued")]
        private static class Queued
        {
            private static void Postfix(OfferingBowl __instance,ref bool __result)
            { if (__instance.m_nview && __instance.m_nview.IsValid() && ZNet.instance && __instance.m_nview.GetZDO().GetLong(Until,0) > ZNet.instance.GetTime().Ticks) __result = true; }
        }
        // Create effects only when the persisted boss actually receives its native components.
        // This does not force-load an abandoned spawn area or retain a prefab callback indefinitely.
        [HarmonyPatch(typeof(ZNetScene),"CreateObject")]
        private static class SpawnEffects
        {
            private static void Postfix(ZNetScene __instance,ZDO zdo,GameObject __result)
            {
                if (!__result || !zdo.IsOwner() || zdo.GetLong(EffectsUntil,0) <= 0) return;
                long until = zdo.GetLong(EffectsUntil,0); zdo.Set(EffectsUntil,0L);
                if (!ZNet.instance || until < ZNet.instance.GetTime().Ticks) return;
                try
                {
                    var prefab = __instance.GetPrefab(zdo.GetInt(AltarPrefab,0));
                    var bowl = prefab ? prefab.GetComponentsInChildren<OfferingBowl>(true).ElementAtOrDefault(zdo.GetInt(BowlIndex,0)) : null;
                    if (!bowl) return;
                    var character = __result.GetComponent<Character>();
                    foreach (var effect in bowl.m_spawnBossDoneffects.Create(zdo.GetPosition(),Quaternion.identity,null,1,-1,default(ZDOID)))
                        foreach (var projectile in effect.GetComponentsInChildren<IProjectile>()) projectile.Setup(character,Vector3.zero,-1,null,null,null);
                }
                catch (Exception error) { ZLog.LogWarning("[Overhaul boss spawn effects] "+error.Message); }
            }
        }
    }
}
