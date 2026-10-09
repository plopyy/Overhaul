using UnityEngine;

namespace Overhaul
{
    // Ground auras of Overhaul's rarities (6 levels, from the "Unique Loot Drops Vol. 1" pack, Vertical02 style).
    // The bundle is embedded in Overhaul.dll and loaded on first use.
    public static class LootAura
    {
        public const int Levels = 6;
        // The pack is made for large scenes: shrunk to item size and lifted so the ground ring is not buried.
        private const float Scale = .35f;
        private const float Lift = .2f;
        private static AssetBundle bundle;
        private static readonly GameObject[] prefabs = new GameObject[Levels];

        private static bool Ready()
        {
            if (bundle) return true;
            // Owned bytes: a bundle loaded from a stream needs that stream alive for its whole life.
            try { bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.LootAura.overhaul_lootaura"); }
            catch (System.Exception e) { Utility.Log.LogWarning("Loot auras unavailable: " + e.Message); return false; }
            if (!bundle) return false;
            for (int i = 0; i < Levels; i++) prefabs[i] = bundle.LoadAsset<GameObject>("OverhaulLootAura" + (i + 1));
            return true;
        }

        // level: 1 to 6. The aura follows its parent's position but always stays upright.
        public static GameObject Spawn(int level, Vector3 position, Transform follow = null)
        {
            if (level < 1 || level > Levels || !Ready() || !prefabs[level - 1]) return null;
            GameObject aura = Object.Instantiate(prefabs[level - 1], position + Vector3.up * Lift, Quaternion.identity);
            aura.transform.localScale = Vector3.one * Scale;
            foreach (var system in aura.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            if (follow) aura.AddComponent<Upright>().Target = follow;
            return aura;
        }

        private class Upright : MonoBehaviour
        {
            public Transform Target;
            private void LateUpdate()
            {
                if (!Target) { Destroy(gameObject); return; }
                transform.SetPositionAndRotation(Target.position + Vector3.up * Lift, Quaternion.identity);
            }
        }

        // Test: the six levels side by side in front of the local player, removed after a minute.
        internal static string Preview()
        {
            Player player = Player.m_localPlayer;
            if (!player) return "Overhaul : commande a lancer depuis un personnage connecte.";
            if (!Ready()) return "Overhaul : effets de loot introuvables.";
            Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(forward, Vector3.up);
            for (int i = 0; i < Levels; i++)
            {
                Vector3 p = player.transform.position + forward * 4f + right * ((i - 2.5f) * 1.2f);
                if (ZoneSystem.instance) p.y = ZoneSystem.instance.GetGroundHeight(p);
                GameObject aura = Spawn(i + 1, p);
                if (aura) Object.Destroy(aura, 60f);
            }
            return "Overhaul : 6 auras de loot affichees pendant 60 s.";
        }
    }
}
