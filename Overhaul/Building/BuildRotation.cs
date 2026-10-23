using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Building
{
    // Building ghost turned every way, always about its own axes as the gizmo shows them: the wheel alone turns it about
    // its Y axis, held with the pitch key (LeftAlt) about its X axis, with the roll key (LeftControl) about its Z axis,
    // 22.5° per notch like the game. Each notch turns from where the ghost stands, so it follows the bright arrow even
    // after other turns. A new piece starts flat, keeping only the turn about the vertical (as the game does). The placed
    // piece takes the ghost's rotation. Ground pieces (hoe, cultivator) keep the game's turn and get no gizmo.
    //
    // The orientation gizmo (overhaul_buildgizmo, OverhaulLab BuildGizmoBundle) follows the ghost on its pivot, the point
    // it turns about, sized to the piece. It is no child of the ghost, which the game tints red when misplaced. The axis
    // the wheel turns about is bright, the two others faded.
    internal static class BuildRotation
    {
        private const float Step = 22.5f;
        // The ghost's own turns, after the game's vertical one (m_placeRotation).
        private static Quaternion local = Quaternion.identity;
        private static float scroll;
        private static AssetBundle bundle;
        private static GameObject prefab, gizmo;
        private static GameObject ghost;
        private static bool logged;
        private static Renderer[] renderers;
        private static MaterialPropertyBlock block;
        private static readonly int Dim = Shader.PropertyToID("_Dim");

        private static bool Tiltable(Piece piece) => piece && !piece.m_groundPiece && !piece.m_groundOnly && !piece.m_cultivatedGroundOnly;

        private static Vector3 Axis()
        {
            if (ZInput.GetKey(OverhaulConfig.BuildPitchKey.Value)) return Vector3.right;
            if (ZInput.GetKey(OverhaulConfig.BuildRollKey.Value)) return Vector3.forward;
            return Vector3.up;
        }

        // The wheel as the game reads it to turn the ghost: taken for a turn about the ghost's own axis.
        private static float Scroll()
        {
            float wheel = ZInput.GetMouseScrollWheel();
            var player = Player.m_localPlayer;
            if (!player || !Tiltable(player.m_placementGhost ? player.m_placementGhost.GetComponent<Piece>() : null)) return wheel;
            scroll += wheel;
            int notch = scroll > player.m_scrollAmountThreshold ? 1 : scroll < -player.m_scrollAmountThreshold ? -1 : 0;
            if (notch != 0)
            {
                scroll = 0;
                local = (local * Quaternion.AngleAxis(Step * notch, Axis())).normalized;
            }
            return 0;
        }

        // The game's vertical turn, then the ghost's own turns.
        private static Quaternion Tilt(Quaternion rotation, Player player) =>
            Tiltable(player.m_placementGhost ? player.m_placementGhost.GetComponent<Piece>() : null) ? rotation * local : rotation;

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class ReadWheel
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                instructions.MethodReplacer(AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel)), AccessTools.Method(typeof(BuildRotation), nameof(Scroll)));
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class TiltGhost
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var euler = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.Euler), new[] { typeof(float), typeof(float), typeof(float) });
                bool done = false;
                foreach (var code in instructions)
                {
                    yield return code;
                    if (done || !code.Calls(euler)) continue;
                    done = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BuildRotation), nameof(Tilt)));
                }
                if (!done) Log.LogError("Build rotation: ghost rotation not found");
            }

            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                Follow(__instance.m_placementGhost);
            }
        }

        // A new piece starts flat, still facing where the last one faced: the vertical part of the own turns goes back
        // into the game's notches.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        private static class NewPiece
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                Vector3 forward = local * Vector3.forward; forward.y = 0;
                if (forward.sqrMagnitude > .01f) __instance.m_placeRotation += Mathf.RoundToInt(Quaternion.LookRotation(forward).eulerAngles.y / Step);
                local = Quaternion.identity; scroll = 0;
            }
        }

        private static void Follow(GameObject current)
        {
            bool shown = current && current.activeInHierarchy && Tiltable(current.GetComponent<Piece>());
            if (!shown) { if (gizmo) gizmo.SetActive(false); return; }
            if (!gizmo && !Create()) return;
            if (current != ghost) { ghost = current; gizmo.transform.localScale = Vector3.one * Size(current); }
            gizmo.transform.SetPositionAndRotation(current.transform.position, current.transform.rotation);
            gizmo.SetActive(true);
            if (!logged) { logged = true; var r0 = renderers[0]; Log.LogInfo("Build gizmo diag: pos " + gizmo.transform.position + " scale " + gizmo.transform.localScale.x + " layer " + gizmo.layer + " renderers " + renderers.Length + " shader " + r0.sharedMaterial?.shader?.name + " supported " + r0.sharedMaterial?.shader?.isSupported + " visible " + r0.isVisible + " enabled " + r0.enabled + " camMask " + (Camera.main ? Camera.main.cullingMask : -1) + " cam " + (Camera.main ? Camera.main.name : "none")); }
            var turn = Axis();
            char axis = turn == Vector3.right ? 'X' : turn == Vector3.forward ? 'Z' : 'Y';
            foreach (var r in renderers)
            {
                r.GetPropertyBlock(block);
                block.SetFloat(Dim, r.name[0] == axis || r.name == "Cube" ? 0 : 1);
                r.SetPropertyBlock(block);
            }
        }

        private static bool Create()
        {
            if (!prefab)
            {
                if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_buildgizmo");
                prefab = bundle ? bundle.LoadAsset<GameObject>("Overhaul_BuildGizmo") : null;
                if (!prefab) { Log.LogError("Build rotation: Overhaul_BuildGizmo missing"); return false; }
            }
            gizmo = Object.Instantiate(prefab);
            Object.DontDestroyOnLoad(gizmo);
            renderers = gizmo.GetComponentsInChildren<Renderer>(true);
            block = new MaterialPropertyBlock();
            return true;
        }

        // The rings (0.2 m across at scale 1) reach a bit past the piece's farthest side from its pivot.
        private static float Size(GameObject current)
        {
            float reach = 0;
            foreach (var r in current.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                var b = r.bounds;
                reach = Mathf.Max(reach, (b.max - current.transform.position).magnitude * .6f, (b.min - current.transform.position).magnitude * .6f);
            }
            return Mathf.Clamp(reach / .2f, 1.5f, 15f);
        }
    }
}
