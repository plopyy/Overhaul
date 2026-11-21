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
    // on every player who sees it. The owner publishes the dyes of the slots it shows (helmet, chest, legs, cape, belt or
    // other utility item; cosmetic slots first, as the visuals do) in its ZDO; every client then tints those pieces on that player.
    // Pieces are tinted through material property blocks (worn meshes) or the body material's _ChestTex / _LegsTex
    // (armour painted on the skin), with textures composed once per (item, colours) and kept.
    internal static class DyeVisuals
    {
        internal const string DataKey = "overhaul_dye";
        internal enum Slot { Helmet, Chest, Legs, Cape, Utility }
        internal const int SlotCount = 5;
        static readonly string[] ZdoKeys = { "overhaul_dye_helmet", "overhaul_dye_chest", "overhaul_dye_legs", "overhaul_dye_cape", "overhaul_dye_utility" };
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
            // Cosmetic slots exist for the four armour pieces only.
            var cosmetic = slot == Slot.Utility ? null : EquipmentAndQuickSlots.CosmeticEquipment.Get(p, (int)slot);
            if (cosmetic != null) return cosmetic;
            switch (slot)
            {
                case Slot.Helmet: return p.m_helmetItem;
                case Slot.Chest: return p.m_chestItem;
                case Slot.Legs: return p.m_legItem;
                case Slot.Utility: return p.m_utilityItem;
                default: return p.m_shoulderItem;
            }
        }
        internal static void Publish(Player p)
        {
            if (!p || !p.m_nview || !p.m_nview.IsValid() || !p.m_nview.IsOwner()) return;
            var zdo = p.m_nview.GetZDO();
            for (int i = 0; i < SlotCount; i++)
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
        sealed class State { public readonly Applied[] Slots = Enumerable.Range(0, SlotCount).Select(_ => new Applied()).ToArray(); }
        static readonly ConditionalWeakTable<VisEquipment, State> States = new ConditionalWeakTable<VisEquipment, State>();

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        static class Show
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(VisEquipment __instance)
            {
                string[] wanted = null;
                ZDO zdo = null;
                if (__instance.m_isArmorStand) { wanted = StandDyes(__instance); if (wanted == null) return; }
                else
                {
                    if (!__instance.m_isPlayer) return;
                    zdo = __instance.m_nview ? __instance.m_nview.GetZDO() : null;
                    if (zdo == null && !Overrides.TryGetValue(__instance, out wanted)) return;
                }
                var state = States.GetOrCreateValue(__instance);
                for (int i = 0; i < SlotCount; i++)
                {
                    string value = zdo != null ? zdo.GetString(ZdoHashes[i], "") : i < wanted.Length ? wanted[i] ?? "" : "";
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

        // Armour stands: the pieces on it keep their item data (dyes included) in the stand's ZDO, one set per slot
        // (ItemDrop.SaveToZDO with the slot index); read again when the ZDO changes. "<prefab>:<ids>" per slot.
        sealed class StandCache { public uint Revision = uint.MaxValue; public string[] Values; }
        static readonly ConditionalWeakTable<VisEquipment, StandCache> standCache = new ConditionalWeakTable<VisEquipment, StandCache>();
        static string[] StandDyes(VisEquipment v)
        {
            var stand = v.GetComponentInParent<ArmorStand>();
            var zdo = stand && stand.m_nview ? stand.m_nview.GetZDO() : null;
            if (zdo == null || !ObjectDB.instance) return null;
            var cache = standCache.GetOrCreateValue(v);
            if (cache.Values != null && cache.Revision == zdo.DataRevision) return cache.Values;
            var values = new string[SlotCount];
            for (int i = 0; i < stand.m_slots.Count; i++)
            {
                var visSlot = stand.m_slots[i].m_slot;
                int slot = visSlot == VisSlot.Helmet ? 0 : visSlot == VisSlot.Chest ? 1 : visSlot == VisSlot.Legs ? 2 : visSlot == VisSlot.Shoulder ? 3 : visSlot == VisSlot.Utility ? 4 : -1;
                if (slot < 0) continue;
                int hash = zdo.GetInt(ArmorStand.s_itemKeyHashes[i], 0);
                var prefab = hash != 0 ? ObjectDB.instance.GetItemPrefab(hash) : null;
                if (!prefab || DyeGear.Get(prefab.name) == null) continue;
                var data = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); data.m_dropPrefab = prefab;
                ItemDrop.LoadFromZDO(data, zdo, i);
                values[slot] = Encode(data);
            }
            cache.Revision = zdo.DataRevision; cache.Values = values;
            return values;
        }

        static object Instances(VisEquipment v, Slot s) =>
            s == Slot.Helmet ? (object)v.m_helmetItemInstance : s == Slot.Chest ? v.m_chestItemInstances : s == Slot.Legs ? (object)v.m_legItemInstances
            : s == Slot.Utility ? v.m_utilityItemInstances : v.m_shoulderItemInstances;
        static int Hash(VisEquipment v, Slot s) =>
            s == Slot.Helmet ? v.m_currentHelmetItemHash : s == Slot.Chest ? v.m_currentChestItemHash : s == Slot.Legs ? v.m_currentLegItemHash
            : s == Slot.Utility ? v.m_currentUtilityItemHash : v.m_currentShoulderItemHash;
        static IEnumerable<GameObject> Objects(VisEquipment v, Slot s)
        {
            if (s == Slot.Helmet) { if (v.m_helmetItemInstance) yield return v.m_helmetItemInstance; yield break; }
            var list = s == Slot.Chest ? v.m_chestItemInstances : s == Slot.Legs ? v.m_legItemInstances : s == Slot.Utility ? v.m_utilityItemInstances : v.m_shoulderItemInstances;
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
            var metals = new float[gear.Zones.Length];
            for (int z = 0; z < colours.Length; z++) { colours[z] = z < ids.Length ? DyePalette.Value(ids[z]) : null; reliefs[z] = z < ids.Length ? DyePalette.Relief(ids[z]) : 1; metals[z] = colours[z].HasValue && z < ids.Length ? DyePalette.Metal(ids[z]) : 0; }
            bool metal = metals.Any(g => g > .005f);
            string key = string.Join(",", ids);
            foreach (var tex in gear.Textures)
            {
                if (tex.Body)
                {
                    var property = slot == Slot.Legs ? LegsTex : ChestTex;
                    var original = item.m_itemData.m_shared.m_armorMaterial ? item.m_itemData.m_shared.m_armorMaterial.GetTexture(property) : null;
                    var dyedTex = Dyed(v, slot, gear, tex, key, colours, reliefs, original);
                    if (v.m_bodyModel && v.m_bodyModel.material.HasProperty(property)) { v.m_bodyModel.material.SetTexture(property, dyedTex); BodyMetal(v, slot, item, gear, tex, key, metal ? metals : null); dyed++; }
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
                        Block(r, MainTex, Dyed(v, slot, gear, tex, key, colours, reliefs, m.mainTexture)); dyed++;
                        // Metal sliders: a copy of the material reading its metal from a map made for the zones.
                        if (metal && m.HasProperty(MetalMapID))
                        {
                            bool hasMap = m.IsKeywordEnabled(MetalKeyword) && m.GetTexture(MetalMapID);
                            float plain = m.HasProperty(Metallic) ? m.GetFloat(Metallic) : 0;
                            var map = Metal(gear, tex, key + "/metal" + (hasMap ? m.GetTexture(MetalMapID).name : plain.ToString("0.##")), metals, hasMap ? m.GetTexture(MetalMapID) : null, plain);
                            MetalMaterial(r, m, 1);
                            Block(r, MetalMapID, map);
                        }
                        else
                        {
                            // Back to the game's metal: the copy dropped, a map set earlier in the block replaced by its own.
                            Unmetal(r);
                            if (m.HasProperty(MetalMapID) && m.GetTexture(MetalMapID)) Block(r, MetalMapID, m.GetTexture(MetalMapID));
                        }
                    }
                }
            }
            if (Debug) Log.LogInfo("Dyes: " + slot + " " + value + " -> " + dyed + " renderers/textures, objects " + string.Join(",", Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true)).Select(r => r.name + "[" + (r.sharedMaterial && r.sharedMaterial.mainTexture ? r.sharedMaterial.mainTexture.name : "-") + "]")));
        }
        internal static bool Debug = false;   // logs each tint and writes the dyed textures to the game test folder
        static readonly int Metallic = Shader.PropertyToID("_Metallic"), MetalMapID = Shader.PropertyToID("_MetallicGlossMap");
        static readonly int ChestMetal = Shader.PropertyToID("_ChestMetal"), LegsMetal = Shader.PropertyToID("_LegsMetal");
        const string MetalKeyword = "_METALLICGLOSSMAP";
        // Renderers switched to a copy of their material reading the metal from a map (metal slider), with the game's
        // material to go back to.
        sealed class Metalled { public Material Original, Copy; }
        static readonly ConditionalWeakTable<Renderer, Metalled> metalled = new ConditionalWeakTable<Renderer, Metalled>();
        static Material Original(Renderer r) => metalled.TryGetValue(r, out var s) && s.Copy && r.sharedMaterial == s.Copy ? s.Original : r.sharedMaterial;
        // scale: the copy's _Metallic (1: the map holds the game's metal, its _Metallic baked in).
        static void MetalMaterial(Renderer r, Material original, float scale)
        {
            if (metalled.TryGetValue(r, out var s) && s.Copy && r.sharedMaterial == s.Copy) return;
            var copy = new Material(original) { name = original.name + " (metal)" };
            copy.EnableKeyword(MetalKeyword); copy.SetFloat(Metallic, scale);
            metalled.Remove(r); metalled.Add(r, new Metalled { Original = original, Copy = copy });
            r.sharedMaterial = copy;
        }
        static void Unmetal(Renderer r)
        {
            if (!metalled.TryGetValue(r, out var s)) return;
            if (s.Copy && r.sharedMaterial == s.Copy) r.sharedMaterial = s.Original;
            if (s.Copy) UnityEngine.Object.Destroy(s.Copy);
            metalled.Remove(r);
        }
        // The body (chest and legs textures on the player's skin): its metal map for the slot, made from the piece's own
        // (none: no metal), or the piece's own map back.
        static void BodyMetal(VisEquipment v, Slot slot, ItemDrop item, DyeGear.Gear gear, DyeGear.Texture tex, string key, float[] metals)
        {
            var body = v.m_bodyModel ? v.m_bodyModel.material : null;
            var property = slot == Slot.Legs ? LegsMetal : ChestMetal;
            if (!body || !body.HasProperty(property)) return;
            var armour = item ? item.m_itemData.m_shared.m_armorMaterial : null;
            var own = armour && armour.HasProperty(property) ? armour.GetTexture(property) : null;
            body.SetTexture(property, metals == null ? own : Metal(gear, tex, key + "/bodymetal", metals, own, own ? 1 : 0));
        }
        static readonly Dictionary<string, Texture2D> metalCache = new Dictionary<string, Texture2D>();
        static Texture2D Metal(DyeGear.Gear gear, DyeGear.Texture tex, string key, float[] metals, Texture original, float plain)
        {
            string id = gear.Item + "/" + tex.Source + "/" + key;
            if (metalCache.TryGetValue(id, out var t) && t) return t;
            return metalCache[id] = DyeBlend.MetalMap(tex.Mask, metals, original, plain, "Metal_" + id);
        }

        static void Block(Renderer r, int property, Texture texture)
        {
            var block = new MaterialPropertyBlock(); r.GetPropertyBlock(block); block.SetTexture(property, texture); r.SetPropertyBlock(block);
        }
        // Back to the game's look: property blocks cleared, the body painted again with the piece's own texture.
        static void Restore(VisEquipment v, Slot slot, int hash)
        {
            foreach (var r in Objects(v, slot).SelectMany(o => o.GetComponentsInChildren<Renderer>(true))) { Unmetal(r); r.SetPropertyBlock(null); }
            if ((slot != Slot.Chest && slot != Slot.Legs) || !v.m_bodyModel || hash == 0) return;
            var go = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(hash) : null;
            var mat = go ? go.GetComponent<ItemDrop>().m_itemData.m_shared.m_armorMaterial : null;
            var property = slot == Slot.Legs ? LegsTex : ChestTex;
            if (mat && mat.HasProperty(property) && v.m_bodyModel.material.HasProperty(property)) v.m_bodyModel.material.SetTexture(property, mat.GetTexture(property));
            var metalProperty = slot == Slot.Legs ? LegsMetal : ChestMetal;
            if (mat && v.m_bodyModel.material.HasProperty(metalProperty)) v.m_bodyModel.material.SetTexture(metalProperty, mat.HasProperty(metalProperty) ? mat.GetTexture(metalProperty) : null);
        }

        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
        // The dyeing window's character (a VisEquipment with overrides) shows colours being dragged on the sliders: each of
        // its textures is composed again in one slot, the former one destroyed, so trying colours keeps nothing.
        static readonly Dictionary<string, KeyValuePair<string, Texture2D>> trial = new Dictionary<string, KeyValuePair<string, Texture2D>>();
        static Texture2D Dyed(VisEquipment v, Slot slot, DyeGear.Gear gear, DyeGear.Texture tex, string key, Color?[] colours, float[] reliefs, Texture original)
        {
            string id = gear.Item + "/" + tex.Source + "/" + key;
            if (Overrides.TryGetValue(v, out _) && !cache.ContainsKey(id))
            {
                string place = (int)slot + "/" + tex.Source;
                if (trial.TryGetValue(place, out var kept) && kept.Key == id && kept.Value) return kept.Value;
                if (kept.Value) UnityEngine.Object.Destroy(kept.Value);
                var made2 = DyeBlend.Compose(tex.White, tex.Mask, colours, reliefs, original, "Trial_" + id);
                trial[place] = new KeyValuePair<string, Texture2D>(id, made2);
                return made2;
            }
            if (cache.TryGetValue(id, out var t) && t) return t;
            var made = DyeBlend.Compose(tex.White, tex.Mask, colours, reliefs, original, "Dyed_" + id);
            var dump = System.Environment.GetEnvironmentVariable("OVERHAUL_TEST_OUT");
            if (Debug && !string.IsNullOrEmpty(dump))
                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, ("dyed-" + id).Replace('/', '_').Replace(',', '_').Replace(' ', '_') + ".png"), ImageConversion.EncodeToPNG(made)); } catch { }
            return cache[id] = made;
        }
    }
}
