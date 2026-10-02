using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    internal static class MobSpecies
    {
        internal static bool Mosquito(MonsterAI ai)=>Utils.GetPrefabName(ai.gameObject)=="Deathsquito";
        internal static ItemDrop.ItemData ChargeWeapon(MonsterAI ai,Humanoid body,float dt)
        {
            if(Utils.GetPrefabName(ai.gameObject)!="Lox")return ai.SelectBestAttack(body,dt);
            // Native lox_bite is the forward head strike; lox_stomp is the ground slam.
            var head=body.GetInventory().GetAllItems().FirstOrDefault(w=>w.m_dropPrefab&&w.m_dropPrefab.name=="lox_bite");
            if(head==null)return null;
            if(body.GetCurrentWeapon()!=head&&!body.EquipItem(head,true))return null;
            return head;
        }
        internal sealed class Sting { internal ItemDrop.ItemData.SharedData Copy; }
        internal static readonly ConditionalWeakTable<ItemDrop.ItemData,Sting> Stings=new ConditionalWeakTable<ItemDrop.ItemData,Sting>();
        private static readonly MethodInfo Clone=AccessTools.Method(typeof(object),"MemberwiseClone");
        internal static void FasterSting(ItemDrop.ItemData weapon)
        {
            if(weapon==null)return;
            var state=Stings.GetOrCreateValue(weapon);
            if(state.Copy==weapon.m_shared)return;
            state.Copy=(ItemDrop.ItemData.SharedData)Clone.Invoke(weapon.m_shared,null);
            state.Copy.m_aiAttackInterval*=.5f;weapon.m_shared=state.Copy;
        }
        [HarmonyPatch(typeof(MonsterAI),"SelectBestAttack")]
        private static class StingCadence
        {
            private static void Postfix(MonsterAI __instance,ItemDrop.ItemData __result)
            {if(MobBehavior.Active(__instance)&&Mosquito(__instance))FasterSting(__result);}
        }
        [HarmonyPatch(typeof(HitData),nameof(HitData.ApplyArmor))]
        private static class StingArmor
        {
            private static void Prefix(HitData __instance,ref float ac)
            {
                var attacker=__instance.GetAttacker();
                if(attacker&&!(attacker is Player)&&Utils.GetPrefabName(attacker.gameObject)=="Deathsquito")ac*=.5f;
            }
        }
    }
}
