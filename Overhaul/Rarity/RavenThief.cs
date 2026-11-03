using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Amber pearl in a trinket (Pie voleuse): Hugin, small, sits on the local player's right shoulder. When an item that fits in the
    // inventory lies within Radius m of the player, it takes off along the way it faces, curves to the item, picks it up on touching
    // it, goes on to the next one, and once nothing is left comes back from behind to land on the shoulder facing forward.
    // Timings are those validated on the preview (Tools/3D/PlayerFbxWork HuginClips.RavenShoulder). Seen by the local player only.
    internal static class RavenThief
    {
        const float Scale = .25f, Lift = 1.6f, Land = 1.5f, LoopStart = 3.8f, Period = .6333f, FadeTime = .9f, SpeedMin = 4.2f, SpeedMax = 4.8f,
            ItemHeight = .35f, Search = .25f, OwnWait = 1f;
        enum State { Perched, TakeOff, Flying, Returning, Landing }

        static GameObject raven, animated; // the clips are sampled on the Animator's object
        static Transform hip, claw, shoulder, arm;
        static Transform[] bones, legs;
        static Quaternion[] tucked, fadeRot; static Vector3[] fadePos;
        static AnimationClip idle, away, swoop;
        static Vector3 clawOffset, hipOffset; // from the raven's root, idle pose, facing +z
        static Player owner;
        static float failedAt = -100;
        static ItemDrop skipped; // an item it could not take after waiting OwnWait s

        static State state;
        static float clock, flight, speed, traveled, fade, nextSearch, waiting;
        static ItemDrop target;
        static Vector3 from, b, c, to, heading;

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            var def = player && !player.IsDead() ? CatalystEffects.InKind(player, "PreciousLoot", CatalystEffects.Trinket) : null;
            if (def == null) { Remove(); return; }
            if (owner != player) { Remove(); owner = player; }
            if (!raven && (Time.time < failedAt + 10 || !Build())) return;
            bool hidden = player.IsTeleporting() || player.InCutscene() || player.InBed();
            if (raven.activeSelf == hidden) { raven.SetActive(!hidden); if (hidden) { state = State.Perched; target = null; } }
            if (hidden) return;

            float dt = Time.deltaTime, radius = def.Get("Radius", 30);
            Quaternion facing = Quaternion.LookRotation(Flat(player.transform.forward), Vector3.up);
            Vector3 surface = Vector3.Lerp(shoulder.position, arm.position, .75f) + Vector3.up * .07f;
            Vector3 perch = surface - facing * clawOffset + facing * hipOffset;
            switch (state)
            {
                case State.Perched:
                    clock += dt;
                    OnShoulder(idle, clock % idle.length, surface, facing);
                    if (Time.time >= nextSearch) { nextSearch = Time.time + Search; target = Find(player, radius, raven.transform.position); if (target) { state = State.TakeOff; clock = 0; } }
                    break;
                case State.TakeOff:
                    clock += dt;
                    OnShoulder(away, clock, surface, facing);
                    if (clock >= Lift)
                    {
                        flight = clock; heading = facing * Vector3.forward;
                        if (Valid(player, target, radius)) Leg(hip.position, heading, target);
                        else Return(hip.position, heading);
                    }
                    break;
                case State.Flying:
                    flight += dt;
                    if (!Valid(player, target, radius))
                    {
                        var at = hip.position; var next = Find(player, radius, at);
                        if (next) Leg(at, Tangent(Param()), next); else Return(at, Tangent(Param()));
                        break;
                    }
                    to = target.transform.position + Vector3.up * ItemHeight;
                    traveled += speed * dt;
                    float k = Param();
                    Fly(away, Beat(flight), k, Mathf.Clamp01((flight - 2.4f) / .8f));
                    if (k >= 1)
                    {
                        if (!target.CanPickup(true)) { target.RequestOwn(); waiting += dt; if (waiting < OwnWait) break; skipped = target; }
                        else { target.Load(); player.Pickup(target.gameObject, true, true); }
                        var leave = Tangent(1); var nextItem = Find(player, radius, to);
                        if (nextItem) Leg(to, leave, nextItem); else Return(to, leave);
                    }
                    break;
                case State.Returning:
                    flight += dt;
                    to = perch; c = perch - facing * Vector3.forward * 2.4f + Vector3.up * .5f;
                    traveled += speed * dt;
                    float r = Param();
                    if (r < .7f && Time.time >= nextSearch)
                    {
                        nextSearch = Time.time + Search;
                        var item = Find(player, radius, hip.position);
                        if (item) { Leg(hip.position, Tangent(r), item); break; }
                    }
                    bool landing = r >= .7f;
                    if (landing && fadeRot == null) { fadeRot = bones.Select(t => t.localRotation).ToArray(); fadePos = bones.Select(t => t.localPosition).ToArray(); fade = 1; }
                    Fly(landing ? swoop : away, landing ? 1f + Mathf.Max(0, r - .84f) / .16f * (Land - 1f) : Beat(flight), r, landing ? 0 : 1);
                    if (r >= 1) { state = State.Landing; clock = Land; fadeRot = null; }
                    break;
                case State.Landing:
                    clock += dt;
                    OnShoulder(swoop, clock, surface, facing);
                    if (clock >= swoop.length) { state = State.Perched; clock = 0; }
                    break;
            }
        }

        // A new curve from a point, leaving along the heading, to an item (or back to the shoulder).
        static void Leg(Vector3 start, Vector3 dir, ItemDrop item)
        {
            target = item; waiting = 0; state = State.Flying;
            to = item.transform.position + Vector3.up * ItemHeight;
            float reach = Vector3.Distance(start, to) * .45f;
            from = start; b = start + Flat(dir) * reach + Vector3.up * .4f; c = to - Flat(to - start) * reach + Vector3.up * .4f;
            Start();
        }
        static void Return(Vector3 start, Vector3 dir)
        {
            target = null; state = State.Returning; fadeRot = null;
            from = start; b = start + Flat(dir) * Vector3.Distance(start, owner.transform.position) * .4f + Vector3.up * .4f;
            Start();
        }
        // Each leg flies at its own speed, the same all along it.
        static void Start() { traveled = 0; speed = Random.Range(SpeedMin, SpeedMax); }

        static Vector3 Point(float k) { float u = 1 - k; return u * u * u * from + 3 * u * u * k * b + 3 * u * k * k * c + k * k * k * to; }
        static Vector3 Tangent(float k) { float u = 1 - k; return 3 * u * u * (b - from) + 6 * u * k * (c - b) + 3 * k * k * (to - c); }
        // The curve parameter at the distance flown, so the speed does not depend on the curve's shape.
        static float Param()
        {
            const int N = 48; float length = 0; var last = from;
            var lengths = new float[N + 1];
            for (int i = 1; i <= N; i++) { var p = Point(i / (float)N); length += Vector3.Distance(last, p); lengths[i] = length; last = p; }
            if (traveled >= length) return 1;
            int at = 1; while (lengths[at] < traveled) at++;
            return (at - 1 + (traveled - lengths[at - 1]) / Mathf.Max(1e-4f, lengths[at] - lengths[at - 1])) / N;
        }
        // The wing beat loop taken from the end of the take-off.
        static float Beat(float t) => t < LoopStart ? t : LoopStart + (t - LoopStart) % Period;
        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        static void OnShoulder(AnimationClip clip, float time, Vector3 surface, Quaternion facing)
        {
            raven.transform.SetPositionAndRotation(surface - facing * clawOffset, facing);
            clip.SampleAnimation(animated, time);
        }
        // In flight the body follows the curve and faces along it; legs tucked as in the swoop, and the switch to the swoop faded.
        static void Fly(AnimationClip clip, float time, float k, float tuck)
        {
            clip.SampleAnimation(animated, time);
            for (int i = 0; i < legs.Length; i++) legs[i].localRotation = Quaternion.Slerp(legs[i].localRotation, tucked[i], tuck);
            if (fade > 0 && fadeRot != null)
            {
                for (int i = 0; i < bones.Length; i++) { bones[i].localRotation = Quaternion.Slerp(bones[i].localRotation, fadeRot[i], fade); if (bones[i] != hip) bones[i].localPosition = Vector3.Lerp(bones[i].localPosition, fadePos[i], fade); }
                fade -= Time.deltaTime / FadeTime;
            }
            raven.transform.rotation = Quaternion.LookRotation(Flat(Tangent(k)), Vector3.up);
            raven.transform.position += Point(k) - hip.position;
        }

        // The nearest item to a point that the player would pick up and that fits.
        static ItemDrop Find(Player player, float radius, Vector3 near)
        {
            ItemDrop best = null; float bestDistance = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(player.transform.position, radius, player.m_autoPickupMask))
            {
                var item = collider.attachedRigidbody ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null;
                if (!Valid(player, item, radius)) continue;
                float d = Vector3.Distance(near, item.transform.position);
                if (d < bestDistance) { best = item; bestDistance = d; }
            }
            return best;
        }
        static bool Valid(Player player, ItemDrop item, float radius)
        {
            if (!item || item == skipped || !item.m_autoPickup || item.IsPiece() || item.InTar() || player.HaveUniqueKey(item.m_itemData.m_shared.m_name)) return false;
            var view = item.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || Vector3.Distance(player.transform.position, item.transform.position) > radius) return false;
            return player.GetInventory().CanAddItem(item.m_itemData) && item.m_itemData.GetWeight() + player.GetInventory().GetTotalWeight() <= player.GetMaxCarryWeight();
        }

        // Hugin (the black raven; Munin is the white one) from the game's tutorial ravens, stripped down to its model and clips.
        static bool Build()
        {
            GameObject source = null;
            if (Tutorial.instance && Tutorial.instance.m_ravenPrefab)
                source = Tutorial.instance.m_ravenPrefab.GetComponentsInChildren<Raven>(true).FirstOrDefault(r => !r.m_isMunin)?.gameObject;
            if (!source) source = Jotunn.Managers.PrefabManager.Cache.GetPrefab<Raven>("Hugin")?.gameObject;
            if (!source) { Utility.Log.LogWarning("Pie voleuse: Hugin not found"); failedAt = Time.time; return false; }
            // Built under an inactive holder so the raven's own scripts never wake.
            var holder = new GameObject("Overhaul_RavenThiefHolder"); holder.SetActive(false);
            raven = Object.Instantiate(source, holder.transform);
            foreach (var behaviour in raven.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
            foreach (var collider in raven.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            foreach (var body in raven.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
            foreach (var t in raven.GetComponentsInChildren<Transform>(true)) if (t.name == "exclamation" || t.name.StartsWith("Particle")) t.gameObject.SetActive(false);
            raven.name = "Overhaul_RavenThief";
            raven.transform.SetParent(null); Object.Destroy(holder);
            raven.transform.localScale = Vector3.one * Scale;
            var animator = raven.GetComponentInChildren<Animator>(true); if (animator) { animator.enabled = false; animated = animator.gameObject; }
            var clips = animator ? animator.runtimeAnimatorController.animationClips : new AnimationClip[0];
            idle = clips.FirstOrDefault(x => x.name == "HuginIdle"); away = clips.FirstOrDefault(x => x.name == "HuginFlyAway"); swoop = clips.FirstOrDefault(x => x.name == "HuginSwoopIn");
            var all = raven.GetComponentsInChildren<Transform>(true);
            hip = all.FirstOrDefault(t => t.name == "Hip"); claw = all.FirstOrDefault(t => t.name == "Mid3.r_end");
            var visual = owner.m_visual ? owner.m_visual.GetComponentsInChildren<Transform>(true) : owner.GetComponentsInChildren<Transform>(true);
            shoulder = visual.FirstOrDefault(t => t.name.EndsWith("RightShoulder")); arm = visual.FirstOrDefault(t => t.name.EndsWith("RightArm"));
            if (!idle || !away || !swoop || !hip || !claw || !shoulder || !arm) { Utility.Log.LogWarning("Pie voleuse: raven model or player shoulder incomplete"); Object.Destroy(raven); raven = null; failedAt = Time.time; return false; }
            bones = all.Where(t => t != raven.transform).ToArray();
            legs = all.Where(t => { for (var q = t; q && q != hip; q = q.parent) if (q.name.StartsWith("Leg1.")) return true; return false; }).ToArray();
            swoop.SampleAnimation(animated, .3f); tucked = legs.Select(l => l.localRotation).ToArray();
            raven.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            idle.SampleAnimation(animated, 0); clawOffset = claw.position; hipOffset = hip.position;
            raven.SetActive(true);
            state = State.Perched; clock = 0; target = null; fadeRot = null;
            return true;
        }

        static void Remove()
        {
            if (raven) Object.Destroy(raven);
            raven = null; owner = null; target = null;
        }
    }
}
