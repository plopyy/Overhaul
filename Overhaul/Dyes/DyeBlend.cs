using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul.Dyes
{
    // The dye tint (same formula as the previews, Tools/3D/DyeWork/scripts/blend.py): the white version's grey is the
    // relief of the material (0.70 = its usual brightness). Light dyes: colour x relief. Dark dyes: the relief is added
    // around the colour instead (a product flattens a dark colour), so black stays deep and its grain still shows.
    // Highlights are rolled off smoothly, so white keeps its relief too.
    internal static class DyeBlend
    {
        // Dark: relief added around dark dyes, mostly downward (folds deepen, highlights rise a little only): a pure black
        // lifted its highlights to a washed dark grey with 0.09 both ways. Knee: roll-off kept high so white reaches white.
        const float GRef = .70f, Dark = .04f, Knee = .92f;

        static float Soft(float x) => x <= Knee ? x : Knee + (1 - Knee) * (float)Math.Tanh((x - Knee) / (1 - Knee));

        // relief: the zone's relief slider, a power on the material's grain (0 flat, 1 as painted, 2 stronger, never
        // crushed to black); raised, it also lifts a dark dye's highlights faster so a black shows its grain.
        internal static Color Tint(Color c, float grey, float relief = 1)
        {
            float s = Mathf.Pow(grey / GRef, relief), dark = 1 - Mathf.Max(c.r, Mathf.Max(c.g, c.b)); dark *= dark;
            float add = Dark * dark * (Mathf.Min(s - 1, 0) + (.35f + 5 * Mathf.Max(relief - 1, 0)) * Mathf.Max(s - 1, 0));
            return new Color(Soft(Mathf.Max(0, c.r * s + add)), Soft(Mathf.Max(0, c.g * s + add)), Soft(Mathf.Max(0, c.b * s + add)), 1);
        }

        // A dyed texture: the white version, each zone of the mask tinted with its colour (null = left as in the game,
        // which the white version holds outside the zones only, so an undyed zone takes the original texture: "original").
        internal static Texture2D Compose(Texture2D white, Texture2D mask, Color?[] colours, float[] reliefs, Texture original, string name)
        {
            var w = white.GetPixels32(); var m = mask.GetPixels32();
            Color32[] o = null;
            bool any = false; foreach (var c in colours) any |= c.HasValue;
            // Undyed zones come back from the game's own texture (the white version is white there).
            if (original && original.width == white.width && original.height == white.height) o = Read(original);
            var result = new Color32[w.Length];
            for (int i = 0; i < w.Length; i++)
            {
                var px = (Color)w[i];
                var mk = m[i];
                bool zoned = mk.r > 0 || mk.g > 0 || mk.b > 0 || mk.a > 0;
                if (zoned && o != null)
                {
                    // Start from the game's pixel where a zone is left undyed.
                    var g = (Color)o[i];
                    Color baseColour = px;
                    for (int z = 0; z < 4; z++)
                    {
                        float a = (z == 0 ? mk.r : z == 1 ? mk.g : z == 2 ? mk.b : mk.a) / 255f;
                        if (a <= 0) continue;
                        var target = z < colours.Length && colours[z].HasValue ? Tint(colours[z].Value, w[i].r / 255f, reliefs != null && z < reliefs.Length ? reliefs[z] : 1) : g;
                        baseColour = new Color(Mathf.Lerp(baseColour.r, target.r, a), Mathf.Lerp(baseColour.g, target.g, a), Mathf.Lerp(baseColour.b, target.b, a), px.a);
                    }
                    px = baseColour;
                }
                result[i] = px;
            }
            var t = new Texture2D(white.width, white.height, TextureFormat.RGBA32, true) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(result); t.Apply(true, !DyeVisuals.Debug);
            return t;
        }

        // A metal map for the zones' metal sliders (red: metallic, read with the material's _Metallic set to 1): the
        // game's metal (its map's red, or "plain" where it has none) raised in each zone toward 1 by the zone's amount.
        // Metal with a dark colour sends no light back (no diffuse part, its reflection tinted by the colour).
        internal static Texture2D MetalMap(Texture2D mask, float[] metals, Texture original, float plain, string name)
        {
            var m = mask.GetPixels32(); int width = mask.width, height = mask.height;
            // The game's map read and the new one stored with the same colour space, so the shader gets the same values.
            bool linear = !original || !original.isDataSRGB;
            Color32[] o = original ? Read(original, linear) : null; int ow = original ? original.width : 1, oh = original ? original.height : 1;
            var result = new Color32[m.Length];
            for (int i = 0; i < m.Length; i++)
            {
                float metal = plain, alpha = 1;
                if (o != null)
                {
                    int x = i % width * ow / width, y = i / width * oh / height; var p = o[y * ow + x];
                    metal = p.r / 255f * plain; alpha = p.a / 255f;
                }
                var mk = m[i];
                for (int z = 0; z < 4 && z < metals.Length; z++)
                {
                    float a = (z == 0 ? mk.r : z == 1 ? mk.g : z == 2 ? mk.b : mk.a) / 255f;
                    if (a > 0 && metals[z] > 0) metal = Mathf.Lerp(metal, Mathf.Lerp(metal, 1, metals[z]), a);
                }
                byte v = (byte)Mathf.RoundToInt(metal * 255);
                result[i] = new Color32(v, v, v, (byte)Mathf.RoundToInt(alpha * 255));
            }
            var t = new Texture2D(width, height, TextureFormat.RGBA32, true, linear) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(result); t.Apply(true, !DyeVisuals.Debug);
            return t;
        }

        // CPU copy of a game texture (not readable): through a render texture, in sRGB like the game draws a colour
        // texture, or linear for a data map (metal: read in sRGB its values would come back raised).
        static readonly Dictionary<Texture, Color32[]> read = new Dictionary<Texture, Color32[]>(), readLinear = new Dictionary<Texture, Color32[]>();
        internal static Color32[] Read(Texture source, bool linear = false)
        {
            var cache = linear ? readLinear : read;
            if (cache.TryGetValue(source, out var px) && px != null) return px;
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var old = RenderTexture.active;
            Graphics.Blit(source, rt); RenderTexture.active = rt;
            var t = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, linear);
            t.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); t.Apply();
            RenderTexture.active = old; RenderTexture.ReleaseTemporary(rt);
            px = t.GetPixels32(); UnityEngine.Object.Destroy(t);
            return cache[source] = px;
        }
    }
}
