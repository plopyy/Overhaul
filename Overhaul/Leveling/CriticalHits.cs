using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Leveling
{
    internal static class CriticalHits
    {
        // Unused high bit of the native HitData flags: no extra bytes, field reuse,
        // or changes to packet boundaries. All peers already require the same mod version.
        private const uint CriticalFlag=0x80000000u;
        private sealed class Critical { }
        private static readonly ConditionalWeakTable<HitData,Critical> Hits=new ConditionalWeakTable<HitData,Critical>();
        internal static bool IsCritical(HitData hit)=>hit!=null && Hits.TryGetValue(hit,out _);
        internal static void Mark(HitData hit){if(hit!=null)Hits.GetValue(hit,_=>new Critical());}
        internal static void Roll(Player player,HitData hit)
        {
            if(IsCritical(hit) || UnityEngine.Random.value>=Math.Min(1,LevelingEffects.Bonus(player,"critical")))return;
            hit.m_damage.Modify((float)LevelingConfig.Current.CriticalMultiplier);Mark(hit);
        }
        private static HitData ProjectileHit(HitData hit,Projectile projectile)
        {
            if(IsCritical(projectile.m_originalHitData))Mark(hit);
            return hit;
        }
        [HarmonyPatch(typeof(HitData),nameof(HitData.Clone))]
        private static class Clone
        {
            private static void Postfix(HitData __instance,HitData __result){if(IsCritical(__instance))Mark(__result);}
        }
        [HarmonyPatch(typeof(HitData),nameof(HitData.Serialize))]
        private static class Serialize
        {
            private static void Prefix(ref ZPackage pkg,out int __state)=>__state=pkg.GetPos();
            private static void Postfix(HitData __instance,ref ZPackage pkg,int __state)
            {
                if(!IsCritical(__instance))return;
                int end=pkg.GetPos();pkg.SetPos(__state);uint flags=pkg.ReadUInt();
                pkg.SetPos(__state);pkg.Write(flags|CriticalFlag);pkg.SetPos(end);
            }
        }
        [HarmonyPatch(typeof(HitData),nameof(HitData.Deserialize))]
        private static class Deserialize
        {
            private static void Prefix(HitData __instance,ref ZPackage pkg)
            {
                int start=pkg.GetPos();uint flags=pkg.ReadUInt();pkg.SetPos(start);
                Hits.Remove(__instance);if((flags&CriticalFlag)!=0)Mark(__instance);
            }
        }
        [HarmonyPatch]
        private static class ProjectileImpact
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Projectile),nameof(Projectile.OnHit));
                yield return AccessTools.Method(typeof(Projectile),"DoAOE");
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int count=0;var ctor=AccessTools.Constructor(typeof(HitData),Type.EmptyTypes);
                foreach(var code in instructions)
                {
                    yield return code;
                    if(code.opcode!=OpCodes.Newobj || !Equals(code.operand,ctor))continue;
                    count++;yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CriticalHits),nameof(ProjectileHit)));
                }
                if(count==0)throw new InvalidOperationException("Native projectile hit construction not found");
            }
        }
        // Use the native damage-text RPC and the victim's native critical effects.
        // This runs only after the native damage checks and resistance calculation.
        private static void ShowText(DamageText text,HitData.DamageModifier mod,Vector3 point,float damage,bool player,Character victim,HitData hit)
        {
            if(IsCritical(hit))
            {
                mod=HitData.DamageModifier.Weak;
                if(!victim.IsStaggering() || victim.IsPlayer())
                    victim.m_critHitEffects.Create(point,Quaternion.identity,victim.transform,1,-1,hit.m_attacker);
            }
            text.ShowText(mod,point,damage,player);
        }
        [HarmonyPatch(typeof(Character),nameof(Character.ApplyDamage))]
        private static class Damage
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var show=AccessTools.Method(typeof(DamageText),nameof(DamageText.ShowText),new[]{typeof(HitData.DamageModifier),typeof(Vector3),typeof(float),typeof(bool)});
                int count=0;
                foreach(var code in instructions)
                {
                    if(!code.Calls(show)){yield return code;continue;}
                    count++;var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);load.blocks.AddRange(code.blocks);
                    yield return load;yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CriticalHits),nameof(ShowText)));
                }
                if(count!=1)throw new InvalidOperationException("Native character damage text call changed");
            }
        }
    }
}
