using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Storage
{
    // Improved shield generator: same model tinted icy blue, fixed 50 m dome fuelled by Ice, and a dome that hostile creatures cannot enter.
    internal static class FrostShieldGenerator
    {
        internal const string Name = "Overhaul_FrostShieldGenerator";
        internal const float Radius = 50f;
        internal static readonly Color Tint = new Color(.62f, .84f, 1f);

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        private static void Register()
        {
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            var ice = PrefabManager.Instance.GetPrefab("Ice");
            if (!ice) { Jotunn.Logger.LogWarning("Frost shield generator skipped: Ice item not found."); return; }
            var custom = new CustomPiece(Name, "piece_shieldgenerator", "_HammerPieceTable");
            Configure(custom.PiecePrefab, ice.GetComponent<ItemDrop>());
            PieceManager.Instance.AddPiece(custom);
        }

        internal static void Configure(GameObject prefab, ItemDrop ice)
        {
            var piece = prefab.GetComponent<Piece>();
            piece.m_name = "$overhaul_frost_shield";
            piece.m_description = "$overhaul_frost_shield_description";
            var shield = prefab.GetComponent<ShieldGenerator>();
            shield.m_name = piece.m_name;
            shield.m_fuelItems = new List<ItemDrop> { ice };
            // Fixed size: the dome stays at full radius until the last fuel is gone (the native code drops it to 0 at empty).
            shield.m_minShieldRadius = shield.m_maxShieldRadius = Radius;
            foreach (var area in prefab.GetComponentsInChildren<EffectArea>(true))
            {
                // Mobs already inside must stay and be dealt with: drop the native "flee the dome" area, the barrier replaces it.
                if ((area.m_type & EffectArea.Type.NoMonsters) != 0) { Object.DestroyImmediate(area.gameObject); continue; }
                if ((area.m_type & EffectArea.Type.PlayerBase) == 0) continue;
                var s = area.transform.lossyScale; float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                if (area.GetComponent<Collider>() is SphereCollider sphere) sphere.radius = Radius / scale;
                else if (area.GetComponent<Collider>() is CapsuleCollider capsule) capsule.radius = Radius / scale;
            }
            var tinted = new Dictionary<Material, Material>();
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (!m || !m.shader || m.shader.name != "Custom/Piece" || !m.HasProperty("_Color")) continue;
                    if (!tinted.TryGetValue(m, out var copy))
                    {
                        copy = new Material(m) { name = m.name + "_frost" };
                        copy.SetColor("_Color", m.GetColor("_Color") * Tint);
                        tinted[m] = copy;
                    }
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
            if (!prefab.GetComponent<FrostShieldBarrier>()) prefab.AddComponent<FrostShieldBarrier>();
        }

        // Players, tamed creatures, allied summons, passive animals and the small wild animals cross freely; everything else is held out.
        private static readonly HashSet<string> SmallAnimals = new HashSet<string> { "Boar", "Neck", "Hen" };
        internal static bool CanCross(Character c)
        {
            if (c is Player || c.IsTamed()) return true;
            if (c.m_faction == Character.Faction.Players || c.m_faction == Character.Faction.PlayerSpawned) return true;
            if (c.GetBaseAI() is AnimalAI) return true;
            return SmallAnimals.Contains(Utils.GetPrefabName(c.gameObject));
        }
    }

    internal sealed class FrostShieldBarrier : MonoBehaviour
    {
        private ShieldGenerator shield;
        private ZNetView nview;
        // Creatures seen inside the dome when it was raised (or that spawned in it) stay free until they leave.
        private readonly Dictionary<Character, bool> inside = new Dictionary<Character, bool>();
        private readonly List<Character> stale = new List<Character>();
        private bool wasRaised;

        internal static readonly List<FrostShieldBarrier> Active = new List<FrostShieldBarrier>();

        private void Awake()
        {
            shield = GetComponent<ShieldGenerator>();
            nview = GetComponent<ZNetView>();
        }
        private void OnEnable() => Active.Add(this);
        private void OnDisable() => Active.Remove(this);

        internal bool Raised => shield && shield.m_shieldDome && shield.m_radius > 0f && shield.m_radiusTarget > 0f;
        internal bool Covers(Vector3 point) => Raised && (point - shield.m_shieldDome.transform.position).sqrMagnitude < shield.m_radius * shield.m_radius;
        internal Vector3 Center => shield.m_shieldDome.transform.position;
        internal float CurrentRadius => shield.m_radius;
        // Held out by this dome: not a free crosser and not one of the creatures that were already inside.
        internal bool HoldsOut(Character c) => !FrostShieldGenerator.CanCross(c) && !(inside.TryGetValue(c, out bool isInside) && isInside);

        // The dome covering a point that this creature is kept out of, if any.
        internal static FrostShieldBarrier Shielding(Vector3 point, Character outsider)
        {
            foreach (var barrier in Active) if (barrier && barrier.Covers(point) && barrier.HoldsOut(outsider)) return barrier;
            return null;
        }

        private void FixedUpdate()
        {
            if (!shield || !nview || !nview.IsValid() || !shield.m_shieldDome) return;
            float radius = shield.m_radius;
            if (radius <= 0f || shield.m_radiusTarget <= 0f) { inside.Clear(); wasRaised = false; return; }
            if (!wasRaised && radius >= FrostShieldGenerator.Radius - .01f) { wasRaised = true; FrostShieldLogs.CheckAll(this); }
            FrostShieldLogs.Tick();
            var center = shield.m_shieldDome.transform.position;
            float reach = FrostShieldGenerator.Radius + 5f;
            foreach (var c in Character.GetAllCharacters())
            {
                if (!c || c.IsDead() || !c.m_nview || !c.m_nview.IsValid() || !c.m_nview.IsOwner()) continue;
                var offset = c.transform.position - center;
                float distance = offset.magnitude;
                if (distance > reach) { if (inside.ContainsKey(c)) inside[c] = false; continue; }
                if (FrostShieldGenerator.CanCross(c)) continue;
                float body = c.GetRadius();
                if (!inside.TryGetValue(c, out bool isInside))
                {
                    // Judge against the full radius so a dome still growing from 0 does not push out mobs that were inside.
                    isInside = distance < FrostShieldGenerator.Radius;
                    inside[c] = isInside;
                }
                if (isInside)
                {
                    if (distance > FrostShieldGenerator.Radius + body) inside[c] = false;
                    continue;
                }
                float limit = radius + body;
                if (distance >= limit) continue;
                var normal = distance > .001f ? offset / distance : Vector3.ProjectOnPlane(c.transform.forward, Vector3.up).normalized * -1f;
                var position = center + normal * limit;
                var rb = c.m_body;
                if (rb)
                {
                    rb.position = position;
                    float inward = Vector3.Dot(rb.linearVelocity, normal);
                    if (inward < 0f) rb.linearVelocity -= normal * inward;
                    inward = Vector3.Dot(c.m_currentVel, normal);
                    if (inward < 0f) c.m_currentVel -= normal * inward;
                }
                c.transform.position = position;
            }
            stale.Clear();
            foreach (var key in inside.Keys) if (!key) stale.Add(key);
            foreach (var key in stale) inside.Remove(key);
        }
    }

    // Tree logs touching a dome (thrown by frost trolls, or a felled trunk falling on it) break into their wood and other loot.
    // Event driven: logs present when a dome is raised are checked once; a log spawned near a raised dome is watched only while it moves.
    internal static class FrostShieldLogs
    {
        private const float WatchTime = 15f;
        private static readonly Dictionary<TreeLog, float> watched = new Dictionary<TreeLog, float>();
        private static readonly List<TreeLog> done = new List<TreeLog>(), breaking = new List<TreeLog>();
        private static List<TreeLog> spawned;
        private static float lastTick = -1f;
        private static readonly System.Action<TreeLog, HitData, bool> NativeDestroy =
            AccessTools.MethodDelegate<System.Action<TreeLog, HitData, bool>>(AccessTools.Method(typeof(TreeLog), "Destroy"));

        [HarmonyPatch(typeof(TreeLog), "Awake")]
        private static class Track
        {
            private static void Postfix(TreeLog __instance)
            {
                if (spawned != null) { spawned.Add(__instance); return; }
                foreach (var barrier in FrostShieldBarrier.Active)
                    if (barrier && barrier.Raised && (__instance.transform.position - barrier.Center).sqrMagnitude < 80f * 80f) { watched[__instance] = Time.time + WatchTime; return; }
            }
        }

        // Once, when a dome reaches its full size.
        internal static void CheckAll(FrostShieldBarrier barrier)
        {
            breaking.Clear();
            foreach (var log in Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None))
                if (Straddles(log, barrier.Center, barrier.CurrentRadius)) breaking.Add(log);
            foreach (var log in breaking) Break(log);
        }

        // Only logs that just spawned near a raised dome, until they rest or time out; once per physics tick for all domes.
        internal static void Tick()
        {
            if (watched.Count == 0 || lastTick == Time.fixedTime) return;
            lastTick = Time.fixedTime;
            done.Clear(); breaking.Clear();
            foreach (var pair in watched)
            {
                var log = pair.Key;
                if (!log || Time.time > pair.Value) { done.Add(log); continue; }
                foreach (var barrier in FrostShieldBarrier.Active)
                    if (barrier && barrier.Raised && Straddles(log, barrier.Center, barrier.CurrentRadius)) { breaking.Add(log); break; }
                var body = log.GetComponent<Rigidbody>();
                if (body && body.IsSleeping()) done.Add(log);
            }
            foreach (var log in breaking) { done.Add(log); Break(log); }
            foreach (var log in done) watched.Remove(log);
        }

        private static bool Straddles(TreeLog log, Vector3 center, float radius)
        {
            var nview = log.GetComponent<ZNetView>();
            if (!nview || !nview.IsValid() || !nview.IsOwner()) return false;
            float near, far;
            if (log.GetComponentInChildren<CapsuleCollider>() is CapsuleCollider capsule)
            {
                var t = capsule.transform; var s = t.lossyScale;
                var axis = capsule.direction == 0 ? t.right * s.x : capsule.direction == 1 ? t.up * s.y : t.forward * s.z;
                float thick = capsule.radius * Mathf.Max(capsule.direction == 0 ? 0 : Mathf.Abs(s.x), capsule.direction == 1 ? 0 : Mathf.Abs(s.y), capsule.direction == 2 ? 0 : Mathf.Abs(s.z));
                var mid = t.TransformPoint(capsule.center);
                var half = axis.normalized * Mathf.Max(0f, capsule.height * axis.magnitude * .5f - thick);
                float a = (mid + half - center).magnitude, b = (mid - half - center).magnitude;
                // Closest point of the segment to the centre, for a log lying across the dome's edge with both ends outside.
                var seg = 2f * half; float u = seg.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(center - (mid - half), seg) / seg.sqrMagnitude) : 0f;
                float c = (mid - half + seg * u - center).magnitude;
                near = Mathf.Min(a, b, c) - thick; far = Mathf.Max(a, b) + thick;
            }
            else
            {
                float d = (log.transform.position - center).magnitude;
                near = d - 1f; far = d + 1f;
            }
            return near < radius && far > radius;
        }

        // Native destruction (loot, effects, origin tracking of other patches); the sub-logs it spawns break as well.
        private static void Break(TreeLog log)
        {
            if (!log) return;
            var previous = spawned; var mine = new List<TreeLog>(); spawned = mine;
            try { NativeDestroy(log, null, false); }
            finally { spawned = previous; }
            foreach (var sub in mine) Break(sub);
        }
    }

    // Monsters kept out of a dome ignore what is inside it; a target that runs into a dome is besieged for 30 s, then dropped.
    internal static class FrostShieldSiege
    {
        internal const float Duration = 30f;
        private sealed class Siege { internal Character Target; internal float Start; internal FrostShieldBarrier Dome; }
        private static readonly ConditionalWeakTable<MonsterAI, Siege> Sieges = new ConditionalWeakTable<MonsterAI, Siege>();

        private static bool Owner(BaseAI ai) => ai && ai.m_nview && ai.m_nview.IsValid() && ai.m_nview.IsOwner() && ai.m_character;

        private static Siege Current(BaseAI ai) => ai is MonsterAI monster && Sieges.TryGetValue(monster, out var siege) && siege.Dome ? siege : null;

        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.FindEnemy))]
        private static class IgnoreShielded
        {
            private static void Postfix(BaseAI __instance, ref Character __result)
            {
                if (__result && FrostShieldBarrier.Active.Count > 0 && Owner(__instance) && FrostShieldBarrier.Shielding(__result.transform.position, __instance.m_character)) __result = null;
            }
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        private static class Besiege
        {
            private static void Postfix(MonsterAI __instance, ref bool canHearTarget, ref bool canSeeTarget)
            {
                var ai = __instance;
                if (FrostShieldBarrier.Active.Count == 0 || !Owner(ai)) { if (ai) Sieges.Remove(ai); return; }
                if (ai.m_targetStatic && FrostShieldBarrier.Shielding(ai.m_targetStatic.transform.position, ai.m_character)) ai.m_targetStatic = null;
                var target = ai.m_targetCreature;
                var dome = target ? FrostShieldBarrier.Shielding(target.transform.position, ai.m_character) : null;
                if (!dome) { Sieges.Remove(ai); return; }
                var siege = Sieges.GetOrCreateValue(ai);
                if (siege.Target != target) { siege.Target = target; siege.Start = Time.time; }
                siege.Dome = dome;
                if (Time.time - siege.Start < Duration) return;
                Sieges.Remove(ai);
                ai.m_targetCreature = null;
                canHearTarget = canSeeTarget = false;
                ai.SetAlerted(false);
                ai.SetTargetInfo(ZDOID.None);
            }
        }

        // Besiegers walk up to the dome, then wait at its edge facing their target.
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTowards))]
        private static class HoldAtEdge
        {
            private static bool Prefix(BaseAI __instance)
            {
                var siege = Current(__instance);
                if (siege == null || !siege.Target) return true;
                var c = __instance.m_character;
                if ((c.transform.position - siege.Dome.Center).magnitude > siege.Dome.CurrentRadius + c.GetRadius() + 4f) return true;
                c.SetMoveDir(Vector3.zero);
                c.SetRun(false);
                var look = siege.Target.transform.position - c.transform.position;
                if (Utils.LengthXZ(look) > .01f) __instance.LookTowards(look.normalized);
                return false;
            }
        }

        // No attack at a target behind the dome (melee through the wall, ranged shots wasted on it).
        [HarmonyPatch(typeof(MonsterAI), "DoAttack")]
        private static class NoAttack
        {
            private static bool Prefix(MonsterAI __instance, ref bool __result)
            {
                if (Current(__instance) == null) return true;
                __result = false;
                return false;
            }
        }
    }
}
