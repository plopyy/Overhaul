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
        // The pack places its ground ring 0.701 below the prefab origin: lift it back to the ground, plus 3 cm.
        private const float Lift = .701f * Scale + .03f;
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
            GameObject aura = Object.Instantiate(prefabs[level - 1], (follow ? Ground(position) : position) + Vector3.up * Lift, Quaternion.identity);
            aura.transform.localScale = Vector3.one * Scale;
            foreach (var system in aura.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            if (follow) aura.AddComponent<Upright>().Target = follow;
            return aura;
        }

        private static int groundMask;
        // The surface under the item (terrain, floors, rocks), or the item itself when nothing is below.
        private static Vector3 Ground(Vector3 position)
        {
            if (groundMask == 0) groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            return Physics.Raycast(position + Vector3.up * .5f, Vector3.down, out RaycastHit hit, 3f, groundMask) ? hit.point : position;
        }

        private class Upright : MonoBehaviour
        {
            public Transform Target;
            private void LateUpdate()
            {
                if (!Target) { Destroy(gameObject); return; }
                transform.SetPositionAndRotation(Ground(Target.position) + Vector3.up * Lift, Quaternion.identity);
            }
        }

        // Test: every loot item lying on the ground (dropped, thrown, from creatures or containers) gets a random level, fixed by its world id
        // (IsEquipment will restrict it to equipment once Overhaul rarities exist).
        [HarmonyLib.HarmonyPatch(typeof(ItemDrop), "Start")]
        private static class GroundItem
        {
            private static void Postfix(ItemDrop __instance)
            {
                if (ZNet.instance && ZNet.instance.IsDedicated()) return;
                if (!__instance || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                // Only loose loot: world pickables (flowers, mushrooms...) and fixed items are not loot.
                if (__instance.GetComponent<Pickable>() || __instance.GetComponentInParent<Piece>()) return;
                Rigidbody body = __instance.GetComponent<Rigidbody>();
                if (!body || body.isKinematic) return;
                int level = 1 + (int)((uint)__instance.m_nview.GetZDO().m_uid.GetHashCode() % Levels);
                Spawn(level, __instance.transform.position, __instance.transform);
            }
        }

        // Equipment, weapons, shields and tools; resources, food, trophies and the like keep the vanilla look.
        internal static bool IsEquipment(ItemDrop.ItemData item)
        {
            switch (item?.m_shared?.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return true;
                default:
                    return false;
            }
        }
    }
}
