using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The mystic dye (Overhaul_Dye_Mystic): the only dye, whatever the colour picked in the dyeing window. Crafted at the
    // workbench, 10 at a time, from a raspberry, blueberries, a dandelion, guck and 2 mystic powders. Its icon is paste
    // n°1 with a rainbow swirl (Assets/Dyes/dye_mystic.png).
    // The 50 former colour dyes (Overhaul_Dye_<colour ID>) stay registered without recipe so the ones already owned are
    // not lost: they turn into mystic dyes one for one in the player's bag, and still count (and are used) as mystic dyes
    // wherever else they lie.
    internal static class DyeItems
    {
        internal const string Name = "Overhaul_Dye_Mystic", Token = "$overhaul_dyeitem_mystic";
        const string Source = "Resin";

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!PrefabManager.Instance.GetPrefab(Name))
            {
                var config = new ItemConfig
                {
                    Name = Token, Description = "$overhaul_dye_mystic_desc",
                    CraftingStation = CraftingStations.Workbench, MinStationLevel = 1, Amount = 10,
                    Requirements = new[]
                    {
                        new RequirementConfig("Raspberry", 1), new RequirementConfig("Blueberries", 1), new RequirementConfig("Dandelion", 1),
                        new RequirementConfig("Guck", 1), new RequirementConfig(Rarity.MysticPowder.Name, 2),
                    },
                };
                var icon = MysticIcon(); if (icon) config.Icons = new[] { icon };
                Add(new CustomItem(Name, Source, config));
            }
            // Former colour dyes: no recipe, picture tinted in their colour as before.
            foreach (var colour in DyePalette.All)
            {
                if (PrefabManager.Instance.GetPrefab(colour.ItemName)) continue;
                var config = new ItemConfig { Name = "$overhaul_dyeitem_" + colour.ID, Description = "$overhaul_dye_desc", Icons = new[] { Icon(colour.Value, colour.ID) } };
                Add(new CustomItem(colour.ItemName, Source, config));
            }
        }
        static void Add(CustomItem item)
        {
            var shared = item.ItemDrop.m_itemData.m_shared;
            shared.m_maxStackSize = 100; shared.m_weight = .1f; shared.m_teleportable = true;
            ItemManager.Instance.AddItem(item);
        }

        static Sprite mystic;
        internal static Sprite MysticIcon()
        {
            if (mystic) return mystic;
            var tex = DyeGear.Image("dye_mystic.png"); if (!tex) return null;
            tex.filterMode = FilterMode.Bilinear;
            mystic = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(.5f, .5f), 100); mystic.name = "DyeMystic";
            return mystic;
        }

        // A former dye's icon in its colour (white dye icon + mask: R where to colour, G its relief).
        static Texture2D icon, mask;
        static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        static Sprite Icon(Color colour, string key)
        {
            if (icons.TryGetValue(key, out var s) && s) return s;
            if (!icon) icon = DyeGear.Image("dye_icon.png");
            if (!mask) mask = DyeGear.Image("dye_icon_mask.png", true);
            if (!icon || !mask) return null;
            var px = icon.GetPixels(); var m = mask.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                if (m[i].r <= 0) continue;
                var t = DyeBlend.Tint(colour, m[i].g);
                px[i] = new Color(Mathf.Lerp(px[i].r, t.r, m[i].r), Mathf.Lerp(px[i].g, t.g, m[i].r), Mathf.Lerp(px[i].b, t.b, m[i].r), px[i].a);
            }
            var tex = new Texture2D(icon.width, icon.height, TextureFormat.RGBA32, false) { name = "DyeIcon_" + key };
            tex.SetPixels(px); tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(.5f, .5f), 100); s.name = "DyeIcon_" + key;
            return icons[key] = s;
        }

        static IEnumerable<string> Former => DyePalette.All.Select(c => "$overhaul_dyeitem_" + c.ID);

        // Mystic dyes owned (former colour dyes count as mystic ones).
        internal static int Count(Inventory inv) => inv == null ? 0 : inv.CountItems(Token) + Former.Sum(n => inv.CountItems(n));
        internal static void Remove(Inventory inv, int amount)
        {
            int take = Mathf.Min(amount, inv.CountItems(Token)); if (take > 0) { inv.RemoveItem(Token, take); amount -= take; }
            foreach (var n in Former)
            {
                if (amount <= 0) break;
                take = Mathf.Min(amount, inv.CountItems(n)); if (take > 0) { inv.RemoveItem(n, take); amount -= take; }
            }
        }

        // Former colour dyes in the bag become mystic dyes, one for one (when the character loads).
        static void Convert(Player player)
        {
            var inv = player ? player.GetInventory() : null; if (inv == null) return;
            int count = Former.Sum(n => inv.CountItems(n)); if (count <= 0) return;
            var prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(Name) : null; if (!prefab) return;
            foreach (var n in Former) { int c = inv.CountItems(n); if (c > 0) inv.RemoveItem(n, c); }
            while (count > 0)
            {
                int stack = Mathf.Min(count, 100);
                var data = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); data.m_dropPrefab = prefab; data.m_stack = stack;
                if (!inv.AddItem(data)) ItemDrop.DropItem(data, stack, player.transform.position + Vector3.up, Quaternion.identity);
                count -= stack;
            }
        }
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        static class ConvertOnSpawn { static void Postfix(Player __instance) { if (__instance == Player.m_localPlayer) Convert(__instance); } }
    }
}
