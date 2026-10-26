using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Hugin's tips on Overhaul's own features: added to the game's tips (Tutorial), so the raven lands to tell them once and
    // they are listed in the compendium's Hugin tab. Each one is shown the first time its moment comes. Their texts carry
    // the keys as set ({0}, {1}...), filled when the tips are added.
    internal static class HuginTips
    {
        const string Levels = "overhaul_levels", Stats = "overhaul_stats", Dash = "overhaul_dash", Rarity = "overhaul_rarity",
            EnchantTable = "overhaul_enchanttable", Catalysts = "overhaul_catalysts", Chests = "overhaul_chests", Move = "overhaul_move",
            QuickBuild = "overhaul_quickbuild", Rotation = "overhaul_rotation", Dungeons = "overhaul_dungeons";

        static string Key(KeyCode key) => AugaUnity.AugaModsSettings.KeyLabel(key);
        static string Yellow(string text) => "<color=yellow>" + text + "</color>";

        [HarmonyPatch(typeof(Tutorial), "Awake")]
        private static class Register
        {
            private static void Postfix(Tutorial __instance)
            {
                if (Localization.instance == null) return;
                string run = Localization.instance.GetBoundKeyString("Run");
                string takeAll = Key(EquipmentAndQuickSlots.ValConfig.EffectiveTakeAllKey.MainKey);
                Add(__instance, Levels);
                Add(__instance, Stats);
                Add(__instance, Dash, run);
                Add(__instance, Rarity);
                Add(__instance, EnchantTable);
                Add(__instance, Catalysts);
                Add(__instance, Chests, takeAll);
                Add(__instance, Move, Storage.RelocationShortcut.Label);
                Add(__instance, QuickBuild);
                Add(__instance, Rotation, Key(Utility.OverhaulConfig.BuildPitchKey.Value), Key(Utility.OverhaulConfig.BuildRollKey.Value),
                    Key(Utility.OverhaulConfig.BuildPreciseKey.Value), Key(Utility.OverhaulConfig.BuildResetKey.Value), Key(Utility.OverhaulConfig.BuildSnapToggleKey.Value));
                Add(__instance, Dungeons);
            }
        }

        // The tips' clips in the compendium (overhaul_huginclips, OverhaulLab HuginClipBundle): each sheet is named
        // HuginClip_<Clip>_<columns>_<rows>_<count>; a tip shows its clips one after the other, in a loop.
        static readonly Dictionary<string, string[]> ClipOf = new Dictionary<string, string[]> { [QuickBuild] = new[] { "QuickBuild" }, [Rotation] = new[] { "Rotation" }, [Move] = new[] { "Move" },
            [Chests] = new[] { "TakeAll", "Rename", "Privacy", "Trash" }, [Rarity] = new[] { "Rarity" }, [Catalysts] = new[] { "Catalysts", "EnchantCatalysts" }, [EnchantTable] = new[] { "EnchantTable", "EnchantReroll" },
            [Stats] = new[] { "StatPoints" }, [Levels] = new[] { "Levels" }, [Dash] = new[] { "Dash" } };
        static AssetBundle clips;

        internal static void Initialize() => AugaUnity.TipClip.Source = Clip;

        static AugaUnity.TipClip.Clip Clip(string topic)
        {
            if (Localization.instance == null) return null;
            foreach (var entry in ClipOf)
            {
                if (Localization.instance.Localize("$" + entry.Key + "_tip_topic") != topic) continue;
                if (!clips) clips = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_huginclips");
                var sheets = clips ? clips.LoadAllAssets<Texture2D>() : new Texture2D[0];
                AugaUnity.TipClip.Clip first = null, last = null;
                foreach (string name in entry.Value)
                {
                    var sheet = sheets.FirstOrDefault(t => t.name.StartsWith("HuginClip_" + name + "_"));
                    if (!sheet) continue;
                    var layout = sheet.name.Split('_');
                    var clip = new AugaUnity.TipClip.Clip { Sheet = sheet, Columns = int.Parse(layout[2]), Rows = int.Parse(layout[3]), Count = int.Parse(layout[4]) };
                    if (last != null) last.Next = clip; else first = clip;
                    last = clip;
                }
                return first;
            }
            return null;
        }

        static void Add(Tutorial tutorial, string name, params string[] keys)
        {
            if (tutorial.m_texts.Any(t => t.m_name == name)) return;
            string text = Localization.instance.Localize("$" + name + "_tip_text");
            if (keys.Length > 0) text = string.Format(text, keys.Select(Yellow).ToArray());
            tutorial.m_texts.Add(new Tutorial.TutorialText
            {
                m_name = name, m_topic = "$" + name + "_tip_topic", m_label = "$" + name + "_tip_label", m_text = text
            });
        }

        static void Show(string name)
        {
            var player = Player.m_localPlayer;
            if (player && Tutorial.instance && !player.HaveSeenTutorial(name)) player.ShowTutorial(name);
        }

        // Experience and levels (Leveling.ExperienceFeed).
        internal static void Experience(int level)
        {
            Show(Levels);
            if (level >= 2) Show(Stats);
        }

        // A weapon in hand: the dash. The hammer: moving a piece.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Equip
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
            {
                if (!__result || item == null || __instance != Player.m_localPlayer) return;
                if (item.m_shared.m_buildPieces && item.m_shared.m_buildPieces.name == "_HammerPieceTable") Show(Move);
                else if (item.IsWeapon() && Utility.OverhaulConfig.DashEnabled.Value) Show(Dash);
            }
        }

        // A new item: a rarity above the first one, or a catalyst.
        [HarmonyPatch(typeof(Player), nameof(Player.AddKnownItem))]
        private static class Known
        {
            private static void Postfix(Player __instance, ItemDrop.ItemData item)
            {
                if (__instance != Player.m_localPlayer || item == null) return;
                if (global::Overhaul.Rarity.ItemRarity.IsCatalyst(item, out _)) { Show(Catalysts); return; }
                var rarity = global::Overhaul.Rarity.ItemRarity.Of(item);
                if (rarity != null && rarity != global::Overhaul.Rarity.RarityConfig.Current.Rarities.FirstOrDefault()) Show(Rarity);
            }
        }

        // The enchanting table becomes buildable.
        [HarmonyPatch(typeof(Player), nameof(Player.AddKnownPiece))]
        private static class Piece
        {
            private static void Postfix(Player __instance, global::Piece piece)
            {
                if (__instance == Player.m_localPlayer && piece && Utils.GetPrefabName(piece.gameObject) == global::Overhaul.Rarity.EnchantTable.Name) Show(EnchantTable);
            }
        }

        // A chest opened.
        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static class Chest
        {
            private static void Postfix(Container __instance, Humanoid character, bool __result)
            {
                if (__result && character == Player.m_localPlayer && __instance.GetComponent<global::Piece>()) Show(Chests);
            }
        }

        // A piece placed: turning it on three axes; a wall: quick build.
        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        private static class Placed
        {
            private static void Postfix(Player __instance, global::Piece piece)
            {
                if (__instance != Player.m_localPlayer || !piece || piece.m_groundPiece || piece.m_groundOnly || piece.m_cultivatedGroundOnly) return;
                Show(Rotation);
                if (Utils.GetPrefabName(piece.gameObject).ToLowerInvariant().Contains("wall")) Show(QuickBuild);
            }
        }

        // Inside a dungeon.
        [HarmonyPatch(typeof(Player), "Update")]
        private static class Dungeon
        {
            static float next;
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || Time.time < next) return;
                next = Time.time + 2;
                if (__instance.InInterior()) Show(Dungeons);
            }
        }
    }
}
