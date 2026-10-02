using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static class BossModifiers
    {
        internal static readonly int HealthKey = "overhaul_guardian_hp_percent_v1".GetStableHashCode();
        internal static readonly int ScaleKey = "overhaul_guardian_scale_v1".GetStableHashCode();
        internal static readonly int AttackSpeedKey = "overhaul_guardian_attack_speed_v1".GetStableHashCode();
        internal static float AttackSpeed(Character character)
        {
            var data=Data(character);if(data==null || character.IsPlayer())return 1;
            float speed=data.GetFloat(AttackSpeedKey,0);
            if(speed==0 && character.m_nview.IsOwner())
            {
                speed=BossData.AttackSpeedFor(character.gameObject.name);
                data.Set(AttackSpeedKey,speed);
            }
            return speed>=.1f && speed<=10f ? speed : 1;
        }
        internal static ZDO Data(Character character)
        {
            if (!character || !character.m_nview || !character.m_nview.IsValid()) return null;
            var data = character.m_nview.GetZDO();
            return data.GetBool(BossEncounter.BossKey, false) ? data : null;
        }

        internal static void Configure(Character character, BossData.Entry entry)
        {
            var data = Data(character);
            if (data == null || !character.m_nview.IsOwner()) return;
            data.Set(HealthKey, entry.HealthBonusPercent);
            data.Set(ScaleKey, entry.Scale);
            data.Set(AttackSpeedKey, entry.AttackSpeed);
            Attach(character);
        }

        internal static void Attach(Character character)
        {
            if (Data(character) == null || character.GetComponent<BossFinalScale>()) return;
            character.gameObject.AddComponent<BossFinalScale>().Character = character;
        }

        [HarmonyPatch(typeof(Character), "SetupMaxHealth")]
        internal static class FinalHealth
        {
            private static void Prefix(Character __instance, out float __state)
            {
                var maximum = __instance.GetMaxHealth();
                __state = maximum > 0 ? Mathf.Clamp01(__instance.GetHealth() / maximum) : 1;
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Character __instance, float __state)
            {
                var data = Data(__instance);
                if (data == null || !__instance.m_nview.IsOwner()) return;
                float bonus = data.GetFloat(HealthKey, 0);
                if (bonus == 0) return;
                float maximum = __instance.GetMaxHealth() * (1 + bonus / 100f);
                __instance.SetMaxHealth(maximum);
                // Native SetMaxHealth clamps current HP before this final multiplier.
                // Restore the prior fraction instead of injuring/healing saved guardians.
                __instance.SetHealth(maximum * __state);
            }
        }

        [HarmonyPatch(typeof(Character), "Awake")]
        internal static class Loaded
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Character __instance) => Attach(__instance);
        }

        [HarmonyPatch(typeof(LevelEffects), "SetupLevelVisualization")]
        internal static class FinalLevelScale
        {
            private static void Prefix(LevelEffects __instance)
            {
                var scale = __instance.GetComponent<BossFinalScale>();
                if (scale) scale.RemoveOwnScale();
            }
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(LevelEffects __instance)
            {
                var scale = __instance.GetComponent<BossFinalScale>();
                if (scale) scale.Apply();
            }
        }
    }

    // Apply to the root after native level/global visuals. Child visual scales
    // multiply naturally with this root factor. Only the factor is stored in ZDO:
    // saving an already multiplied native root scale would compound on reload.
    [DefaultExecutionOrder(10000)]
    internal sealed class BossFinalScale : MonoBehaviour
    {
        internal Character Character;
        private bool applied;
        private Vector3 original, final;
        internal void RemoveOwnScale()
        {
            if (applied && transform.localScale == final) transform.localScale = original;
            applied = false;
        }
        internal void Apply()
        {
            var data = BossModifiers.Data(Character);
            if (data == null || (applied && transform.localScale == final)) return;
            float factor = data.GetFloat(BossModifiers.ScaleKey, 1);
            original = transform.localScale;
            final = original * factor;
            transform.localScale = final;
            applied = true;
        }
        private void LateUpdate() => Apply();
    }
}
