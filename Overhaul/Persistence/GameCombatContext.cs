using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Native combat routines can query a remote avatar without trusting its
    // replicated client stats or temporarily replacing the player's inventory.
    internal static class GameCombatContext
    {
        internal sealed class Frame
        {
            internal Player Player;
            internal PlayerSnapshot State;
            internal ItemDrop.ItemData Weapon,Ammo;
            internal ItemDrop.ItemData[] Equipment;
            internal StatusEffect[] Effects;
        }
        [ThreadStatic] internal static Frame Current;
        internal static bool Matches(Character player)=>Current!=null && Current.Player==player;
        internal static void Run(Player player,PlayerSnapshot snapshot,ItemDrop.ItemData weapon,ItemDrop.ItemData ammo,Action action)
        {
            if(!player || snapshot==null || action==null)throw new ArgumentException("Invalid server combat context");
            var frame=new Frame{Player=player,State=snapshot,Weapon=weapon,Ammo=ammo,Effects=GameAttackResources.Effects(snapshot),
                Equipment=snapshot.Rows.Where(r=>r.Table=="inventory").Select(r=>PlayerInventoryView.ReadItem(r.Values,null,true)).Where(i=>i.m_equipped).ToArray()};
            var previous=Current;Current=frame;try{action();}finally{Current=previous;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.GetSkillFactor))]
        private static class Factor
        {
            private static bool Prefix(Player __instance,Skills.SkillType skill,ref float __result)
            {if(!Matches(__instance))return true;__result=Mathf.Clamp01(GameAttackResources.SkillLevel(Current.State,skill,Current.Effects)/100f);return false;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetSkillLevel))]
        private static class Level
        {
            private static bool Prefix(Character __instance,Skills.SkillType skillType,ref float __result)
            {if(!Matches(__instance))return true;__result=GameAttackResources.SkillLevel(Current.State,skillType,Current.Effects);return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.GetRandomSkillFactor))]
        private static class RandomFactor
        {
            private static bool Prefix(Player __instance,Skills.SkillType skill,ref float __result)
            {
                if(!Matches(__instance))return true;
                float middle=Mathf.Lerp(.4f,1,Mathf.Clamp01(GameAttackResources.SkillLevel(Current.State,skill,Current.Effects)/100f));
                __result=Mathf.Lerp(Mathf.Clamp01(middle-.15f),Mathf.Clamp01(middle+.15f),UnityEngine.Random.value);return false;
            }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.GetCurrentWeapon))]
        private static class Weapon
        {
            private static bool Prefix(Humanoid __instance,ref ItemDrop.ItemData __result)
            {if(!Matches(__instance))return true;__result=Current.Weapon;return false;}
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.GetAmmoItem))]
        private static class Ammo
        {
            private static bool Prefix(Humanoid __instance,ref ItemDrop.ItemData __result)
            {if(!Matches(__instance))return true;__result=Current.Ammo;return false;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetHealth))]
        private static class Health
        {
            private static bool Prefix(Character __instance,ref float __result)
            {if(!Matches(__instance))return true;__result=(float)PlayerResources.Read(Current.State,"health");return false;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetMaxHealth))]
        private static class MaxHealth
        {
            private static bool Prefix(Character __instance,ref float __result)
            {if(!Matches(__instance))return true;__result=(float)PlayerResources.Read(Current.State,"max_health");return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.GetEquipmentAttackStaminaModifier))]
        private static class AttackModifier
        {
            private static bool Prefix(Player __instance,ref float __result)
            {if(!Matches(__instance))return true;__result=Current.Equipment.Sum(i=>i.m_shared.m_attackStaminaModifier);return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.GetEquipmentHomeItemModifier))]
        private static class HomeModifier
        {
            private static bool Prefix(Player __instance,ref float __result)
            {if(!Matches(__instance))return true;__result=Current.Equipment.Sum(i=>i.m_shared.m_homeItemsStaminaModifier);return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyAttackStaminaUsage))]
        private static class StaminaEffects
        {
            private static bool Prefix(SEMan __instance,float baseStaminaUse,ref float staminaUse,bool minZero)
            {
                if(!Matches(__instance.m_character))return true;
                foreach(var effect in Current.Effects)effect.ModifyAttackStaminaUsage(baseStaminaUse,ref staminaUse);
                if(minZero)staminaUse=Mathf.Max(0,staminaUse);return false;
            }
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetLevel))]
        private static class NativeLevel
        {
            private static bool Prefix(Character __instance,ref int __result)
            {if(!Matches(__instance))return true;__result=1;return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyAttack))]
        private static class AttackEffects
        {
            private static bool Prefix(SEMan __instance,Skills.SkillType skill,ref HitData hitData)
            {
                if(!Matches(__instance.m_character))return true;
                foreach(var effect in Current.Effects)effect.ModifyAttack(skill,ref hitData);return false;
            }
        }
        [HarmonyPatch(typeof(global::Overhaul.Leveling.LevelingEffects),"Bonus")]
        private static class Bonus
        {
            private static bool Prefix(Character character,string id,ref float __result)
            {if(!Matches(character))return true;__result=PlayerCraftProgressGame.Bonus(Current.State,id);return false;}
        }
        [HarmonyPatch(typeof(global::Overhaul.Leveling.LevelingEffects),"Passive")]
        private static class Passive
        {
            private static bool Prefix(Character character,string id,ref bool __result)
            {if(!Matches(character))return true;__result=PlayerCraftProgressGame.Passive(Current.State,id);return false;}
        }
    }
}
