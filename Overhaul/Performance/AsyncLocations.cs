using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using SoftReferenceableAssets;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Locations (ruins, fortresses, villages...) appear when the player comes near: their LocationProxy copies the whole
    // location prefab (thousands of objects) in one frame, 150 to 270 ms in the Ashlands. With AsyncLocations the copy is made
    // by Unity's InstantiateAsync, off the main thread and joined to the scene a little each frame; the location shows a few
    // frames later, while still far away.
    // Same result as the game's own spawn (ZoneSystem.SpawnLocation in client mode): the prefab is prepared the same way
    // (random spawns and objects drawn with the location's seed, networked parts left out) and kept that way until the copy is
    // done, then restored. One copy per location prefab at a time; if the game needs that prefab meanwhile (another spawn of
    // it), the copy is finished first. Off on a server or host: their full spawns use the prefab in the same frame. Locations
    // with an interior or a dungeon keep the game's own spawn, which Overhaul's dungeon code relies on.
    internal static class AsyncLocations
    {
        sealed class Job
        {
            internal LocationProxy Proxy; internal ZoneSystem.ZoneLocation Location; internal AsyncInstantiateOperation<GameObject> Op;
            internal ZNetView[] Views; internal RandomObject[] Objects; internal RandomSpawn[] Spawns;
            internal Vector3 AssetPosition; internal Quaternion AssetRotation;
            internal bool Custom; internal Location Component; internal Vector3 GeneratorPosition, InteriorPosition; internal Quaternion InteriorRotation;
            internal GameObject Result; internal bool Restored;
        }

        static readonly Dictionary<LocationProxy, Job> byProxy = new Dictionary<LocationProxy, Job>();
        static readonly Dictionary<ZoneSystem.ZoneLocation, Job> byLocation = new Dictionary<ZoneSystem.ZoneLocation, Job>();
        static readonly List<Job> finished = new List<Job>();
        internal static int Pending => byProxy.Count;

        static bool Enabled => PerfMonitor.On(OverhaulConfig.PerfAsyncLocations) && ZNet.instance && !ZNet.instance.IsServer();

        [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SpawnLocation))]
        private static class Spawn
        {
            private static bool Prefix(LocationProxy __instance, ref bool __result)
            {
                if (byProxy.TryGetValue(__instance, out var job))
                {
                    __result = false;
                    if (!job.Op.isDone) return false;
                    Complete(job);
                    __result = Attach(job);
                    return false;
                }
                if (!Enabled || __instance.m_nview == null || __instance.m_nview.GetZDO() == null) return true;
                var zdo = __instance.m_nview.GetZDO();
                int hash = zdo.GetInt(ZDOVars.s_location), seed = zdo.GetInt(ZDOVars.s_seed);
                if (hash == 0 || ZoneSystem.instance.ShouldDelayProxyLocationSpawning(hash)) return true;
                var location = ZoneSystem.instance.GetLocation(hash);
                if (location == null) return true;
                // One copy of a prefab at a time: the next waits (the proxy tries again next frame).
                if (byLocation.ContainsKey(location)) { Wait(__instance); __result = false; return false; }
                if (!Start(__instance, location, seed)) return true;
                Wait(__instance);
                __result = false;
                return false;
            }
        }

        // The proxy keeps asking each frame and its zone counts as still loading, as with the game's own delay.
        static void Wait(LocationProxy proxy)
        {
            proxy.m_locationNeedsSpawn = true;
            if (proxy.m_zdoSetToBeLoadingInZone == null) { proxy.m_zdoSetToBeLoadingInZone = proxy.m_nview.GetZDO(); ZoneSystem.instance.SetLoadingInZone(proxy.m_zdoSetToBeLoadingInZone); }
        }

        // The prefab prepared as ZoneSystem.SpawnLocation does in client mode, then copied in the background.
        static bool Start(LocationProxy proxy, ZoneSystem.ZoneLocation location, int seed)
        {
            location.m_prefab.Load();
            var asset = location.m_prefab.Asset;
            if (!asset) { location.m_prefab.Release(); return false; }
            // Locations with an interior or a dungeon keep the game's own spawn (Overhaul's dungeon code places their interior then).
            var locationComponent = asset.GetComponent<Location>();
            if (locationComponent && (locationComponent.m_interiorTransform || locationComponent.m_generator || locationComponent.transform.Find("Interior"))
                || asset.GetComponentInChildren<DungeonGenerator>(true)) { location.m_prefab.Release(); return false; }
            var job = new Job { Proxy = proxy, Location = location };
            job.Views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
            job.Objects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
            job.Spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
            foreach (var s in job.Spawns) s.Prepare();
            var component = job.Component = asset.GetComponent<Location>();
            Vector3 pos = proxy.transform.position; Quaternion rot = proxy.transform.rotation;
            Vector3 interior = Vector3.zero, generator = Vector3.zero;
            if (component && component.m_interiorTransform && component.m_generator) { interior = component.m_interiorTransform.localPosition; generator = component.m_generator.transform.localPosition; }
            job.AssetPosition = asset.transform.position; job.AssetRotation = asset.transform.rotation;
            asset.transform.position = Vector3.zero; asset.transform.rotation = Quaternion.identity;
            job.Custom = component && component.m_useCustomInteriorTransform && component.m_interiorTransform && component.m_generator;
            if (job.Custom)
            {
                job.GeneratorPosition = component.m_generator.transform.localPosition;
                job.InteriorPosition = component.m_interiorTransform.localPosition; job.InteriorRotation = component.m_interiorTransform.localRotation;
                Vector3 zonePos = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
                component.m_generator.transform.localPosition = Vector3.zero;
                Vector3 offset = zonePos + interior + generator - pos;
                Vector3 local = (Matrix4x4.Rotate(Quaternion.Inverse(rot)) * Matrix4x4.Translate(offset)).GetColumn(3);
                local.y = component.m_interiorTransform.localPosition.y;
                component.m_interiorTransform.localPosition = local;
                component.m_interiorTransform.localRotation = Quaternion.Inverse(rot);
            }
            Random.InitState(seed);
            foreach (var s in job.Spawns) s.Randomize(pos + rot * s.gameObject.transform.position, component);
            foreach (var o in job.Objects) o.Randomize(pos + rot * o.gameObject.transform.position, component);
            foreach (var v in job.Views) v.gameObject.SetActive(false);
            // The copy joins the scene within 2 ms per frame.
            AsyncInstantiateOperation.SetIntegrationTimeMS(2f);
            job.Op = Object.InstantiateAsync(asset, pos, rot);
            byProxy[proxy] = job; byLocation[location] = job;
            return true;
        }

        // The copy is done: the prefab gets its own state back, the copy is shown.
        static void Complete(Job job)
        {
            if (job.Restored) return;
            job.Restored = true;
            if (!job.Op.isDone) job.Op.WaitForCompletion();
            var result = job.Op.Result; job.Result = result != null && result.Length > 0 ? result[0] : null;
            foreach (var s in job.Spawns) if (s) s.Reset();
            foreach (var o in job.Objects) if (o) o.Reset();
            foreach (var v in job.Views) if (v) v.gameObject.SetActive(true);
            var asset = job.Location.m_prefab.Asset;
            if (asset) { asset.transform.position = job.AssetPosition; asset.transform.rotation = job.AssetRotation; }
            if (job.Custom && job.Component)
            {
                job.Component.m_generator.transform.localPosition = job.GeneratorPosition;
                job.Component.m_interiorTransform.localPosition = job.InteriorPosition;
                job.Component.m_interiorTransform.localRotation = job.InteriorRotation;
            }
            if (job.Result)
            {
                job.Result.SetActive(true);
                job.Result.HoldReferenceTo(job.Location.m_prefab);
            }
            job.Location.m_prefab.Release();
            byLocation.Remove(job.Location);
            SnapToGround.SnappAll();
        }

        // What LocationProxy.SpawnLocation does with the game's own instance.
        static bool Attach(Job job)
        {
            var proxy = job.Proxy;
            byProxy.Remove(proxy);
            if (!job.Result) return false;
            proxy.m_instance = job.Result;
            job.Result.transform.SetParent(proxy.transform, true);
            proxy.m_nview.LoadFields();
            proxy.m_locationNeedsSpawn = false;
            if (proxy.m_zdoSetToBeLoadingInZone != null) { ZoneSystem.instance.UnsetLoadingInZone(proxy.m_zdoSetToBeLoadingInZone); proxy.m_zdoSetToBeLoadingInZone = null; }
            return true;
        }

        // The game spawns a location itself while its prefab is being copied: the copy is finished first.
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnLocation))]
        private static class Guard
        {
            private static void Prefix(ZoneSystem.ZoneLocation location)
            {
                if (location != null && byLocation.TryGetValue(location, out var job)) Complete(job);
            }
        }

        // Called every frame: finished copies give the prefab back at once; copies whose proxy went away are thrown away.
        internal static void Tick()
        {
            if (byProxy.Count == 0) return;
            finished.Clear();
            foreach (var pair in byProxy) if (pair.Value.Op.isDone) finished.Add(pair.Value);
            foreach (var job in finished)
            {
                Complete(job);
                if (!job.Proxy) { byProxy.Remove(job.Proxy); if (job.Result) Object.Destroy(job.Result); }
            }
        }
    }
}
