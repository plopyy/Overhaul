using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerCraftGame
    {
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var command = request.Gameplay;
            var recipe = ObjectDB.instance.m_recipes.FirstOrDefault(r => r && r.name == command.Definition && r.m_enabled);
            if (!recipe || !recipe.m_item) throw new InvalidOperationException("Recipe is unavailable");
            bool upgrade = command.Alternate; int slot = request.Action.FromY*256+request.Action.FromX;
            var old = upgrade ? PlayerInventoryView.ReadItem(inventory.Item(slot),null,true) : null;
            if (upgrade && (old.m_dropPrefab != recipe.m_item.gameObject || !inventory.Available(slot) || old.m_stack != 1))
                throw new InvalidOperationException("Upgrade item is unavailable");
            int quality = upgrade ? checked(old.m_quality+1) : 1;
            int count = request.Action.Amount;
            if (count < 1 || count > 100 || upgrade && count != 1 || !upgrade && recipe.m_noCraftOnlyUpgrade)
                throw new InvalidOperationException("Invalid craft amount");
            var target = command.TargetId == 0 ? null : ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            var station = target ? target.GetComponent<CraftingStation>() : null;
            if (command.TargetId != 0 && !station || station &&
                (Vector3.Distance(actor.GetPosition(),station.transform.position) >= station.m_useDistance || station.m_craftRequireFire && !station.m_haveFire))
                throw new InvalidOperationException("Crafting station is unavailable");
            bool upgrader = station && station.m_upgrader;
            var required = recipe.GetRequiredStation(quality);
            if (upgrader && !upgrade || !upgrader && (quality > recipe.m_item.m_itemData.m_shared.m_maxQuality ||
                required && (!station || station.m_name != required.m_name || station.GetLevel(true) < recipe.GetRequiredStationLevel(quality)) ||
                !required && station && !station.m_showBasicRecipies)) throw new InvalidOperationException("Invalid station or quality");
            if (recipe.m_item.m_itemData.m_shared.m_dlc.Length != 0 &&
                (!DLCMan.instance || !DLCMan.instance.IsDLCInstalled(recipe.m_item.m_itemData.m_shared.m_dlc)))
                throw new InvalidOperationException("Crafting DLC is unavailable");
            bool free = ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost);
            long playerId = actor.GetLong(ZDOVars.s_playerID,0);
            string playerName = (string)snapshot.Rows.First(r => r.Table == "state" && (string)r.Values[0] == "player_name").Values[3];
            using (var resources = new PlayerCraftResourcesGame(inventory,station,playerId))
            {
                var requirements = recipe.m_resources.Where(r => r.m_resItem && r.m_upgraderResource == upgrader).ToArray();
                var known = new HashSet<string>(snapshot.Rows.Where(r => r.Table == "knowledge" && (string)r.Values[0] == "materials").Select(r => (string)r.Values[1]));
                foreach (int key in inventory.Keys)
                { var prefab = ObjectDB.instance.GetItemPrefab(Convert.ToInt32(inventory.Item(key)[3])); if (prefab) known.Add(prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name); }
                bool discovered = snapshot.Rows.Any(r => r.Table == "knowledge" && (string)r.Values[0] == "recipes" && (string)r.Values[1] == recipe.m_item.m_itemData.m_shared.m_name);
                var discovery = recipe.m_resources.Where(r => r.m_resItem && !r.m_upgraderResource && r.m_amount > 0);
                if (!discovered && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.AllRecipesUnlocked) &&
                    !(recipe.m_requireOnlyOneIngredient ? discovery.Any(r => known.Contains(r.m_resItem.m_itemData.m_shared.m_name)) : discovery.All(r => known.Contains(r.m_resItem.m_itemData.m_shared.m_name))))
                    throw new InvalidOperationException("Recipe is not discovered");
                int produced = checked(recipe.m_amount*count);
                if (recipe.m_requireOnlyOneIngredient)
                {
                    Piece.Requirement selected = null; int selectedQuality = 0;
                    foreach (var requirement in requirements)
                    {
                        int needed = checked(requirement.GetAmount(quality)*count);
                        for (int q = 1; q <= requirement.m_resItem.m_itemData.m_shared.m_maxQuality; q++)
                            if (resources.Count(requirement.m_resItem,q) >= Math.Max(1,needed)) { selected = requirement; selectedQuality = q; break; }
                        if (selected != null) break;
                    }
                    if (selected == null) throw new InvalidOperationException("Missing recipe ingredient");
                    produced = checked((recipe.m_amount + Mathf.CeilToInt((selectedQuality-1)*recipe.m_amount*recipe.m_qualityResultAmountMultiplier) + selected.m_extraAmountOnlyOneIngredient)*count);
                    if (!free) resources.Consume(selected.m_resItem,checked(selected.GetAmount(quality)*count),selectedQuality);
                }
                else if (!free)
                    foreach (var requirement in requirements) resources.Consume(requirement.m_resItem,checked(requirement.GetAmount(quality)*count));
                var extra = new List<PlayerChange>();
                if (station && station.m_craftingSkill != Skills.SkillType.None && recipe.m_item.m_itemData.m_shared.m_maxStackSize > 1)
                {
                    var gui = CraftGui(); float chance = gui ? gui.m_craftBonusChance : 0.25f; int amount = gui ? gui.m_craftBonusAmount : 1;
                    int bonus = 0; float factor = PlayerCraftProgressGame.Factor(snapshot,station.m_craftingSkill);
                    for (int i = 0; i < count; i++) if (UnityEngine.Random.value < factor*chance) { bonus += amount; produced = checked(produced+bonus); }
                }
                bool cheated = (resources.Cheated || station && station.GetComponent<ZNetView>().GetZDO().GetBool(ZDOVars.s_cheated,false)) && !PlayerProfile.s_bypassCheatChecks;
                bool success = true, broken = false;
                if (upgrader)
                {
                    var catalyst = requirements.FirstOrDefault(r => r.m_upgraderResource)?.m_resItem;
                    if (!catalyst) throw new InvalidOperationException("Missing upgrade catalyst definition");
                    var rule = catalyst.m_itemData.m_shared; float roll = UnityEngine.Random.Range(0f,1f);
                    success = rule.m_upgradeChance >= roll; broken = !success && rule.m_breakChance >= 1f-roll;
                    if (broken)
                    {
                        inventory.Remove(slot,1);
                        if (rule.m_breakReturnIngreientsAmount > 0)
                            foreach (var resource in recipe.m_resources.Where(r => r.m_resItem && r.m_recover))
                            {
                                int refund = Mathf.CeilToInt((resource.GetAmount(1)+resource.GetAmount(quality-1))*rule.m_breakReturnIngreientsAmount);
                                if (refund > 0) Add(inventory,resource.m_resItem,refund,resource.m_resItem.m_itemData.m_quality,resource.m_resItem.m_itemData.m_variant,playerId,playerName,cheated);
                            }
                    }
                    else if (!success) quality = Math.Max(1,quality-2);
                }
                if (!broken)
                {
                    if (upgrade)
                    {
                        old.m_quality = quality; old.m_durability = old.GetMaxDurability(); old.m_crafterID = playerId; old.m_crafterName = playerName;
                        old.m_cheated |= cheated; old.m_worldLevel = Game.m_worldLevel; old.m_equipped = false;
                        inventory.Set(slot,PlayerActionGame.Row(old,slot%256,slot/256).Values);
                    }
                    else
                    {
                        if (UnityEngine.Random.value < PlayerCraftProgressGame.LootChance(snapshot) && recipe.m_item.m_itemData.m_shared.m_maxStackSize > 1) produced++;
                        Add(inventory,recipe.m_item,produced,quality,command.Variant,playerId,playerName,cheated);
                    }
                }
                if (recipe.m_craftingStation && recipe.m_craftingStation.m_craftingSkill != Skills.SkillType.None)
                    extra.AddRange(PlayerCraftProgressGame.Raise(snapshot,recipe.m_craftingStation.m_craftingSkill,count));
                extra.Add(new PlayerChange("knowledge",false,"recipes",recipe.m_item.m_itemData.m_shared.m_name,""));
                extra.Add(new PlayerChange("knowledge",false,"materials",recipe.m_item.m_itemData.m_shared.m_name,""));
                extra.AddRange(PlayerCraftProgressGame.Statistics(snapshot,recipe.m_item.m_itemData,upgrade,count));
                var delta = inventory.Delta(request.Action.Operation,snapshot.Revision);
                return resources.Finish(new PlayerBatch(delta.Operation,delta.ExpectedRevision,delta.Changes.Concat(extra)),() =>
                {
                    if (station) (success ? station.m_craftItemDoneEffects : station.m_craftItemDoneFailEffects)?.Create(actor.GetPosition(),Quaternion.identity,null,1f,-1,default(ZDOID));
                });
            }
        }
        private static InventoryGui CraftGui() => InventoryGui.instance ? InventoryGui.instance :
            Resources.FindObjectsOfTypeAll<InventoryGui>().FirstOrDefault();
        private static void Add(PlayerActionInventory bag,ItemDrop prefab,int amount,int quality,int variant,long id,string name,bool cheated)
        {
            if (variant < 0 || variant >= prefab.m_itemData.m_shared.m_icons.Length) throw new InvalidOperationException("Invalid item variant");
            var item = prefab.m_itemData.Clone(); item.m_dropPrefab = prefab.gameObject; item.m_quality = quality; item.m_variant = variant;
            item.m_stack = amount; item.m_equipped = false; item.m_crafterID = id; item.m_crafterName = name;
            item.m_worldLevel = Game.m_worldLevel; item.m_cheated = cheated; item.m_durability = item.GetMaxDurability();
            bag.Add(PlayerActionGame.Row(item).Values,item.m_customData);
        }
        [HarmonyPatch(typeof(InventoryGui),"DoCrafting")]
        private static class Intent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(InventoryGui __instance,Player player)
            {
                if (player != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
                if (!__instance.m_craftRecipe) return false;
                var station = player.GetCurrentCraftingStation(); var view = station ? station.GetComponent<ZNetView>() : null;
                var id = view && view.IsValid() ? view.GetZDO().m_uid : ZDOID.None; var item = __instance.m_craftUpgradeItem;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.Craft,
                    Definition = __instance.m_craftRecipe.name,TargetUser = id.UserID,TargetId = id.ID,Alternate = item != null,Variant = __instance.m_craftVariant },
                    item?.m_gridPos.x ?? 0,item?.m_gridPos.y ?? 0,__instance.m_multiCrafting ? __instance.m_multiCraftAmount : 1);
                return false;
            }
        }
    }
}
