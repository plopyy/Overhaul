using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;
public static class DistributionDefaultsChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var type=mod.GetType("Overhaul.Utility.WeaponAttackSpeeds");
        Dictionary<string,float[]> Parse(string text)=>(Dictionary<string,float[]>)type.GetMethod("Parse",F).Invoke(null,new object[]{text});
        var server=Parse(File.ReadAllText("../../../Tools/AugaWork/WeaponRework-server-defaults.cfg"));
        void Equal(Dictionary<string,float[]> values,string label){if(values.Count!=server.Count||server.Any(p=>!values[p.Key].SequenceEqual(p.Value)))throw new Exception(label);}
        Equal(Parse(File.ReadAllText("../../../Packages/Overhaul/WeaponRework.cfg")),"Package differs from server");
        Equal(Parse(""),"Internal defaults differ from server");
        var dir=Path.GetFullPath("../../../Tools/AugaWork/imported-defaults-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var bepin=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));bepin.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(dir,"TestGame.exe"),null,null,new string[0]});
        var legacy=Activator.CreateInstance(bepin.GetType("BepInEx.Configuration.ConfigFile"),new object[]{Path.Combine(dir,"Overhaul.cfg"),true,null});
        type.GetMethod("Initialize",F).Invoke(null,new object[]{legacy,dir});
        Equal(Parse(File.ReadAllText(Path.Combine(dir,"WeaponRework.cfg"))),"Fresh install differs from server");
        var customized=File.ReadAllText(Path.Combine(dir,"WeaponRework.cfg")).Replace("BackstabBonus = 6","BackstabBonus = 9");customized+="\n# Removed utility\n[Tankards]\nLightAttackSpeed = 7\nHeavyAttackSpeed = 8\n\n# Keep custom sword settings\n";File.WriteAllText(Path.Combine(dir,"WeaponRework.cfg"),customized);type.GetMethod("Initialize",F).Invoke(null,new object[]{legacy,dir});
        if(Parse(File.ReadAllText(Path.Combine(dir,"WeaponRework.cfg")))["Bows"][2]!=9)throw new Exception("Existing custom value overwritten");
        var cleaned=File.ReadAllText(Path.Combine(dir,"WeaponRework.cfg"));if(cleaned.Contains("[Tankards]")||!cleaned.Contains("# Keep custom sword settings")||!File.Exists(Path.Combine(dir,"WeaponRework.cfg.weapons-only.bak")))throw new Exception("Retired section migration or backup failed");
        File.WriteAllText(Path.Combine(dir,"WeaponRework.cfg"),File.ReadAllText("../../../Tools/AugaWork/WeaponRework-server-defaults.cfg"));type.GetMethod("Initialize",F).Invoke(null,new object[]{legacy,dir});var fullMigration=File.ReadAllText(Path.Combine(dir,"WeaponRework.cfg"));Equal(Parse(fullMigration),"Full previous config lost retained values");if(System.Text.RegularExpressions.Regex.Matches(fullMigration,@"(?m)^\[").Count!=21)throw new Exception("Retired sections survived full migration");
        var mob=mod.GetType("Overhaul.AI.MobBehaviorConfig");var mobs=(System.Collections.IDictionary)mob.GetMethod("Parse",F).Invoke(null,new object[]{File.ReadAllText("../../../Packages/Overhaul/MobBehaviors.cfg")});
        if(mobs.Count!=163||mobs.Keys.Cast<string>().Any(k=>(float)mobs[k].GetType().GetField("DumbChance",F).GetValue(mobs[k])!=(k=="Deathsquito"?0f:k=="Greyling"?15f:7f)))throw new Exception("Current per-species mob defaults mismatch");
        File.WriteAllText("../../../Tools/AugaWork/distribution-defaults-checks.txt","PASS 105 retained server weapon settings match package, internal defaults and fresh installation; custom edits preserved; retired utility sections removed with backup; 163 mob sections with current per-species chances (0/15/7 percent). Package "+mod.GetName().Version);
    }
}
