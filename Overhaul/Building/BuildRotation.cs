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
    // 22.5° per notch like the game, 5.625° with the precise key (LeftShift) held. Each notch turns from where the ghost stands, so it follows the bright arrow even
    // after other turns. Choosing another piece starts it flat, keeping only the turn about the vertical; placing keeps the turns. The placed
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
        private static GameObject ghost, selected;
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

        // The ghost's own turns (after the game's vertical one), for the piece relocation which turns its own ghost.
        internal static Quaternion Local { get => local; set { local = value; scroll = 0; } }

        // A key press; "<" (KeyCode.Less, the ISO key left of W/Z) has no reliable code: found on the keyboard by the
        // character it shows in the current layout.
        internal static bool Pressed(KeyCode key)
        {
            if (key != KeyCode.Less) return ZInput.GetKeyDown(key);
            var less = LessKey();
            return less != null && less.wasPressedThisFrame;
        }

        internal static UnityEngine.InputSystem.Controls.KeyControl LessKey()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return null;
            foreach (var control in keyboard.allKeys) if (control != null && control.displayName == "<") return control;
            return null;
        }

        // The frame the reset key set the ghost flat, for the relocation's own vertical turn.
        internal static int ResetFrame { get; private set; } = -1;

        // The wheel as the game reads it to turn the ghost: taken for a turn about the ghost's own axis.
        private static float Scroll()
        {
            var player = Player.m_localPlayer;
            return Wheel(player && player.m_placementGhost ? player.m_placementGhost.GetComponent<Piece>() : null);
        }

        // The wheel turns this ghost's piece about its own axes; what is left for the caller (all of it for a ground
        // piece, nothing otherwise).
        internal static float Wheel(Piece piece)
        {
            float wheel = ZInput.GetMouseScrollWheel();
            var player = Player.m_localPlayer;
            if (!player || !Tiltable(piece)) return wheel;
            // A quick build row keeps the turn it started with.
            if (QuickBuild.Active) { scroll = 0; return 0; }
            // The reset key sets the ghost flat and facing north again, on all three axes.
            if (Pressed(OverhaulConfig.BuildResetKey.Value) && !Hud.IsPieceSelectionVisible())
            {
                local = Quaternion.identity; scroll = 0; player.m_placeRotation = 0; ResetFrame = Time.frameCount;
                return 0;
            }
            scroll += wheel;
            int notch = scroll > player.m_scrollAmountThreshold ? 1 : scroll < -player.m_scrollAmountThreshold ? -1 : 0;
            if (notch != 0)
            {
                scroll = 0;
                local = (local * Quaternion.AngleAxis((ZInput.GetKey(OverhaulConfig.BuildPreciseKey.Value) ? Step / 4 : Step) * notch, Axis())).normalized;
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

        // Another piece chosen starts flat, still facing where the last one faced: the vertical part of the own turns goes
        // back into the game's notches. The ghost made again after each placement keeps its turns.
        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        private static class NewPiece
        {
            private static void Postfix(Player __instance)
            {
                // The relocation's ghost sets its own turns.
                if (__instance != Player.m_localPlayer || Storage.RelocationClient.Active) return;
                var chosen = __instance.m_buildPieces ? __instance.m_buildPieces.GetSelectedPrefab() : null;
                if (chosen == selected) return;
                selected = chosen;
                Vector3 forward = local * Vector3.forward; forward.y = 0;
                float yaw = forward.sqrMagnitude > .01f ? Quaternion.LookRotation(forward).eulerAngles.y : 0;
                // Whole notches go to the game, a finer rest (precise turns) stays as an own turn about the vertical.
                int notches = Mathf.FloorToInt(yaw / Step + .001f);
                __instance.m_placeRotation += notches;
                local = Quaternion.AngleAxis(yaw - notches * Step, Vector3.up); scroll = 0;
            }
        }

        // Out of building mode the game no longer updates the ghost: the gizmo goes with it.
        internal static void Hide() { if (gizmo && gizmo.activeSelf) gizmo.SetActive(false); }

        // The gizmo elsewhere than on the game's ghost (the quick build row's aimed piece).
        internal static void ShowAt(Vector3 position, Quaternion rotation)
        {
            if (!gizmo && !Create()) return;
            gizmo.transform.SetPositionAndRotation(position, rotation);
            if (!gizmo.activeSelf) gizmo.SetActive(true);
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

        // A prefab of overhaul_buildgizmo (the gizmo, the quick build handle).
        internal static GameObject Asset(string name)
        {
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_buildgizmo");
            return bundle ? bundle.LoadAsset<GameObject>(name) : null;
        }

        private static bool Create()
        {
            if (!prefab)
            {
                prefab = Asset("Overhaul_BuildGizmo");
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
