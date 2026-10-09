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
                tryGetRarity = api?.GetMethod("TryGetRarity", BindingFlags.Public | BindingFlags.Static);
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

        private static MethodInfo tryGetRarity;
        private static Color? Rarity(ItemDrop.ItemData item)
        {
            if (item == null || rarityColor == null) return null;
            // Only items with a rarity: Epic Loot answers white for every other item.
            if (tryGetRarity != null)
            {
                var args = new object[] { item, 0 };
                try { if (!(bool)tryGetRarity.Invoke(null, args)) return null; } catch { return null; }
            }
            string html;
            try { html = rarityColor(item); } catch { return null; }
            return !string.IsNullOrEmpty(html) && ColorUtility.TryParseHtmlString(html, out Color color) ? color : (Color?)null;
        }

        // What the tooltip shows for a magic item, read from Epic Loot (or, for the staff layout test, sample
        // data in Epic Loot's formats). Both go through Show, so the test displays exactly like real items.
        internal sealed class MagicDisplay
        {
            internal string Topic;                     // decorated name (already coloured)
            internal string Rarity;                    // rarity display, e.g. "<color=#ff7f2a>Mythique</color>"
            internal string Color;                     // rarity colour of the effect lines
            internal readonly List<string> Effects = new List<string>();
            internal readonly List<string> Details = new List<string>();      // Shift: detail block per effect, or null
            internal readonly List<(string Text, string Color, string Icon)> Sockets = new List<(string, string, string)>();
            internal int Slots;
            internal string Set;                       // Epic Loot's raw set text (GetMagicSetTooltip)
        }

        // Tooltip: rarity-coloured name, rarity on its own line, then effects with shard slots and the set,
        // as two-column rows.
        private static void ExtendTooltip(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            try
            {
                if (Rarity(item) == null) return;
                ApplyTextOverrides(); // Before Epic Loot builds the name and rarity texts.
                MagicDisplay display = Read(item);
                if (display != null) Show(tooltip, item, display);
            }
            catch (Exception e) { Utility.Log.LogWarning("Epic Loot : infobulle incomplete : " + e.Message); }
        }

        // Shorter effect texts: every "overhaul_el_<Epic Loot key>" translation of Overhaul replaces that Epic Loot
        // key in the current language. Re-applied when the language changes or Epic Loot reloads its words.
        private const string OverridePrefix = "overhaul_el_";
        private static string overriddenLanguage, sampleKey, sampleText;
        private static void ApplyTextOverrides()
        {
            Localization localization = Localization.instance;
            if (localization == null) return;
            string language = localization.GetSelectedLanguage();
            if (language == overriddenLanguage && (sampleKey == null || localization.m_translations.TryGetValue(sampleKey, out string current) && current == sampleText)) return;
            overriddenLanguage = language; sampleKey = null;
            foreach (var pair in localization.m_translations.Where(p => p.Key.StartsWith(OverridePrefix, StringComparison.Ordinal)).ToArray())
            {
                string key = pair.Key.Substring(OverridePrefix.Length);
                localization.AddWord(key, pair.Value);
                sampleKey = key; sampleText = pair.Value;
            }
        }

        internal static void Show(ComplexTooltip tooltip, ItemDrop.ItemData item, MagicDisplay display)
        {
            ApplyTextOverrides();
            if (!string.IsNullOrEmpty(display.Topic)) tooltip.SetTopic(Localization.instance.Localize(display.Topic));
            if (!string.IsNullOrEmpty(display.Rarity)) tooltip.SetSubtitle(Localization.instance.Localize(display.Rarity) + "\n" + tooltip.GenerateItemSubtext(item));
            var rows = new List<TooltipRow>();
            for (int i = 0; i < display.Effects.Count; i++)
            {
                rows.Add(Row(display.Effects[i], display.Color));
                string block = i < display.Details.Count ? display.Details[i] : null;
                if (!string.IsNullOrEmpty(block)) rows.Add(new TooltipRow("<color=#c0c0c0>" + Localization.instance.Localize(block).TrimEnd('\n') + "</color>"));
            }
            if (display.Slots > 0)
            {
                rows.Add(new TooltipRow(Localization.instance.Localize("$mod_epicloot_sockets") + " (" + display.Sockets.Count + "/" + display.Slots + ")"));
                foreach (var socket in display.Sockets)
                    rows.Add(Row(socket.Text, socket.Color, "  ", (string.IsNullOrEmpty(socket.Icon) ? "<color=" + socket.Color + ">◈</color>" : socket.Icon) + " "));
                for (int i = display.Sockets.Count; i < display.Slots; i++)
                    rows.Add(new TooltipRow("  <color=#808080>◊ " + Localization.instance.Localize("$mod_epicloot_empty_socket") + "</color>"));
            }
            AddRows(tooltip, rows);
            // Set header: just "Name (equipped/size)".
            string set = Regex.Replace(display.Set ?? "", @"\$mod_epicloot_set:\s*", "");
            set = Regex.Replace(set, @"\):</color>", ")</color>");
            // The Auga font has no U+2023 bullet used by Epic Loot: it would draw as a box.
            set = Localization.instance.Localize(set.Replace("‣", "-")).Trim('\n', ' ');
            if (set.Length > 0) AddRows(tooltip, SetRows(set));
        }

        private static MethodInfo effectText, detailBlock, rarityHtml, shardSprite;
        private static MagicDisplay Read(ItemDrop.ItemData item)
        {
            object magic = getMagicItem?.Invoke(null, new object[] { item });
            if (magic == null) return null;
            Type type = magic.GetType();
            effectText = effectText ?? type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetEffectText" && m.GetParameters().Length == 4);
            detailBlock = detailBlock ?? type.GetMethod("GetEffectDetailBlock", BindingFlags.Public | BindingFlags.Static);
            rarityHtml = rarityHtml ?? Type.GetType("EpicLoot.EpicLoot, EpicLoot")?.GetMethod("GetRarityColor", BindingFlags.Public | BindingFlags.Static);
            shardSprite = shardSprite ?? Type.GetType("EpicLoot.ShardStones.ShardTooltipSprites, EpicLoot")?.GetMethod("GetSpriteTag", BindingFlags.Public | BindingFlags.Static);
            object rarity = type.GetField("Rarity").GetValue(magic);
            string legendary = (string)type.GetField("LegendaryID").GetValue(magic);
            bool details = (bool)(type.GetProperty("ShowEffectDetails", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? false);
            var display = new MagicDisplay
            {
                Topic = decoratedName != null ? (string)decoratedName.Invoke(null, new object[] { item, null }) : null,
                Rarity = rarityDisplay != null ? (string)rarityDisplay.Invoke(magic, null) : null,
                Color = Html(rarity),
                Slots = (int)type.GetField("SocketCount").GetValue(magic),
                Set = magicSetTooltip != null ? (string)magicSetTooltip.Invoke(null, new object[] { item }) : null,
            };
            if (effectText != null)
                foreach (object effect in (System.Collections.IEnumerable)type.GetField("Effects").GetValue(magic))
                {
                    display.Effects.Add((string)effectText.Invoke(null, new[] { effect, rarity, (object)false, legendary }));
                    display.Details.Add(details && detailBlock != null ? (string)detailBlock.Invoke(null, new[] { effect, rarity, legendary, null, "   " }) : null);
                }
            foreach (object socket in ((System.Collections.IEnumerable)type.GetField("Sockets").GetValue(magic)).Cast<object>().Where(s => s != null))
            {
                Type socketType = socket.GetType();
                object shard = socketType.GetField("Effect").GetValue(socket);
                object source = socketType.GetField("SourceRarity").GetValue(socket);
                string text = shard != null && effectText != null ? (string)effectText.Invoke(null, new[] { shard, source, (object)false, null }) : "$mod_epicloot_shard_noeffect";
                // The socketed item's own icon, as Epic Loot shows it; a shard glyph when unavailable.
                string sprite = shardSprite != null ? (string)shardSprite.Invoke(null, new[] { socketType.GetField("SourcePrefab").GetValue(socket) }) : "";
                display.Sockets.Add((text, Html(source), sprite));
            }
            return display;
        }

        private static string Html(object rarity)
        {
            try { return rarityHtml != null ? (string)rarityHtml.Invoke(null, new[] { rarity }) : "#ffffff"; }
            catch { return "#ffffff"; }
        }

        // A value at the start or the end of the text ("+18 %", "-15 %", "x2", "5 s") goes to the right
        // column; a text with several numbers, or a number that belongs to the sentence, stays whole.
        private static readonly Regex Value = new Regex(@"^\s*([+\-−]?\d+(?:[.,]\d+)?\s?(?:%|x|s)?)\s*:?\s+|\s*:?\s+([+\-−x]?\d+(?:[.,]\d+)?\s?(?:%|x|s)?)\s*$");
        private static readonly Regex Preposition = new Regex(@"\b(sous|de|du|des|à|au|aux|par|pendant|toutes|tous|chaque|en|under|below|above|of|for|by|every|at|over)$", RegexOptions.IgnoreCase);
        internal static TooltipRow Row(string text, string color, string indent = "", string icon = "")
        {
            text = Regex.Replace(Localization.instance.Localize(text ?? ""), "<[^>]+>", "").Trim();
            // "Movement Speed +5% (Health Critical)": the condition stays with the label.
            Match condition = Regex.Match(text, @"^(.*\S)\s+([+\-−]?\d+(?:[.,]\d+)?\s?%)\s*(\([^)]*\))$");
            if (condition.Success) text = condition.Groups[1].Value + " " + condition.Groups[3].Value + " " + condition.Groups[2].Value;
            Match match = Value.Match(text);
            string whole = indent + icon + "<color=" + color + ">" + text + "</color>";
            if (!match.Success || Regex.Matches(text, @"\d+(?:[.,]\d+)?").Count != 1) return new TooltipRow(whole);
            string value = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            string label = text.Remove(match.Index, match.Length).Trim(' ', ':', ',');
            // "+18 % de dégâts la nuit": the label starts after the value, without its "de".
            if (match.Groups[1].Success)
            {
                label = Regex.Replace(label, @"^(de |d'|du |des )", "", RegexOptions.IgnoreCase);
                if (label.Length > 0) label = char.ToUpper(label[0]) + label.Substring(1);
            }
            if (Preposition.IsMatch(label)) return new TooltipRow(whole);
            string element = ElementColor(label);
            return new TooltipRow(indent + icon + "<color=" + color + ">" + label + "</color>", element != null ? "<color=" + element + ">" + value + "</color>" : value);
        }

        // Elemental values keep their element's colour, as in the damage lines; the others use the
        // native value style of the two-column box (bold font, shadow, regular colour).
        private static string ElementColor(string label)
        {
            string l = label.ToLowerInvariant();
            if (l.Contains("feu") || l.Contains("fire")) return "#FF703D";
            if (l.Contains("givre") || l.Contains("froid") || l.Contains("frost")) return "#65B5FF";
            if (l.Contains("poison")) return "#78D65A";
            if (l.Contains("foudre") || l.Contains("lightning")) return "#FFE45C";
            return null;
        }

        // Set bonus lines "(n) - text value": the value goes to the right column, like the effects. A colour
        // opened on one line may run over the next ones, as in Epic Loot's text.
        internal static List<TooltipRow> SetRows(string set)
        {
            var rows = new List<TooltipRow>();
            string color = null;
            foreach (string line in set.Split('\n'))
            {
                Match open = Regex.Match(line, "<color=([^>]+)>");
                if (open.Success) color = open.Groups[1].Value;
                string plain = Regex.Replace(line, "<[^>]+>", "");
                Match bonus = Regex.Match(plain, @"^\s*\((\d+)\)\s*[-‣]\s*(.*)$");
                string c = color ?? "#D1C9C2";
                if (bonus.Success) rows.Add(Row(bonus.Groups[2].Value, c, "", "<color=" + c + ">(" + bonus.Groups[1].Value + ")</color> "));
                else rows.Add(new TooltipRow(color != null ? "<color=" + color + ">" + plain + "</color>" : plain));
                if (line.Contains("</color>") && line.LastIndexOf("</color>") > line.LastIndexOf("<color=")) color = null;
            }
            return rows;
        }

        internal static void AddRows(ComplexTooltip tooltip, List<TooltipRow> rows) => TooltipRowAligner.Add(tooltip, rows);

        [HarmonyPatch(typeof(FejdStartup), "Awake")]
        private static class AfterPluginsLoaded
        {
            private static void Postfix()
            {
                Initialize();
            }
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
