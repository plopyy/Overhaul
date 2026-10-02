using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;

namespace Overhaul.Utility
{
    internal static class WeaponAttackSpeeds
    {
        internal static readonly string[] Categories={"Swords1H","Axes1H","Clubs1H","Knives","Spears","Swords2H","Axes2H","Clubs2H","Polearms","Pickaxes","Bows","Staffs","Claws","Unarmed","DualKnives","DualAxes","Crossbows","Throwables","Torches","ButcherKnives","Harpoons"};
        private static readonly HashSet<string> Retired=new HashSet<string>{"Scythes","GrapplingHooks","FishingRods","Shovels","OtherWeapons","Lanterns","Tankards"};
        internal static Dictionary<string,float[]> Current=Defaults(), Local;
        private static string RemoveRetired(string text)
        {
            var lines=new List<string>(text.Replace("\r\n","\n").Split('\n'));
            bool removed=false;
            for(int i=lines.Count-1;i>=0;i--)
            {
                string line=lines[i].Trim();
                if(!line.StartsWith("[")||!line.EndsWith("]")||!Retired.Contains(line.Substring(1,line.Length-2)))continue;
                int first=i,last=i+1;
                while(first>0&&IsComment(lines[first-1]))first--;
                while(last<lines.Count&&!lines[last].TrimStart().StartsWith("["))last++;
                while(last>i+1&&IsComment(lines[last-1]))last--;
                lines.RemoveRange(first,last-first);removed=true;i=first;
            }
            return removed?string.Join("\n",lines):text;
        }
        private static bool IsComment(string line)=>string.IsNullOrWhiteSpace(line)||line.TrimStart().StartsWith("#")||line.TrimStart().StartsWith(";");
        private static Dictionary<string,float[]> Defaults()
        {
            var result=new Dictionary<string,float[]>();
            foreach(var name in Categories)
            {
                float rate=Array.IndexOf(Categories,name)>=12?1:name=="Swords2H"?1:name=="Bows"||name=="Staffs"?1.2f:1.5f; if(name=="DualKnives"||name=="DualAxes"||name=="Torches")rate=1.5f;
                if(name=="ButcherKnives"||name=="Harpoons"||name=="Lanterns"||name=="Tankards")rate=1.5f;
                float backstab=name=="Knives"||name=="DualKnives"||name=="Claws"?6:name=="Clubs2H"?2:name=="Unarmed"||name=="Tankards"?4:name=="Lanterns"?0:name=="OtherWeapons"?-1:name=="Staffs"||name=="FishingRods"||name=="GrapplingHooks"||name=="Scythes"||name=="Shovels"||name=="ButcherKnives"||name=="Harpoons"?1:3;
                bool move=name=="Swords1H"||name=="Swords2H"||name=="Axes1H"||name=="Axes2H"||name=="DualAxes"||name=="Clubs1H"||name=="Clubs2H"||name=="Knives"||name=="DualKnives"||name=="Polearms"||name=="Spears"||name=="Torches"||name=="ButcherKnives"||name=="Scythes"||name=="Shovels";
                result[name]=new[]{rate,name=="Swords1H"||name=="Tankards"?rate*.5f:rate,backstab,move?1f:0f,move&&name!="Axes1H"&&name!="Axes2H"&&name!="DualAxes"?1f:0f};
            }
            return ParseValues(ConfigSections.Defaults("WeaponRework.cfg"),result);
        }
        internal static void Initialize(ConfigFile legacy,string directory=null)
        {
            string path=Path.Combine(directory??Leveling.LevelingConfig.DirectoryPath,"WeaponRework.cfg");
            string oldPath=Path.Combine(directory??Leveling.LevelingConfig.DirectoryPath,"WeaponAttackSpeeds.cfg");
            if(!File.Exists(path)&&File.Exists(oldPath))File.Move(oldPath,path);
            var migrated=Defaults();var changedLegacy=new HashSet<string>();bool save=legacy.SaveOnConfigSet;
            legacy.SaveOnConfigSet=false;
            try
            {
                foreach(var name in Categories)
                {
                    var entry=legacy.Bind("AttackSpeed",name+"AttackSpeed",migrated[name][0]-1);
                    float rate=Math.Max(.1f,Math.Min(10,1+entry.Value));
                    if(float.IsNaN(rate)||float.IsInfinity(rate))rate=migrated[name][0];
                    if(rate!=migrated[name][0]){changedLegacy.Add(name);migrated[name][0]=rate;migrated[name][1]=name=="Swords1H"?Math.Max(.1f,rate*.5f):rate;}
                    legacy.Remove(entry.Definition);
                }
                // Write first: never remove the previous settings if creating the new file fails.
                foreach(var pair in new[]{new[]{"DualKnives","Knives"},new[]{"DualAxes","Axes2H"},new[]{"Torches","Clubs1H"},new[]{"ButcherKnives","Knives"},new[]{"Harpoons","Spears"}})if(changedLegacy.Contains(pair[1])&&!changedLegacy.Contains(pair[0])){migrated[pair[0]][0]=migrated[pair[1]][0];migrated[pair[0]][1]=migrated[pair[1]][1];}
                if(!File.Exists(path))File.WriteAllText(path,Format(migrated));
                string original=File.ReadAllText(path),existing=RemoveRetired(original);
                if(existing!=original&&!File.Exists(path+".weapons-only.bak"))File.WriteAllText(path+".weapons-only.bak",original);
                var loaded=Parse(existing);
                string complete=ConfigSections.Complete(ConfigSections.EnglishValues(existing),ConfigSections.Complete(Format(loaded),ConfigSections.Defaults("WeaponRework.cfg")));
                if(complete!=original)File.WriteAllText(path,complete);
                if(File.Exists(oldPath)&&!File.Exists(oldPath+".migrated.bak"))File.Move(oldPath,oldPath+".migrated.bak");
                Current=Local=loaded;
                legacy.Save();
            }
            finally{legacy.SaveOnConfigSet=save;}
        }
        internal static string Format(Dictionary<string,float[]> rates)
        {
            var text=new StringBuilder("# Attack animation multipliers: 1 = vanilla, 1.5 = +50%, 0.75 = -25%.\n# LightAttackSpeed = primary attack; HeavyAttackSpeed = secondary attack.\n# Server values apply to clients on connection. Restart after editing.\n\n");
            foreach(var name in Categories)text.AppendLine("["+name+"]").AppendLine("LightAttackSpeed = "+rates[name][0].ToString(CultureInfo.InvariantCulture)).AppendLine("HeavyAttackSpeed = "+rates[name][1].ToString(CultureInfo.InvariantCulture)).AppendLine("BackstabBonus = "+(rates[name][2]<0?"vanilla":rates[name][2].ToString(CultureInfo.InvariantCulture))).AppendLine("LightFirstAttackMovement = "+(rates[name][3]>0?"true":"false")).AppendLine("HeavyFirstAttackMovement = "+(rates[name][4]>0?"true":"false")).AppendLine();
            return text.ToString();
        }
        internal static Dictionary<string,float[]> Parse(string text)=>ParseValues(text,Defaults());
        private static Dictionary<string,float[]> ParseValues(string text,Dictionary<string,float[]> result)
        {
            string section="";var seen=new HashSet<string>();var heavy=new HashSet<string>();
            foreach(string raw in text.Split('\n'))
            {
                string line=raw.Trim();if(line.Length==0||line.StartsWith("#")||line.StartsWith(";"))continue;
                if(line.StartsWith("[")&&line.EndsWith("]")){section=line.Substring(1,line.Length-2);if(!result.ContainsKey(section)&&!Retired.Contains(section))throw new InvalidDataException("Unknown weapon category: "+section);continue;}
                if(Retired.Contains(section))continue;
                int equals=line.IndexOf('=');if(equals<1)throw new InvalidDataException("Invalid weapon speed: "+line);
                string key=line.Substring(0,equals).Trim(),value=line.Substring(equals+1).Trim();int index=key=="LightAttackSpeed"?0:key=="HeavyAttackSpeed"?1:key=="BackstabBonus"?2:key=="LightFirstAttackMovement"?3:key=="HeavyFirstAttackMovement"?4:-1;
                if(!result.ContainsKey(section)||index<0||!seen.Add(section+"."+key))throw new InvalidDataException("Invalid/duplicate weapon option: "+section+"."+key);
                float number;
                if(index>=3){if(value.Equals("oui",StringComparison.OrdinalIgnoreCase)||value.Equals("true",StringComparison.OrdinalIgnoreCase))number=1;else if(value.Equals("non",StringComparison.OrdinalIgnoreCase)||value.Equals("false",StringComparison.OrdinalIgnoreCase))number=0;else throw new InvalidDataException("Expected true/false: "+section+"."+key);}
                else if(index==2&&value.Equals("vanilla",StringComparison.OrdinalIgnoreCase))number=-1;
                else if(!float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out number)||float.IsNaN(number)||float.IsInfinity(number)||number<(index==2?0:.1f)||number>(index==2?100:10))throw new InvalidDataException("Invalid weapon value: "+section+"."+line);
                result[section][index]=number;if(index==1)heavy.Add(section);
            }            foreach(var name in Categories)if(!heavy.Contains(name)&&seen.Contains(name+".LightAttackSpeed"))result[name][1]=name=="Swords1H"?Math.Max(.1f,result[name][0]*.5f):result[name][0];
            foreach(var pair in new[]{new[]{"DualKnives","Knives"},new[]{"DualAxes","Axes2H"},new[]{"Torches","Clubs1H"},new[]{"ButcherKnives","Knives"},new[]{"Harpoons","Spears"}})
                if(!seen.Contains(pair[0]+".LightAttackSpeed")&&!seen.Contains(pair[0]+".HeavyAttackSpeed")&&(seen.Contains(pair[1]+".LightAttackSpeed")||seen.Contains(pair[1]+".HeavyAttackSpeed"))){result[pair[0]][0]=result[pair[1]][0];result[pair[0]][1]=result[pair[1]][1];}
            return result;
        }
        internal static string Category(ItemDrop.ItemData weapon)
        {
            if(weapon==null)return "Unarmed";
            bool two=weapon.m_shared.m_itemType==ItemDrop.ItemData.ItemType.TwoHandedWeapon||weapon.m_shared.m_itemType==ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            string name;
            string animation=weapon.m_shared.m_attack?.m_attackAnimation??"";
            if(weapon.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Torch)return weapon.m_shared.m_name.StartsWith("$item_lantern")||weapon.m_shared.m_name=="$piece_hoodedlantern"?null:"Torches";
            if(animation=="dualaxes")return "DualAxes";
            if(weapon.m_shared.m_skillType==Skills.SkillType.None)return animation=="throw_bomb"?"Throwables":null;
            switch(weapon.m_shared.m_skillType)
            {
                case Skills.SkillType.Swords:name=weapon.m_shared.m_name.StartsWith("$item_tankard")||weapon.m_shared.m_name=="$item_dvergrtankard"?null:animation=="attack"?null:two?"Swords2H":"Swords1H";break;
                case Skills.SkillType.Axes:name=two?"Axes2H":"Axes1H";break;
                case Skills.SkillType.Clubs:name=two?"Clubs2H":"Clubs1H";break;
                case Skills.SkillType.Knives:name=two?"DualKnives":weapon.m_shared.m_tamedOnly?"ButcherKnives":"Knives";break;
                case Skills.SkillType.Unarmed:name=two?"Claws":"Unarmed";break;
                case Skills.SkillType.Crossbows:name="Crossbows";break;
                case Skills.SkillType.Farming:return null;
                case Skills.SkillType.Spears:name=animation=="spear_throw"?"Harpoons":"Spears";break;
                case Skills.SkillType.Polearms:name="Polearms";break;
                case Skills.SkillType.Pickaxes:name="Pickaxes";break;
                case Skills.SkillType.Bows:name="Bows";break;
                case Skills.SkillType.BloodMagic:case Skills.SkillType.ElementalMagic:name="Staffs";break;
                default:return null;
            }
            return name;
        }
        internal static float Rate(ItemDrop.ItemData weapon,bool secondary)=>Category(weapon) is string name?Current[name][secondary?1:0]:1f;
        internal static float Backstab(ItemDrop.ItemData weapon){float value=Category(weapon) is string name?Current[name][2]:-1;return value<0?weapon?.m_shared.m_backstabBonus??4:value;}
        internal static bool Movement(ItemDrop.ItemData weapon,bool secondary)=>Category(weapon) is string name&&Current[name][secondary?4:3]>0;    }
}
