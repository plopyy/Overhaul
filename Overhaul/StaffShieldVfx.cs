using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul
{
    internal static class StaffShieldVfx
    {
        private const string BundleFileName = "overhaul_shields";

        private sealed class ShieldDefinition
        {
            internal readonly string AssetName;
            internal readonly float Scale;

            internal ShieldDefinition(string assetName, float scale = 1f)
            {
                AssetName = assetName;
                Scale = scale;
            }
        }

        private static readonly Dictionary<string, ShieldDefinition> StaffShields =
            new Dictionary<string, ShieldDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                { "StaffFireball", new ShieldDefinition("vfx_Shield_Fire_01") },
                { "StaffClusterbomb", new ShieldDefinition("vfx_Shield_Fire_01") },
                { "StaffIceShards", new ShieldDefinition("vfx_Shield_Ice_01") },
                { "StaffFrostOrbs", new ShieldDefinition("vfx_Shield_Ice_01") },
                { "StaffLightning", new ShieldDefinition("vfx_Shield_Lightning_01") },
                { "StaffThunderBlood", new ShieldDefinition("vfx_Shield_Blood_01") },
                { "StaffGreenRoots", new ShieldDefinition("vfx_Shield_WindGreen_01") },
                { "StaffShield", new ShieldDefinition("vfx_Shield_Light_01") },
                { "StaffSkeleton", new ShieldDefinition("vfx_Shield_Void_01") },
                { "StaffSpiritCaller", new ShieldDefinition("vfx_Shield_Holy_01") },
                { "StaffOrbofAhri", new ShieldDefinition("vfx_Shield_Cosmic_01") }
            };

        private static readonly Dictionary<string, GameObject> LoadedPrefabs =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static AssetBundle bundle;
        private static GameObject activeVisual;
        private static bool loadAttempted;
        internal static Material GetAuraMaterial()
        {
            Initialize();
            if (!LoadedPrefabs.TryGetValue("vfx_Shield_Blood_01", out var prefab)) return null;
            foreach (var renderer in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
                if (renderer.sharedMaterial) return renderer.sharedMaterial;
            return null;
        }

        internal static void Initialize()
        {
            if (loadAttempted) return;

            loadAttempted = true;
            Log.LogInfo($"Unity runtime detected: {Application.unityVersion}");
            try { bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets." + BundleFileName); }
            catch (InvalidOperationException error)
            {
                Log.LogError(error.Message);
                return;
            }

            string[] assetPaths = bundle.GetAllAssetNames();
            foreach (ShieldDefinition definition in StaffShields.Values)
            {
                if (LoadedPrefabs.ContainsKey(definition.AssetName)) continue;

                string suffix = "/" + definition.AssetName.ToLowerInvariant() + ".prefab";
                string assetPath = Array.Find(assetPaths, name =>
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                GameObject prefab = string.IsNullOrEmpty(assetPath)
                    ? null
                    : bundle.LoadAsset<GameObject>(assetPath);
                if (prefab == null)
                {
                    Log.LogError($"Shield prefab '{definition.AssetName}' was not found in {BundleFileName}");
                    continue;
                }
                LoadedPrefabs.Add(definition.AssetName, prefab);
            }

            Log.LogInfo($"Loaded embedded {BundleFileName}: {LoadedPrefabs.Count} mapped shield VFX");
        }

        internal static void Show(Player player, ItemDrop.ItemData staff)
        {Hide();activeVisual=Create(player,staff);}

        internal static GameObject Create(Player player, ItemDrop.ItemData staff)
        {

            if (player == null || staff == null || staff.m_dropPrefab == null) return null;

            ShieldDefinition definition;
            if (!StaffShields.TryGetValue(staff.m_dropPrefab.name, out definition))
            {
                Log.LogWarning($"No shield VFX mapping for staff '{staff.m_dropPrefab.name}'");
                return null;
            }

            Initialize();
            GameObject prefab;
            if (!LoadedPrefabs.TryGetValue(definition.AssetName, out prefab)) return null;

            var visual = UnityEngine.Object.Instantiate(prefab);
            visual.name = "Overhaul_StaffShield_" + definition.AssetName;
            visual.transform.SetParent(player.transform, false);
            visual.transform.localPosition = player.GetCenterPoint() - player.transform.position;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one
                * (player.GetRadius() * 2f * definition.Scale);
            visual.SetActive(true);
            return visual;
        }

        internal static void Hide()
        {
            if (activeVisual == null) return;
            UnityEngine.Object.Destroy(activeVisual);
            activeVisual = null;
        }

        internal static void Unload()
        {
            Hide();
            LoadedPrefabs.Clear();
            if (bundle != null)
            {
                bundle.Unload(false);
                bundle = null;
            }
            loadAttempted = false;
        }
    }
}

