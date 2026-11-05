using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Auga
{
    [HarmonyPatch]
    [UiModule(UiModule.EnemyHud)]
    public static class EnemyHud_Setup
    {
        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.Awake))]
        public static class EnemyHud_Awake_Patch
        {
            // The game's own EnemyHud object stays where it is, with the canvas the game set up for it (drawn like the rest
            // of the HUD and the damage numbers); only its bars are Auga's: the prefab's HudRoot (bar templates, icons)
            // takes the place of the native one, and the prefab's EnemyHud settings (templates, distances) go onto the
            // native component, whose own Awake then runs as usual.
            public static void Prefix(EnemyHud __instance)
            {
                var prefab = Auga.Assets.EnemyHud;
                if (__instance.name != "EnemyHud" || !prefab) return;
                // A copy made inactive so its own EnemyHud never wakes up.
                var holder = new GameObject("AugaEnemyHudHolder"); holder.SetActive(false);
                var copy = Object.Instantiate(prefab, holder.transform, false);
                var source = copy.GetComponent<EnemyHud>();
                var root = source ? source.m_hudRoot : null;
                if (root)
                {
                    int index = __instance.m_hudRoot ? __instance.m_hudRoot.transform.GetSiblingIndex() : __instance.transform.childCount;
                    if (__instance.m_hudRoot) Object.DestroyImmediate(__instance.m_hudRoot);
                    root.transform.SetParent(__instance.transform, false);
                    root.transform.SetSiblingIndex(index);
                    // Its settings, those Unity keeps in the prefab (objects, numbers, flags, text).
                    foreach (var field in typeof(EnemyHud).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        var type = field.FieldType;
                        if (field.IsNotSerialized || field.IsInitOnly || !(typeof(Object).IsAssignableFrom(type) || type.IsPrimitive || type.IsEnum || type == typeof(string))) continue;
                        field.SetValue(__instance, field.GetValue(source));
                    }
                }
                else Auga.LogWarning("AugaEnemyHud has no HudRoot: native health bars kept.");
                Object.DestroyImmediate(holder);
            }
        }

        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.ShowHud))]
        public static class EnemyHud_ShowHud_Patch
        {
            public static void Postfix(EnemyHud __instance, Character c)
            {
                if (c == null || __instance.m_huds.TryGetValue(c, out EnemyHud.HudData _))
                {
                    return;
                }

                var hud = __instance.m_huds.LastOrDefault();
                if (hud.Key != null && hud.Value != null)
                {
                    const int maxLevelForStarDisplays = 6;
                    const int firstStarDisplayLevel = 2;
                    const int lastStarDisplayLevel = 3;

                    var level = c.GetLevel();
                    if (level > lastStarDisplayLevel)
                    {
                        var hudGui = hud.Value.m_gui;
                        for (var i = firstStarDisplayLevel; i <= maxLevelForStarDisplays; i++)
                        {
                            var levelDisplay = hudGui.transform.Find($"level_{level}");
                            if (levelDisplay != null)
                            {
                                levelDisplay.gameObject.SetActive(i == level);
                            }
                        }

                        var levelDisplayX = hudGui.transform.Find("level_X");
                        if (levelDisplayX)
                        {
                            var useExtendedLevel = level > maxLevelForStarDisplays;
                            if (useExtendedLevel)
                            {
                                var levelXDisplayName = $"level_{level}";
                                var newLevelXDisplay = hudGui.transform.Find(levelXDisplayName);
                                if (newLevelXDisplay == null)
                                {
                                    newLevelXDisplay = Object.Instantiate(levelDisplayX, levelDisplayX.parent, false);
                                    newLevelXDisplay.name = levelXDisplayName;
                                }

                                var text = levelDisplayX.GetComponentInChildren<Text>();
                                text.text = $"x {level - 1}";
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.UpdateHuds))]
        [HarmonyAfter("org.bepinex.plugins.creaturelevelcontrol")]
        public static class EnemyHud_UpdateHuds_Patch
        {
            public static void Postfix(EnemyHud __instance)
            {
                foreach (var hud in __instance.m_huds)
                {
                    if (hud.Key != null && hud.Value != null && hud.Value.m_gui != null)
                    {
                        var name = hud.Value.m_gui.transform.Find("Name");
                        if (name != null)
                        {
                            var rt = (RectTransform)name;
                            rt.anchoredPosition = new Vector2(0, 38.5f);
                        }
                    }
                }
            }
        }
    }
}
