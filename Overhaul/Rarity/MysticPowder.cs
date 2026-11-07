using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Overhaul.Rarity
{
    // Mystic powder (Overhaul_MysticPowder): a material stacking to 5000. Put in the enchanting table's trophy slot instead of a
    // trophy, it enchants straight to the rarity the player picks, using up that rarity's cost ([MysticPowder] Cost of
    // RaritySystem.cfg). Creatures drop it (chance by rank, amount by biome, times the rank's multiplier) and the obliterator
    // turns enchanted gear into Cost of its rarity / DismantleDivisor.
    internal static class MysticPowder
    {
        internal const string Name = "Overhaul_MysticPowder";
        const string Source = "Resin";
        static Sprite icon;

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            var config = new ItemConfig { Name = "$overhaul_mystic_powder", Description = "$overhaul_mystic_powder_desc" };
            var sprite = Icon(); if (sprite) config.Icons = new[] { sprite };
            var custom = new CustomItem(Name, Source, config);
            Dress(custom.ItemPrefab);
            var shared = custom.ItemDrop.m_itemData.m_shared;
            shared.m_maxStackSize = 5000;
            shared.m_weight = 0f;
            shared.m_teleportable = true;
            ItemManager.Instance.AddItem(custom);
        }

        // The resin's pickup, physics and network stay; its look becomes our model (MysticPowder prefab of the
        // overhaul_mysticpowder bundle, OverhaulLab MysticPowderAuthoring), with a box collider fitting it.
        static AssetBundle bundle;
        static void Dress(GameObject prefab)
        {
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_mysticpowder");
            var model = bundle ? bundle.LoadAsset<GameObject>("MysticPowder") : null;
            if (!model) { Log.LogError("Mystic powder: MysticPowder missing from overhaul_mysticpowder"); return; }
            foreach (var lod in prefab.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lod);
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.gameObject == prefab) { Object.DestroyImmediate(renderer.GetComponent<MeshFilter>()); Object.DestroyImmediate(renderer); }
                else Object.DestroyImmediate(renderer.gameObject);
            }
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            var look = Object.Instantiate(model, prefab.transform, false); look.name = "model";
            int layer = prefab.layer; foreach (var t in look.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            // A plain box under the model: 0.5 m wide (the model's size), 0.12 m high, resting on its base.
            var box = prefab.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, .06f, 0f); box.size = new Vector3(.5f, .12f, .5f);
        }

        static Sprite Icon()
        {
            if (icon) return icon;
            using (var stream = typeof(MysticPowder).Assembly.GetManifestResourceStream("Overhaul.Assets.mystic_powder.png"))
            using (var memory = new System.IO.MemoryStream())
            {
                if (stream == null) { Log.LogError("Mystic powder: icon missing"); return null; }
                stream.CopyTo(memory);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, memory.ToArray())) return null;
                icon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
                icon.name = Name + "_icon";
                return icon;
            }
        }

        internal static bool Is(ItemDrop.ItemData item) =>
            item != null && (item.m_dropPrefab ? item.m_dropPrefab.name == Name : item.m_shared.m_name == "$overhaul_mystic_powder");

        // Powder needed to enchant to the rarity, 0 when the powder cannot reach it.
        internal static int Cost(RarityDef rarity) =>
            rarity != null && RarityConfig.Current.PowderCost.TryGetValue(rarity.Id, out int cost) ? cost : 0;

        // Powder given back by the obliterator for an enchanted item.
        internal static int Dismantle(ItemDrop.ItemData item)
        {
            int divisor = Math.Max(1, RarityConfig.Current.PowderDismantleDivisor);
            return Cost(ItemRarity.Of(item)) / divisor * Math.Max(1, item.m_stack);
        }

        internal static ItemDrop.ItemData Make(int amount)
        {
            var prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(Name) : null;
            if (!prefab || amount <= 0) return null;
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = amount;
            return item;
        }

        // Creature loot: the rank's chance, then the biome's amount times the rank's multiplier.
        internal static void Creature(string creature, Vector3 center)
        {
            var data = RarityConfig.Current;
            if (creature == null || !Leveling.LevelingConfig.Current.Monsters.TryGetValue(creature, out var rule)) return;
            if (!data.PowderBiomes.TryGetValue(rule.Biome ?? "", out var range)) return;
            float chance = data.PowderChance.TryGetValue(rule.Category ?? "", out float c) ? c : 0f;
            if (UnityEngine.Random.Range(0f, 100f) >= chance) return;
            float multiplier = data.PowderRankMultiplier.TryGetValue(rule.Category ?? "", out float m) ? m : 1f;
            int amount = Mathf.RoundToInt(UnityEngine.Random.Range(range.Min, range.Max + 1) * multiplier);
            var powder = Make(amount);
            if (powder != null) ItemDrop.DropItem(powder, amount, center + Vector3.up * .5f, Quaternion.identity);
        }
    }
}
