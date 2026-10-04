using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameDeathProgress
    {
        internal const string Dead="server_dead";
        internal static bool IsDead(PlayerSnapshot state)=>state.Rows.Any(r=>r.Table=="state"&&(string)r.Values[0]==Dead&&Convert.ToBoolean(r.Values[1]));
        internal static IEnumerable<PlayerChange> Advance(PlayerSnapshot state,double seconds)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0)throw new InvalidOperationException("Invalid death protection interval");
            if(seconds==0||IsDead(state)||PlayerResources.Read(state,"health")<=0)yield break;
            var row=state.Rows.SingleOrDefault(r=>r.Table=="state"&&(string)r.Values[0]=="time_since_death");
            if(row==null)yield break;
            double elapsed=Convert.ToDouble(row.Values[2]);
            if(double.IsNaN(elapsed)||double.IsInfinity(elapsed)||elapsed<0)throw new InvalidOperationException("Invalid saved death protection time");
            yield return new PlayerChange("state",false,"time_since_death",null,Math.Min(float.MaxValue,elapsed+seconds),null,null);
        }
        internal static PlayerChange[] Prepare(PlayerSnapshot state,Vector3 position,HitData hit,bool hardDeath,bool resetSkills,float skillLoss)
        {
            if(IsDead(state))return Array.Empty<PlayerChange>();
            if(hit==null||float.IsNaN(skillLoss)||float.IsInfinity(skillLoss)||skillLoss<0||skillLoss>1||
                new[]{position.x,position.y,position.z}.Any(v=>float.IsNaN(v)||float.IsInfinity(v)))
                throw new InvalidOperationException("Invalid server death state");
            var changes=new List<PlayerChange>{PlayerResources.Row("health",0),PlayerResources.Row(PlayerResources.Adrenaline,0),PlayerResources.Row(PlayerResources.AdrenalineDelay,0),
                new PlayerChange("state",false,Dead,1,null,null,null),
                new PlayerChange("state",false,GameRespawnGame.After,DateTime.UtcNow.AddSeconds(10).Ticks,null,null,null),
                new PlayerChange("state",false,"time_since_death",null,0d,null,null),
                new PlayerChange("spawn",false,"death",position.x,position.y,position.z),
                new PlayerChange("spawn",true,"logout")};
            foreach(var row in state.Rows)
            {
                if(row.Table=="food"||row.Table=="effects"||row.Table=="status")changes.Add(new PlayerChange(row.Table,true,row.Values[0]));
                if(row.Table=="skills")
                {
                    var v=row.Values;
                    if(resetSkills)changes.Add(new PlayerChange("skills",true,v[0]));
                    else if(hardDeath)
                    {
                        float level=Convert.ToSingle(v[1]);
                        changes.Add(new PlayerChange("skills",false,v[0],level-level*skillLoss,0f));
                    }
                }
            }
            void Stat(PlayerStatType type)=>changes.Add(PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)type).ToString(CultureInfo.InvariantCulture),1));
            Stat(PlayerStatType.Deaths);
            changes.Add(new PlayerChange("knowledge",false,"statistics:0:values",((int)PlayerStatType.ConsecutiveDaysSurvived).ToString(CultureInfo.InvariantCulture),"0"));
            if(Enum.TryParse("DeathBy"+hit.m_hitType,out PlayerStatType cause))Stat(cause);
            if(hit.m_hitType==HitData.HitType.Tree)
            {
                if(hit.m_toolTier<=5)Stat(PlayerStatType.DeathByTreeTier0+(int)hit.m_toolTier);
                var variant=(PlayerStatType)hit.m_variant;
                if(variant>=PlayerStatType.TreeFir&&variant<=PlayerStatType.TreeSnowPine)Stat(variant);
            }
            return changes.ToArray();
        }
    }
}
