using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace Auga
{
    internal enum UiModule { Settings, MainMenu, Inventory, Hud, OtherUi, CharacterSelection, WorldSelection, Vitals, QuickInventory, RightPanel, PauseMenu, TextInput, Construction }

    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class UiModuleAttribute : Attribute
    {
        public readonly UiModule Module;
        public UiModuleAttribute(UiModule module) { Module = module; }
    }

    internal static class UiModules
    {
        private static readonly Dictionary<UiModule, bool> EnabledModules = new Dictionary<UiModule, bool>();

        public static void Configure(ConfigFile config)
        {
            foreach (UiModule module in Enum.GetValues(typeof(UiModule)))
            {
                // Progression lives in this inventory. These two modules are not
                // optional in Overhaul V2: there is no separate vanilla window.
                if (module == UiModule.RightPanel || module == UiModule.QuickInventory)
                {
                    EnabledModules[module] = true;
                    continue;
                }
                bool defaultValue = module == UiModule.Settings || module == UiModule.CharacterSelection || module == UiModule.WorldSelection || module == UiModule.Vitals || module == UiModule.QuickInventory || module == UiModule.RightPanel || module == UiModule.PauseMenu || module == UiModule.TextInput || module == UiModule.Construction;
                var entry = config.Bind("UI Modules", module.ToString(), defaultValue,
                    "Active les remplacements Auga de ce module. Redemarrage du jeu necessaire. " +
                    "Desactive : interface native. Valeur par defaut : " + defaultValue.ToString().ToLowerInvariant() + ". " +
                    (defaultValue ? "Module en cours de test." : "Module non valide, laisser desactive pour les tests progressifs."));
                EnabledModules[module] = entry.Value;
            }
        }

        public static bool Enabled(UiModule module) => EnabledModules.TryGetValue(module, out var enabled) && enabled;

        public static UiModule ModuleFor(Type type)
        {
            for (var current = type; current != null; current = current.DeclaringType)
            {
                var marker = current.GetCustomAttribute<UiModuleAttribute>();
                if (marker != null) return marker.Module;
            }
            return UiModule.OtherUi;
        }

        public static void PatchEnabled(Harmony harmony, Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
                if (type.Namespace != null && (type.Namespace == "Auga" || type.Namespace.StartsWith("Auga.")) && Enabled(ModuleFor(type))) harmony.CreateClassProcessor(type).Patch();
        }
    }

    [UiModule(UiModule.Construction)]
    [HarmonyPatch(typeof(UnityEngine.UI.ScrollRect), nameof(UnityEngine.UI.ScrollRect.OnScroll))]
    internal static class ConstructionRowScroll
    {
        internal static UnityEngine.UI.GridLayoutGroup Grid(UnityEngine.UI.ScrollRect scroll)
        {
            var build = scroll.GetComponentInParent<BuildUi>(true);
            return build != null && build.m_pieceScrollRect == scroll
                ? build.m_pieceButtonsContainer.GetComponent<UnityEngine.UI.GridLayoutGroup>() : null;
        }

        private static void Prefix(UnityEngine.UI.ScrollRect __instance, out float __state)
        {
            __state = __instance.scrollSensitivity;
            var grid = Grid(__instance);
            if (grid == null) return;
            __instance.StopMovement();
            // A wheel notch in the game's UI produces a fractional scroll delta.
            // Apply the 20x correction to the row pitch, rather than to other menus.
            __instance.scrollSensitivity = (grid.cellSize.y + grid.spacing.y) * 20f;
        }

        private static void Finalizer(UnityEngine.UI.ScrollRect __instance, float __state)
        {
            __instance.scrollSensitivity = __state;
        }
    }

    [UiModule(UiModule.Settings)]
    internal static class SettingsPrefabInstaller
    {
        [HarmonyPatch(typeof(UnityEngine.UI.ScrollRect), nameof(UnityEngine.UI.ScrollRect.OnScroll))]
        internal static class MouseScrollSpeed
        {
            private static void Prefix(UnityEngine.UI.ScrollRect __instance, UnityEngine.EventSystems.PointerEventData __0, out UnityEngine.Vector2 __state)
            {
                __state = __0.scrollDelta;
                if (UiModules.Enabled(UiModule.Construction) && ConstructionRowScroll.Grid(__instance) != null) return;
                __0.scrollDelta = __state * UnityEngine.Mathf.Clamp(PlatformPrefs.GetFloat("AugaMouseScrollSpeed", 100f), 25f, 300f) * 3f / 100f;
            }
            private static void Finalizer(UnityEngine.EventSystems.PointerEventData __0, UnityEngine.Vector2 __state)
            {
                __0.scrollDelta = __state;
            }
        }
        [HarmonyPatch(typeof(Localization), "SetupLanguage")]
        internal static class Translations
        {
            private static void Postfix(Localization __instance, string __0) { Auga.LoadTranslations(__instance, __0); }
        }
        [HarmonyPatch(typeof(FejdStartup), "Awake")]
        internal static class MainMenu
        {
            private static void Prefix(FejdStartup __instance) { __instance.m_settingsPrefab = Auga.Assets.SettingsPrefab; }
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.OnSettings))]
        internal static class PauseMenu
        {
            private static void Prefix(Menu __instance) { __instance.m_settingsPrefab = Auga.Assets.SettingsPrefab; }
        }
    }
}
