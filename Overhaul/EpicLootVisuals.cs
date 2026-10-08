using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        private static MethodInfo decoratedName, getMagicItem, magicTooltip, rarityDisplay, magicSetTooltip;

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
                Type extensions = Type.GetType("EpicLoot.ItemDataExtensions, EpicLoot");
                getMagicItem = extensions?.GetMethod("GetMagicItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
                // Legendary/mythic sets only: vanilla sets already have their own Auga box.
                magicSetTooltip = extensions?.GetMethod("GetMagicSetTooltip", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
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
                if (!string.IsNullOrEmpty(rarity)) tooltip.SetSubtitle(Localization.instance.Localize(rarity) + "\n" + tooltip.GenerateItemSubtext(item));
                AddEffects(tooltip, magic);
                // Set header: just "Name (equipped/size)".
                string set = magicSetTooltip != null ? (string)magicSetTooltip.Invoke(null, new object[] { item }) : "";
                set = Regex.Replace(set ?? "", @"\$mod_epicloot_set:\s*", "");
                set = Regex.Replace(set, @"\):</color>", ")</color>");
                // The Auga font has no U+2023 bullet used by Epic Loot: it would draw as a box.
                set = Localization.instance.Localize(set.Replace("\u2023", "-")).Trim('\n', ' ');
                if (set.Length > 0) tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab).Text.text = SetRows(set);
            }
            catch (Exception e) { Utility.Log.LogWarning("Epic Loot : infobulle incomplete : " + e.Message); }
        }

        // Effects as two-column rows like the other stats: the text on the left, the rolled value on the
        // right, in the rarity colour and without Epic Loot's pip. Shard slots follow in their own colour.
        // Holding Shift adds Epic Loot's detail lines under each effect.
        private static MethodInfo effectText, detailBlock, rarityHtml, shardSprite;
        private static void AddEffects(ComplexTooltip tooltip, object magic)
        {
            Type type = magic.GetType();
            effectText = effectText ?? type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetEffectText" && m.GetParameters().Length == 4);
            detailBlock = detailBlock ?? type.GetMethod("GetEffectDetailBlock", BindingFlags.Public | BindingFlags.Static);
            rarityHtml = rarityHtml ?? Type.GetType("EpicLoot.EpicLoot, EpicLoot")?.GetMethod("GetRarityColor", BindingFlags.Public | BindingFlags.Static);
            shardSprite = shardSprite ?? Type.GetType("EpicLoot.ShardStones.ShardTooltipSprites, EpicLoot")?.GetMethod("GetSpriteTag", BindingFlags.Public | BindingFlags.Static);
            if (effectText == null) return;
            object rarity = type.GetField("Rarity").GetValue(magic);
            string legendary = (string)type.GetField("LegendaryID").GetValue(magic);
            string color = Html(rarity);
            bool details = (bool)(type.GetProperty("ShowEffectDetails", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? false);
            var lines = new List<string>();
            foreach (object effect in (System.Collections.IEnumerable)type.GetField("Effects").GetValue(magic))
            {
                lines.Add(Row((string)effectText.Invoke(null, new[] { effect, rarity, (object)false, legendary }), color));
                if (details && detailBlock != null)
                {
                    string block = Localization.instance.Localize((string)detailBlock.Invoke(null, new[] { effect, rarity, legendary, null, "   " })).TrimEnd('\n');
                    if (block.Length > 0) lines.Add("<align=left><color=#c0c0c0>" + block + "</color>");
                }
            }
            int slots = (int)type.GetField("SocketCount").GetValue(magic);
            if (slots > 0) AddSockets(lines, type, magic, slots);
            if (lines.Count > 0) tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab).Text.text = string.Join("\n", lines);
        }

        private static void AddSockets(List<string> lines, Type type, object magic, int slots)
        {
            var sockets = ((System.Collections.IEnumerable)type.GetField("Sockets").GetValue(magic)).Cast<object>().Where(s => s != null).ToList();
            lines.Add("<align=left>" + Localization.instance.Localize("$mod_epicloot_sockets") + " (" + sockets.Count + "/" + slots + ")");
            foreach (object socket in sockets)
            {
                Type socketType = socket.GetType();
                object shard = socketType.GetField("Effect").GetValue(socket);
                object source = socketType.GetField("SourceRarity").GetValue(socket);
                string text = shard != null ? (string)effectText.Invoke(null, new[] { shard, source, (object)false, null }) : "$mod_epicloot_shard_noeffect";
                // The socketed item's own icon, as Epic Loot shows it; a shard glyph when unavailable.
                string sprite = shardSprite != null ? (string)shardSprite.Invoke(null, new[] { socketType.GetField("SourcePrefab").GetValue(socket) }) : "";
                lines.Add(Row(text, Html(source), "  ", (string.IsNullOrEmpty(sprite) ? "<color=" + Html(source) + ">\u25C8</color>" : sprite) + " "));
            }
            for (int i = sockets.Count; i < slots; i++)
                lines.Add("<align=left>  <color=#808080>\u25CA " + Localization.instance.Localize("$mod_epicloot_empty_socket") + "</color>");
        }

        private static string Html(object rarity)
        {
            try { return rarityHtml != null ? (string)rarityHtml.Invoke(null, new[] { rarity }) : "#ffffff"; }
            catch { return "#ffffff"; }
        }

        // A value at the start or the end of the text ("+18 %", "-15 %", "x2", "5 s") is drawn right-aligned
        // on the same line; a text with several numbers, or a number inside the sentence, stays whole. One
        // text per box: the value sits on the label's last line even when the label wraps.
        private static readonly Regex Value = new Regex(@"^\s*([+\-−]?\d+(?:[.,]\d+)?\s?(?:%|x|s)?)\s*:?\s+|\s*:?\s+([+\-−x]?\d+(?:[.,]\d+)?\s?(?:%|x|s)?)\s*$");
        // Values use the regular stat colour; elemental effects keep their element's colour, as in the damage lines.
        static string ValueColor(string label)
        {
            string l = label.ToLowerInvariant();
            if (l.Contains("feu") || l.Contains("fire")) return "#FF703D";
            if (l.Contains("givre") || l.Contains("froid") || l.Contains("frost")) return "#65B5FF";
            if (l.Contains("poison")) return "#78D65A";
            if (l.Contains("foudre") || l.Contains("lightning")) return "#FFE45C";
            return "#FFFFFF";
        }
        internal static string Row(string text, string color, string indent = "", string icon = "")
        {
            text = Regex.Replace(Localization.instance.Localize(text ?? ""), "<[^>]+>", "").Trim();
            Match match = Value.Match(text);
            if (!match.Success || Regex.Matches(text, @"\d+(?:[.,]\d+)?").Count != 1)
                return "<align=left>" + indent + icon + "<color=" + color + ">" + text + "</color>";
            string value = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            string label = text.Remove(match.Index, match.Length).Trim(' ', ':', ',');
        // A number that belongs to the sentence ("below 30 %", "every 5 s") stays in it.
        if (System.Text.RegularExpressions.Regex.IsMatch(label, @"\b(sous|de|du|des|à|au|aux|par|pendant|toutes|tous|chaque|en|under|below|above|of|for|by|every|at|over)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "<align=left>" + indent + icon + "<color=" + color + ">" + text + "</color>";
            return "<align=left>" + indent + icon + "<color=" + color + ">" + label + "</color><line-height=0>\n<align=right><b><color=" + ValueColor(label) + ">" + value + "</color></b></line-height>";
        }
        // Set bonus lines "(n) - text value": the value goes right-aligned in bold, like the effects. A colour
        // opened on one line may run over the next ones, as in Epic Loot's text.
        internal static string SetRows(string set)
        {
            var lines = new List<string>();
            string color = null;
            foreach (string line in set.Split('\n'))
            {
                Match open = Regex.Match(line, "<color=([^>]+)>");
                if (open.Success) color = open.Groups[1].Value;
                string plain = Regex.Replace(line, "<[^>]+>", "");
                Match bonus = Regex.Match(plain, @"^\s*\((\d+)\)\s*[-\u2023]\s*(.*)$");
                string c = color ?? "#D1C9C2";
                if (bonus.Success) lines.Add(Row(bonus.Groups[2].Value, c, "", "<color=" + c + ">(" + bonus.Groups[1].Value + ")</color> "));
                else lines.Add("<align=left>" + (color != null ? "<color=" + color + ">" + plain + "</color>" : plain));
                if (line.Contains("</color>") && line.LastIndexOf("</color>") > line.LastIndexOf("<color=")) color = null;
            }
            return string.Join("\n", lines);
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
        private static class AfterPluginsLoaded
        {
            private static void Postfix()
            {
                Initialize();
                if (!layoutTest) { layoutTest = true; ComplexTooltip.OnComplexTooltipGeneratedForItem += LayoutTest; }
            }
        }

        // TEMPORARY layout test: every staff shows the boxes of a mythic Epic Loot item with shard slots and
        // a set (sample text), with or without Epic Loot, to judge the tooltip height in game.
        private static bool layoutTest;
        private static void LayoutTest(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || (item.m_shared.m_skillType != Skills.SkillType.ElementalMagic && item.m_shared.m_skillType != Skills.SkillType.BloodMagic)) return;
            // Worst case: the projectile line (the element stat is merged into its damage line), the most effects, shard slots and the largest set.
            const string mythic = "#ff7f2a", shard = "#d078ff", label = "#FFF0AD";
            tooltip.SetTopic("<color=" + mythic + ">" + tooltip.Topic.text + "</color>");
            tooltip.SetSubtitle("<color=" + mythic + ">Mythique</color>\n" + tooltip.Subtitle.text);
            string knockback = Localization.instance.Localize("$item_knockback");
            var damage = tooltip.TextBoxContainer.GetComponentsInChildren<TooltipTextBox>(true).FirstOrDefault(b => b.Text && b.Text.text.Contains(knockback));
            if (damage)
            {
                damage.AddLine("<color=" + label + ">Projectile sup.</color>", "25%", false);
            }
            var lines = new List<string>();
            foreach (string effect in new[] { "Dégâts de feu +18 %", "Coût en eitr -15 %", "Vitesse d'attaque +10 %", "Chances de coup critique +6 %",
                "Les attaques enflamment les ennemis touchés", "Régénération d'eitr +12 %", "Les ennemis tués explosent en libérant des flammes" })
                lines.Add(Row(effect, mythic));
            lines.Add("<align=left>Emplacements de shard (3/4)");
            foreach (string effect in new[] { "Dégâts de feu +6 %", "Coût en eitr -4 %", "Gagne de l'adrénaline en infligeant des dégâts de feu" })
                lines.Add(Row(effect, shard, "  ", "<color=" + shard + ">\u25C8</color> "));
            lines.Add("<align=left>  <color=#808080>\u25CA Emplacement vide</color>");
            tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab).Text.text = string.Join("\n", lines);
            tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab).Text.text = SetRows(
                "<color=" + mythic + ">Fureur de Surtr (2/6)</color>\n" +
                "  <color=white>Bâton des braises</color>\n  <color=white>Couronne de Surtr</color>\n  <color=#808080ff>Cape de cendres\n  Plastron de Surtr\n  Jambières de Surtr\n  Anneau des braises</color>\n" +
                "<color=" + mythic + ">(2) - Dégâts de feu +10 %</color>\n<color=#808080ff>(3) - Résistance au feu\n(4) - Régénération d'eitr +15 %\n" +
                "(5) - Les coups critiques libèrent une explosion de flammes\n(6) - Invoque un esprit de feu lorsque la vie passe sous 30 %</color>");
        }
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
