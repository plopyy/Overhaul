using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class Hotfix1016Check
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,a) => { var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll"); return File.Exists(p)?Assembly.LoadFrom(p):null; };
        var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/Hotfix1016/TestGame.exe"),null,null,new string[0]});
        var log=Activator.CreateInstance(bep.GetType("BepInEx.Logging.ManualLogSource"),new object[]{"Hotfix1016"});
        plugin.GetType("Overhaul.Utility.Log").GetMethod("Init",F).Invoke(null,new[]{log});
        var config=Activator.CreateInstance(bep.GetType("BepInEx.Configuration.ConfigFile"),new object[]{Path.GetFullPath("../../../Tools/Hotfix1016/check.cfg"),false});
        plugin.GetType("Overhaul.Utility.OverhaulConfig").GetMethod("Bind",F).Invoke(null,new[]{config});
        plugin.GetType("Overhaul.Leveling.LevelingConfig").GetMethod("Initialize",F).Invoke(null,null);
        var harmonyType=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");
        var harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.hotfix1016.check"});
        var report=new List<string>();int failures=0,patched=0;
        try {
            foreach(var type in plugin.GetTypes().Where(t=>t.Namespace!=null && (t.Namespace=="Overhaul"||t.Namespace.StartsWith("Overhaul.")))) {
                try {
                    var processor=harmonyType.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type});
                    var result=(System.Collections.IEnumerable)processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);
                    if(result!=null)foreach(var method in result){patched++;report.Add("PATCH OK "+type.FullName+" -> "+method);}
                } catch(Exception e) {failures++;report.Add("PATCH FAIL "+type.FullName+" "+e);}
            }
        } finally { harmonyType.GetMethod("UnpatchSelf").Invoke(harmony,null); }
        report.Insert(0,"Packaged "+plugin.GetName().Version+"; game assembly "+typeof(Player).Assembly.ManifestModule.ModuleVersionId+"; patched methods "+patched+"; failures "+failures);
        File.WriteAllLines("../../../Tools/Hotfix1016/runtime-patches.txt",report);
        if(failures!=0)throw new Exception("Patch compatibility failures: "+failures);
        TarDrainCheck.Run();
        File.AppendAllText("../../../Tools/Hotfix1016/runtime-patches.txt","\nTarDrainCheck passed against updated game assemblies.\n");
    }
}
