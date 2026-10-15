using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Rarity;
using UnityEngine;

namespace Overhaul.Magic
{
    // Eitr from food, read from Foods.cfg (the server's file is the reference, sent to every player): every food without
    // eitr of its own gives EitrPercent % of its health + stamina as eitr, and eitr-oriented dishes (one per biome from
    // the Black Forest to the Plains) lead to the Mistlands' eitr dishes. Food values live in the item prefabs' shared
    // data, which every item of that kind uses, so they are set there whenever the rules change.
    internal static class EitrFoods
    {
        internal sealed class Food
        {
            public string Id, NameKey, DescriptionKey, Model, Station;
            public int StationLevel, Amount;
            public (string item, int amount)[] Ingredients;
            public float Health, Stamina, Eitr, Duration, Regen;
            public string Prefab => "Overhaul_" + Id;
        }
        internal sealed class FoodData
        {
            public string Text = "";
            public float EitrPercent = 15;
            public readonly List<Food> Foods = new List<Food>();
        }

        internal const string FileName = "Foods.cfg";
        const string RequestRpc = "Overhaul_FoodsRequest", ConfigRpc = "Overhaul_FoodsConfig";
        static readonly Color EitrTint = new Color(.45f, .7f, 1f);
        internal static FoodData Local, Current = new FoodData();

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(EitrFoods).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("Foods.cfg unreadable: " + e.Message); }
            PrefabManager.OnVanillaPrefabsAvailable += Register;
        }
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Use(FoodData data) { Current = data; Apply(); }

        internal static FoodData Parse(string text)
        {
            var data = new FoodData { Text = text };
            foreach (var section in RarityConfig.Sections(text))
            {
                var v = section.Value;
                if (section.Key.Equals("Settings", StringComparison.OrdinalIgnoreCase)) { data.EitrPercent = Mathf.Max(0, RarityConfig.Float(v, "EitrPercent", 15)); continue; }
                if (!section.Key.StartsWith("Food_", StringComparison.OrdinalIgnoreCase)) { Utility.Log.LogWarning("Foods.cfg: unknown section [" + section.Key + "]"); continue; }
                string id = section.Key.Substring("Food_".Length);
                data.Foods.Add(new Food
                {
                    Id = id,
                    NameKey = RarityConfig.Get(v, "Name", "overhaul_food_" + id.ToLowerInvariant()),
                    DescriptionKey = RarityConfig.Get(v, "Description", "overhaul_food_" + id.ToLowerInvariant() + "_desc"),
                    Model = RarityConfig.Get(v, "Model", "CarrotSoup"),
                    Station = RarityConfig.Get(v, "Station", "piece_cauldron"),
                    StationLevel = Mathf.Max(1, (int)RarityConfig.Float(v, "StationLevel", 1)),
                    Amount = Mathf.Max(1, (int)RarityConfig.Float(v, "Amount", 1)),
                    Ingredients = RarityConfig.List(RarityConfig.Get(v, "Ingredients", "")).Select(i => i.Split(':')).Select(p => (p[0].Trim(), p.Length > 1 ? Mathf.Max(1, (int)RarityConfig.ParseFloat(p[1], 1)) : 1)).ToArray(),
                    Health = RarityConfig.Float(v, "Health", 0),
                    Stamina = RarityConfig.Float(v, "Stamina", 0),
                    Eitr = RarityConfig.Float(v, "Eitr", 0),
                    Duration = RarityConfig.Float(v, "Duration", 1200),
                    Regen = RarityConfig.Float(v, "Regen", 1),
                });
            }
            return data;
        }

        // ---------------------------------------------------------------- dishes
        // Made once from the local file: a clone of the model dish with an eitr-blue icon and its recipe.
        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            foreach (var food in (Local ?? Current).Foods)
            {
                if (PrefabManager.Instance.GetPrefab(food.Prefab)) continue;
                var model = PrefabManager.Instance.GetPrefab(food.Model)?.GetComponent<ItemDrop>();
                if (!model) { Utility.Log.LogWarning("Foods.cfg: [Food_" + food.Id + "] model " + food.Model + " not found"); continue; }
                var config = new ItemConfig { CraftingStation = food.Station, MinStationLevel = food.StationLevel, Amount = food.Amount,
                    Requirements = food.Ingredients.Select(i => new RequirementConfig(i.item, i.amount)).ToArray() };
                var item = new CustomItem(food.Prefab, food.Model, config);
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_name = "$" + food.NameKey; shared.m_description = "$" + food.DescriptionKey;
                var icons = shared.m_icons?.Select(Tinted).Where(s => s).ToArray();
                if (icons != null && icons.Length > 0) shared.m_icons = icons;
                ItemManager.Instance.AddItem(item);
            }
        }

        // The model's icon tinted eitr blue (keeps its shading); null where textures cannot be read (dedicated server).
        static Sprite Tinted(Sprite source)
        {
            if (!source) return null;
            try
            {
                var r = source.textureRect;
                var rt = RenderTexture.GetTemporary(source.texture.width, source.texture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source.texture, rt);
                var previous = RenderTexture.active; RenderTexture.active = rt;
                var copy = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
                var px = copy.GetPixels();
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i]; float l = c.r * .3f + c.g * .59f + c.b * .11f;
                    var blue = new Color(l * EitrTint.r * 1.25f, l * EitrTint.g * 1.25f, l * EitrTint.b * 1.35f, c.a);
                    px[i] = Color.Lerp(c, blue, .6f);
                }
                copy.SetPixels(px); copy.Apply();
                return Sprite.Create(copy, new Rect(0, 0, r.width, r.height), new Vector2(.5f, .5f), source.pixelsPerUnit);
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- food values
        // Foods that had no eitr of their own before this rule touched them.
        static readonly HashSet<ItemDrop.ItemData.SharedData> NoEitr = new HashSet<ItemDrop.ItemData.SharedData>();
        static void Apply()
        {
            var db = ObjectDB.instance;
            if (!db || db.m_items == null) return;
            var dishes = Current.Foods.ToDictionary(f => f.Prefab);
            foreach (var go in db.m_items)
            {
                var shared = go ? go.GetComponent<ItemDrop>()?.m_itemData.m_shared : null;
                if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (dishes.TryGetValue(go.name, out var food))
                {
                    shared.m_food = food.Health; shared.m_foodStamina = food.Stamina; shared.m_foodEitr = food.Eitr;
                    shared.m_foodBurnTime = food.Duration; shared.m_foodRegen = food.Regen;
                    continue;
                }
                if (shared.m_food + shared.m_foodStamina <= 0) continue;
                if (shared.m_foodEitr <= 0) NoEitr.Add(shared);
                if (NoEitr.Contains(shared)) shared.m_foodEitr = Mathf.Round((shared.m_food + shared.m_foodStamina) * Current.EitrPercent / 100f);
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        static class Awake { [HarmonyPriority(Priority.Last)] static void Postfix() => Apply(); }
        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        static class Copy { [HarmonyPriority(Priority.Last)] static void Postfix() => Apply(); }

        // ---------------------------------------------------------------- server rules
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        static class Connection
        {
            static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register(RequestRpc, rpc => rpc.Invoke(ConfigRpc, Current.Text));
                else peer.m_rpc.Register<string>(ConfigRpc, (rpc, text) =>
                {
                    try { Use(Parse(text)); Utility.Log.LogInfo("Foods received from the server"); }
                    catch (Exception e) { Utility.Log.LogWarning("Foods from the server rejected: " + e.Message); }
                });
            }
        }
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        static class Spawned
        {
            static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !ZNet.instance) return;
                if (ZNet.instance.IsServer()) { Use(Local ?? Current); return; }
                ZNet.instance.GetServerRPC()?.Invoke(RequestRpc);
            }
        }
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        static class LocalRules { static void Prefix() { if (Local != null) Use(Local); } }
    }
}
