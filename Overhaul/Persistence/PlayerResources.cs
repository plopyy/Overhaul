using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Canonical resource arithmetic. Callers supply costs from server definitions.
    internal static class PlayerResources
    {
        internal const string StaminaDelay="overhaul_stamina_delay",EitrDelay="overhaul_eitr_delay",FoodRegen="overhaul_food_regen";
        internal const string Adrenaline="adrenaline",AdrenalineDelay="adrenaline_delay",AdrenalineMaximum="max_adrenaline",AdrenalineLastMaximum="adrenaline_last_max";
        internal const string BlockCharges="block_charges",BlockChargeAge="block_charge_age";
        internal static bool IsKey(string key)=>key=="health" || key=="stamina" || key=="eitr" || key=="max_health" || key=="max_stamina" || key=="max_eitr" || key=="guardian_cooldown" || key==StaminaDelay || key==EitrDelay || key==FoodRegen || key==Adrenaline || key==AdrenalineDelay || key==AdrenalineMaximum || key==AdrenalineLastMaximum || key==BlockCharges || key==BlockChargeAge;
        internal static double Read(PlayerSnapshot snapshot,string key)
        {
            var row=snapshot.Rows.SingleOrDefault(r=>r.Table=="state" && (string)r.Values[0]==key);
            if(row==null || row.Delete || row.Values[2]==null)throw new InvalidDataException("Missing canonical resource: "+key);
            double value=Convert.ToDouble(row.Values[2]);
            if(double.IsNaN(value) || double.IsInfinity(value) || value<0)throw new InvalidDataException("Invalid canonical resource: "+key);
            return value;
        }
        internal static PlayerChange Row(string key,double value)
        {
            if(!IsKey(key) || double.IsNaN(value) || double.IsInfinity(value) || value<0)throw new InvalidDataException("Invalid server resource change");
            return new PlayerChange("state",false,key,null,value,null,null);
        }
        internal static PlayerChange[] Spend(PlayerSnapshot snapshot,double stamina,double eitr,double staminaDelay,double eitrDelay)
        {
            foreach(double value in new[]{stamina,eitr,staminaDelay,eitrDelay})
                if(double.IsNaN(value) || double.IsInfinity(value) || value<0)throw new InvalidOperationException("Invalid resource cost");
            var changes=new List<PlayerChange>();
            void Debit(string key,double cost,string timer,double delay)
            {
                if(cost==0)return;
                double current=Read(snapshot,key);
                if(current<=cost)throw new InvalidOperationException("Insufficient "+key);
                changes.Add(Row(key,current-cost));changes.Add(Row(timer,delay));
            }
            Debit("stamina",stamina,StaminaDelay,staminaDelay);Debit("eitr",eitr,EitrDelay,eitrDelay);
            return changes.ToArray();
        }
        internal static PlayerChange[] Restore(PlayerSnapshot snapshot,double health,double stamina,double eitr)
        {
            var result=new List<PlayerChange>();
            void Add(string key,double amount)
            {
                if(double.IsNaN(amount) || double.IsInfinity(amount) || amount<0)throw new InvalidOperationException("Invalid resource recovery");
                if(amount==0)return;
                double current=Read(snapshot,key),maximum=Read(snapshot,"max_"+key);
                if(key=="health" && current==0)throw new InvalidOperationException("Recovery cannot resurrect a dead player");
                result.Add(Row(key,Math.Min(maximum,current+amount)));
            }
            Add("health",health);Add("stamina",stamina);Add("eitr",eitr);return result.ToArray();
        }
        internal sealed class Rates
        {
            internal double MaxHealth,MaxStamina,MaxEitr,FoodHeal,PassiveHeal;
            internal double Stamina,Eitr,StaminaShape;
        }
        internal static PlayerChange[] Regenerate(PlayerSnapshot snapshot,double seconds,Rates rates)
        {
            foreach(double value in new[]{seconds,rates.MaxHealth,rates.MaxStamina,rates.MaxEitr,rates.FoodHeal,rates.PassiveHeal,rates.Stamina,rates.Eitr,rates.StaminaShape})
                if(double.IsNaN(value) || double.IsInfinity(value) || value<0)throw new InvalidOperationException("Invalid regeneration parameters");
            if(seconds>3600)throw new InvalidOperationException("Regeneration interval exceeds active session limit");
            double Optional(string key)=>snapshot.Rows.Any(r=>r.Table=="state" && (string)r.Values[0]==key)?Read(snapshot,key):0;
            var result=new List<PlayerChange>();
            void Changed(string key,double value){if(Optional(key)!=value)result.Add(Row(key,value));}
            double health=Math.Min(Read(snapshot,"health"),rates.MaxHealth),stamina=Math.Min(Read(snapshot,"stamina"),rates.MaxStamina),eitr=Math.Min(Read(snapshot,"eitr"),rates.MaxEitr);
            double staminaDelay=Optional(StaminaDelay),eitrDelay=Optional(EitrDelay),foodTimer=Optional(FoodRegen);
            // Closed form of the native 20 ms linear recurrence: no per-frame replay loop
            // when a database operation delays the next simulation step.
            double Regen(double value,double max,double rate,double shape,double active)
            {
                if(max==0 || rate==0 || active==0)return value;
                if(shape==0)return Math.Min(max,value+rate*active);
                double step=.02,whole=Math.Floor(active/step),fraction=active-whole*step;
                double factor=1-rate*shape*step/max,target=max*(1+shape)/shape;
                if(whole>0)value=factor<=0?max:Math.Min(max,target+(value-target)*Math.Pow(factor,whole));
                return Math.Min(max,value+rate*(1+(1-value/max)*shape)*fraction);
            }
            stamina=Regen(stamina,rates.MaxStamina,rates.Stamina,rates.StaminaShape,Math.Max(0,seconds-staminaDelay));
            eitr=Regen(eitr,rates.MaxEitr,rates.Eitr,1,Math.Max(0,seconds-eitrDelay));
            staminaDelay=Math.Max(0,staminaDelay-seconds);eitrDelay=Math.Max(0,eitrDelay-seconds);
            foodTimer+=seconds;double heals=Math.Floor(foodTimer/10);foodTimer-=heals*10;
            if(health>0)health=Math.Min(rates.MaxHealth,health+rates.PassiveHeal*seconds+rates.FoodHeal*heals);
            Changed("max_health",rates.MaxHealth);Changed("max_stamina",rates.MaxStamina);Changed("max_eitr",rates.MaxEitr);
            Changed("health",health);Changed("stamina",stamina);Changed("eitr",eitr);
            Changed(StaminaDelay,staminaDelay);Changed(EitrDelay,eitrDelay);Changed(FoodRegen,foodTimer);
            return result.ToArray();
        }
    }
}
