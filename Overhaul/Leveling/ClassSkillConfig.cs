using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AugaUnity;
using UnityEngine;

namespace Overhaul.Leveling
{
    internal static class ClassSkillConfig
    {
        internal static ClassSkillDefinition[] Current;
        static string previous, rejected;
        static string PathName=>Path.Combine(LevelingConfig.DirectoryPath,"ClassSkill.cfg");
        internal static void Initialize()
        {
            if(!File.Exists(PathName))File.WriteAllText(PathName,Defaults());
            Reload();
        }
        static string Defaults() => Utility.ConfigSections.Defaults("ClassSkill.cfg");
        internal static bool Reload()
        {
            try {
                string text=File.ReadAllText(PathName);
                if(text==previous)return false;
                var parsed=Parse(text);Current=parsed;previous=text;rejected=null;return true;
            }catch(Exception e){
                if(rejected!=e.Message){Debug.LogWarning("[Overhaul] ClassSkill.cfg: "+e.Message+". Keeping last valid class tree.");rejected=e.Message;}
                if(Current==null)Current=Parse(Defaults());return false;
            }
        }
        internal static ClassSkillDefinition[] Parse(string text)
        {
            var sections=new List<KeyValuePair<string,Dictionary<string,string>>>();Dictionary<string,string> values=null;
            int lineNumber=0;
            foreach(var raw in text.Split('\n')){
                lineNumber++;var line=raw.Trim().TrimStart('\uFEFF');if(line.Length==0||line.StartsWith("#")||line.StartsWith(";"))continue;
                if(line.StartsWith("[")&&line.EndsWith("]")){
                    string id=line.Substring(1,line.Length-2).Trim();
                    if(!Regex.IsMatch(id,@"^[A-Za-z][A-Za-z0-9_]*\.[A-Za-z][A-Za-z0-9_]*$")||sections.Any(s=>s.Key==id))throw new InvalidDataException("Invalid/duplicate section "+id);
                    values=new Dictionary<string,string>();sections.Add(new KeyValuePair<string,Dictionary<string,string>>(id,values));continue;
                }
                int eq=line.IndexOf('=');if(values==null||eq<1)throw new InvalidDataException("Invalid line "+lineNumber);
                string key=line.Substring(0,eq).Trim();if(values.ContainsKey(key))throw new InvalidDataException("Duplicate setting "+key+" at line "+lineNumber);
                values.Add(key,line.Substring(eq+1).Trim());
            }
            var result=new List<ClassSkillDefinition>();
            var allowed=new[]{"Name","Description","Tier","MaxRank","RequiredLevel","RequiredSkill"};
            foreach(var section in sections){
                var v=section.Value;string id=section.Key;
                if(v.Keys.Any(k=>!allowed.Contains(k))||allowed.Any(k=>!v.ContainsKey(k)))throw new InvalidDataException(id+": expected Name, Description, Tier, MaxRank, RequiredLevel, RequiredSkill");
                int Number(string key,int min,int max){if(!int.TryParse(v[key],out int n)||n<min||n>max)throw new InvalidDataException(id+": invalid "+key);return n;}
                string[] List(string key){var value=v[key];if(!value.StartsWith("[")||!value.EndsWith("]"))throw new InvalidDataException(id+": "+key+" must use [values]");value=value.Substring(1,value.Length-2).Trim();return value.Length==0?new string[0]:value.Split(',').Select(x=>x.Trim()).ToArray();}
                var skill=new ClassSkillDefinition{ClassId=id.Split('.')[0],Id=id.Split('.')[1],Name=v["Name"].TrimStart('$'),Description=v["Description"].TrimStart('$'),Tier=Number("Tier",1,50),MaxRank=Number("MaxRank",1,100),RequiredSkill=List("RequiredSkill")};
                if(!Regex.IsMatch(skill.Name,@"^[A-Za-z0-9_]+$")||!Regex.IsMatch(skill.Description,@"^[A-Za-z0-9_]+$"))throw new InvalidDataException(id+": invalid translation key");
                skill.RequiredLevel=List("RequiredLevel").Select(x=>{if(!int.TryParse(x,out int n)||n<1||n>10000)throw new InvalidDataException(id+": invalid required level");return n;}).ToArray();
                if(skill.RequiredLevel.Length!=skill.MaxRank||!skill.RequiredLevel.SequenceEqual(skill.RequiredLevel.OrderBy(x=>x)))throw new InvalidDataException(id+": RequiredLevel must have MaxRank ascending values");
                if(skill.RequiredSkill.Distinct().Count()!=skill.RequiredSkill.Length)throw new InvalidDataException(id+": duplicate prerequisite");
                result.Add(skill);
            }
            if(result.Count==0||result.Count>512)throw new InvalidDataException("Expected 1 to 512 skills");
            foreach(var skill in result)foreach(var parent in skill.RequiredSkill){var found=result.FirstOrDefault(s=>s.ClassId==skill.ClassId&&s.Id==parent);if(found==null||found.Tier>=skill.Tier)throw new InvalidDataException(skill.ClassId+"."+skill.Id+": prerequisite "+parent+" missing or not in an earlier tier");}
            if(!result.Any(s=>s.ClassId=="Guardian"))throw new InvalidDataException("The current preview requires class Guardian");
            return result.ToArray();
        }
    }
}
