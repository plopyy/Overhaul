using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Overhaul.Storage
{
    // World-drop metadata only: picking an item up does not make future player drops exempt.
    internal static class ProductionDrops
    {
        internal static readonly int ProtectedKey = "overhaul_production_drop_v1".GetStableHashCode();
        [ThreadStatic] private static int depth;
        internal static bool Protected(ItemDrop item) => item && item.m_nview && item.m_nview.IsValid() && item.m_nview.GetZDO().GetBool(ProtectedKey, false);
        internal static ItemDrop Compatible(ItemDrop candidate, ItemDrop receiver) =>
            candidate && (Protected(candidate) || Protected(receiver)) ? null : candidate;

        [HarmonyPatch(typeof(Smelter), "QueueProcessed")]
        private static class IndividualOutputs
        {
            private static void Prefix(Smelter __instance, out bool __state)
            { __state = __instance.m_spawnStack; __instance.m_spawnStack = false; }
            private static void Finalizer(Smelter __instance, bool __state) => __instance.m_spawnStack = __state;
        }

        // Also split a stack already queued by an older save or another caller.
        [HarmonyPatch(typeof(Smelter), "Spawn")]
        private static class SplitExistingOutput
        {
            private static bool Prefix(Smelter __instance, string ore, int stack)
            {
                if (stack <= 1) return true;
                for (int i = 0; i < stack; i++) __instance.Spawn(ore, 1);
                return false;
            }
        }

        [HarmonyPatch]
        private static class OutputScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter), "Spawn");
                yield return AccessTools.Method(typeof(CookingStation), "SpawnItem");
                yield return AccessTools.Method(typeof(Fermenter), "DelayedTap");
            }
            private static void Prefix(out int __state) { __state = depth; depth++; }
            private static void Finalizer(int __state) { depth = __state; }
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        private static class MarkOutput
        {
            private static void Postfix(ItemDrop __instance)
            {
                if (depth <= 0 || !__instance.m_nview || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;
                __instance.m_nview.GetZDO().Set(ProtectedKey, true);
            }
        }

        [HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
        private static class KeepSeparateStacks
        {
            private static bool Prefix(ItemDrop __instance) => !Protected(__instance);
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
                {
                    yield return instruction;
                    if (instruction.operand is MethodInfo method && method.Name == "GetComponent" &&
                        method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(ItemDrop))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProductionDrops), nameof(Compatible)));
                    }
                }
            }
        }
    }
}
