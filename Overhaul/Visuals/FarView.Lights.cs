using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;

namespace Overhaul.Visuals
{
    // Far view, lights: at night the fires, torches and lamps of far buildings glow (their light's colour), a fire only
    // while it has fuel. Workshops (forges, ovens, smelters...) stay dark. Gathered with the far buildings' cells, drawn as
    // one additive draw of camera-facing glows (FarGlow), fading in at dusk.
    internal static partial class FarView
    {
        sealed class GlowModel { internal readonly List<(Vector3 Local, Color Colour, float Size)> Lights = new List<(Vector3, Color, float)>(); internal bool Fire, Infinite; }
        static readonly Dictionary<int, GlowModel> glowModels = new Dictionary<int, GlowModel>();
        static readonly Dictionary<(int, int), List<(Vector4 Position, Vector4 Colour)>> cellGlows = new Dictionary<(int, int), List<(Vector4, Vector4)>>();
        static ComputeBuffer glowPositions, glowColours;
        static int glowCount;
        static bool glowsDirty;
        static Material glowMaterial;
        static Shader glowShader;
        static float night;
        static bool lightsOn = true;

        static GlowModel GlowOf(int hash)
        {
            if (glowModels.TryGetValue(hash, out var model)) return model;
            model = null;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab && prefab.GetComponent<Piece>() && !prefab.GetComponent<CraftingStation>() && !prefab.GetComponent<Smelter>() && !prefab.GetComponent<CookingStation>())
            {
                var toRoot = prefab.transform.worldToLocalMatrix;
                var candidate = new GlowModel();
                foreach (var light in prefab.GetComponentsInChildren<Light>(true))
                {
                    if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                    candidate.Lights.Add((toRoot.MultiplyPoint3x4(light.transform.position), light.color * Mathf.Clamp(light.intensity, 0.5f, 3f), Mathf.Clamp(light.range * 0.35f, 2f, 9f)));
                }
                var fire = prefab.GetComponent<Fireplace>();
                candidate.Fire = fire; candidate.Infinite = fire && fire.m_infiniteFuel;
                if (candidate.Lights.Count > 0) model = candidate;
            }
            glowModels[hash] = model;
            return model;
        }

        // Called while a buildings cell is merged: the glows of its far pieces.
        static void GlowsOfCell((int, int) key, List<ZDO> pieces)
        {
            if (!lightsOn) { ForgetCellGlows(key); return; }
            List<(Vector4, Vector4)> list = null;
            foreach (var zdo in pieces)
            {
                var model = GlowOf(zdo.GetPrefab());
                if (model == null) continue;
                if (model.Fire && !model.Infinite && zdo.GetFloat(ZDOVars.s_fuel, 0f) <= 0f) continue;
                var position = zdo.GetPosition(); var rotation = zdo.GetRotation();
                foreach (var (local, colour, size) in model.Lights)
                {
                    var p = position + rotation * local;
                    (list ??= new List<(Vector4, Vector4)>()).Add((new Vector4(p.x, p.y, p.z, size), new Vector4(colour.r, colour.g, colour.b, 1.5f)));
                }
            }
            if (list == null) { if (cellGlows.Remove(key)) glowsDirty = true; }
            else { cellGlows[key] = list; glowsDirty = true; }
        }

        static void ForgetCellGlows((int, int) key) { if (cellGlows.Remove(key)) glowsDirty = true; }

        static void TickLights(Vector3 eye)
        {
            bool on = ClientVisuals.FarLights && ClientVisuals.FarBuildings;
            if (on != lightsOn)
            {
                // Switched: drop the glows, or merge the cells again so they gather theirs.
                lightsOn = on;
                cellGlows.Clear(); glowsDirty = true;
                if (on) foreach (var cell in buildingCells.Values) cell.Print = 0;
            }
            if (!glowShader) { if (forestFailed || kinds == null) return; glowShader = farGlowShader; if (!glowShader) return; glowMaterial = new Material(glowShader); }
            // Night from the time of day (the game turns its sun light around into the moon at night, so its direction
            // can't tell): sun height -1 at midnight, 1 at noon; glows from just before sunset to just after sunrise.
            float target = 0f;
            if (EnvMan.instance) target = Mathf.Clamp01((0.15f + Mathf.Cos(EnvMan.instance.GetDayFraction() * Mathf.PI * 2f)) / 0.3f);
            night = Mathf.MoveTowards(night, target, Time.deltaTime * 0.5f);
            if (glowsDirty) UploadGlows();
            if (glowCount == 0 || night <= 0.01f) return;
            float reach = BuildingReach(ClientVisuals.FarViewInEffect);
            var sim = ZNet.instance.GetSyncedSimulationDistance();
            glowMaterial.SetFloat("_Night", night);
            glowMaterial.SetVector("_Range", new Vector2(sim.NearSimulationDistance * 64f, reach));
            Graphics.DrawMeshInstancedProcedural(quad, 0, glowMaterial, new Bounds(eye, Vector3.one * (reach * 2f + 1000f)), glowCount, null, ShadowCastingMode.Off, false);
        }

        static void UploadGlows()
        {
            glowsDirty = false;
            var positions = new List<Vector4>(); var colours = new List<Vector4>();
            foreach (var list in cellGlows.Values) foreach (var (p, c) in list) { positions.Add(p); colours.Add(c); }
            glowCount = positions.Count;
            if (glowCount == 0) return;
            if (glowPositions == null || glowPositions.count < glowCount)
            {
                glowPositions?.Release(); glowColours?.Release();
                int capacity = Mathf.Max(64, Mathf.NextPowerOfTwo(glowCount));
                glowPositions = new ComputeBuffer(capacity, 16); glowColours = new ComputeBuffer(capacity, 16);
                glowMaterial.SetBuffer("_Glows", glowPositions); glowMaterial.SetBuffer("_Colours", glowColours);
            }
            glowPositions.SetData(positions); glowColours.SetData(colours);
        }

        static void StopLights()
        {
            cellGlows.Clear(); glowCount = 0;
            glowPositions?.Release(); glowColours?.Release(); glowPositions = glowColours = null;
            if (glowMaterial) Object.Destroy(glowMaterial);
            glowMaterial = null; glowShader = null;
        }
    }
}
