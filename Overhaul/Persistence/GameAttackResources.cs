using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameAttackResources
    {
        internal sealed class Costs { internal double Stamina,Eitr,Health; }
        internal static StatusEffect[] Effects(PlayerSnapshot snapshot)
        {
            var equipped=GameCombatEquipment.Equipped(snapshot);
            var effects=PlayerPotionGame.Active(snapshot).ToList();
            foreach(var header in snapshot.Rows.Where(r=>r.Table=="status"))
            {
                int id=Convert.ToInt32(header.Values[0]);
                effects.Insert(0,GameStatusCodec.Modifiers(snapshot.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id)));
            }
            foreach(var item in equipped)
            {
                if(item.m_shared.m_equipStatusEffect)effects.Add(item.m_shared.m_equipStatusEffect);
                if(item.m_shared.m_setStatusEffect && equipped.Count(i=>i.m_shared.m_setName==item.m_shared.m_setName)>=item.m_shared.m_setSize)effects.Add(item.m_shared.m_setStatusEffect);
            }
            return effects.GroupBy(e=>e.NameHash()).Select(g=>g.First()).ToArray();
        }
        internal static float SkillLevel(PlayerSnapshot snapshot,Skills.SkillType type,IEnumerable<StatusEffect> effects)
        {
            if(type==Skills.SkillType.None)return 0;
            var row=snapshot.Rows.FirstOrDefault(r=>r.Table=="skills" && Convert.ToInt32(r.Values[0])==(int)type);
            float level=row==null?0:Convert.ToSingle(row.Values[1]);foreach(var effect in effects)effect.ModifySkillLevel(type,ref level);
            return Mathf.Floor(level);
        }
        internal static Costs Calculate(PlayerSnapshot snapshot,ItemDrop.ItemData weapon,Attack attack)
        {
            if(weapon==null || attack==null)throw new InvalidOperationException("Attack resource definition is unavailable");
            var equipped=GameCombatEquipment.Equipped(snapshot);
            var effects=Effects(snapshot);
            float factor=Mathf.Clamp01(SkillLevel(snapshot,weapon.m_shared.m_skillType,effects)/100f);
            float stamina=0;
            if(attack.m_attackStamina>0)
            {
                stamina=attack.m_attackStamina*(1+equipped.Sum(i=>attack.m_isHomeItem?i.m_shared.m_homeItemsStaminaModifier:i.m_shared.m_attackStaminaModifier));
                float original=stamina;foreach(var effect in effects)effect.ModifyAttackStaminaUsage(original,ref stamina);
                stamina=Mathf.Max(0,stamina)*(1-.33f*factor);
                if(attack.m_staminaReturnPerMissingHP>0)stamina-=(float)(PlayerResources.Read(snapshot,"max_health")-PlayerResources.Read(snapshot,"health"))*attack.m_staminaReturnPerMissingHP;
            }
            // Native GetAttackEitr uses the weapon's primary attack definition,
            // including when executing its secondary attack.
            double eitr=Math.Max(0,weapon.m_shared.m_attack?.m_attackEitr??0)*(1-.33f*factor);
            double health=(attack.m_attackHealth>0 || attack.m_attackHealthPercentage>0)?
                (attack.m_attackHealth+PlayerResources.Read(snapshot,"health")*attack.m_attackHealthPercentage/100d)*(1-.33f*factor):0;
            foreach(double value in new[]{(double)stamina,eitr,health})if(double.IsNaN(value) || double.IsInfinity(value))throw new InvalidOperationException("Invalid attack resource cost");
            return new Costs{Stamina=stamina,Eitr=eitr,Health=health};
        }
        internal static void ValidateStart(PlayerSnapshot snapshot,ItemDrop.ItemData weapon,Attack attack)
        {
            if(PlayerResources.Read(snapshot,"health")<=0)throw new InvalidOperationException("Dead character cannot attack");
            var cost=Calculate(snapshot,weapon,attack);
            if(cost.Stamina>0 && PlayerResources.Read(snapshot,"stamina")<=cost.Stamina+.1 ||
                cost.Eitr>0 && (PlayerResources.Read(snapshot,"max_eitr")==0 || PlayerResources.Read(snapshot,"eitr")<=cost.Eitr+.1))
                throw new InvalidOperationException("Insufficient attack resources");
        }
        internal static PlayerChange[] Spend(PlayerSnapshot snapshot,ItemDrop.ItemData weapon,Attack attack,bool burst)
        {
            bool perBurst=attack.m_attackType==Attack.AttackType.Projectile && attack.m_perBurstResourceUsage;
            if(burst!=perBurst)return Array.Empty<PlayerChange>();
            var cost=Calculate(snapshot,weapon,attack);
            double health=PlayerResources.Read(snapshot,"health"),stamina=PlayerResources.Read(snapshot,"stamina"),eitr=PlayerResources.Read(snapshot,"eitr");
            if(health<=0 || burst && (cost.Stamina>0 && stamina<=cost.Stamina || cost.Eitr>0 && eitr<=cost.Eitr || attack.m_attackHealthLowBlockUse && health<=cost.Health))
                throw new InvalidOperationException("Insufficient burst resources");
            var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            if(!definition)throw new InvalidOperationException("Player resource definitions are unavailable");
            double eitrCost=cost.Eitr*Game.m_eitrRate*(1-Mathf.Clamp01(PlayerCraftProgressGame.Bonus(snapshot,"eitr_cost"))),staminaCost=cost.Stamina*Game.m_staminaRate;
            if(double.IsNaN(staminaCost) || double.IsInfinity(staminaCost) || double.IsNaN(eitrCost) || double.IsInfinity(eitrCost) || eitrCost<0)throw new InvalidOperationException("Invalid world attack resource rate");
            var rows=new List<PlayerChange>();
            if(staminaCost!=0)
            {rows.Add(PlayerResources.Row("stamina",Math.Max(0,Math.Min(PlayerResources.Read(snapshot,"max_stamina"),stamina-staminaCost))));rows.Add(PlayerResources.Row(PlayerResources.StaminaDelay,definition.m_staminaRegenDelay));}
            if(eitrCost>0){rows.Add(PlayerResources.Row("eitr",Math.Max(0,eitr-eitrCost)));rows.Add(PlayerResources.Row(PlayerResources.EitrDelay,definition.m_eitrRegenDelay));}
            if(cost.Health>0)rows.Add(PlayerResources.Row("health",Math.Max(Math.Min(1,health),health-cost.Health)));
            if(attack.m_attackUseAdrenaline>0)GamePlayerHit.Merge(rows,GameAdrenaline.Change(PlayerProgressService.Overlay(snapshot,rows),definition,attack.m_attackUseAdrenaline));
            return rows.ToArray();
        }
    }
}
