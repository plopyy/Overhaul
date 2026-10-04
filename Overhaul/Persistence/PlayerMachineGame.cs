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
        internal const string Ferment = "fermenter.input";
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
        private static IEnumerable<int> Prefab(ItemDrop item) => item ? new[] { item.gameObject.name.GetStableHashCode() } : Array.Empty<int>();
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
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance,Humanoid user,ItemDrop.ItemData item,MethodBase __originalMethod,ref bool __result)
            {
                if (!Managed(user)) return true;
                __result = false;
                string action = __instance is CookingStation ? CookingFuel : __originalMethod.Name == "OnAddOre" ? Ore : Fuel;
                Send(__instance,user,item,action,Storage.SmelterQuickFill.ShiftHeld()); return false;
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
                if (!Managed(user) || item == null || !__instance.m_fuelItem || item.m_shared.m_name != __instance.m_fuelItem.m_itemData.m_shared.m_name) return true;
                __result = false; Send(__instance,user,item,FireFuel,Storage.SmelterQuickFill.ShiftHeld()); return false;
            }
        }
        [HarmonyPatch(typeof(Fermenter),nameof(Fermenter.Interact))]
        private static class FermentInput
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Fermenter __instance,Humanoid user,bool hold,ref bool __result)
            {
                if (!Managed(user) || __instance.GetContent() != 0) return true;
                __result = false; if (!hold) Send(__instance,user,null,Ferment,false); return false;
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
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance) => !Enabled && !Held(__instance);
        }
        [HarmonyPatch]
        private static class PendingMutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter),"RPC_EmptyProcessed");
                yield return AccessTools.Method(typeof(CookingStation),"DropAllItems");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_AddFuelAmount");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_SetFuelAmount");
                yield return AccessTools.Method(typeof(Fireplace),"RPC_ToggleOn");
                yield return AccessTools.Method(typeof(Fermenter),"RPC_Tap");
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
            }
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Component __instance) => !Held(__instance);
        }
    }
}
