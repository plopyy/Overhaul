using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;

public static class OverhaulBossDataCheck
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);UnityEngine.Debug.Log("BOSS DATA PASS "+message);}
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var path=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var data=plugin.GetType("Overhaul.Dungeons.BossData",true);
        var read=data.GetMethod("Read",F);
        var path=Path.GetFullPath("../../../Packages/Overhaul/DungeonBosses.cfg");
        var values=(System.Collections.IDictionary)read.Invoke(null,new object[]{path});
        int Stars(string id)=>(int)values[id].GetType().GetField("Stars").GetValue(values[id]);
        Check(values.Count==13&&Stars("Ulv")==3&&Stars("Fenring_Cultist")==3&&Stars("StoneGolem")==1&&Stars("Abomination")==1&&Stars("BlobElite")==1&&Stars("Ghost")==2&&Stars("Troll")==3,"Packaged roster and star data");
        var surtling=values["Surtling"];Check(Stars("Surtling")==3&&(float)surtling.GetType().GetField("HealthBonusPercent").GetValue(surtling)==500&&(float)surtling.GetType().GetField("Scale").GetValue(surtling)==2&&(float)surtling.GetType().GetField("AttackSpeed").GetValue(surtling)==2,"Surtling has requested stars, health, scale and speed");
        var roster=(string[])plugin.GetType("Overhaul.Dungeons.BossEncounter").GetField("SwampRoster",F).GetValue(null);Check(Array.IndexOf(roster,"Surtling")>=0&&roster.Length==5,"Surtling included in actual swamp guardian selection roster");
        var upgradedPath=Path.GetFullPath("../../../Tools/AugaWork/surtling-migration-"+Guid.NewGuid().ToString("N")+".cfg");var old=System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path),@"(?ms)^\[Surtling\]\s*.*?(?=^\[|\z)","").Replace("HealthBonusPercent = 500","HealthBonusPercent = 321");File.WriteAllText(upgradedPath,old);var upgraded=(System.Collections.IDictionary)data.GetMethod("ReadAndMigrate",F).Invoke(null,new object[]{upgradedPath});Check(upgraded.Count==13&&(float)upgraded["Surtling"].GetType().GetField("HealthBonusPercent").GetValue(upgraded["Surtling"])==500&&(float)upgraded["Ghost"].GetType().GetField("HealthBonusPercent").GetValue(upgraded["Ghost"])==321&&File.Exists(upgradedPath+".surtling.bak"),"existing config gains Surtling without overwriting customized guardians");var once=File.ReadAllText(upgradedPath);data.GetMethod("ReadAndMigrate",F).Invoke(null,new object[]{upgradedPath});Check(once==File.ReadAllText(upgradedPath),"Surtling migration is idempotent");
        var brute=values["SeekerBrute"];Check(Stars("SeekerBrute")==3&&(float)brute.GetType().GetField("HealthBonusPercent").GetValue(brute)==100&&(float)brute.GetType().GetField("Scale").GetValue(brute)==2&&(float)brute.GetType().GetField("AttackSpeed").GetValue(brute)==2,"Mistlands guardian has 3 stars, +100% health, scale 2, attack speed 2");
        var mistRoster=(string[])plugin.GetType("Overhaul.Dungeons.BossEncounter").GetField("MistlandsRoster",F).GetValue(null);Check(mistRoster.Length==1&&mistRoster[0]=="SeekerBrute","only SeekerBrute selected for Mistlands");
        var mistPath=Path.GetFullPath("../../../Tools/AugaWork/mistlands-migration-"+Guid.NewGuid().ToString("N")+".cfg");
        string previous=System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path),@"(?ms)^\[SeekerBrute\]\s*.*?(?=^\[|\z)","").Replace("HealthBonusPercent = 500","HealthBonusPercent = 321");File.WriteAllText(mistPath,previous);
        var migrated=(System.Collections.IDictionary)data.GetMethod("ReadAndMigrate",F).Invoke(null,new object[]{mistPath});Check(migrated.Count==13&&(float)migrated["SeekerBrute"].GetType().GetField("Scale").GetValue(migrated["SeekerBrute"])==2&&(float)migrated["Ghost"].GetType().GetField("HealthBonusPercent").GetValue(migrated["Ghost"])==321&&File.ReadAllText(mistPath+".mistlands.bak")==previous,"Mistlands migration preserves custom guardians and exact backup");
        string migratedText=File.ReadAllText(mistPath);data.GetMethod("ReadAndMigrate",F).Invoke(null,new object[]{mistPath});Check(File.ReadAllText(mistPath)==migratedText,"Mistlands migration idempotent");
        var loot=plugin.GetType("Overhaul.Dungeons.BossLootData").GetMethod("Read",F);string lootText=File.ReadAllText(Path.GetDirectoryName(path)+"/DungeonBossLoot.cfg");var lootValues=(System.Collections.IDictionary)loot.Invoke(null,new object[]{lootText});Check(lootValues.Count==4&&((Array)lootValues["Mistlands"]).Length==3,"four loot tables including Mistlands");
        var oldLoot=System.Text.RegularExpressions.Regex.Replace(lootText,@"(?ms)^\[Mistlands\]\s*.*?(?=^\[|\z)","");var compatible=(System.Collections.IDictionary)loot.Invoke(null,new object[]{oldLoot});Check(compatible.Count==4&&((Array)compatible["Mistlands"]).Length==3,"old loot config receives Mistlands defaults");
        var levelRules=plugin.GetType("Overhaul.Leveling.LevelingConfig").GetMethod("Load").Invoke(null,new object[]{Path.GetDirectoryName(path)});var monsters=(System.Collections.IDictionary)levelRules.GetType().GetField("Monsters").GetValue(levelRules);foreach(var name in new[]{"Surtling","Lox"})Check((string)monsters[name].GetType().GetField("Category").GetValue(monsters[name])=="Captain",name+" classified Captain by runtime rules");
        Check((int)data.GetMethod("StarsFor",F).Invoke(null,new object[]{"Abomination(Clone)"})==1,"Runtime reads abomination data for prefab and clone names");
        string fixture=Path.GetFullPath("../../../Tools/OverhaulV2Work/legacy-boss-config.cfg");
        File.WriteAllText(fixture,"[DungeonBossStars]\nAbomination = 3\nTroll = 2\n[Other]\nKeep = 42\n");
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/OverhaulV2Work/TestGame.exe"),null,null,new string[0]});
        var config=Activator.CreateInstance(bep.GetType("BepInEx.Configuration.ConfigFile"),new object[]{fixture,false});
        plugin.GetType("Overhaul.Dungeons.BossEncounter").GetMethod("RemoveLegacyStars",F).Invoke(null,new[]{config});
        string cleaned=File.ReadAllText(fixture);
        Check(!cleaned.Contains("DungeonBossStars")&&cleaned.Contains("Keep = 42"),"Old options removed without losing unrelated settings");
        Check((int)data.GetMethod("StarsFor",F).Invoke(null,new object[]{"Abomination"})==1,"Legacy 3 stars cannot override content data");
        plugin.GetType("Overhaul.Dungeons.BossEncounter").GetMethod("RemoveLegacyStars",F).Invoke(null,new[]{config});
        Check(File.ReadAllText(fixture)==cleaned,"Legacy cleanup is idempotent");
        foreach(string invalid in new[]{"[Stars]\nAbomination = 4",File.ReadAllText(path)+"\nAbomination = 2","[Stars]\nAbomination = 1"})
        {
            File.WriteAllText(fixture,invalid);bool rejected=false;
            try{read.Invoke(null,new object[]{fixture});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"Reject invalid, duplicate or incomplete boss data");
        }
        string directory=Path.GetFullPath("../../../Tools/OverhaulV2Work/lightning-data-fixture");Directory.CreateDirectory(directory);
        foreach(string file in Directory.GetFiles(Path.GetDirectoryName(path),"*.cfg"))File.Copy(file,Path.Combine(directory,Path.GetFileName(file)),true);
        string statsPath=Path.Combine(directory,"LevelingStats.cfg"),statsText=File.ReadAllText(statsPath).Replace("\r\n","\n");
        var configType=plugin.GetType("Overhaul.Leveling.LevelingConfig");
        void VerifyLightning(double expected)
        {
            var rules=configType.GetMethod("Load").Invoke(null,new object[]{directory});
            var stats=(System.Collections.IDictionary)rules.GetType().GetField("Stats").GetValue(rules);
            var rule=stats["element_spirit"];
            Check((double)rule.GetType().GetField("PerPoint").GetValue(rule)==expected,"Lightning data preserves saved stat ID with expected gain "+expected);
        }
        Check(statsText.Contains("[element_lightning]")&&!statsText.Contains("[element_spirit]"),"Distributed file uses lightning name");
        VerifyLightning(1);
        File.WriteAllText(statsPath,statsText.Replace("[element_lightning]","[element_spirit]").Replace("[element_spirit]\nMaxRank = 50\nPerPoint = 1","[element_spirit]\nMaxRank = 50\nPerPoint = 7").Replace("[element_spirit]\r\nMaxRank = 50\r\nPerPoint = 1","[element_spirit]\r\nMaxRank = 50\r\nPerPoint = 7"));
        VerifyLightning(7);
        File.AppendAllText(statsPath,"\n[element_lightning]\nMaxRank = 40\nPerPoint = 9\n");VerifyLightning(9);
        File.WriteAllText("../../../Tools/OverhaulV2Work/boss-data-check-results.txt","PASS: packaged boss data, runtime lookup, old BepInEx options removed, other settings preserved, no legacy override, idempotency, malformed/duplicate/missing entries rejected. Lightning: new name, old customized config, new-section precedence, stable saved stat ID verified.\n");
    }
}
