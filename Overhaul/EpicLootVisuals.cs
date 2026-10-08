using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AugaUnity;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // Epic Loot compatibility, display side. Epic Loot's own item display (replaced tooltip text, an extra
    // vanilla tooltip window, slot backgrounds) breaks the Auga interface, so its display patches are
    // removed; its gameplay is untouched. Overhaul reads Epic Loot's data instead and shows it the Auga way:
    // a rarity background in the slots, the rarity-coloured name and a magic effects section in the tooltip.
    // No assembly reference: Epic Loot stays optional.
    internal static class EpicLootVisuals
    {
        internal const string Guid = "randyknapp.mods.epicloot";
        internal static bool Loaded => Chainloader.PluginInfos.ContainsKey(Guid);
        private const string BackgroundName = "OverhaulRarity";

        private static bool ready;
        private static Func<ItemDrop.ItemData, string> rarityColor;
        private static MethodInfo decoratedName, getMagicItem, magicTooltip, rarityDisplay;

        // Called once every plugin has loaded (Epic Loot patches in its Awake).
        internal static void Initialize()
        {
            if (ready || !Loaded) return;
            ready = true;
            try
            {
                RemoveDisplayPatches();
                Type api = Type.GetType("EpicLoot.API, EpicLoot");
                MethodInfo color = api?.GetMethod("GetItemRarityColor", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
                if (color != null) rarityColor = (Func<ItemDrop.ItemData, string>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData, string>), color);
                decoratedName = api?.GetMethod("GetItemDecoratedName", BindingFlags.Public | BindingFlags.Static);
                getMagicItem = Type.GetType("EpicLoot.ItemDataExtensions, EpicLoot")?.GetMethod("GetMagicItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
                Type magicItem = getMagicItem?.ReturnType;
                magicTooltip = magicItem?.GetMethod("GetTooltip", Type.EmptyTypes);
                rarityDisplay = magicItem?.GetMethod("GetRarityDisplay", Type.EmptyTypes);
                if (rarityColor == null || magicTooltip == null) Utility.Log.LogWarning("Epic Loot : API incomplete, rarete ou effets magiques non affiches");
                ComplexTooltip.OnComplexTooltipGeneratedForItem += ExtendTooltip;
                Utility.Log.LogInfo("Epic Loot detecte : affichage des objets magiques pris en charge par Overhaul");
            }
            catch (Exception e) { Utility.Log.LogError("Epic Loot : compatibilite d'affichage impossible : " + e); }
        }

        private static void RemoveDisplayPatches()
        {
            var targets = new List<MethodBase>
            {
                AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.CreateItemTooltip)),
                AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui)),
                AccessTools.Method(typeof(HotkeyBar), nameof(HotkeyBar.UpdateIcons)),
                AccessTools.Method(typeof(UITooltip), nameof(UITooltip.OnPointerExit)),
                AccessTools.Method(typeof(UITooltip), nameof(UITooltip.OnHoverStart)),
            };
            targets.AddRange(typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name == nameof(ItemDrop.ItemData.GetTooltip)));
            var harmony = new Harmony(Guid);
            foreach (MethodBase method in targets.Where(m => m != null))
                harmony.Unpatch(method, HarmonyPatchType.All, Guid);
        }

        private static Color? Rarity(ItemDrop.ItemData item)
        {
            if (item == null || rarityColor == null) return null;
            string html;
            try { html = rarityColor(item); } catch { return null; }
            return !string.IsNullOrEmpty(html) && ColorUtility.TryParseHtmlString(html, out Color color) ? color : (Color?)null;
        }

        // Tooltip: rarity-coloured name, rarity in the subtitle, then the effects and shard slots in their own box.
        private static void ExtendTooltip(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            try
            {
                if (Rarity(item) == null) return;
                if (decoratedName != null) tooltip.SetTopic(Localization.instance.Localize((string)decoratedName.Invoke(null, new object[] { item, null })));
                object magic = getMagicItem?.Invoke(null, new object[] { item });
                if (magic == null || magicTooltip == null) return;
                string rarity = rarityDisplay != null ? (string)rarityDisplay.Invoke(magic, null) : null;
                if (!string.IsNullOrEmpty(rarity)) tooltip.SetSubtitle(Localization.instance.Localize(rarity) + ", " + tooltip.GenerateItemSubtext(item));
                string effects = Localization.instance.Localize((string)magicTooltip.Invoke(magic, null)).Trim('\n');
                if (effects.Length == 0) return;
                tooltip.AddDivider();
                var box = tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab);
                box.Text.text = effects;
            }
            catch (Exception e) { Utility.Log.LogWarning("Epic Loot : infobulle incomplete : " + e.Message); }
        }

        // Slots: a rarity tint drawn behind the icon, in Overhaul's own image.
        private static void Paint(Image icon, ItemDrop.ItemData item)
        {
            if (!icon || !icon.transform.parent) return;
            Color? color = Rarity(item);
            Transform existing = icon.transform.parent.Find(BackgroundName);
            if (color == null) { if (existing) existing.gameObject.SetActive(false); return; }
            Image background;
            if (existing) background = existing.GetComponent<Image>();
            else
            {
                var go = new GameObject(BackgroundName, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(icon.transform.parent, false);
                go.transform.SetSiblingIndex(icon.transform.GetSiblingIndex());
                var rect = (RectTransform)go.transform;
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(3, 3); rect.offsetMax = new Vector2(-3, -3);
                background = go.GetComponent<Image>();
                background.raycastTarget = false;
            }
            Color tint = color.Value; tint.a = 0.35f;
            background.color = tint;
            background.gameObject.SetActive(true);
        }

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
        private static class GridBackgrounds
        {
            private static bool Prepare() => Loaded;
            private static void Postfix(InventoryGrid __instance)
            {
                Inventory inventory = __instance.GetInventory();
                if (!ready || inventory == null) return;
                foreach (var element in __instance.m_elements)
                    if (element) Paint(element.m_icon, element.m_used ? inventory.GetItemAt(element.Position.x, element.Position.y) : null);
            }
        }

        [HarmonyPatch(typeof(HotkeyBar), nameof(HotkeyBar.UpdateIcons))]
        private static class HotbarBackgrounds
        {
            private static bool Prepare() => Loaded;
            private static void Postfix(HotkeyBar __instance, Player player)
            {
                if (!ready || !player) return;
                for (int i = 0; i < __instance.m_elements.Count; i++)
                    Paint(__instance.m_elements[i].m_icon, __instance.m_items.FirstOrDefault(it => it.m_gridPos.x == i));
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "Awake")]
        private static class AfterPluginsLoaded { private static void Postfix() => Initialize(); }
    }

    // The Auga tooltip never grows past the screen: when it is taller, it is scaled down to fit.
    internal sealed class TooltipScreenFit : MonoBehaviour
    {
        private Vector3 baseScale;
        private void Awake() => baseScale = transform.localScale;
        private void LateUpdate()
        {
            var rect = transform as RectTransform;
            if (!rect || baseScale.y == 0 || transform.localScale.y == 0) return;
            float unit = transform.lossyScale.y / transform.localScale.y * baseScale.y;
            float height = rect.rect.height * unit;
            float limit = Screen.height * 0.95f;
            float factor = height > limit && height > 0 ? limit / height : 1f;
            transform.localScale = baseScale * factor;
        }

        [HarmonyPatch(typeof(ComplexTooltip), nameof(ComplexTooltip.Start))]
        private static class Attach
        {
            private static void Postfix(ComplexTooltip __instance)
            { if (!__instance.GetComponent<TooltipScreenFit>()) __instance.gameObject.AddComponent<TooltipScreenFit>(); }
        }
    }
}
