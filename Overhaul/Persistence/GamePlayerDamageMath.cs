using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul.Persistence
{
    // The caller supplies a server-origin hit after dodge, shield and block
    // processing. Native resistance/armor formulas are shared with Valheim.
    // Publication, status timers and death are owned by the damage scheduler.
    internal static class GamePlayerDamageMath
    {
        internal sealed class Result
        {
            internal HitData Direct;
            internal HitData.DamageModifier Modifier;
            internal PlayerChange[] Changes;
            internal float Fire,Poison,Spirit,Damage,Stagger;
            internal bool Lethal;
        }
        internal static void Validate(HitData hit,float takenRate)
        {
            if(hit==null)throw new ArgumentNullException(nameof(hit));
            var d=hit.m_damage;
            foreach(float value in new[]{d.m_damage,d.m_blunt,d.m_slash,d.m_pierce,d.m_chop,d.m_pickaxe,d.m_fire,d.m_frost,d.m_lightning,d.m_poison,d.m_spirit,hit.m_staggerMultiplier,takenRate})
                if(float.IsNaN(value) || float.IsInfinity(value) || value<0)throw new InvalidOperationException("Invalid server damage value");
        }
        internal static Result Mitigate(PlayerSnapshot state,HitData incoming,HitData.DamageModifiers baseResistance,float takenRate,int armorSelection)
        {
            Validate(incoming,takenRate);var hit=incoming.Clone();
            var result=new Result{Direct=hit,Changes=Array.Empty<PlayerChange>()};
            if(PlayerResources.Read(state,"health")<=0)return result;
            var equipment=GameCombatEquipment.Equipped(state);var effects=GameAttackResources.Effects(state);
            var modifiers=baseResistance.Clone();
            foreach(var item in equipment.Where(GameCombatEquipment.Armor))modifiers.Apply(item.m_shared.m_damageModifiers);
            foreach(var effect in effects)effect.ModifyDamageMods(ref modifiers);
            hit.ApplyResistance(modifiers,out result.Modifier);
            hit.ApplyArmor(GameCombatEquipment.BodyArmor(equipment,effects));
            var rows=new List<PlayerChange>(GameCombatEquipment.ArmorWear(state,hit,armorSelection));
            // Native fire/poison/spirit enter their own timers and do not also
            // reduce health immediately. Armor still wears from that impact.
            result.Fire=hit.m_damage.m_fire;result.Poison=hit.m_damage.m_poison;result.Spirit=hit.m_damage.m_spirit;
            hit.m_damage.m_fire=0;hit.m_damage.m_poison=0;hit.m_damage.m_spirit=0;
            hit.ApplyModifier(takenRate);result.Damage=hit.GetTotalDamage();
            if(float.IsNaN(result.Damage) || float.IsInfinity(result.Damage))throw new InvalidOperationException("Server damage overflow");
            if(result.Damage>.1f)
            {
                double health=Math.Max(0,PlayerResources.Read(state,"health")-result.Damage);
                rows.Add(PlayerResources.Row("health",health));result.Lethal=health==0;
                result.Stagger=hit.m_damage.GetTotalStaggerDamage()*hit.m_staggerMultiplier;
            }
            result.Changes=rows.ToArray();return result;
        }
    }
}
