using HarmonyLib;
using UnityEngine;

namespace Auga
{
    [RequireComponent(typeof(Player))]
    [UiModule(UiModule.Hud)]
    public class LastHitTracker : MonoBehaviour
    {
        public HitData LastHit;
        protected Player _player;

        public void Awake()
        {
            _player = GetComponent<Player>();
        }

        public virtual void OnDamaged(HitData hitData)
        {
            LastHit = hitData;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDamaged))]
    [UiModule(UiModule.Hud)]
    public static class Player_OnDamaged_Patch
    {
        public static void Postfix(Player __instance, HitData hit)
        {
            var hitTracker = __instance.RequireComponent<LastHitTracker>();
            hitTracker.OnDamaged(hit);
        }
    }
}
