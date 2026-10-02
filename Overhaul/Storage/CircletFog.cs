using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using EquipmentAndQuickSlots.src.MultiUtility;

namespace Overhaul.Storage
{
    internal static class CircletFog
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly HashSet<string> FogMaterials = new HashSet<string> {
            "fog", "distant_fog", "heavymist", "heavymist_mistlands", "heavymist_mistlands_small_lux",
            "forest_groundmist", "swamp_mist", "swamp_mist_low", "mountaincave_mist", "rain_fogclouds", "darklands_groundfog",
            "build_fog_lowres", "grave_fog", "ice_fog", "smoke", "dev_smoke", "green_smoke", "grill_burnt_smoke",
            "slowwispysmoke", "slowwispysmoke_gradient", "slowwispysmoke_gradient_alphablend", "slowwispysmoke_hard",
            "slowwispysmoke_near_far_fade", "slowwispysmoke_nearfade", "slowwispysmoke_nearfade_hard", "wispysmoke", "risingsmoke",
            "smokeball", "SmokeBlob", "smokepuff_mat", "Yaggesmoke", "aoe_smoke", "ooze_smoke", "mussle_smoke",
            "magetable_smoke", "magetable_smoke_cinder", "magetable_smoke_edge", "magetable_smoke_flames",
            "ghost_smoke", "wraith_smoke", "dverger_buff_smoke", "Immobilize_smoke",
            "dust", "dust_footstep", "dust_particle", "dust_particle 1", "dust_particle_pixel", "sawdust", "winddust", "SkeletonSpawnDust"
        };
        private sealed class Entry
        {
            internal ParticleSystemRenderer Renderer;
            internal Material Material;
            internal MaterialPropertyBlock Original;
            internal Color Applied;
            internal float Opacity;
        }
        private static readonly Dictionary<ParticleSystemRenderer, Entry> Entries = new Dictionary<ParticleSystemRenderer, Entry>();
        private static readonly MaterialPropertyBlock Scratch = new MaterialPropertyBlock();
        private static float nextScan, originalFog, appliedFog;
        private static bool fogChanged, wasEquipped;
        internal static bool Equipped() => GetOpacity() < 1f;
        internal static float GetOpacity()
        {
            var player = Player.m_localPlayer;
            if (!player || player.IsDead()) return 1f;
            float opacity = Mathf.Min(ItemOpacity(player.m_helmetItem), ItemOpacity(player.m_utilityItem));
            for (int i = 0; i < MultiUtility.GetExtraCount(player); i++)
                opacity = Mathf.Min(opacity, ItemOpacity(MultiUtility.GetExtra(player, i)));
            return opacity;
        }
        private static float ItemOpacity(ItemDrop.ItemData item) => DvergerCirclet.IsSpirit(item) ? 0f :
            DvergerCirclet.IsCirclet(item) || item?.m_shared?.m_name == "$item_helmet_dverger" ? .5f : 1f;
        internal static bool IsFog(Material material)
        {
            if (!material || !material.HasProperty(ColorId)) return false;
            return FogMaterials.Contains(material.name.Replace(" (Instance)", ""));
        }
        internal static void ApplyRenderer(ParticleSystemRenderer renderer)
        {
            if (!renderer) return;
            var material = renderer.sharedMaterial;
            float opacity = GetOpacity();
            renderer.GetPropertyBlock(Scratch);
            if (Entries.TryGetValue(renderer, out var entry))
            {
                if (entry.Material == material && entry.Opacity == opacity && Scratch.HasProperty(ColorId) && Scratch.GetColor(ColorId) == entry.Applied) return;
                if (Scratch.HasProperty(ColorId) && Scratch.GetColor(ColorId) == entry.Applied)
                {
                    renderer.SetPropertyBlock(entry.Original.isEmpty ? null : entry.Original);
                    renderer.GetPropertyBlock(Scratch);
                }
                // The renderer was updated by the game: capture its new baseline.
                Entries.Remove(renderer);
            }
            if (!IsFog(material)) return;
            var baseline = new MaterialPropertyBlock(); renderer.GetPropertyBlock(baseline);
            var color = baseline.HasProperty(ColorId) ? baseline.GetColor(ColorId) : material.GetColor(ColorId);
            color.a *= opacity;
            Scratch.SetColor(ColorId, color); renderer.SetPropertyBlock(Scratch);
            Entries[renderer] = new Entry { Renderer = renderer, Material = material, Original = baseline, Applied = color, Opacity = opacity };
        }
        internal static void ApplyFog(bool freshlySet)
        {
            if (freshlySet) fogChanged = false;
            if (!Equipped()) { RestoreFog(); return; }
            float current = RenderSettings.fogDensity;
            if (!fogChanged || !Mathf.Approximately(current, appliedFog)) originalFog = current;
            appliedFog = originalFog * GetOpacity();
            RenderSettings.fogDensity = appliedFog; fogChanged = true;
        }
        private static void RestoreFog()
        {
            if (fogChanged && Mathf.Approximately(RenderSettings.fogDensity, appliedFog)) RenderSettings.fogDensity = originalFog;
            fogChanged = false;
        }
        internal static void Tick()
        {
            bool equipped = Equipped();
            if (!equipped) { if (wasEquipped || Entries.Count > 0 || fogChanged) Clear(); return; }
            ApplyFog(false);
            if (!wasEquipped || Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + .5f;
                foreach (var dead in new List<ParticleSystemRenderer>(Entries.Keys)) if (!dead) Entries.Remove(dead);
                foreach (var renderer in Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None)) ApplyRenderer(renderer);
            }
            wasEquipped = true;
        }
        internal static void Clear()
        {
            foreach (var entry in Entries.Values)
            {
                if (!entry.Renderer) continue;
                entry.Renderer.GetPropertyBlock(Scratch);
                if (Scratch.HasProperty(ColorId) && Scratch.GetColor(ColorId) == entry.Applied)
                    entry.Renderer.SetPropertyBlock(entry.Original.isEmpty ? null : entry.Original);
            }
            Entries.Clear(); RestoreFog(); wasEquipped = false; nextScan = 0;
        }
    }
    [HarmonyPatch(typeof(EnvMan), "SetEnv")]
    internal static class CircletWeatherFogPatch
    {
        private static void Postfix() => CircletFog.ApplyFog(true);
    }
}
