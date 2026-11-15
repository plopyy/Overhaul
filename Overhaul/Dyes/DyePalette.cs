using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The 50 dye colours, as codes: 16 hues with a light, normal and dark shade, then black and white. Nothing is drawn per
    // colour: the swatches, the dye icons and the dyed gear are all tinted from these codes at run time, so a colour is
    // changed or added here only. A colour is saved on gear by its ID (e.g. "light_blue"), never by its code.
    internal static class DyePalette
    {
        internal sealed class Colour
        {
            public string ID;          // saved on the gear, part of the dye item's prefab name
            public string Hue;         // hue ID (red...), or black / white
            public int Shade;          // 0 light, 1 normal, 2 dark (black and white: 1)
            public Color Value;
            public int Column, Row;    // place in the window's grid
            public string NameKey => "$overhaul_dye_c_" + ID;
            public string ItemName => "Overhaul_Dye_" + ID;
        }

        internal static readonly string[] Hues = { "red", "orange", "yellow", "lime", "green", "teal", "cyan", "sky", "blue", "indigo", "purple", "magenta", "pink", "brown", "beige", "grey" };
        // light, normal, dark per hue (hand-picked: computed shades looked muddy).
        static readonly string[,] Codes =
        {
            { "#E8736A", "#B8231C", "#6E1210" }, // red
            { "#F4A35E", "#D9661A", "#8A3B0C" }, // orange
            { "#F6DD72", "#E0B21A", "#8C6A08" }, // yellow
            { "#C2E07E", "#86B82C", "#4A6A12" }, // lime
            { "#7CC584", "#2C8A3A", "#14501E" }, // green
            { "#72C6B8", "#1C8C7C", "#0C5048" }, // teal
            { "#7ED4EA", "#1CA2C4", "#0C5E72" }, // cyan
            { "#ACD4F6", "#5AA2E4", "#2A6298" }, // sky
            { "#7E98E8", "#2A4CC0", "#142874" }, // blue
            { "#9484D6", "#46329E", "#24185C" }, // indigo
            { "#BE8ADE", "#7C30B0", "#461866" }, // purple
            { "#E482C6", "#B82E8E", "#6A1852" }, // magenta
            { "#F7BED6", "#E676A6", "#A6426C" }, // pink
            { "#B88A62", "#7A4A26", "#432812" }, // brown
            { "#EDE2C8", "#CBB386", "#8A744C" }, // beige
            { "#C4C4C4", "#848484", "#484848" }, // grey
        };
        const string Black = "#141414", White = "#F2F0EB";

        internal static readonly List<Colour> All = new List<Colour>();
        static readonly Dictionary<string, Colour> byID = new Dictionary<string, Colour>();

        static DyePalette()
        {
            for (int h = 0; h < Hues.Length; h++)
                for (int s = 0; s < 3; s++)
                    Add((s == 0 ? "light_" : s == 2 ? "dark_" : "") + Hues[h], Hues[h], s, Codes[h, s], h % 8, (h / 8) * 3 + s);
            Add("black", "black", 1, Black, 0, 6);
            Add("white", "white", 1, White, 1, 6);
        }
        static void Add(string id, string hue, int shade, string code, int col, int row)
        {
            ColorUtility.TryParseHtmlString(code, out var value);
            var c = new Colour { ID = id, Hue = hue, Shade = shade, Value = value, Column = col, Row = row };
            All.Add(c); byID[id] = c;
        }

        internal static Colour Get(string id) => id != null && byID.TryGetValue(id, out var c) ? c : null;
    }
}
