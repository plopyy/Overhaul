using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static class BossAttackSpeed
    {
        private sealed class AnimationState { internal bool Applied, WasRemote; internal float Original, Scaled; }
        private static readonly ConditionalWeakTable<CharacterAnimEvent,AnimationState> States=new ConditionalWeakTable<CharacterAnimEvent,AnimationState>();
        private static bool Owner(CharacterAnimEvent events) => events.m_character && events.m_nview && events.m_nview.IsValid() && events.m_nview.IsOwner();
        private static void Remove(CharacterAnimEvent events)
        {
            if(!Owner(events))
            {
                if(BossModifiers.Data(events.m_character)!=null)
                {
                    var remote=States.GetValue(events,_=>new AnimationState());remote.WasRemote=true;remote.Applied=false;
                }
                return;
            }
            if(!States.TryGetValue(events,out var state))return;
            if(state.WasRemote && events.m_character.InAttack())
            {
                // A newly owning peer starts with an already scaled speed from ZSyncAnimation.
                float factor=BossModifiers.AttackSpeed(events.m_character);
                if(events.m_pauseTimer>0)events.m_pauseSpeed/=factor;
                else if(events.m_animator.speed>=.01f)events.m_animator.speed/=factor;
            }
            else if(state.Applied && events.m_animator.speed==state.Scaled)events.m_animator.speed=state.Original;
            state.WasRemote=false;
            state.Applied=false;
        }
        private static void Apply(CharacterAnimEvent events)
        {
            if(!Owner(events) || !events.m_character.InAttack() || events.m_pauseTimer>0)return;
            float factor=BossModifiers.AttackSpeed(events.m_character);if(factor==1)return;
            var state=States.GetValue(events,_=>new AnimationState());
            state.Original=events.m_animator.speed;state.Scaled=state.Original*factor;
            events.m_animator.speed=state.Scaled;state.Applied=true;
        }
        // Some native attack clips have Speed events; others have none. Preserve
        // their own speed changes and freeze frames, applying our factor once.
        [HarmonyPatch(typeof(CharacterAnimEvent),nameof(CharacterAnimEvent.CustomFixedUpdate))]
        private static class Tick
        {
            private static void Prefix(CharacterAnimEvent __instance)=>Remove(__instance);
            private static void Postfix(CharacterAnimEvent __instance)=>Apply(__instance);
        }
        [HarmonyPatch(typeof(CharacterAnimEvent),nameof(CharacterAnimEvent.Speed))]
        private static class Event
        {
            private static void Postfix(CharacterAnimEvent __instance)
            {
                if(States.TryGetValue(__instance,out var state)){state.Applied=false;if(Owner(__instance))state.WasRemote=false;}
                Apply(__instance);
            }
        }
        [HarmonyPatch(typeof(CharacterAnimEvent),nameof(CharacterAnimEvent.FreezeFrame))]
        private static class Freeze
        {
            private static void Prefix(CharacterAnimEvent __instance,float delay){if(delay>0)Remove(__instance);}
        }
        internal static float Interval(float native,Character character)=>native/BossModifiers.AttackSpeed(character);
        [HarmonyPatch]
        private static class Cooldowns
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MonsterAI),nameof(MonsterAI.UpdateAI));
                yield return AccessTools.Method(typeof(Humanoid),nameof(Humanoid.EquipBestWeapon));
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase original)
            {
                var interval=AccessTools.Field(typeof(ItemDrop.ItemData.SharedData),"m_aiAttackInterval");
                var minimum=AccessTools.Field(typeof(MonsterAI),"m_minAttackInterval");int count=0;
                foreach(var code in instructions)
                {
                    yield return code;
                    if(!code.LoadsField(interval) && !code.LoadsField(minimum))continue;
                    count++;yield return new CodeInstruction(OpCodes.Ldarg_0);
                    if(original.DeclaringType==typeof(MonsterAI))yield return new CodeInstruction(OpCodes.Ldfld,AccessTools.Field(typeof(BaseAI),"m_character"));
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(BossAttackSpeed),nameof(Interval)));
                }
                if(count==0)throw new InvalidOperationException("Missing native attack cooldown in "+original.Name);
            }
        }
    }
}
