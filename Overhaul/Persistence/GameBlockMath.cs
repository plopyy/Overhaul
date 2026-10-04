using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    // A server-validated blocking pose supplies the selected blocker and timing.
    // Visuals, counterattacks, adrenaline and status procs belong to publication.
    internal static class GameBlockMath
    {
        internal sealed class Result
        {
            internal HitData Hit;
            internal PlayerChange[] Changes;
            internal bool Attempted,Blocked,Timed,Staggered;
            internal float Absorbed,Stagger,SkillGain;
        }
        internal static Result Prepare(PlayerSnapshot state,Player definition,HitData incoming,int slot,bool timed,float stagger,bool hasAttacker)
        {
            if(!definition||incoming==null||float.IsNaN(stagger)||float.IsInfinity(stagger)||stagger<0)throw new InvalidOperationException("Invalid server block input");
            var hit=incoming.Clone();var result=new Result{Hit=hit,Changes=Array.Empty<PlayerChange>(),Stagger=stagger};
            if(PlayerResources.Read(state,"health")<=0)return result;
            var inventory=new PlayerActionInventory(state.Rows,InventoryMoveGame.PlayerLayout(state.Rows));
            var row=inventory.Item(slot);var item=PlayerInventoryView.ReadItem(row,null,true);
            if(!item.m_equipped||inventory.Layout.Cosmetic(slot)||item.m_shared.m_useDurability&&item.m_durability<=0)throw new InvalidOperationException("Canonical blocking item is unavailable");
            var effects=GameAttackResources.Effects(state);var equipment=GameCombatEquipment.Equipped(state);
            float skill=Mathf.Clamp01(GameAttackResources.SkillLevel(state,Skills.SkillType.Blocking,effects)/100f);
            float power=item.GetBlockPower(skill);timed&=item.m_shared.m_timedBlockBonus>1;
            if(timed){power*=item.m_shared.m_timedBlockBonus;foreach(var effect in effects)effect.ModifyTimedBlockBonus(ref power);}
            if(float.IsNaN(power)||float.IsInfinity(power))throw new InvalidOperationException("Invalid canonical block power");
            if(power<=0)return result;
            if(item.m_shared.m_damageModifiers.Count>0){var mods=new HitData.DamageModifiers();mods.Apply(item.m_shared.m_damageModifiers);hit.ApplyResistance(mods,out _);}
            var reduced=hit.m_damage.Clone();reduced.ApplyArmor(power);
            float before=hit.GetTotalBlockableDamage(),absorbed=before-reduced.GetTotalBlockableDamage(),fraction=Mathf.Clamp01(absorbed/power);
            double stamina=PlayerResources.Read(state,"stamina"),maximum=PlayerResources.Read(state,"max_stamina");
            float modifier=equipment.Sum(i=>i.m_shared.m_blockStaminaModifier);bool spent=false;
            void Cost(float cost)
            {
                foreach(var effect in effects)effect.ModifyBlockStaminaUsage(cost,ref cost,false);
                if(float.IsNaN(cost)||float.IsInfinity(cost))throw new InvalidOperationException("Invalid block stamina cost");
                if(cost>0){stamina=Math.Max(0,stamina-cost*Game.m_staminaRate);spent=true;}
                else stamina=Math.Min(maximum,stamina-cost);
            }
            Cost((timed?definition.m_perfectBlockStaminaDrain:definition.m_blockStaminaDrain*fraction)*(1+modifier));
            float added=reduced.GetTotalStaggerDamage();foreach(var effect in effects)effect.ModifyStagger(added,ref added);
            float threshold=(float)PlayerResources.Read(state,"max_health")*definition.m_staggerDamageFactor;
            if(threshold>0){result.Stagger=Mathf.Min(threshold,stagger+added);result.Staggered=result.Stagger>=threshold;}
            result.Attempted=true;result.Timed=timed;result.Blocked=stamina>0&&!result.Staggered;result.Absorbed=absorbed;result.SkillGain=timed?2:1;
            if(result.Blocked){hit.m_statusEffectHash=0;hit.BlockDamage(absorbed);hit.m_pushForce*=fraction;}
            if(item.m_shared.m_useDurability)GameEquipmentWear.Apply(inventory,slot,row,item.m_shared.m_useDurabilityDrain*(before/power)*Game.m_durabilityRate);
            if(hasAttacker&&timed&&result.Blocked)
            {
                if(item.m_shared.m_perfectBlockStaminaRegen>0)stamina=Math.Min(maximum,stamina+item.m_shared.m_perfectBlockStaminaRegen);
                else Cost(definition.m_perfectBlockStaminaDrain*(1-modifier));
            }
            var changes=inventory.Delta(Guid.NewGuid().ToString("N"),state.Revision).Changes.ToList();
            changes.Add(PlayerResources.Row("stamina",stamina));if(spent)changes.Add(PlayerResources.Row(PlayerResources.StaminaDelay,definition.m_staminaRegenDelay));
            result.Changes=changes.ToArray();return result;
        }
    }
}
