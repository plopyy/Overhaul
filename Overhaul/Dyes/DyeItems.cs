using System.Collections.Generic;
using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The 50 dyes (Overhaul_Dye_<colour ID>), crafted at the workbench from game ingredients: each normal colour from
    // something of that colour, its light shade with bone fragments added, its dark shade with coal added; black from
    // coal, white from bone fragments. They share one picture, the white dye icon, tinted in their colour by code
    // (icon + mask: R where to colour, G its relief), so a new colour needs no new image.
    internal static class DyeItems
    {
        const string Source = "Resin";
        // Ingredient giving each hue's normal shade.
        static readonly Dictionary<string, string> Ingredient = new Dictionary<string, string>
        {
            { "red", "Raspberry" }, { "orange", "Carrot" }, { "yellow", "Dandelion" }, { "lime", "MushroomJotunPuffs" },
            { "green", "Guck" }, { "teal", "Ooze" }, { "cyan", "FreezeGland" }, { "sky", "Crystal" },
            { "blue", "Blueberries" }, { "indigo", "MushroomBlue" }, { "purple", "Thistle" }, { "magenta", "Onion" },
            { "pink", "Ruby" }, { "brown", "LeatherScraps" }, { "beige", "Turnip" }, { "grey", "Stone" },
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
            var reqs = new List<RequirementConfig> { new RequirementConfig(Ingredient[c.Hue], 2) };
            if (c.Shade == 0) reqs.Add(new RequirementConfig("BoneFragments", 1));
            if (c.Shade == 2) reqs.Add(new RequirementConfig("Coal", 1));
            return reqs.ToArray();
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
