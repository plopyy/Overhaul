using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Freeze gland in the weapon held: an ice shard (the game's ice staff projectile, Overhaul_IceShard, seen by every player)
    // forms every ShardEvery s, up to ShardMax, in a half circle over the head. While creatures hunt the local player, one
    // leaves every ShardDelay s at a random one of them, curving on its way, for ShardDamage frost damage. On leaving, it
    // splits by the extra projectiles (enchantment lines, leveling chance and the held weapon's FanVolley). Shards already formed stay when the weapon
    // changes; only new ones need it. A creature publishes its target in its ZDO, so hunters are seen whoever owns them.
    internal static class IceShards
    {
        const string Prefab = "Overhaul_IceShard", Source = "staff_iceshard_projectile";
        const float Speed = 20f, HuntRange = 40f, CurveMin = .2f, CurveMax = .5f, ShardScale = .4f;
        static readonly List<GameObject> held = new List<GameObject>();
        static float nextShard, nextLaunch;
        static bool equipped;
        // The freeze gland's values, for the shards left once its weapon is put away.
        static CatalystDef values;
        const string TargetKey = "overhaul_hunt_target";

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        private static class PublishTarget
        {
            private static void Postfix(MonsterAI __instance)
            {
                var view = __instance.m_nview;
                if (!view || !view.IsValid() || !view.IsOwner()) return;
                var zdo = view.GetZDO(); var target = __instance.GetTargetCreature();
                var id = target ? target.GetZDOID() : ZDOID.None;
                if (zdo.GetZDOID(TargetKey) != id) zdo.Set(TargetKey, id);
            }
        }

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

        static List<Character> Hunters(Player player) => Character.GetAllCharacters().Where(c => c && c != player && !c.IsDead() && c.m_nview && c.m_nview.IsValid()
            && c.m_nview.GetZDO().GetZDOID(TargetKey) == player.GetZDOID() && Vector3.Distance(c.transform.position, player.transform.position) <= HuntRange).ToList();

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            var def = player ? CatalystEffects.InWeapon(player, "ColdWalker") : null;
            if (!player || player.IsDead()) { if (held.Count > 0) Clear(); return; }
            held.RemoveAll(s => !s);
            if (def != null) values = def;
            // The first shard takes its full time once the weapon is in hand.
            if (def == null) equipped = false;
            else if (!equipped) { equipped = true; nextShard = Time.time + def.Get("ShardEvery", 5); }
            if (values == null || held.Count == 0 && def == null) return;
            int max = (int)values.Get("ShardMax", 5);
            if (def == null || held.Count >= max) nextShard = Time.time + values.Get("ShardEvery", 5);
            else if (Time.time >= nextShard)
            {
                nextShard = Time.time + values.Get("ShardEvery", 5);
                var shard = Spawn(player.transform.position);
                if (shard) held.Add(shard);
            }
            for (int i = 0; i < held.Count; i++) held[i].transform.SetPositionAndRotation(Place(player, i, max), Quaternion.LookRotation(player.transform.forward));
            if (held.Count == 0 || Time.time < nextLaunch) return;
            var hunters = Hunters(player); if (hunters.Count == 0) return;
            nextLaunch = Time.time + values.Get("ShardDelay", 1);
            var first = held[held.Count - 1]; held.RemoveAt(held.Count - 1);
            var target = hunters[Random.Range(0, hunters.Count)];
            int count = 1 + MythicEffects.Extra(player.GetCurrentWeapon()) + Mathf.RoundToInt(Effects.Effects.Get(player, "ProjectileCount"))
                + (Random.value < Leveling.LevelingEffects.Bonus(player, "projectile") ? 1 : 0);
            for (int i = 0; i < count; i++)
            {
                var shard = i == 0 ? first : Spawn(first.transform.position);
                if (shard) player.StartCoroutine(Fly(player, target, shard, values.Get("ShardDamage", 5)));
            }
        }

        // A curve bent a random way, anywhere from the right through up to the left (never down), ending on the creature however it moves.
        static IEnumerator Fly(Player owner, Character target, GameObject shard, float damage)
        {
            Vector3 start = shard.transform.position;
            float length = Mathf.Max(1, Vector3.Distance(start, target.GetCenterPoint())), time = length / Speed;
            Vector3 right = Vector3.Cross(Vector3.up, (target.GetCenterPoint() - start).normalized).normalized;
            float angle = Random.Range(0f, 180f) * Mathf.Deg2Rad;
            Vector3 lateral = (right * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * Random.Range(CurveMin, CurveMax);
            Vector3 last = start;
            for (float t = 0; t < time && shard && owner && target && !target.IsDead(); t += Time.deltaTime)
            {
                float k = t / time;
                Vector3 end = target.GetCenterPoint(), control = (start + end) / 2 + lateral * length;
                Vector3 position = Vector3.Lerp(Vector3.Lerp(start, control, k), Vector3.Lerp(control, end, k), k);
                if (position != last) shard.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - last));
                last = position;
                yield return null;
            }
            if (shard && owner && target && !target.IsDead())
            {
                var hit = new HitData { m_point = target.GetCenterPoint(), m_dir = (target.GetCenterPoint() - last).normalized, m_dodgeable = false, m_blockable = false };
                hit.m_damage.m_frost = damage; hit.SetAttacker(owner);
                var model = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Source)?.GetComponent<Projectile>() : null;
                if (model) model.m_hitEffects.Create(hit.m_point, Quaternion.identity);
                target.Damage(hit);
            }
            Remove(shard);
        }
    }
}
