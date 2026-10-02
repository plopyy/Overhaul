using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
    internal static class WoodenArrowRecipePatch
    {
        [HarmonyPostfix]
        private static void Postfix(ObjectDB __instance)
        {
            foreach (var recipe in __instance.m_recipes)
            {
                if (!recipe || !recipe.m_item || recipe.m_item.gameObject.name != "ArrowWood") continue;
                recipe.m_craftingStation = null;
            }
        }
    }

    // Remove item provenance only; leave character/world achievement state untouched.
    [HarmonyPatch]
    internal static class ItemOriginInventoryPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(Inventory)).Where(m =>
                m.Name == "AddItem" || m.Name == "CanAddItem" || m.Name == "Save");

        private static void Prefix(Inventory __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            foreach (var item in __instance.GetAllItems()) item.m_cheated = false;
            var parameters = __originalMethod.GetParameters();
            for (int i = 0; i < __args.Length; ++i)
            {
                if (__args[i] is ItemDrop.ItemData item) item.m_cheated = false;
                if (parameters[i].Name == "cheated" && parameters[i].ParameterType == typeof(bool)) __args[i] = false;
            }
        }
        private static void Postfix(Inventory __instance)
        {
            foreach (var item in __instance.GetAllItems()) item.m_cheated = false;
        }
    }

    [HarmonyPatch]
    internal static class ItemOriginDataPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(ItemDrop.ItemData)).Where(m =>
                m.Name == "Save" || m.Name == "GetTooltip" || m.Name == "IsSameType");
        private static void Prefix(object __instance, object[] __args)
        {
            if (__instance is ItemDrop.ItemData item) item.m_cheated = false;
            foreach (var argument in __args)
                if (argument is ItemDrop.ItemData other) other.m_cheated = false;
        }
    }

    [HarmonyPatch(typeof(ItemDrop.ItemData), "Load", new[] { typeof(ZPackage), typeof(ItemDrop.ItemData), typeof(global::Version.Item) })]
    internal static class ItemOriginLoadPatch
    {
        private static void Postfix(ItemDrop.ItemData itemData) => itemData.m_cheated = false;
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.OnCreateNew), new[] { typeof(ItemDrop), typeof(bool) })]
    internal static class ItemOriginCreationPatch
    {
        private static void Prefix(ref bool cheated) => cheated = false;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
    internal class InventoryPatches
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.High)]
        public static void Inventory_IsTeleportable_Pretfix(Inventory __instance, ref bool __result)
        {
            __result = true;
            return;
        }
    }

    // Vanilla and Auga both refresh their inventory cells through this method.
    // Keep the indicator consistent with the unrestricted portal inventory check.
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
    internal static class InventoryPortalIndicatorPatch
    {
        [HarmonyPostfix]
        private static void Postfix(InventoryGrid __instance)
        {
            foreach (var element in __instance.m_elements)
            {
                if (element && element.m_noteleport)
                    element.m_noteleport.enabled = false;
            }
        }
    }
}
