using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.AI
{
    internal enum Aggression { Normal, Aggressive, Cunning }
    internal enum Intelligence { Normal, Dumb, Smart, Vanilla }
    internal enum GroupBehavior { Normal, Helper, Pack }
    internal sealed class MobRule
    {
        internal Aggression Aggression;
        internal Intelligence Intelligence;
        internal GroupBehavior Group;
        internal bool Charge;
        internal float DumbChance;
        internal MobRule[] Resolved;
        internal bool Vanilla=>!Charge&&DumbChance==0&&Aggression==Aggression.Normal&&Intelligence==Intelligence.Vanilla&&Group==GroupBehavior.Normal;
    }
    internal static class MobBehaviorConfig
    {
        internal static Dictionary<string,MobRule> Current=new Dictionary<string,MobRule>(StringComparer.OrdinalIgnoreCase),Local;
        internal static bool Enabled=>Current.Values.Any(r=>!r.Vanilla);
        internal const string IntelligenceKey="overhaul_ai_intelligence_v1";
        internal const string StarRollKey="overhaul_ai_star_roll_v1";
        private static readonly MobRule Vanilla=new MobRule{Intelligence=Intelligence.Vanilla};
        internal static bool Excluded(Character character)=>character&&character.IsBoss();
        internal static MobRule Rule(Character character)
        {
            if(Excluded(character))return Vanilla;
            var rule=character&&Current.TryGetValue(Utils.GetPrefabName(character.gameObject),out var configured)?configured:Vanilla;
            if(!character||character is Player||!character.m_nview||!character.m_nview.IsValid())return rule;
            var data=character.m_nview.GetZDO();int saved=data.GetInt(IntelligenceKey,-1);
            if(data.GetBool(Dungeons.BossEncounter.BossKey,false))
            {
                saved=(int)Intelligence.Smart;
                if(character.m_nview.IsOwner()&&data.GetInt(IntelligenceKey,-1)!=saved)data.Set(IntelligenceKey,saved);
            }
            else if(saved<0||saved>(int)Intelligence.Vanilla)
            {
                if(!character.m_nview.IsOwner())return rule;
                saved=(int)((rule.DumbChance>=100||rule.DumbChance>0&&UnityEngine.Random.value*100f<rule.DumbChance)?Intelligence.Dumb:rule.Intelligence);
                data.Set(IntelligenceKey,saved);
            }
            // The same persisted roll also covers stars assigned by SpawnSystem after Awake.
            // A dumb result is final; stars only promote the other intelligence profiles.
            if(saved!=(int)Intelligence.Dumb&&saved!=(int)Intelligence.Smart&&character.m_nview.IsOwner()&&Current.ContainsKey(Utils.GetPrefabName(character.gameObject)))
            {
                float roll=data.GetFloat(StarRollKey,-1);
                if(roll<0){roll=UnityEngine.Random.value;data.Set(StarRollKey,roll);}
                int level=character.GetLevel();float chance=level>=3?.5f:level==2?.2f:0;
                if(roll<chance){saved=(int)Intelligence.Smart;data.Set(IntelligenceKey,saved);}
            }
            if((Intelligence)saved==rule.Intelligence&&rule.DumbChance==0)return rule;
            if(rule.Resolved==null)rule.Resolved=new MobRule[4];
            return rule.Resolved[saved]??(rule.Resolved[saved]=new MobRule{Aggression=rule.Aggression,Intelligence=(Intelligence)saved,Group=rule.Group,Charge=rule.Charge});
        }
        internal static Dictionary<string,MobRule> Parse(string text)
        {
            text=Utility.ConfigSections.EnglishValues(text);
            var result=new Dictionary<string,MobRule>(StringComparer.OrdinalIgnoreCase);MobRule rule=null;var seen=new HashSet<string>();string section="";
            foreach(var raw in text.Split('\n'))
            {
                var line=raw.Trim();if(line.Length==0||line.StartsWith("#")||line.StartsWith(";"))continue;
                if(line.StartsWith("[")&&line.EndsWith("]")){section=line.Substring(1,line.Length-2).Trim();if(section.Length==0||result.ContainsKey(section))throw new InvalidDataException("Duplicate/empty mob section: "+section);result.Add(section,rule=new MobRule());continue;}
                int split=line.IndexOf('=');if(rule==null||split<1)throw new InvalidDataException("Invalid mob setting: "+line);
                string key=line.Substring(0,split).Trim(),value=line.Substring(split+1).Trim().ToLowerInvariant();
                if(!seen.Add(section+"."+key))throw new InvalidDataException("Duplicate mob setting: "+section+"."+key);
                bool normal=value=="normal"||value=="vanilla";
                switch(key)
                {
                    case "DumbChance":
                        if(!float.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out rule.DumbChance)||float.IsNaN(rule.DumbChance)||float.IsInfinity(rule.DumbChance)||rule.DumbChance<0||rule.DumbChance>100)throw new InvalidDataException("DumbChance expects a percentage from 0 to 100");break;
                    case "Charge":rule.Charge=value=="oui"||value=="true"?true:value=="non"||value=="false"?false:throw new InvalidDataException("Charge expects true/false");break;
                    case "Aggressiveness":rule.Aggression=normal?Aggression.Normal:value=="aggressive"?Aggression.Aggressive:value=="cunning"?Aggression.Cunning:throw new InvalidDataException("Invalid aggression: "+value);break;
                    case "Intelligence":rule.Intelligence=value=="vanilla"?Intelligence.Vanilla:value=="normal"?Intelligence.Normal:value=="dumb"||value=="dÃ©bile"?Intelligence.Dumb:value=="smart"?Intelligence.Smart:throw new InvalidDataException("Invalid intelligence: "+value);break;
                    case "GroupBehavior":rule.Group=normal?GroupBehavior.Normal:value=="helper"?GroupBehavior.Helper:value=="pack"?GroupBehavior.Pack:throw new InvalidDataException("Invalid group behavior: "+value);break;
                    default:throw new InvalidDataException("Unknown mob option: "+key);
                }
            }
            return result;
        }
        internal static string Format(Dictionary<string,MobRule> rules)
        {
            var text=new StringBuilder();foreach(var entry in rules.OrderBy(e=>e.Key))text.AppendLine("["+entry.Key+"]")
                .AppendLine("Aggressiveness = "+(entry.Value.Aggression==Aggression.Normal?"normal":entry.Value.Aggression==Aggression.Aggressive?"aggressive":"cunning"))
                .AppendLine("Intelligence = "+entry.Value.Intelligence.ToString().ToLowerInvariant())
                .AppendLine("DumbChance = "+entry.Value.DumbChance.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine("GroupBehavior = "+(entry.Value.Group==GroupBehavior.Normal?"normal":entry.Value.Group==GroupBehavior.Helper?"helper":"pack"))
                .AppendLine("Charge = "+(entry.Value.Charge?"true":"false")).AppendLine();return text.ToString();
        }
        internal static void Initialize()
        {
            string path=Path.Combine(Leveling.LevelingConfig.DirectoryPath,"MobBehaviors.cfg");string defaults;
            defaults=Utility.ConfigSections.Defaults("MobBehaviors.cfg");
            if(!File.Exists(path))File.WriteAllText(path,defaults);
            string existing=File.ReadAllText(path),complete=Utility.ConfigSections.Complete(Utility.ConfigSections.EnglishValues(existing),defaults);
            var rules=Parse(complete);if(existing!=complete)File.WriteAllText(path,complete);
            Current=Local=rules;
        }
    }
}
