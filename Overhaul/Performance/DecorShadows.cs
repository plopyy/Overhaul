using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Decoration pieces (the build menu's Furniture tab: chairs, tables, rugs, banners, torches, candles...) cast no shadow.
    // Structure (walls, beams, floors, roofs: the other tabs) keeps its shadows. Every shadow caster is drawn again for each
    // shadow cascade, and a base holds hundreds of these small objects. Switching the option off gives their shadows back.
    internal static class DecorShadows
    {
        static readonly Dictionary<Renderer, ShadowCastingMode> turnedOff = new Dictionary<Renderer, ShadowCastingMode>();
        static readonly List<Renderer> gone = new List<Renderer>();
        static float nextPrune;
        static bool applied;

        static bool IsDecor(Piece piece) => piece && piece.m_category == Piece.PieceCategory.Furniture;

        static void Apply(Piece piece)
        {
            foreach (var renderer in piece.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer || renderer.shadowCastingMode == ShadowCastingMode.Off) continue;
                turnedOff[renderer] = renderer.shadowCastingMode;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        // Built pieces only: a placement ghost or a menu icon has no network object yet.
        [HarmonyPatch(typeof(Piece), "Awake")]
        private static class OnAwake
        {
            private static void Postfix(Piece __instance)
            {
                if (!PerfMonitor.On(OverhaulConfig.PerfDecorShadows) || !IsDecor(__instance)) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view && view.GetZDO() != null) Apply(__instance);
            }
        }

        // Called every frame by PerfMonitor: follows the option (and "operf ab") live.
        internal static void Tick()
        {
            bool on = PerfMonitor.On(OverhaulConfig.PerfDecorShadows);
            if (Time.time >= nextPrune)
            {
                nextPrune = Time.time + 10f;
                gone.Clear();
                foreach (var renderer in turnedOff.Keys) if (!renderer) gone.Add(renderer);
                foreach (var renderer in gone) turnedOff.Remove(renderer);
            }
            if (on == applied) return;
            applied = on;
            if (on)
            {
                foreach (var piece in Piece.s_allPieces) if (IsDecor(piece) && piece.GetComponent<ZNetView>()?.GetZDO() != null) Apply(piece);
            }
            else
            {
                foreach (var pair in turnedOff) if (pair.Key) pair.Key.shadowCastingMode = pair.Value;
                turnedOff.Clear();
            }
        }
    }
}
