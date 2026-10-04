using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Canonical resource arithmetic. Callers supply costs from server definitions.
    internal static class PlayerResources
    {
        internal const string StaminaDelay="overhaul_stamina_delay",EitrDelay="overhaul_eitr_delay";
        internal static bool IsKey(string key)=>key=="health" || key=="stamina" || key=="eitr" || key=="max_health" || key=="max_stamina" || key=="max_eitr" || key==StaminaDelay || key==EitrDelay;
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
    }
}
