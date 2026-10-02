using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class TrainingDamageMeter
    {
        internal const string Rpc = "Overhaul_TrainingDamage";
        internal static readonly TrainingDamageSession Session = new TrainingDamageSession();
        internal static double Now => ZNet.instance ? ZNet.instance.GetTimeSeconds() : Time.timeAsDouble;
        internal static Character SourceTarget;
        internal static ZDOID SourcePlayer;
        internal static StatusEffect TickEffect;
        internal static Capture Active;
        internal sealed class Capture
        {
            internal Character Target;
            internal HitData Hit;
            internal StatusEffect Effect;
            internal float Fire, Spirit, Poison;
            internal float DisplayDamage;
            internal bool Applied;
        }

        // Fire/spirit stack in native pools. Keep the same fractions per contributor,
        // including an unattributed share for monsters/environment. Poison replaces
        // its pool only when the native stronger-poison rule accepts the new dose.
        internal sealed class Pool
        {
            internal readonly Dictionary<ZDOID, float> Shares = new Dictionary<ZDOID, float>();
            internal void Add(ZDOID player, float damage, float nativeRemaining, bool replace)
            {
                if (replace) Shares.Clear();
                else
                {
                    float sum = 0; foreach (var value in Shares.Values) sum += value;
                    if (sum > 0)
                        foreach (var key in new List<ZDOID>(Shares.Keys)) Shares[key] *= nativeRemaining / sum;
                    else if (nativeRemaining > 0) Shares[ZDOID.None] = nativeRemaining;
                }
                Shares.TryGetValue(player, out var previous); Shares[player] = previous + damage;
            }
            internal void Take(float rawDamage, float finalDamage, Dictionary<ZDOID, float> result)
            {
                float sum = 0; foreach (var value in Shares.Values) sum += value;
                if (sum <= 0) return;
                foreach (var key in new List<ZDOID>(Shares.Keys))
                {
                    float fraction = Shares[key] / sum;
                    result.TryGetValue(key, out var previous); result[key] = previous + finalDamage * fraction;
                    Shares[key] = Mathf.Max(0, Shares[key] - rawDamage * fraction);
                }
            }
        }
        internal sealed class EffectPools { internal readonly Pool Fire = new Pool(), Spirit = new Pool(), Poison = new Pool(); }
        internal static readonly ConditionalWeakTable<StatusEffect, EffectPools> Pools = new ConditionalWeakTable<StatusEffect, EffectPools>();

        internal static void Report(Capture capture)
        {
            if (capture == null || !capture.Applied) return;
            var hit = capture.Hit;
            if (capture.Effect && Pools.TryGetValue(capture.Effect, out var pools))
            {
                var parts = new Dictionary<ZDOID, float>();
                float total=capture.Fire+capture.Spirit+capture.Poison;
                float scale=total>0?capture.DisplayDamage/total:0;
                pools.Fire.Take(capture.Fire, capture.Fire*scale, parts);
                pools.Spirit.Take(capture.Spirit, capture.Spirit*scale, parts);
                pools.Poison.Take(capture.Poison, capture.Poison*scale, parts);
                foreach (var part in parts) Send(capture.Target, part.Key, part.Value);
            }
            else if (!capture.Effect && hit.GetAttacker() is Player player)
                Send(capture.Target, player.GetZDOID(), capture.DisplayDamage);
        }
        internal static void Send(Character dummy, ZDOID playerId, float damage)
        {
            if (playerId == ZDOID.None || damage <= 0 || !dummy.m_nview.IsOwner()) return;
            var playerObject = ZNetScene.instance ? ZNetScene.instance.FindInstance(playerId) : null;
            var player = playerObject ? playerObject.GetComponent<Player>() : null;
            if (!player || !player.m_nview || !player.m_nview.IsValid()) return;
            var package = new ZPackage(); package.Write(dummy.GetZDOID()); package.Write(damage); package.Write(Now);
            player.m_nview.InvokeRPC(player.m_nview.GetZDO().GetOwner(), Rpc, package);
        }
        internal static void Receive(Player player, long sender, ZPackage package)
        {
            if (player != Player.m_localPlayer || !player.m_nview.IsOwner()) return;
            var dummy = ZDOMan.instance?.GetZDO(package.ReadZDOID());
            float damage = package.ReadSingle(); double time = package.ReadDouble();
            if (dummy == null || dummy.GetPrefab() != "piece_TrainingDummy".GetStableHashCode() || dummy.GetOwner() != sender) return;
            if(!TrainingDamageHud.InRange(player.transform.position,dummy.GetPosition()))return;
            if (Now - time >= 10 || time > Now + 1) return;
            Session.Add(damage, time);
        }
    }

    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class TrainingDamageReceiver
    {
        private static void Postfix(Player __instance)
        {
            if (__instance.m_nview && __instance.m_nview.IsValid())
                __instance.m_nview.Register<ZPackage>(TrainingDamageMeter.Rpc, (sender, package) => TrainingDamageMeter.Receive(__instance, sender, package));
        }
    }

    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class TrainingDamageSource
    {
        private static void Prefix(Character __instance, HitData hit, out Tuple<Character, ZDOID> __state)
        {
            __state = Tuple.Create(TrainingDamageMeter.SourceTarget, TrainingDamageMeter.SourcePlayer);
            TrainingDamageMeter.SourceTarget = TrainingDummyProtection.IsDummy(__instance) && __instance.m_nview.IsOwner() ? __instance : null;
            TrainingDamageMeter.SourcePlayer = hit.GetAttacker() is Player player ? player.GetZDOID() : ZDOID.None;
        }
        private static void Finalizer(Tuple<Character, ZDOID> __state)
        {
            TrainingDamageMeter.SourceTarget = __state.Item1; TrainingDamageMeter.SourcePlayer = __state.Item2;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class TrainingDamageCapture
    {
        private static void Prefix(Character __instance, HitData hit, out TrainingDamageMeter.Capture __state)
        {
            __state = TrainingDamageMeter.Active;
            TrainingDamageMeter.Active = TrainingDummyProtection.IsDummy(__instance) && __instance.m_nview.IsOwner() &&
                hit.m_statusEffectHash != TrainingDummyProtection.HammerRemoval ? new TrainingDamageMeter.Capture {
                    Target = __instance, Hit = hit, Effect = TrainingDamageMeter.TickEffect,
                    Fire = hit.m_damage.m_fire, Spirit = hit.m_damage.m_spirit, Poison = hit.m_damage.m_poison,
                    DisplayDamage=hit.GetTotalDamage()
                } : null;
        }
        private static void Postfix() => TrainingDamageMeter.Report(TrainingDamageMeter.Active);
        private static void Finalizer(TrainingDamageMeter.Capture __state) => TrainingDamageMeter.Active = __state;
    }

    [HarmonyPatch(typeof(DamageText),nameof(DamageText.ShowText),new[]{typeof(HitData.DamageModifier),typeof(Vector3),typeof(float),typeof(bool)})]
    internal static class TrainingDisplayedDamage
    {
        private static void Prefix(float dmg)
        {
            var capture=TrainingDamageMeter.Active;
            if(capture!=null)capture.DisplayDamage=dmg;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
    internal static class TrainingDamageApplied
    {
        private static void Prefix(Character __instance)
        {
            var capture = TrainingDamageMeter.Active;
            if (capture != null && capture.Target == __instance) capture.Applied = true;
        }
    }

    [HarmonyPatch]
    internal static class TrainingPeriodicContext
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect));
            yield return AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect));
        }
        private static void Prefix(StatusEffect __instance, out StatusEffect __state)
        { __state = TrainingDamageMeter.TickEffect; TrainingDamageMeter.TickEffect = __instance; }
        private static void Finalizer(StatusEffect __state) => TrainingDamageMeter.TickEffect = __state;
    }

    [HarmonyPatch]
    internal static class TrainingBurningContribution
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddFireDamage));
            yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddSpiritDamage));
        }
        private static void Prefix(SE_Burning __instance, MethodBase __originalMethod, out float __state)
        { __state = __originalMethod.Name == "AddFireDamage" ? __instance.m_fireDamageLeft : __instance.m_spiritDamageLeft; }
        private static void Postfix(SE_Burning __instance, MethodBase __originalMethod, float damage, float __state, bool __result)
        {
            if (!__result || !TrainingDummyProtection.IsDummy(__instance.m_character) || !__instance.m_character.m_nview.IsOwner()) return;
            var pools = TrainingDamageMeter.Pools.GetOrCreateValue(__instance);
            var pool = __originalMethod.Name == "AddFireDamage" ? pools.Fire : pools.Spirit;
            pool.Add(TrainingDamageMeter.SourceTarget == __instance.m_character ? TrainingDamageMeter.SourcePlayer : ZDOID.None, damage, __state, false);
        }
    }

    [HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.AddDamage))]
    internal static class TrainingPoisonContribution
    {
        private static void Prefix(SE_Poison __instance, out float __state) => __state = __instance.m_damageLeft;
        private static void Postfix(SE_Poison __instance, float damage, float __state)
        {
            if (damage < __state || !TrainingDummyProtection.IsDummy(__instance.m_character) || !__instance.m_character.m_nview.IsOwner()) return;
            TrainingDamageMeter.Pools.GetOrCreateValue(__instance).Poison.Add(
                TrainingDamageMeter.SourceTarget == __instance.m_character ? TrainingDamageMeter.SourcePlayer : ZDOID.None, damage, 0, true);
        }
    }
}
