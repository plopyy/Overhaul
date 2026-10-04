using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameHitFeedback
    {
        private const string Rpc="Overhaul_HitFeedback";
        private sealed class Meter{internal float Value;internal double Time;}
        private static readonly Dictionary<ZDOID,Meter> meters=new Dictionary<ZDOID,Meter>();
        internal static void Forget(ZDOID actor)=>meters.Remove(actor);
        internal static void Clear()=>meters.Clear();
        internal static float Stagger(Player player,PlayerSnapshot state)
        {
            if(!meters.TryGetValue(player.GetZDOID(),out var meter))return 0;
            return Mathf.Max(0,meter.Value-(float)(Time.timeAsDouble-meter.Time)*(float)PlayerResources.Read(state,"max_health")*player.m_staggerDamageFactor/5f);
        }
        internal static void Publish(Player player,PlayerSnapshot state,HitData impact,GamePlayerDamageMath.Result damage,GameBlockMath.Result block,bool direct)
        {
            if(!player||!player.m_nview||!player.m_nview.IsValid())return;
            float stagger=block?.Stagger??Stagger(player,state),added=damage.Damage>.1f?damage.Stagger:0;
            foreach(var effect in GameAttackResources.Effects(state))effect.ModifyStagger(damage.Stagger,ref added);
            float threshold=(float)PlayerResources.Read(state,"max_health")*player.m_staggerDamageFactor;
            bool stunned=block?.Staggered==true||impact.m_staggerMultiplier>=100;
            if(threshold>0){stagger=Mathf.Min(threshold,stagger+added);stunned|=stagger>=threshold;}
            meters[player.GetZDOID()]=new Meter{Value=stagger,Time=Time.timeAsDouble};
            player.m_lastHit=damage.Direct.Clone();player.SetHealth((float)PlayerResources.Read(state,"health"));
            if(damage.Damage>.1f)
            {
                if(DamageText.instance)DamageText.instance.ShowText(damage.Modifier,impact.m_point,damage.Damage,true);
                if(damage.Damage>PlayerResources.Read(state,"max_health")/10&&damage.Direct.m_damage.GetTotalPhysicalDamage()>0)
                    player.m_hitEffects.Create(impact.m_point,Quaternion.identity,player.transform,1,-1,player.GetZDOID());
                player.m_onDamaged?.Invoke(damage.Damage,impact.GetAttacker());
            }
            var attacker=impact.GetAttacker();
            if(block?.Attempted==true)block.Item.m_shared.m_blockEffect.Create(impact.m_point,Quaternion.identity,null,1,-1,player.GetZDOID());
            if(block?.ChargeEffect>0)GameBlockCharges.Publish(player,state,block);
            if(block?.Blocked==true)
            {
                if(DamageText.instance)DamageText.instance.ShowText(DamageText.TextType.Blocked,impact.m_point+Vector3.up*.5f,block.Absorbed,false);
                if(attacker&&block.Timed)
                {
                    player.m_perfectBlockEffect.Create(impact.m_point,Quaternion.identity,null,1,-1,player.GetZDOID());
                    if(attacker.m_staggerWhenBlocked)attacker.Stagger(-impact.m_dir);
                }
                if(attacker&&!impact.m_ranged)
                {
                    var direction=attacker.transform.position-player.transform.position;direction.y=0;
                    attacker.Damage(new HitData{m_pushForce=block.Item.GetDeflectionForce()*(1-Mathf.Clamp01(block.Fraction*.5f)),m_dir=direction.normalized});
                }
            }
            var package=new ZPackage();var view=damage.Direct.Clone();view.m_pushForce=direct?0:impact.m_pushForce;
            view.Serialize(ref package);package.Write(stagger);package.Write(stunned);package.Write(damage.Damage>PlayerResources.Read(state,"max_health")/10);
            player.m_nview.InvokeRPC(Rpc,package);
        }
        [HarmonyPatch(typeof(Player),nameof(Player.Awake))]
        private static class Register
        {
            private static void Postfix(Player __instance)
            {
                if(!__instance.m_nview||!__instance.m_nview.IsValid())return;
                var player=__instance;
                player.m_nview.Register<ZPackage>(Rpc,(sender,package)=>
                {
                    if(!PlayerSessionGame.Managed||player!=Player.m_localPlayer||!ZNet.instance||
                        sender!=(ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid))return;
                    var hit=new HitData();hit.Deserialize(ref package);float stagger=package.ReadSingle();bool stunned=package.ReadBool(),flash=package.ReadBool();
                    player.m_lastHit=hit;player.m_staggerDamage=stagger;player.ApplyPushback(hit);
                    if(stunned){player.Stagger(hit.m_dir);if(Hud.instance)Hud.instance.StaggerBarFlash();}
                    if(flash){player.DoDamageCameraShake(hit);if(Hud.instance)Hud.instance.DamageFlash();}
                });
            }
        }
    }
}
