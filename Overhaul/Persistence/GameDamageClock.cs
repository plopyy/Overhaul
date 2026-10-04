using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    // One clock per admitted endpoint; all steps run through its serialized
    // server action queue. Offline time never enters this clock.
    internal sealed class GameDamageClock
    {
        private double advanced=Time.timeAsDouble;
        internal double Elapsed=>Math.Max(0,Time.timeAsDouble-advanced);
        internal void Reset()=>advanced=Time.timeAsDouble;
        internal static bool Active(PlayerSnapshot state)=>state!=null&&PlayerResources.Read(state,"health")>0&&state.Rows.Any(r=>r.Table=="status"&&
            (ObjectDB.instance.GetStatusEffect(Convert.ToInt32(r.Values[0])) is SE_Burning||ObjectDB.instance.GetStatusEffect(Convert.ToInt32(r.Values[0])) is SE_Poison));
        internal PlayerActionPlan Prepare(PlayerSnapshot state,Player player)
        {
            if(!player||!Active(state)){Reset();return null;}
            double seconds=Math.Min(60,Elapsed);if(seconds<=0)return null;
            var result=GameDamageOverTime.Advance(state,player,seconds);advanced+=seconds;
            if(result.Lethal)return GameDeathTransaction.Prepare(state,result.Changes,player,result.LastHit??new HitData());
            if(result.Changes.Length==0)return null;
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,result.Changes),new Dictionary<long,ObjectRecord>()),()=>{});
        }
    }
}
