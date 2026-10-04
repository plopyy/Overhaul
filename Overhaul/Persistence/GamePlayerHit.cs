using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GamePlayerHit
    {
        internal static void Merge(List<PlayerChange> changes,IEnumerable<PlayerChange> delta)
        {
            foreach(var row in delta)
            {
                changes.RemoveAll(r=>r.SameKey(row)||row.Delete&&row.Table=="status"&&r.Table=="status_data"&&r.SameStatus(row)||
                    row.Delete&&row.Table=="inventory"&&r.Table=="item_data"&&r.SameInventorySlot(row));
                changes.Add(row);
            }
        }
        internal static PlayerActionPlan Prepare(PlayerSnapshot state,Player player,HitData incoming,bool direct=false)
        {
            if(!player||PlayerResources.Read(state,"health")<=0||GameDeathProgress.IsDead(state)||player.IsTeleporting()||player.InCutscene())return null;
            var hit=incoming.Clone();var attacker=hit.GetAttacker();
            if(!direct&&(hit.HaveAttacker()&&!attacker||hit.m_dodgeable&&player.IsDodgeInvincible()||attacker is Player&&!player.IsPVPEnabled()&&!hit.m_ignorePVP))return null;
            var changes=new List<PlayerChange>();var current=state;var publish=new List<Action>();
            void Change(IEnumerable<PlayerChange> delta){var rows=delta.ToArray();Merge(changes,rows);current=PlayerProgressService.Overlay(current,rows);}
            if(!direct&&attacker&&!attacker.IsPlayer())hit.ApplyModifier(Game.instance.GetDifficultyDamageScalePlayer(player.transform.position)*Game.m_enemyDamageRate);
            if(!direct&&hit.m_eitrAdd>0)Change(PlayerResources.Restore(current,0,0,hit.m_eitrAdd));
            GameBlockMath.Result block=null;
            if(!direct)
            {
                var shield=GameShieldDamage.Prepare(current,player,hit);Change(shield.Changes);hit=shield.Hit;publish.Add(shield.Publish);
                var pose=hit.m_blockable?GameBlockControl.Blocking(player.GetZDOID(),player,current,hit.m_dir):null;
                if(pose!=null)
                {
                    block=GameBlockMath.Prepare(current,player,hit,pose.Slot,pose.Timed,GameHitFeedback.Stagger(player,current),attacker);
                    Change(block.Changes);hit=block.Hit;
                    if(block.Attempted)Change(PlayerCraftProgressGame.Raise(current,Skills.SkillType.Blocking,block.SkillGain));
                }
                int status=hit.m_statusEffectHash;
                var mods=player.m_damageModifiers;
                if(status!=0&&!(status==SEMan.s_statusEffectBurning&&mods.m_fire==HitData.DamageModifier.Immune)&&
                    !(status==SEMan.s_statusEffectFrost&&mods.m_frost==HitData.DamageModifier.Immune)&&
                    !(status==SEMan.s_statusEffectLightning&&mods.m_lightning==HitData.DamageModifier.Immune)&&
                    !(status==SEMan.s_statusEffectPoison&&mods.m_poison==HitData.DamageModifier.Immune))
                    Change(GameStatusImpact.Prepare(current,player,status,hit.m_itemLevel,hit.m_skillLevel,hit.m_variant,hit.m_attacker));
            }
            GamePlayerDamageMath.Result damage;
            if(direct)
            {
                hit.ApplyModifier(Game.m_localDamgeTakenRate);float amount=hit.GetTotalDamage();
                if(float.IsNaN(amount)||float.IsInfinity(amount)||amount<0)throw new InvalidOperationException("Invalid direct server damage");
                double health=Math.Max(0,PlayerResources.Read(current,"health")-amount);
                damage=new GamePlayerDamageMath.Result{Direct=hit,Damage=amount,Stagger=hit.m_damage.GetTotalStaggerDamage()*hit.m_staggerMultiplier,Lethal=amount>.1f&&health==0,
                    Changes=amount>.1f?new[]{PlayerResources.Row("health",health)}:Array.Empty<PlayerChange>()};
            }
            else
            {
                var weak=player.GetWeakSpot(hit.m_weakSpot);
                damage=GamePlayerDamageMath.Mitigate(current,hit,weak?weak.m_damageModifiers:player.m_damageModifiers,Game.m_localDamgeTakenRate,UnityEngine.Random.Range(0,int.MaxValue));
            }
            Change(damage.Changes);
            Change(new[]{PlayerCraftProgressGame.Increment(current,"statistics:0:values",((int)(attacker is Player?PlayerStatType.HitsTakenPlayers:PlayerStatType.HitsTakenEnemies)).ToString(CultureInfo.InvariantCulture),1)});
            if(!direct&&!damage.Lethal)
            {
                Change(GameElementalDamage.Prepare(current,player,damage.Fire,damage.Spirit,damage.Poison,hit.m_variant,hit.m_attacker));
                if(damage.Direct.m_damage.m_frost>0)Change(GameStatusImpact.Prepare(current,player,SEMan.s_statusEffectFrost,0,0,hit.m_variant,hit.m_attacker,damage.Direct.m_damage.m_frost));
                if(damage.Direct.m_damage.m_lightning>0)Change(GameStatusImpact.Prepare(current,player,SEMan.s_statusEffectLightning,0,0,hit.m_variant,hit.m_attacker));
            }
            var final=current;var impact=hit.Clone();
            publish.Add(()=>GameHitFeedback.Publish(player,final,impact,damage,block,direct));
            PlayerActionPlan death=damage.Lethal?GameDeathTransaction.Prepare(state,changes,player,damage.Direct):null;
            if(death!=null)return new PlayerActionPlan(death.Change,()=>{foreach(var action in publish)action();death.Publish();});
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>{foreach(var action in publish)action();});
        }
    }
}
