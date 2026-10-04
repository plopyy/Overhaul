using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameHarpoonRuntime
    {
        private sealed class Rope{internal PlayerChange Header;internal SE_Harpooned Effect;internal float Drain;}
        private static readonly Dictionary<ZDOID,Rope> ropes=new Dictionary<ZDOID,Rope>();
        [ThreadStatic] private static SE_Harpooned updating;
        internal static void Forget(ZDOID actor)=>ropes.Remove(actor);
        internal static bool Active(ZDOID actor)=>ropes.TryGetValue(actor,out var rope)&&rope.Effect!=null&&!rope.Effect.m_broken;
        internal static bool Broken(SE_Harpooned effect)
        {
            var attacker=effect.m_attacker;var target=effect.m_character;
            if(effect.m_broken||!attacker||!target||attacker.IsDead()||target.IsDead())return true;
            if(Vector3.Distance(attacker.transform.position,target.transform.position)-effect.m_baseDistance>effect.m_breakDistance)return true;
            bool blocking=attacker.IsBlocking(),stamina=attacker.HaveStamina(0);
            if(attacker is Player player&&GameMovementRuntime.Managed(player))
            {var state=InventoryMoveGame.State(player.GetZDOID());blocking=GameBlockControl.Blocking(player.GetZDOID(),player,state,Vector3.zero)!=null;stamina=InventoryMoveGame.Stamina(player.GetZDOID())>0;}
            return !stamina||effect.m_time>2&&(blocking||attacker.InAttack());
        }
        internal static void Tick(Player player,float dt)
        {
            var state=InventoryMoveGame.State(player.GetZDOID());
            var header=state.Rows.FirstOrDefault(r=>r.Table=="status"&&ObjectDB.instance.GetStatusEffect(Convert.ToInt32(r.Values[0])) is SE_Harpooned);
            if(header==null){Forget(player.GetZDOID());return;}
            if(!ropes.TryGetValue(player.GetZDOID(),out var rope)){rope=new Rope();ropes.Add(player.GetZDOID(),rope);}
            if(!ReferenceEquals(header,rope.Header))
            {int id=Convert.ToInt32(header.Values[0]);rope.Header=header;rope.Effect=(SE_Harpooned)GameStatusCodec.Restore(state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id),player);}
            var effect=rope.Effect;if(Broken(effect)||!player.m_body||player.IsAttached()||player.GetStandingOnShip())return;
            float pull=Utils.Pull(player.m_body,effect.m_attacker.transform.position,effect.m_baseDistance,effect.m_pullSpeed,effect.m_pullForce,effect.m_smoothDistance,true,true,effect.m_forcePower);
            rope.Drain+=dt;
            if(rope.Drain>effect.m_staminaDrainInterval&&pull>0)
            {rope.Drain=0;float cost=effect.m_staminaDrain*pull*player.GetMass();if(effect.m_attacker is Player attacker&&GameMovementRuntime.Managed(attacker))InventoryMoveGame.SpendStamina(attacker.GetZDOID(),cost*Game.m_staminaRate,attacker.m_staminaRegenDelay);else effect.m_attacker.UseStamina(cost);}
        }
        internal static void Visual(SE_Harpooned effect)
        {
            if(!effect.m_attacker||!effect.m_character)return;
            if(!effect.m_line&&effect.m_startEffectInstances!=null)
                foreach(var instance in effect.m_startEffectInstances)
                {var line=instance?instance.GetComponent<LineConnect>():null;if(line){line.SetPeer(effect.m_attacker.m_nview);effect.m_line=line;}}
            if(effect.m_line)effect.m_line.SetSlack((1-Utils.LerpStep(effect.m_baseDistance/2,effect.m_baseDistance,Vector3.Distance(effect.m_attacker.transform.position,effect.m_character.transform.position)))*effect.m_maxLineSlack);
        }
        [HarmonyPatch(typeof(SE_Harpooned),nameof(SE_Harpooned.UpdateStatusEffect))]
        private static class Native
        {
            private static void Prefix(SE_Harpooned __instance,out SE_Harpooned __state){__state=updating;updating=__instance;}
            private static void Finalizer(SE_Harpooned __state)=>updating=__state;
        }
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class Drain
        {
            [HarmonyPriority(Priority.First+350)]
            private static bool Prefix(Player __instance,float v)
            {if(updating==null||updating.m_attacker!=__instance||!GameMovementRuntime.Managed(__instance))return true;InventoryMoveGame.SpendStamina(__instance.GetZDOID(),v*Game.m_staminaRate,__instance.m_staminaRegenDelay);return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.HaveStamina))]
        private static class Stamina
        {private static bool Prefix(Player __instance,float amount,ref bool __result){if(updating==null||updating.m_attacker!=__instance||!GameMovementRuntime.Managed(__instance))return true;__result=InventoryMoveGame.Stamina(__instance.GetZDOID())>amount;return false;}}
        [HarmonyPatch(typeof(SE_Harpooned),nameof(SE_Harpooned.IsDone))]
        private static class ViewLifetime
        {private static bool Prefix(SE_Harpooned __instance,ref bool __result){if(PlayerSessionGame.Managed&&__instance.m_character==Player.m_localPlayer){__result=false;return false;}if(!GameCreatureAuthority.Enabled)return true;__result=Broken(__instance)||__instance.m_ttl>0&&__instance.m_time>__instance.m_ttl;return false;}}
    }
}
