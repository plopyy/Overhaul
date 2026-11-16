using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dyes
{
    // Dyes on gear: saved on the item (custom data "overhaul_dye" = colour IDs per zone, "-" = left as in the game) and shown
    // on every player who sees it. The owner publishes the dyes of the four armour slots it shows (helmet, chest, legs,
    // cape; cosmetic slots first, as the visuals do) in its ZDO; every client then tints those pieces on that player.
    // Pieces are tinted through material property blocks (worn meshes) or the body material's _ChestTex / _LegsTex
    // (armour painted on the skin), with textures composed once per (item, colours) and kept.
    internal static class DyeVisuals
    {
        internal const string DataKey = "overhaul_dye";
        internal enum Slot { Helmet, Chest, Legs, Cape }
        static readonly string[] ZdoKeys = { "overhaul_dye_helmet", "overhaul_dye_chest", "overhaul_dye_legs", "overhaul_dye_cape" };
        static readonly int[] ZdoHashes = ZdoKeys.Select(k => k.GetStableHashCode()).ToArray();
        static readonly int MainTex = Shader.PropertyToID("_MainTex"), ChestTex = Shader.PropertyToID("_ChestTex"), LegsTex = Shader.PropertyToID("_LegsTex");

        // ---------------------------------------------------------------- data on the item
        internal static string[] Read(ItemDrop.ItemData item, int zones)
        {
            var result = new string[zones];
            if (item != null && item.m_customData != null && item.m_customData.TryGetValue(DataKey, out var value))
            {
                var p = value.Split(',');
                for (int i = 0; i < zones && i < p.Length; i++) result[i] = DyePalette.Value(p[i]) != null ? p[i] : null;
            }
            return result;
        }
        internal static void Write(ItemDrop.ItemData item, string[] ids)
        {
            if (ids == null || ids.All(i => i == null)) item.m_customData.Remove(DataKey);
            else item.m_customData[DataKey] = string.Join(",", ids.Select(i => i ?? "-"));
        }
        static string Encode(ItemDrop.ItemData item)
        {
            var gear = DyeGear.Of(item);
            if (gear == null || item.m_customData == null || !item.m_customData.TryGetValue(DataKey, out var value) || string.IsNullOrEmpty(value)) return "";
            return item.m_dropPrefab.name + ":" + value;
        }

        // ---------------------------------------------------------------- publishing (owner)
        // The piece shown in each slot: the cosmetic one when set, else the equipped one.
        static ItemDrop.ItemData Shown(Player p, Slot slot)
        {
            var cosmetic = EquipmentAndQuickSlots.CosmeticEquipment.Get(p, (int)slot);
            if (cosmetic != null) return cosmetic;
            switch (slot)
            {
                case Slot.Helmet: return p.m_helmetItem;
                case Slot.Chest: return p.m_chestItem;
                case Slot.Legs: return p.m_legItem;
                default: return p.m_shoulderItem;
            }
        }
        internal static void Publish(Player p)
        {
            if (!p || !p.m_nview || !p.m_nview.IsValid() || !p.m_nview.IsOwner()) return;
            var zdo = p.m_nview.GetZDO();
            for (int i = 0; i < 4; i++)
            {
                var value = Encode(Shown(p, (Slot)i));
                if (zdo.GetString(ZdoHashes[i], "") != value) zdo.Set(ZdoHashes[i], value);
            }
        }

        [HarmonyPatch(typeof(Humanoid), "SetupVisEquipment")]
        static class PublishOnVisuals
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(Humanoid __instance) { if (__instance is Player p) Publish(p); }
        }
        [HarmonyPatch(typeof(Player), "OnInventoryChanged")]
        static class PublishOnInventory { static void Postfix(Player __instance) { if (!__instance.m_isLoading) Publish(__instance); } }

        // ---------------------------------------------------------------- showing (every client)
        // A visual without a ZDO (the dyeing window's preview) takes its dyes from here: "<prefab>:<ids>" per slot.
        internal static readonly ConditionalWeakTable<VisEquipment, string[]> Overrides = new ConditionalWeakTable<VisEquipment, string[]>();

        sealed class Applied { public string Value = ""; public object Instances; public int Hash; }
        sealed class State { public readonly Applied[] Slots = { new Applied(), new Applied(), new Applied(), new Applied() }; }
        static readonly ConditionalWeakTable<VisEquipment, State> States = new ConditionalWeakTable<VisEquipment, State>();

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        static class Show
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(VisEquipment __instance)
            {
                if (!__instance.m_isPlayer || __instance.m_isArmorStand) return;
                string[] wanted = null;
                var zdo = __instance.m_nview ? __instance.m_nview.GetZDO() : null;
                if (zdo == null && !Overrides.TryGetValue(__instance, out wanted)) return;
                var state = States.GetOrCreateValue(__instance);
                for (int i = 0; i < 4; i++)
                {
                    string value = zdo != null ? zdo.GetString(ZdoHashes[i], "") : wanted[i] ?? "";
                    object instances = Instances(__instance, (Slot)i);
                    int hash = Hash(__instance, (Slot)i);
                    var applied = state.Slots[i];
                    if (applied.Value == value && ReferenceEquals(applied.Instances, instances) && applied.Hash == hash) continue;
                    bool hadDye = applied.Value.Length > 0;
                    applied.Value = value; applied.Instances = instances; applied.Hash = hash;
                    Apply(__instance, (Slot)i, value, hadDye);
                }
            }
        }

        static object Instances(VisEquipment v, Slot s) =>
            s == Slot.Helmet ? (object)v.m_helmetItemInstance : s == Slot.Chest ? v.m_chestItemInstances : s == Slot.Legs ? (object)v.m_legItemInstances : v.m_shoulderItemInstances;
        static int Hash(VisEquipment v, Slot s) =>
            s == Slot.Helmet ? v.m_currentHelmetItemHash : s == Slot.Chest ? v.m_currentChestItemHash : s == Slot.Legs ? v.m_currentLegItemHash : v.m_currentShoulderItemHash;
        static IEnumerable<GameObject> Objects(VisEquipment v, Slot s)
        {
            if (s == Slot.Helmet) { if (v.m_helmetItemInstance) yield return v.m_helmetItemInstance; yield break; }
            var list = s == Slot.Chest ? v.m_chestItemInstances : s == Slot.Legs ? v.m_legItemInstances : v.m_shoulderItemInstances;
            if (list != null) foreach (var o in list) if (o) yield return o;
        }

        // Tints the slot's piece with "<prefab>:<ids>" (empty = as in the game). The piece must be the one shown in the slot.
        static void Apply(VisEquipment v, Slot slot, string value, bool hadDye)
        {
            ItemDrop item = null; string[] ids = null; DyeGear.Gear gear = null;
            int hash = Hash(v, slot);
            if (value.Length > 0)
            {
                int colon = value.IndexOf(':');
                var prefab = colon > 0 ? value.Substring(0, colon) : null;
                if (prefab != null && prefab.GetStableHashCode() == hash)
                {
                    gear = DyeGear.Get(prefab);
                    var go = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab) : null; item = go ? go.GetComponent<ItemDrop>() : null;
                    ids = value.Substring(colon + 1).Split(',');
                }
            }
            if (gear == null || item == null)
            {
                if (hadDye) Restore(v, slot, hash);
                return;
            }
            int dyed = 0;
            var colours = new Color?[gear.Zones.Length];
            var reliefs = new float[gear.Zones.Length];
            var shines = new float[gear.Zones.Length];
            for (int z = 0; z < colours.Length; z++) { colours[z] = z < ids.Length ? DyePalette.Value(ids[z]) : null; reliefs[z] = z < ids.Length ? DyePalette.Relief(ids[z]) : 1; shines[z] = colours[z].HasValue && z < ids.Length ? DyePalette.Shine(ids[z]) : 1; }
            bool shine = shines.Any(g => Mathf.Abs(g - 1) > .005f);
            string key = string.Join(",", ids);
            foreach (var tex in gear.Textures)
            {
                if (tex.Body)
                {
                    var property = slot == Slot.Legs ? LegsTex : ChestTex;
                    var original = item.m_itemData.m_shared.m_armorMaterial ? item.m_itemData.m_shared.m_armorMaterial.GetTexture(property) : null;
                    var dyedTex = Dyed(v, slot, gear, tex, key, colours, reliefs, original);
                    if (v.m_bodyModel && v.m_bodyModel.material.HasProperty(property)) { v.m_bodyModel.material.SetTexture(property, dyedTex); BodyShine(v, slot, Coverage(tex.Mask, shines)); dyed++; }
                    // Worn parts drawn with the player shader (the leather tunic's shorts) carry the same texture.
                    foreach (var r in Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true)))
                        foreach (var m in r.sharedMaterials) if (m && m.HasProperty(property) && m.GetTexture(property) && m.GetTexture(property).name == tex.Source) { Block(r, property, dyedTex); dyed++; }
                }
                else
                {
                    foreach (var r in Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true)))
                    {
                        var m = Original(r);
                        if (!m || !m.HasProperty(MainTex) || !m.mainTexture || m.mainTexture.name != tex.Source) continue;
                        // Shine: the material switched to read its smoothness from the texture's alpha (not for a material
                        // cutting out by its alpha), the dyed texture carrying each zone's smoothness there.
                        if (shine && !m.IsKeywordEnabled("_ALPHATEST_ON") && m.HasProperty(Glossiness))
                        {
                            bool albedo = m.IsKeywordEnabled(AlbedoAlpha); float gloss = m.GetFloat(Glossiness);
                            var made = Dyed(v, slot, gear, tex, key + "/shine" + gloss.ToString("0.###") + (albedo ? "a" : ""), colours, reliefs, m.mainTexture, shines, gloss, albedo);
                            ShineMaterial(r, m);
                            Block(r, MainTex, made); dyed++;
                        }
                        else { Unshine(r); Block(r, MainTex, Dyed(v, slot, gear, tex, key, colours, reliefs, m.mainTexture)); dyed++; }
                    }
                }
            }
            if (Debug) Log.LogInfo("Dyes: " + slot + " " + value + " -> " + dyed + " renderers/textures, objects " + string.Join(",", Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true)).Select(r => r.name + "[" + (r.sharedMaterial && r.sharedMaterial.mainTexture ? r.sharedMaterial.mainTexture.name : "-") + "]")));
        }
        internal static bool Debug = false;   // logs each tint and writes the dyed textures to the game test folder
        static readonly int Glossiness = Shader.PropertyToID("_Glossiness");
        const string AlbedoAlpha = "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A";
        // Renderers switched to a copy of their material reading the smoothness from the texture alpha (shine slider),
        // with the game's material to go back to.
        sealed class Shined { public Material Original, Copy; }
        static readonly ConditionalWeakTable<Renderer, Shined> shined = new ConditionalWeakTable<Renderer, Shined>();
        static Material Original(Renderer r) => shined.TryGetValue(r, out var s) && s.Copy && r.sharedMaterial == s.Copy ? s.Original : r.sharedMaterial;
        static void ShineMaterial(Renderer r, Material original)
        {
            if (shined.TryGetValue(r, out var s) && s.Copy && r.sharedMaterial == s.Copy) return;
            var copy = new Material(original) { name = original.name + " (shine)" };
            copy.EnableKeyword(AlbedoAlpha); copy.SetFloat(Glossiness, 1);
            shined.Remove(r); shined.Add(r, new Shined { Original = original, Copy = copy });
            r.sharedMaterial = copy;
        }
        static void Unshine(Renderer r)
        {
            if (!shined.TryGetValue(r, out var s)) return;
            if (s.Copy && r.sharedMaterial == s.Copy) r.sharedMaterial = s.Original;
            if (s.Copy) UnityEngine.Object.Destroy(s.Copy);
            shined.Remove(r);
        }
        // The body (chest and legs textures in one material, one smoothness): the shine of the zones it shows, weighted
        // by their surface on the texture; the slot last dyed sets it. Back to the game's value when undyed.
        static readonly ConditionalWeakTable<Material, float[]> bodyGloss = new ConditionalWeakTable<Material, float[]>();
        static void BodyShine(VisEquipment v, Slot slot, float factor)
        {
            var body = v.m_bodyModel ? v.m_bodyModel.material : null; if (!body || !body.HasProperty(Glossiness)) return;
            if (!bodyGloss.TryGetValue(body, out var g)) bodyGloss.Add(body, g = new[] { body.GetFloat(Glossiness) });
            body.SetFloat(Glossiness, Mathf.Clamp01(g[0] * factor));
        }
        static readonly Dictionary<Texture2D, float[]> coverage = new Dictionary<Texture2D, float[]>();
        static float Coverage(Texture2D mask, float[] shines)
        {
            if (!coverage.TryGetValue(mask, out var c))
            {
                c = new float[4];
                foreach (var p in mask.GetPixels32()) { c[0] += p.r; c[1] += p.g; c[2] += p.b; c[3] += p.a; }
                coverage[mask] = c;
            }
            float sum = 0, weighted = 0;
            for (int z = 0; z < 4 && z < shines.Length; z++) { sum += c[z]; weighted += c[z] * shines[z]; }
            return sum > 0 ? weighted / sum : 1;
        }

        static void Block(Renderer r, int property, Texture texture)
        {
            var block = new MaterialPropertyBlock(); r.GetPropertyBlock(block); block.SetTexture(property, texture); r.SetPropertyBlock(block);
        }
        // Back to the game's look: property blocks cleared, the body painted again with the piece's own texture.
        static void Restore(VisEquipment v, Slot slot, int hash)
        {
            foreach (var r in Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true))) { Unshine(r); r.SetPropertyBlock(null); }
            if ((slot == Slot.Chest || slot == Slot.Legs) && v.m_bodyModel) BodyShine(v, slot, 1);
            if ((slot != Slot.Chest && slot != Slot.Legs) || !v.m_bodyModel || hash == 0) return;
            var go = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(hash) : null;
            var mat = go ? go.GetComponent<ItemDrop>().m_itemData.m_shared.m_armorMaterial : null;
            var property = slot == Slot.Legs ? LegsTex : ChestTex;
            if (mat && mat.HasProperty(property) && v.m_bodyModel.material.HasProperty(property)) v.m_bodyModel.material.SetTexture(property, mat.GetTexture(property));
        }

        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
        // The dyeing window's character (a VisEquipment with overrides) shows colours being dragged on the sliders: each of
        // its textures is composed again in one slot, the former one destroyed, so trying colours keeps nothing.
        static readonly Dictionary<string, KeyValuePair<string, Texture2D>> trial = new Dictionary<string, KeyValuePair<string, Texture2D>>();
        static Texture2D Dyed(VisEquipment v, Slot slot, DyeGear.Gear gear, DyeGear.Texture tex, string key, Color?[] colours, float[] reliefs, Texture original, float[] shines = null, float glossBase = 0, bool albedoAlpha = false)
        {
            string id = gear.Item + "/" + tex.Source + "/" + key;
            if (Overrides.TryGetValue(v, out _) && !cache.ContainsKey(id))
            {
                string place = (int)slot + "/" + tex.Source;
                if (trial.TryGetValue(place, out var kept) && kept.Key == id && kept.Value) return kept.Value;
                if (kept.Value) UnityEngine.Object.Destroy(kept.Value);
                var made2 = DyeBlend.Compose(tex.White, tex.Mask, colours, reliefs, original, "Trial_" + id, shines, glossBase, albedoAlpha);
                trial[place] = new KeyValuePair<string, Texture2D>(id, made2);
                return made2;
            }
            if (cache.TryGetValue(id, out var t) && t) return t;
            var made = DyeBlend.Compose(tex.White, tex.Mask, colours, reliefs, original, "Dyed_" + id, shines, glossBase, albedoAlpha);
            var dump = System.Environment.GetEnvironmentVariable("OVERHAUL_TEST_OUT");
            if (Debug && !string.IsNullOrEmpty(dump))
                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, ("dyed-" + id).Replace('/', '_').Replace(',', '_').Replace(' ', '_') + ".png"), ImageConversion.EncodeToPNG(made)); } catch { }
            return cache[id] = made;
        }
    }
}
