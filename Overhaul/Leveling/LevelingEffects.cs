using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Leveling
{
    internal static class LevelingEffects
    {
        internal static OverhaulCharacter State(Character character)=>character is Player player?OverhaulCharacter.Get(player):null;
        internal static float Bonus(Character character,string id)=>(float)(State(character)?.Bonus(id)??0);
        internal static bool Passive(Character character,string id)=>State(character)?.HasPassive(id)==true;
        internal sealed class Participants { internal readonly Dictionary<ZDOID,float> Hits=new Dictionary<ZDOID,float>(); }
        internal static readonly ConditionalWeakTable<Character,Participants> Combatants=new ConditionalWeakTable<Character,Participants>();
    }

    [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyAttack))]
    internal static class LevelingAttackPatch
    {
        private static void Postfix(SEMan __instance,ref HitData hitData)
        {
            Character attacker=__instance.m_character;if(!(attacker is Player player))return;
            LevelingNetwork.MarkCombat(player);
            // -1 means ALL effect variants, not the default variant. Non-elemental
            // weapons commonly use it; keep explicitly selected native weapon variants.
            if(hitData.m_variant<0 && (LevelingEffects.Bonus(player,"element_poison")>0 ||
                LevelingEffects.Bonus(player,"element_frost")>0 || LevelingEffects.Bonus(player,"element_fire")>0 ||
                LevelingEffects.Bonus(player,"element_spirit")>0))hitData.m_variant=0;
            hitData.m_damage.m_poison+=LevelingEffects.Bonus(player,"element_poison");
            hitData.m_damage.m_frost+=LevelingEffects.Bonus(player,"element_frost");
            hitData.m_damage.m_fire+=LevelingEffects.Bonus(player,"element_fire");
            hitData.m_damage.m_lightning+=LevelingEffects.Bonus(player,"element_spirit");
            CriticalHits.Roll(player,hitData);
        }
    }
    [HarmonyPatch(typeof(Character),"RPC_Damage")]
    internal static class LevelingDamagePatch
    {
        private static void Prefix(Character __instance,HitData hit)
        {
            if(!__instance.m_nview || !__instance.m_nview.IsOwner() || __instance.IsDead())return;
            Player attacker=hit.GetAttacker() as Player;
            if(attacker && !__instance.IsPlayer())LevelingEffects.Combatants.GetOrCreateValue(__instance).Hits[attacker.GetZDOID()]=Time.time;
            if(attacker)LevelingNetwork.MarkCombat(attacker);
            if(__instance is Player player)
            {
                if(hit.GetTotalDamage()>0)LevelingNetwork.MarkCombat(player);
                hit.m_damage.m_poison*=1-Mathf.Clamp01(LevelingEffects.Bonus(player,"poison_resist"));
            }
        }
    }
    [HarmonyPatch(typeof(Character),nameof(Character.OnDeath))]
    internal static class LevelingDeathPatch
    {
        private static void Prefix(Character __instance)
        {
            if(__instance.IsPlayer())return;
            var ids=new List<ZDOID>();
            if(LevelingEffects.Combatants.TryGetValue(__instance,out var combatants))ids.AddRange(combatants.Hits.Where(p=>Time.time-p.Value<=LevelingConfig.Current.ParticipationSeconds).Select(p=>p.Key));
            if(__instance.m_lastHit?.GetAttacker() is Player last && !ids.Contains(last.GetZDOID()))ids.Add(last.GetZDOID());
            LevelingNetwork.Death(__instance,ids);
        }
    }
    [HarmonyPatch(typeof(Destructible),nameof(Destructible.Destroy))]
    internal static class LevelingBirdDeathPatch
    {
        private static void Prefix(Destructible __instance,HitData hit)=>LevelingNetwork.BirdDeath(__instance,hit);
    }
    [HarmonyPatch(typeof(Player),nameof(Player.GetMaxCarryWeight))]
    internal static class LevelingCarryPatch { private static void Postfix(Player __instance,ref float __result){__result+=LevelingEffects.Bonus(__instance,"carry");if(LevelingEffects.Passive(__instance,"unburdened"))__result=float.MaxValue;} }
    [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyHealthRegen))]
    internal static class LevelingHealthRegenPatch { private static void Postfix(SEMan __instance,ref float regenMultiplier)=>regenMultiplier*=1+LevelingEffects.Bonus(__instance.m_character,"health_regen"); }
    [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyEitrRegen))]
    internal static class LevelingEitrRegenPatch { private static void Postfix(SEMan __instance,ref float eitrMultiplier)=>eitrMultiplier*=1+LevelingEffects.Bonus(__instance.m_character,"eitr_regen"); }
    [HarmonyPatch(typeof(Player),nameof(Player.UseEitr))]
    internal static class LevelingEitrCostPatch { private static void Prefix(Player __instance,ref float v){if(v>0)v*=1-Mathf.Clamp01(LevelingEffects.Bonus(__instance,"eitr_cost"));} }
    [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyFallDamage))]
    internal static class LevelingFallPatch { private static void Postfix(SEMan __instance,ref float damage)=>damage*=LevelingEffects.Passive(__instance.m_character,"feather")?0:1-Mathf.Clamp01(LevelingEffects.Bonus(__instance.m_character,"fall_resist")); }
    [HarmonyPatch(typeof(Player),"GetTotalFoodValue")]
    internal static class LevelingVitalityPatch { private static void Postfix(Player __instance,ref float hp){if(LevelingEffects.Passive(__instance,"vitality"))hp+=(float)LevelingConfig.Current.VitalityHealth;} }
    [HarmonyPatch(typeof(ItemDrop.ItemData),nameof(ItemDrop.ItemData.GetBlockPower),new[]{typeof(int),typeof(float)})]
    internal static class LevelingBlockPatch
    {
        private static void Postfix(ItemDrop.ItemData __instance,ref float __result)
        {Player player=Player.m_localPlayer;if(player && player.GetInventory().GetAllItems().Contains(__instance))__result*=1+LevelingEffects.Bonus(player,"block");}
    }
    [HarmonyPatch(typeof(Attack),"FireProjectileBurst")]
    internal static class LevelingProjectilePatch
    {
        private sealed class Attempt { internal bool Consumed; }
        private static readonly ConditionalWeakTable<Attack,Attempt> Attempts=new ConditionalWeakTable<Attack,Attempt>();
        internal static void Begin(Attack attack)=>Attempts.Remove(attack);
        private static void Prefix(Attack __instance,bool __runOriginal,out int __state)
        {
            __state=__instance.m_projectiles;if(!__runOriginal)return;
            var attempt=Attempts.GetOrCreateValue(__instance);if(attempt.Consumed)return;
            attempt.Consumed=true;
            if(UnityEngine.Random.value<Math.Min(1,LevelingEffects.Bonus(__instance.m_character,"projectile")))__instance.m_projectiles++;
        }
        private static void Finalizer(Attack __instance,int __state)=>__instance.m_projectiles=__state;
    }
    [HarmonyPatch(typeof(Attack),nameof(Attack.OnAttackTrigger))]
    internal static class LevelingProjectileBeginPatch { private static void Prefix(Attack __instance)=>LevelingProjectilePatch.Begin(__instance); }
    [HarmonyPatch]
    internal static class LevelingDurabilityPatch
    {
        private static int depth;
        internal sealed class Snapshot { internal Player Player;internal Dictionary<ItemDrop.ItemData,float> Items; }
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // AttackDurability applies these bonuses when a creature is hit, including
            // delayed projectiles. Taking an attack-wide snapshot would refund twice.
            yield return AccessTools.Method(typeof(Humanoid),"DrainEquipedItemDurability");
            yield return AccessTools.Method(typeof(Player),"DamageArmorDurability");
            yield return AccessTools.Method(typeof(Player),"UpdatePlacement");
            yield return AccessTools.Method(typeof(Player),"Repair");
        }
        private static void Prefix(object __instance,out Snapshot __state)
        {
            __state=null;Player player=(__instance as Attack)?.m_character as Player ?? __instance as Player;
            if(depth++!=0 || !player)return;
            __state=new Snapshot{Player=player,Items=player.GetInventory().GetAllItems().ToDictionary(i=>i,i=>i.m_durability)};
        }
        private static void Finalizer(Snapshot __state)
        {
            depth=Math.Max(0,depth-1);if(__state==null)return;
            float restore=LevelingEffects.Passive(__state.Player,"artisan")?1:Mathf.Clamp01(LevelingEffects.Bonus(__state.Player,"durability"));
            foreach(var pair in __state.Items)if(pair.Key.m_durability<pair.Value)pair.Key.m_durability+=(pair.Value-pair.Key.m_durability)*restore;
        }
    }
    [HarmonyPatch(typeof(Player),nameof(Player.HaveRequirements),new[]{typeof(Piece),typeof(Player.RequirementMode)})]
    internal static class LevelingBuildPatch
    {
        private static void Prefix(Player __instance,Piece piece,out CraftingStation __state){__state=null;if(piece && LevelingEffects.Passive(__instance,"artisan")){__state=piece.m_craftingStation;piece.m_craftingStation=null;}}
        private static void Finalizer(Piece piece,CraftingStation __state){if(piece && __state)piece.m_craftingStation=__state;}
    }
    [HarmonyPatch(typeof(CharacterDrop),nameof(CharacterDrop.GenerateDropList))]
    internal static class LevelingLootPatch
    {
        private static void Postfix(CharacterDrop __instance,List<KeyValuePair<GameObject,int>> __result)
        {
            Player attacker=__instance.m_character?.m_lastHit?.GetAttacker() as Player;if(!attacker || __instance.m_character.IsTamed())return;
            double chance=LevelingEffects.Bonus(attacker,"bonus_loot");
            if(Persistence.GameCreatureAuthority.Enabled)
            {
                var state=Persistence.InventoryMoveGame.State(attacker.GetZDOID());
                chance=state==null?0:Persistence.PlayerCraftProgressGame.LootChance(state);
            }
            else if(attacker!=Player.m_localPlayer && attacker.m_nview?.GetZDO()!=null)chance=attacker.m_nview.GetZDO().GetFloat("overhaul_loot_chance",0);
            for(int i=0;i<__result.Count;i++)
            {var entry=__result[i];var item=entry.Key.GetComponent<ItemDrop>();if(item && item.m_itemData.m_shared.m_maxStackSize>1 && UnityEngine.Random.value<Math.Min(1,chance))__result[i]=new KeyValuePair<GameObject,int>(entry.Key,checked(entry.Value+1));}
        }
    }
    [HarmonyPatch(typeof(InventoryGui),"DoCrafting")]
    internal static class LevelingCraftPatch
    {
        internal sealed class Snapshot { internal ItemDrop.ItemData Item;internal int Count; }
        private static void Prefix(InventoryGui __instance,Player player,out Snapshot __state)
        {
            __state=null;if(__instance.m_craftUpgradeItem!=null || !__instance.m_craftRecipe)return;
            var item=__instance.m_craftRecipe.m_item.m_itemData;if(item.m_shared.m_maxStackSize<=1)return;
            __state=new Snapshot{Item=item,Count=player.GetInventory().CountItems(item.m_shared.m_name)};
        }
        private static void Postfix(Player player,Snapshot __state)
        {
            if(__state==null || player.GetInventory().CountItems(__state.Item.m_shared.m_name)<=__state.Count || UnityEngine.Random.value>=Math.Min(1,LevelingEffects.Bonus(player,"bonus_loot")))return;
            ItemDrop.ItemData bonus=__state.Item.Clone();bonus.m_stack=1;
            if(!player.GetInventory().AddItem(bonus))ItemDrop.DropItem(bonus,1,player.transform.position+Vector3.up,Quaternion.identity);
        }
    }
}
