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
        private double? stopped;
        internal double Elapsed=>Math.Max(0,(stopped??Time.timeAsDouble)-advanced);
        internal void Reset()=>advanced=stopped??Time.timeAsDouble;
        internal void Freeze(){if(!stopped.HasValue)stopped=Time.timeAsDouble;}
        internal static bool Active(PlayerSnapshot state)=>state!=null&&PlayerResources.Read(state,"health")>0&&(GameAdrenaline.Read(state,PlayerResources.BlockCharges)>0||state.Rows.Any(r=>r.Table=="status"&&
            GameDamageOverTime.Supported(ObjectDB.instance.GetStatusEffect(Convert.ToInt32(r.Values[0])))));
        internal PlayerActionPlan Prepare(PlayerSnapshot state,Player player,bool final=false)
        {
            if(!player||!Active(state)){Reset();return null;}
            var changes=new List<PlayerChange>();var current=state;var publish=new List<Action>();
            do
            {
                double seconds=Math.Min(60,Elapsed);if(seconds<=0)break;
                var result=GameDamageOverTime.Advance(current,player,seconds);advanced+=seconds;
                if(result.Publish!=null)publish.Add(result.Publish);
                foreach(var row in result.Changes)
                {
                    changes.RemoveAll(r=>r.SameKey(row)||row.Delete&&row.Table=="status"&&r.Table=="status_data"&&r.SameStatus(row));
                    changes.Add(row);
                }
                current=PlayerProgressService.Overlay(current,result.Changes);
                if(result.Lethal)
                {
                    if(result.Death!=null)return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,changes),result.Death.Change.Objects),()=>{foreach(var action in publish)action();});
                    var death=GameDeathTransaction.Prepare(state,changes,player,result.LastHit??new HitData());
                    return new PlayerActionPlan(death.Change,()=>{foreach(var action in publish)action();death.Publish();});
                }
            }while(final&&Elapsed>0&&Active(current));
            if(changes.Count==0)return null;
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>{foreach(var action in publish)action();});
        }
    }
}
