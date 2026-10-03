using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.Utility
{
    internal static class ConfigSections
    {
        internal static string EnglishValues(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text,
                @"(?m)^(\s*)(Charge|LightFirstAttackMovement|HeavyFirstAttackMovement|Aggressiveness|Intelligence|GroupBehavior)(\s*=\s*)([^\r\n]+)", match=>
                {
                    string key=match.Groups[2].Value,value=match.Groups[4].Value.Trim().ToLowerInvariant(), replacement=null;
                    switch(key)
                    {
                        case "Charge":case "LightFirstAttackMovement":case "HeavyFirstAttackMovement":
                            replacement=value=="oui"?"true":value=="non"?"false":null;break;
                        case "Aggressiveness":replacement=value=="agressif"?"aggressive":value=="vicieux"?"cunning":null;break;
                        case "Intelligence":replacement=value=="debile"||value=="dÃ©bile"?"dumb":value=="intelligent"?"smart":null;break;
                        case "GroupBehavior":replacement=value=="meute"?"pack":null;break;
                    }
                    return replacement==null?match.Value:match.Groups[1].Value+key+match.Groups[3].Value+replacement;
                });
        }
        private sealed class Section
        {
            internal string Name;internal string[] Comments;
            internal readonly List<string> Settings=new List<string>();
        }
        internal static string Defaults(string file)
                {
            return Jotunn.Utils.AssetUtils.LoadTextFromResources("Overhaul.Defaults." + file, typeof(ConfigSections).Assembly)
                ?? throw new InvalidDataException("Missing embedded Overhaul defaults: " + file);
        }
        internal static string Complete(string existing,string template)
        {
            var sections=new Dictionary<string,Section>();var comments=new List<string>();Section current=null;
            foreach(var raw in template.Split('\n'))
            {
                string line=raw.TrimEnd('\r'),trim=line.Trim();
                if(trim.StartsWith("#")||trim.StartsWith(";")){comments.Add(line);continue;}
                if(trim.StartsWith("[")&&trim.EndsWith("]")){string name=trim.Substring(1,trim.Length-2);current=new Section{Name=name,Comments=comments.ToArray()};sections.Add(name,current);comments.Clear();continue;}
                if(trim.Contains("=")&&current!=null){current.Settings.Add(line);comments.Clear();}
            }
            var lines=existing.Replace("\r\n","\n").Split('\n');var output=new StringBuilder();var found=new HashSet<string>();
            for(int i=0;i<lines.Length;i++)
            {
                string trimmed=lines[i].Trim();
                if(trimmed.StartsWith("[")&&trimmed.EndsWith("]")&&sections.TryGetValue(trimmed.Substring(1,trimmed.Length-2),out var section))
                {
                    found.Add(section.Name);
                    foreach(var comment in section.Comments)if(!lines.Contains(comment))output.AppendLine(comment);
                    output.AppendLine(lines[i]);
                    int end=i+1;while(end<lines.Length&&!lines[end].Trim().StartsWith("["))end++;
                    var keys=new HashSet<string>(lines.Skip(i+1).Take(end-i-1).Where(l=>!l.TrimStart().StartsWith("#")&&!l.TrimStart().StartsWith(";")&&l.Contains("=")).Select(l=>l.Split('=')[0].Trim()));
                    foreach(var setting in section.Settings)if(!keys.Contains(setting.Split('=')[0].Trim()))output.AppendLine(setting);
                }
                else output.AppendLine(lines[i]);
            }
            foreach(var section in sections.Values.Where(s=>!found.Contains(s.Name)))
            {output.AppendLine();foreach(var comment in section.Comments)output.AppendLine(comment);output.AppendLine("["+section.Name+"]");foreach(var line in section.Settings)output.AppendLine(line);}
            return output.ToString().TrimEnd()+Environment.NewLine;
        }
    }
}
