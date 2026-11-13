using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Dungeons
{
    // Dungeon interiors on demand. A dungeon's inside lies high above its entrance (above 3000 m, the game's own interior
    // line), in the same zone, and the game creates every networked object of the zones around the player whatever its
    // height: walking past a crypt or a mine created its whole inside (its generator, which rebuilds the rooms, its chests,
    // monsters, pickables), never seen. Now the objects of the other side of that line are left out of what the scene
    // creates: from outside, no interior object exists; teleported in, the game's loading screen creates them (the player is
    // placed at the arrival before it waits for the area), and they go once back outside. The surface stays created from
    // inside, as the game has it (bases next to an entrance keep running).
    // The game's "is the area ready" test leaves them out too, else an arrival in a zone holding a dungeon would wait for
    // objects that are never created. Switch: o_opti interiors on|off.
    internal static class InteriorStreaming
    {
        const float Line = 3000f; // Character.InInterior
        static bool Active => PerfMonitor.On(OverhaulConfig.PerfInteriorStreaming);
        static bool wasInside, wasActive = true;
        static int left;
        static float nextLog;

        // Objects the scene must not create for a reference point: interior objects while the point is outside.
        static bool Hidden(ZDO zdo, bool inside) => !inside && zdo.GetPosition().y > Line;

        static void Filter(List<ZDO> list, bool inside)
        {
            if (list == null || inside) return;
            int before = list.Count;
            if (fresh.Count > 0) { float now = Time.time; foreach (var z in new List<Vector2s>(fresh.Keys)) if (fresh[z] < now) fresh.Remove(z); }
            list.RemoveAll(zdo => zdo.GetPosition().y > Line && (fresh.Count == 0 || !fresh.ContainsKey(zdo.GetSector())));
            left += before - list.Count;
        }

        // A zone spawned for the first time (its dungeon generated here): its interior is kept a while, so the game's and our
        // own work on the new dungeon (boss room, guardian, chests) ends before its objects go.
        static readonly Dictionary<Vector2s, float> fresh = new Dictionary<Vector2s, float>();
        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        static class NewZone
        {
            static void Postfix(Vector2s zoneID, ZoneSystem.SpawnMode mode, bool __result)
            {
                if (__result && mode == ZoneSystem.SpawnMode.Full) fresh[zoneID] = Time.time + 15f;
            }
        }

        // In ZNetScene.CreateDestroyObjects, right after the sector search: what is left out is neither created nor kept
        // (the removal pass then destroys interior instances once the player is out).
        public static void FilterScene(ZNetScene scene)
        {
            if (!Active || !ZNet.instance) return;
            bool inside = ZNet.instance.GetReferencePosition().y > Line;
            left = 0;
            Filter(scene.m_tempCurrentObjects, inside);
            Filter(scene.m_tempCurrentDistantObjects, inside);
            if (left > 0 && Time.time >= nextLog) { nextLog = Time.time + 60f; Log.LogInfo("[Dungeons] interiors on demand: " + left + " interior objects not created from outside"); }
        }

        // In ZNetScene.IsAreaReady: the same for the area tested.
        public static void FilterArea(ZNetScene scene, Vector3 point)
        {
            if (!Active) return;
            Filter(scene.m_tempCurrentObjects, point.y > Line);
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        static class Scene
        {
            // Crossing the line (a dungeon door) changes what a run creates without changing the zone: the streaming skip
            // must run again. Also when the switch changes.
            [HarmonyPriority(Priority.First + 100)]
            static void Prefix()
            {
                if (!ZNet.instance) return;
                bool inside = ZNet.instance.GetReferencePosition().y > Line, active = Active;
                if (inside != wasInside || active != wasActive) { wasInside = inside; wasActive = active; StreamingSkip.Version++; }
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var find = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects));
                int done = 0;
                foreach (var instruction in code)
                {
                    yield return instruction;
                    if (instruction.Calls(find)) { done++; yield return new CodeInstruction(OpCodes.Ldarg_0); yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InteriorStreaming), nameof(FilterScene))); }
                }
                if (done != 1) Log.LogWarning("[Dungeons] interiors on demand: expected one sector search in ZNetScene.CreateDestroyObjects, found " + done);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
        static class Area
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var find = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects));
                int done = 0;
                foreach (var instruction in code)
                {
                    yield return instruction;
                    if (instruction.Calls(find)) { done++; yield return new CodeInstruction(OpCodes.Ldarg_0); yield return new CodeInstruction(OpCodes.Ldarg_1); yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InteriorStreaming), nameof(FilterArea))); }
                }
                if (done != 1) Log.LogWarning("[Dungeons] interiors on demand: expected one sector search in ZNetScene.IsAreaReady, found " + done);
            }
        }
    }
}
