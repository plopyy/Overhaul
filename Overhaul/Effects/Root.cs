using HarmonyLib;
using UnityEngine;

namespace Overhaul.Effects
{
    // Rooted: the character cannot move until the time is up, but still attacks within reach. No damage. The end time lives
    // in the character's ZDO (set by its owner), so the root holds through a change of owner.
    internal static class Root
    {
        static readonly int Key = "overhaul_root_until".GetStableHashCode();
        static double Now => ZNet.instance ? ZNet.instance.GetTimeSeconds() : Time.time;
        static ZDO Zdo(Character c) => c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO() : null;

        // On the character's owner only.
        internal static void Apply(Character target, float seconds)
        {
            var zdo = Zdo(target); if (zdo == null || !target.m_nview.IsOwner() || target.IsDead()) return;
            zdo.Set(Key, (float)System.Math.Max(zdo.GetFloat(Key, 0), Now + seconds));
        }
        internal static bool Rooted(Character c) { var zdo = Zdo(c); return zdo != null && Now < zdo.GetFloat(Key, 0); }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class Hold
        {
            private static void Postfix(SEMan __instance, ref float speed) { if (__instance.m_character && Rooted(__instance.m_character)) speed = 0; }
        }
    }
}
