using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    // All times supplied here come from the server clock. No client duration,
    // resource total or charge percentage is accepted.
    internal static class GameWeaponPreparation
    {
        internal sealed class Step
        {
            internal PlayerChange[] Changes;
            internal float Fraction;
            internal bool Exhausted;
        }
        internal static double Duration(PlayerSnapshot state,ItemDrop.ItemData weapon,bool reload)
        {
            var attack=weapon.m_shared.m_attack;
            float skill=Mathf.Clamp01(GameAttackResources.SkillLevel(state,weapon.m_shared.m_skillType,GameAttackResources.Effects(state))/100f);
            double duration=reload?Mathf.Lerp(attack.m_reloadTime,attack.m_reloadTime*.5f,skill):
                Mathf.Lerp(attack.m_drawDurationMin,attack.m_drawDurationMin*.2f,skill);
            if(double.IsNaN(duration) || double.IsInfinity(duration) || duration<0)throw new InvalidOperationException("Invalid weapon preparation duration");
            return duration;
        }
        internal static Step Advance(PlayerSnapshot state,ItemDrop.ItemData weapon,bool reload,double from,double until)
        {
            if(double.IsNaN(from) || double.IsInfinity(from) || double.IsNaN(until) || double.IsInfinity(until) || from<0 || until<from)
                throw new InvalidOperationException("Invalid server preparation interval");
            double duration=Duration(state,weapon,reload),elapsed=until-from;
            var attack=weapon.m_shared.m_attack;var effects=GameAttackResources.Effects(state);
            double stamina,eitr;
            if(reload)
            {
                // Reload stops draining at completion even when the worker was busy.
                elapsed=Math.Max(0,Math.Min(until,duration)-Math.Min(from,duration));
                stamina=Math.Max(0,attack.m_reloadStaminaDrain)*elapsed;
                eitr=Math.Max(0,attack.m_reloadEitrDrain)*elapsed;
            }
            else
            {
                float skill=Mathf.Clamp01(GameAttackResources.SkillLevel(state,weapon.m_shared.m_skillType,effects)/100f);
                float rate=Mathf.Max(0,attack.m_drawStaminaDrain)*(1-.33f*skill);
                float equipment=state.Rows.Where(r=>r.Table=="inventory").Select(r=>PlayerInventoryView.ReadItem(r.Values,null,true))
                    .Where(i=>i.m_equipped).Sum(i=>i.m_shared.m_attackStaminaModifier);
                rate*=1+equipment;float original=rate;
                foreach(var effect in effects)effect.ModifyAttackStaminaUsage(original,ref rate,true);
                // Integrate both sides of the full-charge threshold. Splitting a
                // time interval into multiple writes must not change its cost.
                double full=Math.Max(0,until-Math.Max(from,duration));
                stamina=Math.Max(0,rate)*(elapsed-full*.5);
                eitr=Math.Max(0,attack.m_drawEitrDrain)*(1-.33f*skill)*elapsed;
            }
            stamina*=Game.m_staminaRate;
            eitr*=Game.m_eitrRate*(1-Mathf.Clamp01(PlayerCraftProgressGame.Bonus(state,"eitr_cost")));
            if(double.IsNaN(stamina) || double.IsInfinity(stamina) || stamina<0 || double.IsNaN(eitr) || double.IsInfinity(eitr) || eitr<0)
                throw new InvalidOperationException("Invalid weapon preparation cost");
            var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            if(!definition)throw new InvalidOperationException("Player resource definitions are unavailable");
            var rows=new List<PlayerChange>();bool exhausted=PlayerResources.Read(state,"health")<=0;
            void Debit(string key,double amount,string delay,float delayValue)
            {
                if(amount<=0)return;
                double available=PlayerResources.Read(state,key);exhausted|=available<=amount;
                rows.Add(PlayerResources.Row(key,Math.Max(0,available-amount)));rows.Add(PlayerResources.Row(delay,delayValue));
            }
            Debit("stamina",stamina,PlayerResources.StaminaDelay,definition.m_staminaRegenDelay);
            Debit("eitr",eitr,PlayerResources.EitrDelay,definition.m_eitrRegenDelay);
            return new Step{Changes=rows.ToArray(),Exhausted=exhausted,Fraction=duration<=0?1:(float)Math.Min(1,until/duration)};
        }
    }
}
