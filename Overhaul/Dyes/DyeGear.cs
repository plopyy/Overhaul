using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The dyeable gear (Assets/Dyes, made by Tools/3D/DyeWork): per item its zones (1 to 4 materials, e.g. cloth, leather) and
    // per game texture of the item a zone mask (one channel per zone: R, G, B, A) and its "white" version (the zones in
    // white keeping their relief, the rest as in the game). Dyed textures are composed from these two and the colours.
    internal static class DyeGear
    {
        internal sealed class Texture
        {
            public string Source;      // name of the game texture the item draws with
            public bool Body;          // painted on the player's body (_ChestTex / _LegsTex), else on a worn mesh
            public Texture2D Mask, White;
        }
        internal sealed class Gear
        {
            public string Item;
            public string[] Zones;
            public List<Texture> Textures = new List<Texture>();
        }

        static Dictionary<string, Gear> gear;
        internal static IEnumerable<Gear> All { get { Load(); return gear.Values; } }
        internal static Gear Get(string prefab) { Load(); return prefab != null && gear.TryGetValue(prefab, out var g) ? g : null; }
        internal static Gear Of(ItemDrop.ItemData item) => item?.m_dropPrefab ? Get(item.m_dropPrefab.name) : null;

        static void Load()
        {
            if (gear != null) return;
            gear = new Dictionary<string, Gear>();
            var table = Text("gear.tsv");
            if (table == null) { Log.LogError("Dyes: gear table missing"); return; }
            foreach (var line in table.Split('\n'))
            {
                var p = line.Trim('\r').Split('\t');
                if (p.Length < 3) continue;
                var g = new Gear { Item = p[0], Zones = p[1].Split(',') };
                foreach (var t in p[2].Split(';'))
                {
                    var f = t.Split('|'); if (f.Length < 3) continue;
                    var mask = Image(p[0] + "." + f[1] + ".mask.png"); var white = Image(p[0] + "." + f[1] + ".white.png");
                    if (!mask || !white) { Log.LogError("Dyes: textures of " + p[0] + " / " + f[1] + " missing"); continue; }
                    g.Textures.Add(new Texture { Source = f[0], Body = f[2] == "body", Mask = mask, White = white });
                }
                gear[g.Item] = g;
            }
        }

        static Stream Resource(string name) => typeof(DyeGear).Assembly.GetManifestResourceStream("Overhaul.Assets.Dyes." + name);
        static string Text(string name)
        {
            using (var s = Resource(name)) { if (s == null) return null; using (var r = new StreamReader(s)) return r.ReadToEnd(); }
        }
        internal static Texture2D Image(string name, bool linear = false)
        {
            using (var s = Resource(name))
            {
                if (s == null) return null;
                var bytes = Utility.EmbeddedAssets.ReadAll(s);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { name = "Dye_" + name, filterMode = FilterMode.Point };
                return ImageConversion.LoadImage(t, bytes) ? t : null;
            }
        }
    }
}
