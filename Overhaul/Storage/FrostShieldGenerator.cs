using System.Collections.Generic;
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

        private void Awake()
        {
            shield = GetComponent<ShieldGenerator>();
            nview = GetComponent<ZNetView>();
        }

        private void FixedUpdate()
        {
            if (!shield || !nview || !nview.IsValid() || !shield.m_shieldDome) return;
            float radius = shield.m_radius;
            if (radius <= 0f || shield.m_radiusTarget <= 0f) { inside.Clear(); return; }
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
}
