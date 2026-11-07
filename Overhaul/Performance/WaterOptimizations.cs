using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Two per-frame water costs.
    // Materials: WaterVolume.UpdateMaterials asks Unity for the surface's material instance every frame;
    // the instance is kept instead (checked again now and then in case it was replaced).
    // Floaters: WaterVolume.UpdateFloaters works out the water height under every floating thing (boats,
    // items, swimmers) one by one, asking Unity for the volume's size and normalising the wind again for each.
    // Those are looked up once per pass; the arithmetic is the game's own, in the same order, and the first
    // heights of each session are checked against the game's GetWaterSurface: any difference turns it off.
    internal static class WaterOptimizations
    {
        private sealed class MaterialEntry { internal Renderer Renderer; internal Material Material; internal int Calls; }
        private static readonly Dictionary<WaterVolume, MaterialEntry> materials = new Dictionary<WaterVolume, MaterialEntry>();

        [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials))]
        private static class Materials
        {
            private static bool Prefix(WaterVolume __instance)
            {
                if (!PerfMonitor.On(OverhaulConfig.PerfWaterMaterials)) return true;
                var renderer = __instance.m_waterSurface;
                if (!renderer) return true;
                if (!materials.TryGetValue(__instance, out var entry) || entry.Renderer != renderer || !entry.Material
                    || (++entry.Calls % 120 == 0 && renderer.sharedMaterial != entry.Material))
                {
                    entry = new MaterialEntry { Renderer = renderer, Material = renderer.material };
                    materials[__instance] = entry;
                }
                entry.Material.SetFloat(WaterVolume.s_shaderWaterTime, WaterVolume.s_waterTime);
                return false;
            }
        }

        [HarmonyPatch(typeof(WaterVolume), "OnDestroy")]
        private static class Forget
        {
            private static void Postfix(WaterVolume __instance) => materials.Remove(__instance);
        }

        // ---- Floaters ----
        private const int ParityChecks = 512;
        private static int checkedHeights, nextForeignCheck;
        private static bool broken, foreign;
        private static readonly Vector2[] directions = new Vector2[10], tangents = new Vector2[10];
        private static readonly Vector2[] wind1 = new Vector2[2], wind2 = new Vector2[2];
        private static readonly List<int> removeIndices = new List<int>();

        // Another mod changing how the water height is worked out keeps the game's own path.
        private static bool ForeignPatches()
        {
            if (Time.frameCount < nextForeignCheck) return foreign;
            nextForeignCheck = Time.frameCount + 600;
            var methods = typeof(WaterVolume).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "GetWaterSurface" || m.Name == "CalcWave" || m.Name == "Depth" || m.Name == "CreateWave" || m.Name == "TrochSin");
            foreign = methods.Any(m =>
            {
                var info = Harmony.GetPatchInfo(m);
                return info != null && info.Owners.Any(owner => owner != "plopyy.valheim.Overhaul");
            });
            return foreign;
        }

        [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateFloaters))]
        private static class Floaters
        {
            private static bool Prefix(WaterVolume __instance)
            {
                if (broken || !PerfMonitor.On(OverhaulConfig.PerfFastFloaters) || ForeignPatches()) return true;
                var inWater = __instance.m_inWater;
                int count = inWater.Count;
                if (count == 0) return false;
                if (WaterVolume.s_createWaveTangents == null) return true;
                PerfMonitor.FloaterPasses++;

                var volume = __instance.transform;
                float baseY = volume.position.y;
                bool needSize = __instance.m_oneDepth <= -1f;
                Vector3 size = needSize ? __instance.m_collider.bounds.size : Vector3.zero;
                if (__instance.m_useGlobalWind)
                {
                    for (int i = 1; i < 10; i++) { directions[i] = WaterVolume.s_createWaveDirections[i]; tangents[i] = WaterVolume.s_createWaveTangents[i]; }
                    Direction(WaterVolume.s_globalWind1, wind1);
                    Direction(WaterVolume.s_globalWind2, wind2);
                }

                removeIndices.Clear();
                for (int i = 0; i < count; i++)
                {
                    var item = inWater[i];
                    if (item == null) { removeIndices.Add(i); continue; }
                    var transform = item.GetTransform();
                    if (!transform) { removeIndices.Add(i); continue; }
                    Vector3 point = transform.position;
                    float height = Surface(__instance, volume, point, baseY, size);
                    if (checkedHeights < ParityChecks)
                    {
                        checkedHeights++;
                        float game = __instance.GetWaterSurface(point);
                        if (height != game)
                        {
                            broken = true;
                            Log.LogWarning("[Performance] Fast floaters off: water height " + height.ToString("R") + " differs from the game's " + game.ToString("R") + " at " + point);
                            height = game;
                        }
                    }
                    item.SetLiquidLevel(height, LiquidType.Water, __instance);
                }
                for (int i = removeIndices.Count - 1; i >= 0; i--) inWater.RemoveAt(removeIndices[i]);
                return false;
            }
        }

        // WaterVolume.CalcWave normalises the wind into the first wave's direction on every call.
        private static void Direction(Vector4 wind, Vector2[] result)
        {
            Vector2 direction = default;
            direction.x = wind.x;
            direction.y = wind.z;
            direction.Normalize();
            result[0] = direction;
            result[1] = new Vector2(0f - direction.y, direction.x);
        }

        // GetWaterSurface(point, 1f) with the volume's position and size read once.
        private static float Surface(WaterVolume v, Transform volume, Vector3 point, float baseY, Vector3 size)
        {
            float wave = 0f;
            if (v.m_useGlobalWind)
            {
                float waterTime = WaterVolume.s_wrappedDayTimeSeconds;
                float depth = Depth(v, volume, point, size);
                float waveFactorBig = 1f - (float)WorldGenerator.DeepNorthWaveFade(point.x, point.z);
                wave = depth == 0f ? 0f : CalcWave(point, depth, waterTime, 1f, waveFactorBig);
            }
            float height = baseY + wave + v.m_surfaceOffset;
            if (v.m_forceDepth < 0f && Utils.LengthXZ(point) > 10500f) height -= 100f;
            return height;
        }

        private static float Depth(WaterVolume v, Transform volume, Vector3 point, Vector3 size)
        {
            if (v.m_oneDepth > -1f) return v.m_oneDepth;
            Vector3 local = volume.InverseTransformPoint(point);
            float t = (local.x + size.x / 2f) / size.x;
            float t2 = (local.z + size.z / 2f) / size.z;
            float a = Mathf.Lerp(v.m_normalizedDepth[3], v.m_normalizedDepth[2], t);
            float b = Mathf.Lerp(v.m_normalizedDepth[0], v.m_normalizedDepth[1], t);
            return Mathf.Lerp(a, b, t2);
        }

        private static float CalcWave(Vector3 point, float depth, float waterTime, float waveFactor, float waveFactorBig)
        {
            if (WaterVolume.s_globalWindAlpha == 0f) return CalcWave(point, depth, WaterVolume.s_globalWind1, wind1, waterTime, waveFactor, waveFactorBig);
            float a = CalcWave(point, depth, WaterVolume.s_globalWind1, wind1, waterTime, waveFactor, waveFactorBig);
            float b = CalcWave(point, depth, WaterVolume.s_globalWind2, wind2, waterTime, waveFactor, waveFactorBig);
            return Mathf.LerpUnclamped(a, b, WaterVolume.s_globalWindAlpha);
        }

        private static float CalcWave(Vector3 p, float depth, Vector4 wind, Vector2[] first, float waterTime, float waveFactor, float waveFactorBig)
        {
            directions[0] = first[0];
            tangents[0] = first[1];
            float w = wind.w;
            float strength = Mathf.Lerp(0f, w, depth);
            float time = waterTime / 20f;
            float n2 = CreateWave(p, time, 10f, 0.04f, 8f * waveFactorBig, directions[0], tangents[0], 0.5f);
            float n3 = CreateWave(p, time, 14.123f, 0.08f, 6f * waveFactorBig, directions[1], tangents[1], 0.5f);
            float n4 = CreateWave(p, time, 22.312f, 0.1f, 4f * waveFactorBig, directions[2], tangents[2], 0.5f);
            float n5 = CreateWave(p, time, 31.42f, 0.2f, 2f * waveFactorBig, directions[3], tangents[3], 0.5f);
            float n6 = CreateWave(p, time, 35.42f, 0.4f, 1f * waveFactorBig, directions[4], tangents[4], 0.5f);
            float n7 = CreateWave(p, time, 38.1223f, 1f, 0.8f * waveFactorBig, directions[5], tangents[5], 0.7f);
            float n8 = CreateWave(p, time, 41.1223f, 1.2f, 0.6f * waveFactor, directions[6], tangents[6], 0.8f);
            float n9 = CreateWave(p, time, 51.5123f, 1.3f, 0.4f * waveFactor, directions[7], tangents[7], 0.9f);
            float n10 = CreateWave(p, time, 54.2f, 1.3f, 0.3f * waveFactor, directions[8], tangents[8], 0.9f);
            float n11 = CreateWave(p, time, 56.123f, 1.5f, 0.2f * waveFactor, directions[9], tangents[9], 0.9f);
            return (n2 + n3 + n4 + n5 + n6 + n7 + n8 + n9 + n10 + n11) * strength;
        }

        private static float TrochSin(float x, float k) => Mathf.Sin(x - Mathf.Cos(x) * k) * 0.5f + 0.5f;

        private static float CreateWave(Vector3 worldPos, float time, float waveSpeed, float waveLength, float waveHeight, Vector2 dir, Vector2 tangent, float sharpness)
        {
            Vector2 vector = -(worldPos.z * dir + worldPos.x * tangent);
            float num = time * waveSpeed;
            return (TrochSin(num + vector.y * waveLength, sharpness) * TrochSin(num * 0.123f + vector.x * 0.13123f * waveLength, sharpness) - 0.2f) * waveHeight;
        }
    }
}
