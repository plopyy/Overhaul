using System;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static class GameElementalDamage
    {
        // Damage amounts have already passed resistance and armor. Native
        // accumulation rules decide whether weaker damage refreshes an effect.
        internal static PlayerChange[] Prepare(PlayerSnapshot state,Player player,float fire,float spirit,float poison,short variant,ZDOID attacker)
        {
            foreach(float value in new[]{fire,spirit,poison})if(float.IsNaN(value)||float.IsInfinity(value)||value<0)throw new InvalidOperationException("Invalid elemental damage");
            if(!player)throw new ArgumentNullException(nameof(player));
            if(PlayerResources.Read(state,"health")<=0)return Array.Empty<PlayerChange>();
            var changes=new List<PlayerChange>();
            void Add(int id,float amount,Func<StatusEffect,float,bool> accumulate)
            {
                if(amount<=0)return;
                var before=state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id).ToArray();
                bool existing=before.Any(r=>r.Table=="status");StatusEffect effect;
                if(existing)effect=GameStatusCodec.Restore(before,player);
                else
                {
                    var definition=ObjectDB.instance.GetStatusEffect(id);if(!definition)throw new InvalidOperationException("Elemental status definition is unavailable");
                    effect=definition.Clone();effect.m_character=player;effect.m_startEffectInstances=null;effect.m_time=0;effect.m_hitVariant=variant;
                }
                bool retained=accumulate(effect,amount);
                if(retained)changes.AddRange(GameStatusCodec.Delta(state,effect,attacker));
                else if(existing)changes.Add(new PlayerChange("status",true,id));
            }
            bool Burn(StatusEffect effect,float amount,bool isFire)
            {
                if(!(effect is SE_Burning burning)||burning.m_damageInterval<=0||burning.m_ttl<burning.m_damageInterval)
                    throw new InvalidOperationException("Invalid burning definition");
                return isFire?burning.AddFireDamage(amount):burning.AddSpiritDamage(amount);
            }
            Add(SEMan.s_statusEffectBurning,fire,(effect,amount)=>Burn(effect,amount,true));
            Add(SEMan.s_statusEffectSpirit,spirit,(effect,amount)=>Burn(effect,amount,false));
            Add(SEMan.s_statusEffectPoison,poison,(effect,amount)=>
            {
                if(!(effect is SE_Poison poisoned)||poisoned.m_damageInterval<=0)throw new InvalidOperationException("Invalid poison definition");
                poisoned.AddDamage(amount);return true;
            });
            return changes.ToArray();
        }
    }
}
