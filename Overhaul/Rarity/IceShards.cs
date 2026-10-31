using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Freeze gland in the weapon held: an ice shard (the game's ice staff projectile, Overhaul_IceShard, seen by every player)
    // forms every ShardEvery s, up to ShardMax, in a half circle over the head. Each attack of the local player sends them all at
    // what the crosshair aims at (a fixed point, nothing is followed), curving on their way, for ShardDamage frost damage. On leaving, it
    // splits by the extra projectiles (enchantment lines, leveling chance and the held weapon's FanVolley). Shards already formed stay up to KeepTime s when the weapon
    // changes; only new ones need it.
    internal static class IceShards
    {
        const string Prefab = "Overhaul_IceShard", Source = "staff_iceshard_projectile";
        const float Speed = 20f, Range = 100f, HitRadius = 1.5f, KeepTime = 30f, MeleeCurve = 3f, CurveMin = .2f, CurveMax = .5f, ShardScale = .4f;
        static readonly List<GameObject> held = new List<GameObject>();
        static float nextShard, stowed;
        static bool equipped;
        // The freeze gland's values, for the shards left once its weapon is put away.
        static CatalystDef values;

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            var clone = PrefabManager.Instance.CreateClonedPrefab(Prefab, Source);
            if (!clone) { Utility.Log.LogError("Ice shard: " + Source + " missing"); return; }
            Object.DestroyImmediate(clone.GetComponent<Projectile>());
            foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            clone.GetComponent<ZNetView>().m_persistent = false;
            if (!clone.GetComponent<ZSyncTransform>()) clone.AddComponent<ZSyncTransform>();
            clone.transform.localScale = Vector3.one * ShardScale;
            foreach (var ps in clone.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
            PrefabManager.Instance.AddPrefab(new CustomPrefab(clone, false));
        }

        // Place i of ShardMax in the half circle over the player's head, right shoulder to left shoulder.
        static Vector3 Place(Player owner, int i, int count)
        {
            float a = Mathf.Lerp(15, 165, count > 1 ? i / (count - 1f) : .5f) * Mathf.Deg2Rad;
            var t = owner.transform;
            return t.position + Vector3.up * 1.75f - t.forward * .1f + (t.right * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)) * .7f;
        }

        static GameObject Spawn(Vector3 position)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Prefab) : null;
            return prefab ? Object.Instantiate(prefab, position, Quaternion.LookRotation(Vector3.up)) : null;
        }
        static void Remove(GameObject shard) { if (shard && ZNetScene.instance) ZNetScene.instance.Destroy(shard); }

        static void Clear() { foreach (var shard in held) Remove(shard); held.Clear(); }

        // The point the crosshair aims at within Range, on whatever is there (the far end of the aim in open air).
        static Vector3 Aim(Player player)
        {
            var camera = Utils.GetMainCamera();
            if (!camera) return player.GetCenterPoint() + player.transform.forward * Range;
            var ray = new Ray(camera.transform.position, camera.transform.forward);
            var hits = Physics.RaycastAll(ray, Range, Projectile.s_rayMaskSolids);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
                if (hit.collider.GetComponentInParent<Character>() != player) return hit.point;
            return ray.GetPoint(Range);
        }

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            var def = player ? CatalystEffects.InWeapon(player, "ColdWalker") : null;
            if (!player || player.IsDead()) { if (held.Count > 0) Clear(); return; }
            held.RemoveAll(s => !s);
            if (def != null) values = def;
            // Without the weapon, the shards left last KeepTime s.
            if (def != null) stowed = Time.time;
            else if (held.Count > 0 && Time.time >= stowed + KeepTime) { Clear(); return; }
            // The first shard takes its full time once the weapon is in hand.
            if (def == null) equipped = false;
            else if (!equipped) { equipped = true; nextShard = Time.time + def.Get("ShardEvery", 3); }
            if (values == null || held.Count == 0 && def == null) return;
            int max = (int)values.Get("ShardMax", 5);
            if (def == null || held.Count >= max) nextShard = Time.time + values.Get("ShardEvery", 3);
            else if (Time.time >= nextShard)
            {
                nextShard = Time.time + values.Get("ShardEvery", 3);
                var shard = Spawn(player.transform.position);
                if (shard) held.Add(shard);
            }
            for (int i = 0; i < held.Count; i++) held[i].transform.SetPositionAndRotation(Place(player, i, max), Quaternion.LookRotation(player.transform.forward));
        }

        // An attack of the player sends every shard held at what is aimed at.
        internal static void Release(Player player)
        {
            held.RemoveAll(shard => !shard);
            if (held.Count == 0) return;
            // A melee attack: the point it should strike in front of the player, on a wider curve.
            var attack = player.m_currentAttack;
            bool melee = attack != null && attack.m_attackType != Attack.AttackType.Projectile;
            Vector3 point = melee ? player.GetCenterPoint() + player.transform.forward * attack.m_attackRange : Aim(player);
            float damage = values.Get("ShardDamage", 10);
            foreach (var first in held)
            {
                int count = 1 + MythicEffects.Extra(player.GetCurrentWeapon()) + Mathf.RoundToInt(Effects.Effects.Get(player, "ProjectileCount"))
                    + (Random.value < Leveling.LevelingEffects.Bonus(player, "projectile") ? 1 : 0);
                for (int i = 0; i < count; i++)
                {
                    var shard = i == 0 ? first : Spawn(first.transform.position);
                    if (shard) player.StartCoroutine(Fly(player, point, shard, damage, melee ? MeleeCurve : 1f));
                }
            }
            held.Clear();
        }

        // A curve bent a random way, anywhere from the right through up to the left (never down), ending on the point aimed at.
        static IEnumerator Fly(Player owner, Vector3 point, GameObject shard, float damage, float curve)
        {
            Vector3 start = shard.transform.position;
            float length = Mathf.Max(1, Vector3.Distance(start, point)), time = length / Speed;
            Vector3 right = Vector3.Cross(Vector3.up, (point - start).normalized).normalized;
            float angle = Random.Range(0f, 180f) * Mathf.Deg2Rad;
            Vector3 control = (start + point) / 2 + (right * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * Random.Range(CurveMin, CurveMax) * curve * length;
            Vector3 last = start;
            for (float t = 0; t < time && shard && owner; t += Time.deltaTime)
            {
                float k = t / time;
                Vector3 position = Vector3.Lerp(Vector3.Lerp(start, control, k), Vector3.Lerp(control, point, k), k);
                if (position != last) shard.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - last));
                last = position;
                yield return null;
            }
            if (shard && owner)
            {
                var model = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Source)?.GetComponent<Projectile>() : null;
                if (model) model.m_hitEffects.Create(point, Quaternion.identity);
                // Only what still stands at the point takes the hit.
                var target = Character.GetAllCharacters().Where(c => c && c != owner && !c.IsDead() && BaseAI.IsEnemy(owner, c) && Vector3.Distance(c.GetCenterPoint(), point) <= HitRadius)
                    .OrderBy(c => Vector3.Distance(c.GetCenterPoint(), point)).FirstOrDefault();
                if (target)
                {
                    var hit = new HitData { m_point = point, m_dir = (point - last).normalized, m_dodgeable = false, m_blockable = false };
                    hit.m_damage.m_frost = damage; hit.SetAttacker(owner);
                    target.Damage(hit);
                }
            }
            Remove(shard);
        }
    }
}
