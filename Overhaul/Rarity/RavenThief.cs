using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Amber pearl in a trinket (Pie voleuse): Hugin, small, sits on the local player's right shoulder. When an item that fits in the
    // inventory lies within Radius m of the player, it takes off along the way it faces, curves to the item, picks it up on touching
    // it, goes on to the next one, and once nothing is left comes back from behind to land on the shoulder facing forward.
    // When walls, roofs, rocks or trees stand in the way of that curve, it flies a path found around them (an open door, a window…);
    // an item with no way to it is left alone for a while. Timings are those validated on the preview
    // (Tools/3D/PlayerFbxWork HuginClips.RavenShoulder). Seen by the local player only.
    internal static class RavenThief
    {
        const float Scale = .25f, Lift = 1.6f, Land = 1.5f, LoopStart = 3.8f, Period = .6333f, FadeTime = .9f, SpeedMin = 12.6f, SpeedMax = 14.4f, Radius = 30f, TakeOffSpeed = 2f,
            ItemHeight = .35f, Search = .25f, OwnWait = 1f, SkipTime = 10f, PickRadius = 2f, GoneTime = .4f, Appear = 1f;
        enum State { Perched, TakeOff, Flying, Hovering, Returning, Gone, Landing }

        static GameObject raven, animated; // the clips are sampled on the Animator's object
        static Transform hip, claw, shoulder, arm, leftArm;
        static Transform[] bones, legs;
        static Quaternion[] tucked, fadeRot; static Vector3[] fadePos;
        static AnimationClip idle, away, swoop;
        static Vector3 clawOffset, hipOffset; // from the raven's root, idle pose, facing +z
        static Player owner;
        static float failedAt = -100;
        // Items it could not reach or take, left alone until the time given (a door may open meanwhile).
        static readonly Dictionary<ItemDrop, float> skipped = new Dictionary<ItemDrop, float>();

        static State state;
        static float clock, flight, speed, traveled, fade, nextSearch, waiting;
        static ItemDrop target;
        // The way flown: a curve (from, b, c, to) or, when route is set, a path through the free space.
        static Vector3 from, b, c, to, heading;
        static Vector3[] route; static float[] routeLength;
        // A path being searched, a few steps per frame, while the raven waits (on the shoulder or hovering).
        static RavenPath planning;
        static ItemDrop planned; // null: the path leads back to the player
        static Vector3 hoverAt;
        static EffectList poof; // Hugin's own despawn puff
        static Renderer[] renderers;

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            var def = player && !player.IsDead() ? CatalystEffects.InKind(player, "PreciousLoot", CatalystEffects.Trinket) : null;
            if (def == null) { Remove(); return; }
            if (owner != player) { Remove(); owner = player; }
            if (!raven && (Time.time < failedAt + 10 || !Build())) return;
            bool hidden = player.IsTeleporting() || player.InCutscene() || player.InBed();
            if (raven.activeSelf == hidden) { raven.SetActive(!hidden); if (hidden) { state = State.Perched; target = null; planning = null; route = null; Show(true); } }
            if (hidden) return;

            float dt = Time.deltaTime;
            switch (state)
            {
                case State.Perched:
                    clock += dt;
                    OnShoulder(idle, clock % idle.length);
                    if (planning != null)
                    {
                        if (!planning.Step()) break;
                        var found = planning; planning = null;
                        if (found.Path == null || !Valid(player, planned)) { Skip(planned); break; }
                        target = planned; route = found.Path; state = State.TakeOff; clock = 0;
                        break;
                    }
                    if (Time.time < nextSearch) break;
                    nextSearch = Time.time + Search;
                    target = Find(player, raven.transform.position); if (!target) break;
                    // The curve out of the shoulder, or else a path searched from it.
                    Curve(hip.position, Flat(raven.transform.forward), Goal(target));
                    if (CurveClear()) { route = null; state = State.TakeOff; clock = 0; }
                    else { planned = target; planning = new RavenPath(hip.position, Goal(target), player.transform.position); }
                    break;
                case State.TakeOff:
                    clock += dt * TakeOffSpeed;
                    OnShoulder(away, clock);
                    if (clock >= Lift)
                    {
                        flight = clock; heading = Flat(raven.transform.forward); Detach();
                        if (!Valid(player, target)) { Next(player, hip.position, heading); break; }
                        if (route != null) Follow(hip.position, route, target);
                        else { Curve(hip.position, heading, Goal(target)); Begin(State.Flying); }
                    }
                    break;
                case State.Flying:
                    flight += dt;
                    if (!Valid(player, target)) { Next(player, hip.position, Tangent(Param())); break; }
                    if (route == null) to = Goal(target);
                    traveled += speed * dt;
                    float k = Param();
                    Fly(away, Beat(flight), k, Mathf.Clamp01((flight - 2.4f) / .8f));
                    if (k >= 1)
                    {
                        if (!target.CanPickup(true)) { target.RequestOwn(); waiting += dt; if (waiting < OwnWait) break; Skip(target); }
                        else { target.Load(); player.Pickup(target.gameObject, true, true); }
                        PickAround(player, Point(1));
                        Next(player, Point(1), Tangent(1));
                    }
                    break;
                case State.Hovering:
                    flight += dt;
                    Hover(Beat(flight));
                    if (planning == null || !planning.Step()) break;
                    var path = planning; planning = null;
                    if (planned == null)
                    {
                        // Back to the player: along the path found up to behind them, else straight (better than staying lost).
                        if (path.Path != null) Follow(hoverAt, path.Path, null);
                        else Vanish(hip.position);
                    }
                    else if (path.Path != null && Valid(player, planned)) Follow(hoverAt, path.Path, planned);
                    else { Skip(planned); Next(player, hoverAt, heading); }
                    break;
                case State.Returning:
                    flight += dt;
                    Seat(out var perch, out var approach);
                    if (route == null) { to = perch; c = approach; OverGround(); }
                    traveled += speed * dt;
                    float r = Param();
                    if (route != null)
                    {
                        // On a path: the landing curve takes over at its end, or as soon as it is clear.
                        Fly(away, Beat(flight), r, 1);
                        if (r >= 1 || Time.time >= nextSearch && LandingClear(perch, approach)) { nextSearch = Time.time + Search; Return(hip.position, Tangent(r)); }
                        break;
                    }
                    if (r < .7f && Time.time >= nextSearch)
                    {
                        nextSearch = Time.time + Search;
                        var item = Find(player, hip.position);
                        if (item) { Toward(player, hip.position, Tangent(r), item); break; }
                    }
                    bool landing = r >= .7f;
                    if (landing && fadeRot == null) { fadeRot = bones.Select(t => t.localRotation).ToArray(); fadePos = bones.Select(t => t.localPosition).ToArray(); fade = 1; }
                    Fly(landing ? swoop : away, landing ? 1f + Mathf.Max(0, r - .84f) / .16f * (Land - 1f) : Beat(flight), r, landing ? 0 : 1);
                    if (r >= 1) { state = State.Landing; clock = Land; fadeRot = null; }
                    break;
                case State.Gone:
                    clock += dt;
                    if (clock < GoneTime) break;
                    Seat(out var seat, out _);
                    Show(true); Puff(seat + Vector3.up * .15f);
                    state = State.Landing; clock = Appear;
                    OnShoulder(swoop, clock);
                    break;
                case State.Landing:
                    clock += dt;
                    OnShoulder(swoop, clock);
                    if (clock >= swoop.length) { state = State.Perched; clock = 0; }
                    break;
            }
        }

        // From where it is: on to the nearest item, or back to the player.
        static void Next(Player player, Vector3 at, Vector3 dir)
        {
            var item = Find(player, at);
            if (item) Toward(player, at, dir, item); else Back(player, at, dir);
        }
        // To an item: along the curve if nothing is in its way, else it hovers while a path is searched.
        static void Toward(Player player, Vector3 at, Vector3 dir, ItemDrop item)
        {
            target = item;
            Curve(at, dir, Goal(item));
            if (CurveClear()) { route = null; Begin(State.Flying); return; }
            Wait(at, dir, item, Goal(item), player);
        }
        static void Back(Player player, Vector3 at, Vector3 dir)
        {
            target = null;
            Seat(out var perch, out var approach);
            from = at; b = at + Flat(dir) * Vector3.Distance(at, player.transform.position) * .4f + Vector3.up * .4f; c = approach; to = perch; route = null;
            OverGround();
            if (CurveClear()) Return(at, dir);
            else Wait(at, dir, null, approach, player);
        }
        // No way back to the player: it vanishes in a puff, to reappear in another above the shoulder and land there.
        static void Vanish(Vector3 at)
        {
            Puff(at); Show(false);
            target = null; route = null; planning = null; fadeRot = null;
            state = State.Gone; clock = 0;
        }
        static void Puff(Vector3 at) { if (poof != null) poof.Create(at, Quaternion.identity); }
        static void Show(bool visible) { if (renderers != null) foreach (var r in renderers) if (r) r.enabled = visible; }
        static void Wait(Vector3 at, Vector3 dir, ItemDrop item, Vector3 goal, Player player)
        {
            hoverAt = at; heading = Flat(dir); planned = item; route = null;
            planning = new RavenPath(at, goal, player.transform.position);
            state = State.Hovering;
        }

        // Where its claws hold: on the top of the upper arm, just past the shoulder.
        static Vector3 Perch() { var body = Body(); return arm.position + Vector3.up * .06f + body * Vector3.forward * .08f + body * Vector3.right * .04f; }
        // The way the upper body faces: square to the line of the shoulders (the torso turns away from the character's own facing in some stances).
        static Quaternion Body() { var right = Flat(arm.position - leftArm.position); return Quaternion.LookRotation(Vector3.Cross(right, Vector3.up), Vector3.up); }
        static Vector3 Goal(ItemDrop item) => item.transform.position + Vector3.up * ItemHeight;
        // A new curve from a point, leaving along the heading, to a point.
        static void Curve(Vector3 start, Vector3 dir, Vector3 end)
        {
            float reach = Vector3.Distance(start, end) * .45f;
            from = start; to = end; b = start + Flat(dir) * reach + Vector3.up * .4f; c = end - Flat(end - start) * reach + Vector3.up * .4f;
            route = null;
            OverGround();
        }
        // A path found: flown from the raven's position through its points.
        static void Follow(Vector3 start, Vector3[] path, ItemDrop item)
        {
            var points = new List<Vector3> { start }; points.AddRange(path.Skip(1));
            route = RavenPath.Smooth(points);
            routeLength = new float[route.Length];
            for (int i = 1; i < route.Length; i++) routeLength[i] = routeLength[i - 1] + Vector3.Distance(route[i - 1], route[i]);
            target = item;
            Begin(item ? State.Flying : State.Returning);
        }
        static void Return(Vector3 start, Vector3 dir)
        {
            target = null; fadeRot = null; route = null;
            from = start; b = start + Flat(dir) * Vector3.Distance(start, owner.transform.position) * .4f + Vector3.up * .4f;
            Begin(State.Returning);
        }
        // Each leg flies at its own speed, the same all along it.
        static void Begin(State next) { state = next; waiting = 0; traveled = 0; speed = Random.Range(SpeedMin, SpeedMax); if (route == null) OverGround(); }
        static void Skip(ItemDrop item) { if (item) skipped[item] = Time.time + SkipTime; }

        // The terrain height (pieces and roofs ignored, so it never climbs over a house it flies in).
        static float Ground(Vector3 p) => ZoneSystem.instance ? ZoneSystem.instance.GetGroundHeight(p) : float.MinValue;
        // Raises the middle of the curve so it stays ItemHeight above the terrain all along.
        static void OverGround()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                float deficit = 0;
                for (int i = 1; i < 12; i++) { var p = Point(i / 12f); deficit = Mathf.Max(deficit, Ground(p) + ItemHeight - p.y); }
                if (deficit <= .01f) return;
                b.y += deficit * 1.4f; c.y += deficit * 1.4f;
            }
        }
        static bool CurveClear()
        {
            var last = Point(0);
            for (int i = 1; i <= 24; i++) { var p = Point(i / 24f); if (!RavenPath.Clear(last, p)) return false; last = p; }
            return true;
        }
        // Whether the landing curve from where the raven is would be clear.
        static bool LandingClear(Vector3 perch, Vector3 approach)
        {
            var at = hip.position; var dir = Tangent(Param());
            Vector3 f = from, bb = b, cc = c, t = to; var saved = route;
            from = at; b = at + Flat(dir) * Vector3.Distance(at, owner.transform.position) * .4f + Vector3.up * .4f; c = approach; to = perch; route = null;
            OverGround(); bool clear = CurveClear();
            from = f; b = bb; c = cc; to = t; route = saved;
            return clear;
        }

        static Vector3 Point(float k)
        {
            if (route == null) { float u = 1 - k; return u * u * u * from + 3 * u * u * k * b + 3 * u * k * k * c + k * k * k * to; }
            float s = k * routeLength[routeLength.Length - 1]; int i = 1;
            while (i < route.Length - 1 && routeLength[i] < s) i++;
            float span = routeLength[i] - routeLength[i - 1];
            return Vector3.Lerp(route[i - 1], route[i], span > 1e-4f ? (s - routeLength[i - 1]) / span : 1);
        }
        static Vector3 Tangent(float k)
        {
            if (route == null) { float u = 1 - k; return 3 * u * u * (b - from) + 6 * u * k * (c - b) + 3 * k * k * (to - c); }
            float s = k * routeLength[routeLength.Length - 1]; int i = 1;
            while (i < route.Length - 1 && routeLength[i] < s) i++;
            return route[i] - route[i - 1];
        }
        // The curve parameter at the distance flown, so the speed does not depend on the way's shape.
        static float Param()
        {
            if (route != null) { float total = routeLength[routeLength.Length - 1]; return total <= 0 ? 1 : Mathf.Clamp01(traveled / total); }
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

        // On the shoulder it is a child of the shoulder bone, placed once as it lands: the body carries it from then on.
        static bool attached;
        static void OnShoulder(AnimationClip clip, float time)
        {
            if (!attached)
            {
                var body = Body();
                raven.transform.SetPositionAndRotation(Perch() - body * clawOffset, body);
                raven.transform.SetParent(shoulder, true); attached = true;
            }
            clip.SampleAnimation(animated, time);
        }
        static void Detach()
        {
            if (!attached) return;
            raven.transform.SetParent(null, true); raven.transform.localScale = Vector3.one * Scale; attached = false;
        }
        // Where its hip will be once landed, and the point behind the player it comes in from (for the flight back).
        static void Seat(out Vector3 perch, out Vector3 approach)
        {
            var body = Body();
            perch = Perch() - body * clawOffset + body * hipOffset;
            approach = perch - body * Vector3.forward * 2.4f + Vector3.up * .5f;
        }
        static void Pose(AnimationClip clip, float time, float tuck)
        {
            clip.SampleAnimation(animated, time);
            for (int i = 0; i < legs.Length; i++) legs[i].localRotation = Quaternion.Slerp(legs[i].localRotation, tucked[i], tuck);
        }
        // In flight the body follows the way and faces along it; legs tucked as in the swoop, and the switch to the swoop faded.
        static void Fly(AnimationClip clip, float time, float k, float tuck)
        {
            Detach(); Pose(clip, time, tuck);
            if (fade > 0 && fadeRot != null)
            {
                for (int i = 0; i < bones.Length; i++) { bones[i].localRotation = Quaternion.Slerp(bones[i].localRotation, fadeRot[i], fade); if (bones[i] != hip) bones[i].localPosition = Vector3.Lerp(bones[i].localPosition, fadePos[i], fade); }
                fade -= Time.deltaTime / FadeTime;
            }
            raven.transform.rotation = Quaternion.LookRotation(Flat(Tangent(k)), Vector3.up);
            var at = Point(k); if (route == null) at.y = Mathf.Max(at.y, Ground(at) + ItemHeight);
            raven.transform.position += at - hip.position;
        }
        // Beating its wings in place while it looks for a way.
        static void Hover(float time)
        {
            Detach(); Pose(away, time, 1);
            raven.transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
            raven.transform.position += hoverAt - hip.position;
        }

        // The nearest item to a point that the player would pick up and that fits.
        static ItemDrop Find(Player player, Vector3 near)
        {
            ItemDrop best = null; float bestDistance = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(player.transform.position, Radius, player.m_autoPickupMask))
            {
                var item = collider.attachedRigidbody ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null;
                if (!Valid(player, item) || skipped.TryGetValue(item, out float until) && Time.time < until) continue;
                float d = Vector3.Distance(near, item.transform.position);
                if (d < bestDistance) { best = item; bestDistance = d; }
            }
            return best;
        }
        // The other items within PickRadius m of where it lands, taken at once (those another player still owns are asked for, and left for later).
        static void PickAround(Player player, Vector3 at)
        {
            foreach (var collider in Physics.OverlapSphere(at, PickRadius, player.m_autoPickupMask))
            {
                var item = collider.attachedRigidbody ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null;
                if (!Valid(player, item)) continue;
                if (!item.CanPickup(true)) { item.RequestOwn(); continue; }
                item.Load(); player.Pickup(item.gameObject, true, true);
            }
        }
        static bool Valid(Player player, ItemDrop item)
        {
            if (!item || !item.m_autoPickup || item.IsPiece() || item.InTar() || player.HaveUniqueKey(item.m_itemData.m_shared.m_name)) return false;
            var view = item.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || Vector3.Distance(player.transform.position, item.transform.position) > Radius) return false;
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
            poof = source.GetComponent<Raven>()?.m_despawnEffect;
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
            leftArm = visual.FirstOrDefault(t => t.name.EndsWith("LeftArm"));
            if (!idle || !away || !swoop || !hip || !claw || !shoulder || !arm || !leftArm) { Utility.Log.LogWarning("Pie voleuse: raven model or player shoulder incomplete"); Object.Destroy(raven); raven = null; failedAt = Time.time; return false; }
            bones = all.Where(t => t != raven.transform).ToArray();
            legs = all.Where(t => { for (var q = t; q && q != hip; q = q.parent) if (q.name.StartsWith("Leg1.")) return true; return false; }).ToArray();
            swoop.SampleAnimation(animated, .3f); tucked = legs.Select(l => l.localRotation).ToArray();
            raven.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            idle.SampleAnimation(animated, 0); clawOffset = claw.position; hipOffset = hip.position;
            renderers = raven.GetComponentsInChildren<Renderer>(true).Where(x => x.enabled).ToArray();
            raven.SetActive(true);
            state = State.Perched; clock = 0; target = null; fadeRot = null; route = null; planning = null; attached = false;
            return true;
        }

        static void Remove()
        {
            if (raven) Object.Destroy(raven);
            raven = null; owner = null; target = null; planning = null; route = null; attached = false; skipped.Clear();
        }
    }

    // A flying path through the free space (a 3D grid of 0.5 m cells, A*), searched a few hundred cells per frame. Walls, roofs, closed
    // doors, rocks, trees and the terrain block it; an open door or a window lets it through. Path stays null when there is no way.
    internal class RavenPath
    {
        const float Cell = .5f, Body = .2f, Reach = 40f;
        const int PerFrame = 300, MaxCells = 12000;
        static int mask;
        internal static int Mask => mask != 0 ? mask : mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");

        internal Vector3[] Path;
        readonly Vector3 origin, goal, center;
        readonly Vector3Int goalKey;
        readonly Dictionary<Vector3Int, bool> free = new Dictionary<Vector3Int, bool>();
        readonly Dictionary<Vector3Int, float> cost = new Dictionary<Vector3Int, float>();
        readonly Dictionary<Vector3Int, Vector3Int> came = new Dictionary<Vector3Int, Vector3Int>();
        readonly List<(float f, Vector3Int key)> open = new List<(float, Vector3Int)>();
        int expanded; bool done;

        internal RavenPath(Vector3 start, Vector3 end, Vector3 player)
        {
            origin = start; goal = end; center = player; goalKey = Key(end);
            cost[Vector3Int.zero] = 0; Push(Vector3.Distance(start, end), Vector3Int.zero);
        }

        // Some more of the search; true once it is over (Path set if a way was found).
        internal bool Step()
        {
            for (int n = 0; n < PerFrame && !done; n++)
            {
                if (open.Count == 0 || expanded++ > MaxCells) { done = true; break; }
                var key = Pop();
                var at = Pos(key);
                if ((key - goalKey).sqrMagnitude <= 3 && Clear(at, goal)) { Build(key); done = true; break; }
                float g = cost[key];
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    var next = key + new Vector3Int(x, y, z);
                    if (!Free(next)) continue;
                    float step = Mathf.Sqrt(x * x + y * y + z * z) * Cell, ng = g + step;
                    if (cost.TryGetValue(next, out float old) && old <= ng) continue;
                    cost[next] = ng; came[next] = key;
                    Push(ng + Vector3.Distance(Pos(next), goal), next);
                }
            }
            return done;
        }

        Vector3Int Key(Vector3 p) { var d = (p - origin) / Cell; return new Vector3Int(Mathf.RoundToInt(d.x), Mathf.RoundToInt(d.y), Mathf.RoundToInt(d.z)); }
        Vector3 Pos(Vector3Int k) => origin + (Vector3)k * Cell;
        bool Free(Vector3Int k)
        {
            if (free.TryGetValue(k, out bool ok)) return ok;
            var p = Pos(k);
            ok = (p - center).sqrMagnitude < Reach * Reach && !Physics.CheckSphere(p, Body, Mask, QueryTriggerInteraction.Ignore);
            free[k] = ok; return ok;
        }
        void Push(float f, Vector3Int k)
        {
            open.Add((f, k)); int i = open.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (open[p].f <= open[i].f) break; (open[p], open[i]) = (open[i], open[p]); i = p; }
        }
        Vector3Int Pop()
        {
            var top = open[0].key; open[0] = open[open.Count - 1]; open.RemoveAt(open.Count - 1);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < open.Count && open[l].f < open[m].f) m = l;
                if (r < open.Count && open[r].f < open[m].f) m = r;
                if (m == i) break;
                (open[m], open[i]) = (open[i], open[m]); i = m;
            }
            return top;
        }
        // The cells walked back from the end, then only the corners it cannot cut.
        void Build(Vector3Int end)
        {
            var cells = new List<Vector3> { goal };
            for (var k = end; ; k = came[k]) { cells.Add(Pos(k)); if (k == Vector3Int.zero) break; }
            cells.Reverse(); cells[0] = origin;
            var kept = new List<Vector3> { cells[0] };
            int at = 0;
            while (at < cells.Count - 1)
            {
                int far = at + 1;
                for (int j = cells.Count - 1; j > at + 1; j--) if (Clear(cells[at], cells[j])) { far = j; break; }
                kept.Add(cells[far]); at = far;
            }
            Path = kept.ToArray();
        }

        // Whether the raven's body passes in a straight line.
        internal static bool Clear(Vector3 a, Vector3 b)
        {
            var d = b - a; float length = d.magnitude;
            return length < 1e-3f || !Physics.SphereCast(a, Body * .75f, d / length, out _, length, Mask, QueryTriggerInteraction.Ignore);
        }
        // Rounded corners: a Catmull-Rom curve through the points, as dense points; it keeps a straight line where that one would cut a corner.
        internal static Vector3[] Smooth(List<Vector3> points)
        {
            if (points.Count < 3) return points.ToArray();
            var result = new List<Vector3> { points[0] };
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 p0 = points[Mathf.Max(0, i - 1)], p1 = points[i], p2 = points[i + 1], p3 = points[Mathf.Min(points.Count - 1, i + 2)];
                int steps = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(p1, p2) / .3f));
                var segment = new List<Vector3>();
                for (int s = 1; s <= steps; s++)
                {
                    float t = s / (float)steps, t2 = t * t, t3 = t2 * t;
                    segment.Add(.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (3 * p1 - p0 - 3 * p2 + p3) * t3));
                }
                bool clear = true; var last = p1;
                foreach (var p in segment) { if (!Clear(last, p)) { clear = false; break; } last = p; }
                if (clear) result.AddRange(segment); else result.Add(p2);
            }
            return result.ToArray();
        }
    }
}
