using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerMachineGame
    {
        internal const string Ore = "smelter.ore", Fuel = "smelter.fuel", CookingFuel = "cooking.fuel", FireFuel = "fire.fuel";
        internal const string ShieldFuel = "shield.fuel", Fireworks = "fire.fireworks";
        internal const string Ferment = "fermenter.input", Tap = "fermenter.output", Processed = "smelter.output";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        private static bool Managed(Humanoid user) => user && user == Player.m_localPlayer && PlayerSessionGame.Managed;
        private static bool Held(Component component)
        {
            var view = component ? component.GetComponent<ZNetView>() : null;
            return view && view.IsValid() && GamePersistence.ActionReserved(view.GetZDO().m_uid);
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var command = request.Gameplay;
            var target = ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            var view = target ? target.GetComponent<ZNetView>() : null; var data = view && view.IsValid() ? view.GetZDO() : null;
            if (data == null || !data.Persistent || GamePersistence.ActionReserved(data.m_uid) ||
                Vector3.Distance(actor.GetPosition(),data.GetPosition()) > 5f) throw new InvalidOperationException("Machine is unavailable");
            if (command.Definition == PlayerCookingGame.Interaction) return PlayerCookingGame.Prepare(actor,target,request,snapshot,inventory);
            if (command.Definition == PlayerRecyclerGame.Interaction) return PlayerRecyclerGame.Prepare(actor,target,request,snapshot);
            if (command.Definition == Fireworks) return LaunchFireworks(actor,target,request,snapshot,inventory);
            if (command.Definition == Processed) return TakeProcessed(target,request,snapshot);
            if (command.Definition == Tap) return TapFermenter(actor,target,request,snapshot);
            if (command.Definition == PlayerStandGame.Attach || command.Definition == PlayerStandGame.Drop || command.Definition == PlayerStandGame.Rotate || command.Definition == PlayerStandGame.Power)
                return PlayerStandGame.Prepare(actor,target,request,snapshot,inventory);
            if (command.Definition == PlayerArmorStandGame.Attach || command.Definition == PlayerArmorStandGame.Drop || command.Definition == PlayerArmorStandGame.Pose)
                return PlayerArmorStandGame.Prepare(actor,target,request,snapshot,inventory);
            int? selected = command.Alternate ? (int?)(request.Action.FromY * 256 + request.Action.FromX) : null;
            int capacity; float currentFuel = data.GetFloat(ZDOVars.s_fuel,0); IEnumerable<int> allowed;
            bool ore = command.Definition == Ore, oneType = true;
            bool ferment = command.Definition == Ferment;
            EffectList effects; Smelter smelter = null;
            switch (command.Definition)
            {
                case Ore: case Fuel:
                    smelter = target.GetComponent<Smelter>();
                    if (!smelter) throw new InvalidOperationException("Target is not a smelter");
                    if (ore)
                    {
                        Storage.CharcoalKilnWoods.PrioritizeNormalWood(smelter);
                        capacity = Math.Max(0,smelter.m_maxOre - data.GetInt(ZDOVars.s_queued,0));
                        allowed = smelter.m_conversion.Where(c => c.m_from).Select(c => c.m_from.gameObject.name.GetStableHashCode());
                        oneType = selected.HasValue || Utils.GetPrefabName(target) == "charcoal_kiln";
                        effects = smelter.m_oreAddedEffects;
                    }
                    else
                    {
                        capacity = Math.Max(0,Mathf.FloorToInt(smelter.m_maxFuel-currentFuel));
                        allowed = Prefab(smelter.m_fuelItem); effects = smelter.m_fuelAddedEffects;
                    }
                    break;
                case ShieldFuel:
                    var shield = target.GetComponent<ShieldGenerator>();
                    if (!shield || !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                        throw new InvalidOperationException("Shield generator is unavailable");
                    currentFuel = data.GetFloat(ZDOVars.s_fuel,shield.m_defaultFuel);
                    capacity = Math.Max(0,Mathf.FloorToInt(shield.m_maxFuel-currentFuel));
                    allowed = shield.m_fuelItems.Where(i => i).Select(i => i.gameObject.name.GetStableHashCode());
                    effects = shield.m_fuelAddedEffects; break;
                case CookingFuel:
                    var cooking = target.GetComponent<CookingStation>();
                    if (!cooking || !cooking.m_useFuel) throw new InvalidOperationException("Target does not use cooking fuel");
                    capacity = Math.Max(0,Mathf.FloorToInt(cooking.m_maxFuel-currentFuel));
                    allowed = Prefab(cooking.m_fuelItem); effects = cooking.m_fuelAddedEffects;
                    break;
                case FireFuel:
                    var fire = target.GetComponent<Fireplace>();
                    if (!fire || !fire.m_canRefill || fire.m_infiniteFuel) throw new InvalidOperationException("Fire cannot be refilled");
                    capacity = Math.Max(0,Mathf.FloorToInt(fire.m_maxFuel-currentFuel));
                    allowed = Prefab(fire.m_fuelItem); effects = fire.m_fuelAddedEffects;
                    break;
                case Ferment:
                    var fermenter = target.GetComponent<Fermenter>();
                    if (!fermenter || data.GetInt(ZDOVars.s_content,0) != 0 ||
                        !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                        throw new InvalidOperationException("Fermenter input is unavailable");
                    if (!selected.HasValue)
                    {
                        Cover.GetCoverForPoint(fermenter.m_roofCheckPoint.position,out float cover,out bool roof,0.5f);
                        if (!roof || cover < 0.7f) throw new InvalidOperationException("Fermenter requires cover");
                    }
                    capacity = 1; allowed = fermenter.m_conversion.Where(c => c.m_from).Select(c => c.m_from.gameObject.name.GetStableHashCode());
                    effects = fermenter.m_addedEffects; break;
                default: throw new InvalidOperationException("Unknown machine interaction");
            }
            int limit = Math.Min(4096,Math.Min(capacity,request.Action.Amount));
            var consumed = inventory.ConsumeAvailable(allowed,limit,Game.m_worldLevel,selected,oneType);
            var delta = inventory.Delta(request.Action.Operation,snapshot.Revision);
            using (var world = new PlayerActionObjectGame(data))
            {
                if (ore)
                {
                    int size = data.GetInt(ZDOVars.s_queued,0);
                    foreach (var resource in consumed)
                    {
                        var prefab = ObjectDB.instance.GetItemPrefab(resource.Prefab);
                        if (!prefab) throw new InvalidOperationException("Machine resource prefab is unavailable");
                        for (int i = 0; i < resource.Count; i++) world.Set(("item" + size++).GetStableHashCode(),prefab.name);
                    }
                    world.Set(ZDOVars.s_queued,size);
                    // Preserve an existing cheated input marker when adding an ordinary input.
                    world.Set(ZDOVars.s_cheatedQueued,data.GetBool(ZDOVars.s_cheatedQueued,false) || consumed.Any(c => c.Cheated) ? 1 : 0);
                }
                else if (ferment)
                {
                    world.Set(ZDOVars.s_content,consumed[0].Prefab); world.Set(ZDOVars.s_startTime,ZNet.instance.GetTime().Ticks);
                    world.Set(ZDOVars.s_cheatedQueued,consumed[0].Cheated ? 1 : 0);
                }
                else world.Set(ZDOVars.s_fuel,currentFuel + consumed.Sum(c => c.Count));
                return world.Finish(delta,() =>
                {
                    if (!target) return;
                    effects?.Create(target.transform.position,target.transform.rotation,null,1f,-1,default(ZDOID));
                    if (smelter && ore) { smelter.m_addedOreTime = Time.time; if (smelter.m_addOreAnimationDuration > 0) smelter.SetAnimation(true); }
                });
            }
        }
        private static PlayerActionPlan LaunchFireworks(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var fire = target.GetComponent<Fireplace>();
            if (!fire || !fire.m_canRefill || !fire.IsBurning() || request.Action.Amount != 1 || !request.Gameplay.Alternate ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Fireworks require an accessible burning fire");
            int slot = request.Action.FromY*256+request.Action.FromX;
            var selected = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
            var definition = (fire.m_fireworkItemList ?? Array.Empty<Fireplace.FireworkItem>()).FirstOrDefault(f => f.m_fireworkItem && f.m_fireworkItem.m_itemData.m_shared.m_name == selected.m_shared.m_name);
            if (!definition.m_fireworkItem || definition.m_fireworkItemCount < 1 || definition.m_fireworkItemCount > 4096)
                throw new InvalidOperationException("Item is not a firework ingredient");
            var consumed = inventory.ConsumeAvailable(new[] { definition.m_fireworkItem.gameObject.name.GetStableHashCode() },definition.m_fireworkItemCount,Game.m_worldLevel,slot,false);
            if (consumed.Sum(c => c.Count) != definition.m_fireworkItemCount) throw new InvalidOperationException("Not enough firework ingredients");
            // The effect is transient; its ingredient debit is durable before the network effect is spawned.
            return new PlayerActionPlan(new PlayerWorldAction(inventory.Delta(request.Action.Operation,snapshot.Revision),new Dictionary<long,ObjectRecord>()),() =>
            {
                try
                {
                    if (!fire) return;
                    var rotation = Quaternion.Euler(UnityEngine.Random.Range(-fire.m_fireworksMaxRandomAngle,fire.m_fireworksMaxRandomAngle),0,
                        UnityEngine.Random.Range(-fire.m_fireworksMaxRandomAngle,fire.m_fireworksMaxRandomAngle));
                    definition.m_fireworksEffects.Create(fire.transform.position,rotation,null,1,-1,default(ZDOID));
                    fire.m_fuelAddedEffects.Create(fire.transform.position,fire.transform.rotation,null,1,-1,default(ZDOID));
                }
                catch (Exception error) { ZLog.LogWarning("[Overhaul fireworks visual] "+error.Message); }
            });
        }
        private static IEnumerable<int> Prefab(ItemDrop item) => item ? new[] { item.gameObject.name.GetStableHashCode() } : Array.Empty<int>();
        private static PlayerActionPlan TapFermenter(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var fermenter = target.GetComponent<Fermenter>();
            if (!fermenter || request.Action.Amount != 1 || request.Gameplay.Alternate ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)) ||
                fermenter.GetStatus() != Fermenter.Status.Ready)
                throw new InvalidOperationException("Fermenter is not ready for collection");
            var data = fermenter.m_nview.GetZDO(); var conversion = fermenter.GetItemConversion(fermenter.GetContent());
            if (conversion?.m_to == null || conversion.m_producedItems < 1 || conversion.m_producedItems > 127 || !fermenter.m_outputPoint)
                throw new InvalidOperationException("Invalid fermentation output");
            var position = fermenter.m_outputPoint.position + Vector3.up*0.3f;
            var outputs = new List<ObjectRecord>();
            for (int i = 0; i < conversion.m_producedItems; i++)
            {
                var item = conversion.m_to.m_itemData.Clone(); item.m_dropPrefab = conversion.m_to.gameObject;
                item.m_equipped = false; item.m_worldLevel = Game.m_worldLevel;
                item.m_cheated = !PlayerProfile.s_bypassCheatChecks &&
                    (data.GetBool(ZDOVars.s_cheatedQueued,false) || data.GetBool(ZDOVars.s_cheated,false));
                outputs.Add(PlayerDropGame.Ground(item,position,Quaternion.identity));
            }
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_content,0); world.Set(ZDOVars.s_startTime,0L); world.Set(ZDOVars.s_cheatedQueued,0);
                return world.FinishWithDelayedObjects(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),
                    outputs,Math.Max(0,fermenter.m_tapDelay),
                    () => { if (fermenter) fermenter.m_tapEffects.Create(target.transform.position,target.transform.rotation,null,1,-1,default(ZDOID)); },
                    () => { if (fermenter) fermenter.m_spawnEffects.Create(fermenter.m_outputPoint.position,Quaternion.identity,null,1,-1,default(ZDOID)); });
            }
        }
        private static PlayerActionPlan TakeProcessed(GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var smelter = target.GetComponent<Smelter>();
            if (!smelter || request.Action.Amount != 1 || request.Gameplay.Alternate)
                throw new InvalidOperationException("Invalid processed output request");
            var data = smelter.m_nview.GetZDO(); int count = data.GetInt(ZDOVars.s_spawnAmount,0);
            var conversion = smelter.GetItemConversion(data.GetString(ZDOVars.s_spawnOre,""));
            if (count <= 0 || conversion?.m_to == null || !smelter.m_outputPoint)
                throw new InvalidOperationException("No processed output is available");
            var item = conversion.m_to.m_itemData.Clone(); item.m_dropPrefab = conversion.m_to.gameObject;
            // The amount comes only from the machine's completed queue, never from the request.
            item.m_stack = count; item.m_equipped = false; item.m_worldLevel = Game.m_worldLevel;
            item.m_cheated = !PlayerProfile.s_bypassCheatChecks &&
                (data.GetBool(ZDOVars.s_cheatedQueued,false) || data.GetBool(ZDOVars.s_cheated,false));
            var output = PlayerDropGame.Ground(item,smelter.m_outputPoint.position,smelter.m_outputPoint.rotation);
            using (var world = new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_spawnOre,""); world.Set(ZDOVars.s_spawnAmount,0);
                return world.FinishWithObjects(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),new[] { output },
                    () => smelter.m_produceEffects.Create(target.transform.position,target.transform.rotation,null,1,-1,default(ZDOID)));
            }
        }
        private static void Send(Component machine,Humanoid user,ItemDrop.ItemData item,string interaction,bool bulk)
        {
            var view = machine.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || user.IsTeleporting() || item != null && !user.GetInventory().ContainsItem(item)) return;
            var id = view.GetZDO().m_uid;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = interaction,
                TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null },item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,bulk ? 4096 : 1);
        }
        [HarmonyPatch]
        private static class SwitchInput
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter),"OnAddOre"); yield return AccessTools.Method(typeof(Smelter),"OnAddFuel");
                yield return AccessTools.Method(typeof(CookingStation),"OnAddFuelSwitch");
                yield return AccessTools.Method(typeof(ShieldGenerator),"OnAddFuel");
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance,Humanoid user,ItemDrop.ItemData item,MethodBase __originalMethod,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false;
                string action = __instance is ShieldGenerator ? ShieldFuel : __instance is CookingStation ? CookingFuel : __originalMethod.Name == "OnAddOre" ? Ore : Fuel;
                Send(__instance,user,item,action,Storage.SmelterQuickFill.ShiftHeld()); return false;
            }
        }
        [HarmonyPatch(typeof(Smelter),"OnEmpty")]
        private static class ProcessedOutput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Smelter __instance,Humanoid user,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false; Send(__instance,user,null,Processed,false); return false;
            }
        }
        [HarmonyPatch(typeof(Fireplace),nameof(Fireplace.Interact))]
        private static class FireInput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Fireplace __instance,Humanoid user,bool hold,bool alt,ref bool __result)
            {
                if (!Managed(user)) return true;
                bool bulk = Storage.SmelterQuickFill.ShiftHeld();
                if (!bulk && !hold && !alt && __instance.m_canTurnOff && __instance.m_nview.IsValid() &&
                    __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel,0) > 0) return true;
                __result = false;
                if (hold && (__instance.m_holdRepeatInterval <= 0 || Time.time-__instance.m_lastUseTime < __instance.m_holdRepeatInterval)) return false;
                __instance.m_lastUseTime = Time.time; Send(__instance,user,null,FireFuel,bulk); return false;
            }
        }
        [HarmonyPatch(typeof(Fireplace),nameof(Fireplace.UseItem))]
        private static class FireItemInput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Fireplace __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false; if (item == null) return false;
                bool fuel = !__instance.m_infiniteFuel && __instance.m_fuelItem && item.m_shared.m_name == __instance.m_fuelItem.m_itemData.m_shared.m_name;
                Send(__instance,user,item,fuel ? FireFuel : Fireworks,fuel && Storage.SmelterQuickFill.ShiftHeld()); return false;
            }
        }
        [HarmonyPatch(typeof(Fermenter),nameof(Fermenter.Interact))]
        private static class FermentInput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Fermenter __instance,Humanoid user,bool hold,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false; if (!hold) Send(__instance,user,null,__instance.GetContent() == 0 ? Ferment : Tap,false); return false;
            }
        }
        [HarmonyPatch(typeof(Fermenter),nameof(Fermenter.UseItem))]
        private static class FermentItemInput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Fermenter __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false; if (item != null) Send(__instance,user,item,Ferment,false); return false;
            }
        }
        [HarmonyPatch]
        private static class LegacyInput
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter),"RPC_AddOre"); yield return AccessTools.Method(typeof(Smelter),"RPC_AddFuel");
                yield return AccessTools.Method(typeof(CookingStation),"RPC_AddFuel"); yield return AccessTools.Method(typeof(Fireplace),"RPC_AddFuel");
                yield return AccessTools.Method(typeof(Fermenter),"RPC_AddItem");
                yield return AccessTools.Method(typeof(CookingStation),"RPC_AddItem");
                yield return AccessTools.Method(typeof(CookingStation),"RPC_RemoveDoneItem");
                yield return AccessTools.Method(typeof(Smelter),"RPC_EmptyProcessed");
                yield return AccessTools.Method(typeof(Fermenter),"RPC_Tap");
                yield return AccessTools.Method(typeof(ShieldGenerator),"RPC_AddFuel");
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance) => !Enabled && !Held(__instance);
        }
        [HarmonyPatch]
        private static class PendingMutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter),"DropAllItems");
                yield return AccessTools.Method(typeof(CookingStation),"DropAllItems");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_AddFuelAmount");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_SetFuelAmount");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_ToggleOn");
                yield return AccessTools.Method(typeof(Fermenter),"OnDestroyed");
                yield return AccessTools.Method(typeof(Fermenter),"DelayedTap");
                yield return AccessTools.Method(typeof(ShieldGenerator),"RPC_SetFuel");
                yield return AccessTools.Method(typeof(ShieldGenerator),"RPC_Attack");
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance,MethodBase __originalMethod,object[] __args) =>
                !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
        }
        [HarmonyPatch]
        private static class Production
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter),"UpdateSmelter");
                yield return AccessTools.Method(typeof(CookingStation),"UpdateCooking");
                yield return AccessTools.Method(typeof(Fireplace),"UpdateFireplace");
                yield return AccessTools.Method(typeof(Fermenter),"SlowUpdate");
                yield return AccessTools.Method(typeof(ShieldGenerator),"UpdateShield");
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance) => !Held(__instance);
        }
    }
}




