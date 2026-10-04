using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Overhaul.Persistence
{
    // Native effect state stays readable: one status row and only numeric fields
    // that differ from its registered definition. No Unity reference is stored.
    internal static class GameStatusCodec
    {
        private static readonly Dictionary<Type,Dictionary<string,FieldInfo>> fields=new Dictionary<Type,Dictionary<string,FieldInfo>>();
        private static bool Numeric(Type type)=>type==typeof(float)||type==typeof(double)||type==typeof(int)||type==typeof(short)||type==typeof(byte)||type==typeof(bool)||type.IsEnum;
        private static Dictionary<string,FieldInfo> Fields(Type type)
        {
            if(fields.TryGetValue(type,out var found))return found;
            var result=new Dictionary<string,FieldInfo>();
            for(var current=type;current!=null&&typeof(StatusEffect).IsAssignableFrom(current);current=current.BaseType)
                foreach(var field in current.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                    if(!field.IsInitOnly && Numeric(field.FieldType) && field.Name!="m_time" && field.Name!="m_ttl" && field.Name!="m_hitVariant" && field.Name!="m_nameHash")
                        result.Add(current.Name+"."+field.Name,field);
            fields.Add(type,result);return result;
        }
        private static double Number(object value)
        {
            double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
            if(double.IsNaN(number)||double.IsInfinity(number))throw new InvalidDataException("Non-finite native status field");
            return number;
        }
        private static StatusEffect Definition(int id,string name=null)
        {
            var effect=ObjectDB.instance?ObjectDB.instance.GetStatusEffect(id):null;
            if(!effect || name!=null && effect.name!=name)throw new InvalidDataException("Saved status definition is unavailable");
            return effect;
        }
        internal static PlayerChange[] Capture(StatusEffect effect,ZDOID attacker)
        {
            if(!effect)throw new ArgumentNullException(nameof(effect));
            int id=effect.NameHash();var definition=Definition(id);
            if(effect.GetType()!=definition.GetType() || Number(effect.m_time)<0 || Number(effect.m_ttl)<0)throw new InvalidDataException("Invalid native status state");
            var result=new List<PlayerChange>{new PlayerChange("status",false,id,definition.name,(double)effect.m_time,(double)effect.m_ttl,attacker.UserID,(long)attacker.ID,(int)effect.m_hitVariant)};
            foreach(var pair in Fields(effect.GetType()))
            {
                double value=Number(pair.Value.GetValue(effect));
                if(value!=Number(pair.Value.GetValue(definition)))result.Add(new PlayerChange("status_data",false,id,pair.Key,value));
            }
            return result.ToArray();
        }
        internal static StatusEffect Restore(IEnumerable<PlayerChange> source,Player player)
        {
            var rows=source.ToArray();var header=rows.SingleOrDefault(r=>r.Table=="status"&&!r.Delete);
            if(header==null || rows.Any(r=>r.Delete || r.Table!="status"&&r.Table!="status_data"))throw new InvalidDataException("Invalid saved native status rows");
            var values=header.Values;int id=Convert.ToInt32(values[0]);var definition=Definition(id,(string)values[1]);
            double age=Number(values[2]),duration=Number(values[3]);long owner=Convert.ToInt64(values[4]),instance=Convert.ToInt64(values[5]);int variant=Convert.ToInt32(values[6]);
            if(age<0 || duration<0 || age>float.MaxValue || duration>float.MaxValue || instance<0 || instance>uint.MaxValue || variant<short.MinValue || variant>short.MaxValue)
                throw new InvalidDataException("Invalid saved status header");
            // Use Valheim's managed clone, just as SEMan does. Instantiating or
            // destroying a Unity asset here would leak or alter the definition.
            var effect=definition.Clone();effect.m_character=player;effect.m_startEffectInstances=null;
            effect.m_time=(float)age;effect.m_ttl=(float)duration;effect.m_hitVariant=(short)variant;
            var allowed=Fields(effect.GetType());var seen=new HashSet<string>();
            foreach(var row in rows.Where(r=>r.Table=="status_data"))
            {
                var v=row.Values;string key=(string)v[1];
                if(Convert.ToInt32(v[0])!=id || !seen.Add(key) || !allowed.TryGetValue(key,out var field))throw new InvalidDataException("Unknown or repeated native status field");
                double number=Number(v[2]);var type=field.FieldType;
                if(type==typeof(bool) && number!=0&&number!=1 || type!=typeof(float)&&type!=typeof(double)&&type!=typeof(bool)&&number!=Math.Truncate(number) || type==typeof(float)&&Math.Abs(number)>float.MaxValue)
                    throw new InvalidDataException("Invalid native status field value");
                field.SetValue(effect,type.IsEnum?Enum.ToObject(type,checked((int)number)):Convert.ChangeType(number,type,CultureInfo.InvariantCulture));
            }
            if(instance!=0)
            {var go=ZNetScene.instance?ZNetScene.instance.FindInstance(new ZDOID(owner,(uint)instance)):null;var attacker=go?go.GetComponent<Character>():null;if(attacker)effect.SetAttacker(attacker);}
            return effect;
        }
        internal static PlayerChange[] Delta(PlayerSnapshot state,StatusEffect effect,ZDOID attacker)
        {
            var after=Capture(effect,attacker);int id=effect.NameHash();
            var before=state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id).ToArray();
            bool Equal(object a,object b)=>a==null||b==null||a is string||b is string?Equals(a,b):Number(a)==Number(b);
            var changes=after.Where(row=>!before.Any(old=>old.SameKey(row)&&old.Values.Zip(row.Values,Equal).All(v=>v))).ToList();
            foreach(var row in before.Where(r=>r.Table=="status_data"&&!after.Any(r.SameKey)))
                changes.Add(new PlayerChange("status_data",true,row.Values[0],row.Values[1]));
            return changes.ToArray();
        }
        internal static void Apply(StatusEffect source,StatusEffect target)
        {
            if(source.GetType()!=target.GetType() || source.NameHash()!=target.NameHash())throw new InvalidDataException("Status presentation type mismatch");
            foreach(var field in Fields(source.GetType()).Values)field.SetValue(target,field.GetValue(source));
            target.m_time=source.m_time;target.m_ttl=source.m_ttl;target.m_hitVariant=source.m_hitVariant;
        }
    }
}
