using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameAdrenaline
    {
        [ThreadStatic] private static int depth;
        internal static double Read(PlayerSnapshot state,string key)=>state.Rows.Any(r=>r.Table=="state"&&(string)r.Values[0]==key)?PlayerResources.Read(state,key):0;
        internal static float Maximum(PlayerSnapshot state,Player definition)=>Mathf.Max(0,definition.m_maxAdrenaline+GameCombatEquipment.Equipped(state).Sum(i=>i.m_shared.m_maxAdrenaline));
        internal static PlayerChange[] Change(PlayerSnapshot state,Player definition,float amount)
        {
            if(!definition||float.IsNaN(amount)||float.IsInfinity(amount))throw new InvalidOperationException("Invalid server adrenaline event");
            if(PlayerResources.Read(state,"health")<=0)return Array.Empty<PlayerChange>();
            if(depth>=8)throw new InvalidOperationException("Recursive adrenaline status definition");
            depth++;
            try
            {
                float maximum=Maximum(state,definition),value=(float)Read(state,PlayerResources.Adrenaline);
                var changes=new List<PlayerChange>();var current=state;
                void Apply(IEnumerable<PlayerChange> rows){var delta=rows.ToArray();GamePlayerHit.Merge(changes,delta);current=PlayerProgressService.Overlay(current,delta);}
                void Row(string key,double number)=>Apply(new[]{PlayerResources.Row(key,number)});
                if(amount>0&&maximum>0)
                {
                    float ratio=value/maximum;Row(PlayerResources.AdrenalineDelay,Mathf.Max(0,definition.m_adrenalineDegenDelay.Evaluate(ratio)));
                    amount*=Game.m_adrenalineRate*definition.m_adrenalineGainMultiplier.Evaluate(ratio);
                    float baseAmount=amount;foreach(var effect in GameAttackResources.Effects(state))effect.ModifyAdrenaline(baseAmount,ref amount);
                }
                if(float.IsNaN(amount)||float.IsInfinity(amount))throw new InvalidOperationException("Invalid modified adrenaline event");
                if(amount<0||amount>0&&value<maximum)value+=amount;
                if(value>=maximum&&maximum>0)
                {
                    var procs=GameCombatEquipment.Equipped(state).Where(i=>i.m_shared.m_fullAdrenalineSE).Select(i=>i.m_shared.m_fullAdrenalineSE).GroupBy(e=>e.NameHash()).Select(g=>g.First()).ToArray();
                    // Store the post-proc value before setup of any triggered
                    // status, so nested resource effects see a consistent state.
                    value=procs.Length==0?maximum:0;Row(PlayerResources.Adrenaline,value);
                    foreach(var effect in procs)Apply(GameStatusImpact.Prepare(current,definition,effect.NameHash(),0,0,-1,ZDOID.None));
                    value=(float)Read(current,PlayerResources.Adrenaline);
                }
                Row(PlayerResources.Adrenaline,Mathf.Max(0,value));Row(PlayerResources.AdrenalineMaximum,maximum);
                if(maximum>0)Row(PlayerResources.AdrenalineLastMaximum,maximum);
                var tier=definition.m_adrenalineEffects.LastOrDefault(e=>value>=e.m_rate).m_se;
                int chosen=tier?tier.NameHash():0;
                foreach(var level in definition.m_adrenalineEffects)
                    if(level.m_se&&level.m_se.NameHash()!=chosen&&current.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==level.m_se.NameHash()))
                        Apply(new[]{new PlayerChange("status",true,level.m_se.NameHash())});
                if(tier&&!current.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==chosen))Apply(GameStatusImpact.Prepare(current,definition,chosen,0,0,-1,ZDOID.None));
                return changes.ToArray();
            }
            finally{depth--;}
        }
        internal static PlayerChange[] Advance(PlayerSnapshot state,Player definition,double seconds)
        {
            if(!definition||seconds<=0||PlayerResources.Read(state,"health")<=0)return Array.Empty<PlayerChange>();
            double delay=Read(state,PlayerResources.AdrenalineDelay),elapsed=Math.Max(0,seconds-delay);
            var rows=Change(state,definition,0).ToList();var current=PlayerProgressService.Overlay(state,rows);
            double value=Read(current,PlayerResources.Adrenaline),maximum=Read(current,PlayerResources.AdrenalineLastMaximum);
            // Curves are evaluated at bounded time steps only while the meter
            // actually decays; idle characters perform no replay loop.
            while(elapsed>0&&value>0&&maximum>0)
            {
                double step=Math.Min(.1,elapsed);float drain=Mathf.Max(0,definition.m_adrenalineDegen.Evaluate((float)(value/maximum)))*(float)step;
                var delta=Change(current,definition,-drain);GamePlayerHit.Merge(rows,delta);current=PlayerProgressService.Overlay(current,delta);
                value=Read(current,PlayerResources.Adrenaline);elapsed-=step;if(drain==0)break;
            }
            GamePlayerHit.Merge(rows,new[]{PlayerResources.Row(PlayerResources.AdrenalineDelay,Math.Max(0,delay-seconds))});
            return rows.Where(row=>!state.Rows.Any(old=>old.SameKey(row)&&old.Values.SequenceEqual(row.Values))).ToArray();
        }
    }
}
