using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Overhaul.Utility
{
    internal static class WeaponReworkEffects
    {
        [ThreadStatic] private static Humanoid starting;
        [ThreadStatic] private static bool secondary;
        internal static bool IsSecondary(Attack attack,ItemDrop.ItemData weapon)=>starting==attack.m_character?secondary:
            weapon.m_shared.m_secondaryAttack!=null&&attack.m_attackAnimation==weapon.m_shared.m_secondaryAttack.m_attackAnimation&&attack.m_attackAnimation!=weapon.m_shared.m_attack.m_attackAnimation;
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.StartAttack))]
        private static class StartContext
        {
            private static void Prefix(Humanoid __instance,bool secondaryAttack,out Tuple<Humanoid,bool> __state){__state=Tuple.Create(starting,secondary);starting=__instance;secondary=secondaryAttack;}
            private static void Finalizer(Tuple<Humanoid,bool> __state){starting=__state.Item1;secondary=__state.Item2;}
        }
        private static float HitBonus(ItemDrop.ItemData.SharedData shared,Attack attack)=>attack.m_character is Player?WeaponAttackSpeeds.Backstab(attack.m_weapon):shared.m_backstabBonus;
        private static float TooltipBonus(ItemDrop.ItemData.SharedData shared)=>WeaponAttackSpeeds.Backstab(new ItemDrop.ItemData{m_shared=shared});
        [HarmonyPatch]
        private static class Damage
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach(var name in new[]{"DoMeleeAttack","FireProjectileBurst"})yield return AccessTools.Method(typeof(Attack),name);
                // Area hits are built by an instance local function, outside DoAreaAttack's body.
                bool found=false;
                foreach(var method in typeof(Attack).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public))
                    if(method.Name.StartsWith("<DoAreaAttack>g__checkHits|",StringComparison.Ordinal)){found=true;yield return method;}
                if(!found)throw new InvalidOperationException("Native area hit function not found");
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int count=0;foreach(var code in instructions)
                {
                    if(!code.LoadsField(AccessTools.Field(typeof(ItemDrop.ItemData.SharedData),nameof(ItemDrop.ItemData.SharedData.m_backstabBonus)))){yield return code;continue;}
                    var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);load.blocks.AddRange(code.blocks);yield return load;yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(WeaponReworkEffects),nameof(HitBonus)));count++;
                }
                if(count==0)throw new InvalidOperationException("Native backstab hit field not found");
            }
        }
        [HarmonyPatch]
        private static class Tooltip
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {foreach(var method in typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance))if(method.Name=="GetTooltip")yield return method;}
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach(var code in instructions)
                {
                    if(code.LoadsField(AccessTools.Field(typeof(ItemDrop.ItemData.SharedData),nameof(ItemDrop.ItemData.SharedData.m_backstabBonus)))){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(WeaponReworkEffects),nameof(TooltipBonus));}
                    yield return code;
                }
            }
        }
    }
}
