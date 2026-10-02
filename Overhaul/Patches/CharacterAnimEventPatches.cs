using System.Runtime.CompilerServices;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.Speed))]
    internal static class CharacterAnimEventPatches
    {
        private sealed class State { internal bool Applied;internal float Native,Scaled,WithoutDodge; }
        private static readonly ConditionalWeakTable<CharacterAnimEvent,State> States=new ConditionalWeakTable<CharacterAnimEvent,State>();
        private static Player Local(CharacterAnimEvent events)=>events.m_character is Player player&&player==Player.m_localPlayer?player:null;
        private static void Remove(CharacterAnimEvent events)
        {
            if(!Local(events)||!States.TryGetValue(events,out var state)||!state.Applied)return;
            if(Mathf.Approximately(events.m_animator.speed,state.Scaled)||Mathf.Approximately(events.m_animator.speed,state.WithoutDodge))events.m_animator.speed=state.Native;
            state.Applied=false;
        }
        private static void Apply(CharacterAnimEvent events)
        {
            var player=Local(events);if(!player||!player.InAttack()||events.m_pauseTimer>0)return;
            var state=States.GetOrCreateValue(events);state.Native=events.m_animator.speed;
            float speed=state.Native*WeaponAttackSpeeds.Rate(player.GetCurrentWeapon(),player.m_currentAttackIsSecondary);
            state.WithoutDodge=speed;DynamicCombat.AdjustDodgeAttackSpeed(player,ref speed);
            state.Scaled=Mathf.Max(.01f,speed);events.m_animator.speed=state.Scaled;state.Applied=true;
        }
        private static void Postfix(CharacterAnimEvent __instance)
        {
            if(States.TryGetValue(__instance,out var state))state.Applied=false;
            Apply(__instance);
        }
        [HarmonyPatch(typeof(CharacterAnimEvent),nameof(CharacterAnimEvent.CustomFixedUpdate))]
        private static class Tick
        {
            private static void Prefix(CharacterAnimEvent __instance)=>Remove(__instance);
            private static void Postfix(CharacterAnimEvent __instance)=>Apply(__instance);
        }
        [HarmonyPatch(typeof(CharacterAnimEvent),nameof(CharacterAnimEvent.FreezeFrame))]
        private static class Freeze
        {
            private static void Prefix(CharacterAnimEvent __instance,float delay){if(delay>0)Remove(__instance);}
        }
    }
}
