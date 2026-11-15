using System.Collections.Generic;
using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The 50 dyes (Overhaul_Dye_<colour ID>), crafted at the workbench from common game ingredients: each normal colour
    // from something of that colour (2) or two mixed (1 each), its light shade with a bone fragment added, its dark shade
    // with a coal added; black from coal, white from bone fragments, grey from both. They share one picture, the white dye icon, tinted in their colour by code
    // (icon + mask: R where to colour, G its relief), so a new colour needs no new image.
    internal static class DyeItems
    {
        const string Source = "Resin";
        // Ingredients giving each hue's normal shade: common ones, two mixed like a real dye where no single one fits.
        static readonly Dictionary<string, string[]> Ingredients = new Dictionary<string, string[]>
        {
            { "red", new[] { "Raspberry" } }, { "orange", new[] { "Carrot" } }, { "yellow", new[] { "Dandelion" } },
            { "lime", new[] { "Dandelion", "Guck" } }, { "green", new[] { "Guck" } }, { "teal", new[] { "Guck", "Blueberries" } },
            { "cyan", new[] { "Blueberries", "Ooze" } }, { "sky", new[] { "Blueberries", "Turnip" } }, { "blue", new[] { "Blueberries" } },
            { "indigo", new[] { "Blueberries", "Thistle" } }, { "purple", new[] { "Thistle" } }, { "magenta", new[] { "Raspberry", "Thistle" } },
            { "pink", new[] { "Raspberry", "Turnip" } }, { "brown", new[] { "Resin" } }, { "beige", new[] { "Turnip" } },
            { "grey", new[] { "Coal", "BoneFragments" } },
        };

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            foreach (var colour in DyePalette.All)
            {
                if (PrefabManager.Instance.GetPrefab(colour.ItemName)) continue;
                var config = new ItemConfig
                {
                    Name = "$overhaul_dyeitem_" + colour.ID, Description = "$overhaul_dye_desc",
                    CraftingStation = CraftingStations.Workbench, MinStationLevel = 1, Amount = 1,
                    Requirements = Recipe(colour), Icons = new[] { Icon(colour.Value, colour.ID) },
                };
                var item = new CustomItem(colour.ItemName, Source, config);
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_maxStackSize = 50; shared.m_weight = .1f; shared.m_teleportable = true;
                ItemManager.Instance.AddItem(item);
            }
        }

        static RequirementConfig[] Recipe(DyePalette.Colour c)
        {
            if (c.ID == "black") return new[] { new RequirementConfig("Coal", 2) };
            if (c.ID == "white") return new[] { new RequirementConfig("BoneFragments", 2) };
            // Light shade: one bone fragment more, dark shade: one coal more (added to the same entry when already there).
            var amounts = new Dictionary<string, int>();
            foreach (var i in Ingredients[c.Hue]) amounts[i] = (amounts.TryGetValue(i, out int n) ? n : 0) + (Ingredients[c.Hue].Length == 1 ? 2 : 1);
            if (c.Shade == 0) amounts["BoneFragments"] = (amounts.TryGetValue("BoneFragments", out int b) ? b : 0) + 1;
            if (c.Shade == 2) amounts["Coal"] = (amounts.TryGetValue("Coal", out int k) ? k : 0) + 1;
            return amounts.Select(x => new RequirementConfig(x.Key, x.Value)).ToArray();
        }

        // The dye icon in a colour (also the cost icon of the dyeing window).
        static Texture2D icon, mask;
        static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        internal static Sprite Icon(Color colour, string key)
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

        internal static int Count(Inventory inv, DyePalette.Colour c) => inv == null ? 0 : inv.CountItems("$overhaul_dyeitem_" + c.ID);
    }
}
