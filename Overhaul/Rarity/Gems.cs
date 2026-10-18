using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The orange and yellow gems (catalysts: Burn and Shock), made like the game's red, green and blue ones: each is the red gem
    // (its pickup, weight, stack, physics and sparkle) wearing Overhaul's own model, material and icon from the overhaul_gems
    // bundle (OverhaulLab GemsAuthoring). They are found wherever the red gem is (chests, creatures, rocks...), each with the
    // red gem's own chance.
    internal static class Gems
    {
        internal static readonly string[] Names = { "GemstoneOrange", "GemstoneYellow" };
        const string Source = "GemstoneRed";
        static AssetBundle bundle;

        internal static void Initialize()
        {
            PrefabManager.OnVanillaPrefabsAvailable += Register;
            PrefabManager.OnPrefabsRegistered += AddToDrops;
        }

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_gems");
            foreach (var name in Names)
            {
                if (PrefabManager.Instance.GetPrefab(name)) continue;
                var model = bundle.LoadAsset<GameObject>(name);
                var icon = bundle.LoadAssetWithSubAssets<Sprite>(name + "_Icon").FirstOrDefault();
                if (!model) { Log.LogError("Gems: " + name + " missing from overhaul_gems"); continue; }
                string key = name.Substring("Gemstone".Length).ToLowerInvariant();
                var config = new ItemConfig { Name = "$item_gemstone_" + key, Description = "$item_gemstone_" + key + "_desc" };
                if (icon) config.Icons = new[] { icon };
                var custom = new CustomItem(name, Source, config);
                // The red gem's visible piece takes our model's mesh and material; its collider fits the new shape.
                var source = model.GetComponentInChildren<MeshFilter>(true); var sourceRenderer = model.GetComponentInChildren<MeshRenderer>(true);
                foreach (var filter in custom.ItemPrefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    filter.sharedMesh = source.sharedMesh;
                    var renderer = filter.GetComponent<MeshRenderer>(); if (renderer) renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                    var box = filter.GetComponent<BoxCollider>(); if (box) { box.center = source.sharedMesh.bounds.center; box.size = source.sharedMesh.bounds.size; }
                }
                ItemManager.Instance.AddItem(custom);
            }
        }

        // Every drop list holding the red gem gets the orange and yellow ones with the same settings (once per list).
        static void AddToDrops()
        {
            if (!ZNetScene.instance) return;
            var red = ZNetScene.instance.GetPrefab(Source);
            var gems = Names.Select(n => ZNetScene.instance.GetPrefab(n)).Where(g => g).ToArray();
            if (!red || gems.Length == 0) return;
            int lists = 0;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (!prefab) continue;
                foreach (var component in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (!component) continue;
                    if (component is CharacterDrop creature)
                    {
                        foreach (var drop in creature.m_drops.Where(d => d.m_prefab == red).ToList())
                            foreach (var gem in gems)
                                if (!creature.m_drops.Any(d => d.m_prefab == gem))
                                {
                                    creature.m_drops.Add(new CharacterDrop.Drop { m_prefab = gem, m_amountMin = drop.m_amountMin, m_amountMax = drop.m_amountMax, m_chance = drop.m_chance, m_onePerPlayer = drop.m_onePerPlayer, m_levelMultiplier = drop.m_levelMultiplier, m_dontScale = drop.m_dontScale });
                                    lists++;
                                }
                        continue;
                    }
                    foreach (var field in DropFields(component.GetType()))
                    {
                        if (field.FieldType == typeof(DropTable)) lists += Add(field.GetValue(component) as DropTable, red, gems);
                        else if (typeof(IEnumerable<DropTable>).IsAssignableFrom(field.FieldType) && field.GetValue(component) is IEnumerable<DropTable> tables)
                            foreach (var table in tables) lists += Add(table, red, gems);
                    }
                }
            }
            if (lists > 0) Log.LogInfo("Gems: orange and yellow gems added to " + lists + " drop lists holding the red gem");
        }

        // The fields of a component type holding drop tables, looked up once per type.
        static readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
        static FieldInfo[] DropFields(Type type)
        {
            if (fields.TryGetValue(type, out var found)) return found;
            return fields[type] = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(DropTable) || typeof(IEnumerable<DropTable>).IsAssignableFrom(f.FieldType)).ToArray();
        }

        static int Add(DropTable table, GameObject red, GameObject[] gems)
        {
            if (table?.m_drops == null) return 0;
            int added = 0;
            foreach (var drop in table.m_drops.Where(d => d.m_item == red).ToList())
                foreach (var gem in gems)
                    if (!table.m_drops.Any(d => d.m_item == gem))
                    {
                        table.m_drops.Add(new DropTable.DropData { m_item = gem, m_stackMin = drop.m_stackMin, m_stackMax = drop.m_stackMax, m_weight = drop.m_weight, m_dontScale = drop.m_dontScale });
                        added++;
                    }
            return added;
        }
    }
}
