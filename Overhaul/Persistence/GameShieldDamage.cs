using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameShieldDamage
    {
        internal sealed class Result
        {
            internal HitData Hit;
            internal PlayerChange[] Changes;
            internal Action Publish;
        }
        internal static Result Prepare(PlayerSnapshot state,Player player,HitData incoming)
        {
            if(!player||incoming==null)throw new ArgumentException("Invalid shield impact");
            var hit=incoming.Clone();float damage=hit.GetTotalDamage();
            if(float.IsNaN(damage)||float.IsInfinity(damage)||damage<0)throw new InvalidOperationException("Invalid shield damage");
            var changes=new List<PlayerChange>();var visuals=new List<Action>();var current=state;
            foreach(var header in state.Rows.Where(r=>r.Table=="status"))
            {
                int id=Convert.ToInt32(header.Values[0]);
                if(!(ObjectDB.instance.GetStatusEffect(id) is SE_Shield))continue;
                var shield=(SE_Shield)GameStatusCodec.Restore(state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id),player);
                if(shield is SE_StaffGuard inactive&&(!inactive.m_guardActive||!GameStaffGuardRuntime.CanHold(current,player)))continue;
                bool expired=shield.m_ttl>0&&shield.m_time>shield.m_ttl;
                bool broken=shield.m_damage>shield.m_totalAbsorbDamage;
                if(!expired&&!broken)
                {
                    // Native shields absorb the entire impact, including the
                    // hit that exceeds their remaining capacity. IsDone has
                    // live skill/visual side effects, so it cannot run here.
                    shield.m_damage+=hit.GetTotalDamage();hit.ApplyModifier(0);
                    if(float.IsNaN(shield.m_damage)||float.IsInfinity(shield.m_damage))throw new InvalidOperationException("Invalid shield capacity");
                    broken=shield.m_damage>shield.m_totalAbsorbDamage;
                    visuals.Add(()=>{if(player)shield.m_hitEffects.Create(hit.m_point,hit.m_dir.sqrMagnitude>0?Quaternion.LookRotation(-hit.m_dir):player.transform.rotation,player.transform);});
                }
                PlayerChange[] delta;
                if(broken&&shield is SE_StaffGuard guard)
                {GameStaffGuardRules.Break(guard);delta=GameStatusCodec.Delta(current,guard,ZDOID.None);visuals.Add(()=>{if(player)player.Stagger(Vector3.zero);});}
                else if(expired||broken)delta=new[]{new PlayerChange("status",true,id)};
                else delta=GameStatusCodec.Delta(current,shield,new ZDOID(Convert.ToInt64(header.Values[4]),checked((uint)Convert.ToInt64(header.Values[5]))));
                changes.AddRange(delta);current=PlayerProgressService.Overlay(current,delta);
                if(broken)
                {
                    if(shield.m_levelUpSkillOnBreak!=Skills.SkillType.None)
                    {
                        var skill=PlayerCraftProgressGame.Raise(current,shield.m_levelUpSkillOnBreak,shield.m_levelUpSkillFactor).ToArray();
                        foreach(var row in skill){changes.RemoveAll(r=>r.SameKey(row));changes.Add(row);}
                        current=PlayerProgressService.Overlay(current,skill);
                    }
                    visuals.Add(()=>{if(player)shield.m_breakEffects.Create(player.GetCenterPoint(),player.transform.rotation,player.transform,player.GetRadius()*2);});
                }
            }
            return new Result{Hit=hit,Changes=changes.ToArray(),Publish=()=>{foreach(var visual in visuals)visual();}};
        }
    }
}

