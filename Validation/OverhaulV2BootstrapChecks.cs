using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class OverhaulV2BootstrapChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static void Check(bool value,string message){if(!value)throw new Exception(message);Debug.Log("V2 BOOTSTRAP PASS "+message);}
    public static void Run(Assembly plugin)
    {
        var plugins=plugin.GetTypes().Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="BepInPlugin")).ToArray();
        Check(plugins.Length==1 && plugins[0].FullName=="Overhaul.Overhaul","Single Overhaul plugin entry point");
        Check(!plugin.GetManifestResourceNames().Contains("Overhaul.Assets.overhaul_leveling"),"Vanilla leveling bundle removed");
        Check(!plugin.GetReferencedAssemblies().Any(a=>a.Name=="Auga"),"No external Auga assembly dependency");
        var ui=plugin.GetType("Overhaul.IntegratedUi",true);
        Check(plugin.GetType("Auga.Auga").GetProperty("instance").GetValue(null)==null,"Gameplay checks ran without starting UI (server path)");
        var host=new GameObject("Overhaul V2 UI bootstrap check");host.SetActive(false);
        var bep=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="BepInEx");
        var logger=Activator.CreateInstance(bep.GetType("BepInEx.Logging.ManualLogSource"),new object[]{"OverhaulV2Check"});
        try
        {
            ui.GetMethod("Start",F).Invoke(null,new[]{logger,(object)host});
            Check(plugin.GetType("Auga.Auga").GetProperty("instance").GetValue(null)!=null,"Integrated UI bootstrap completes with embedded assets");
            var assets=plugin.GetType("Auga.Auga").GetField("Assets").GetValue(null);
            var inventory=(GameObject)assets.GetType().GetField("InventoryScreen").GetValue(assets);
            Check(inventory && inventory.GetComponentInChildren<AugaUnity.AugaTabController>(true),"Embedded inventory has resolved serialized components");
            var modules=plugin.GetType("Auga.UiModules");var modType=plugin.GetType("Auga.UiModule");
            foreach(string name in new[]{"QuickInventory","RightPanel"})Check((bool)modules.GetMethod("Enabled").Invoke(null,new[]{Enum.Parse(modType,name)}),"Required integrated module enabled: "+name);
            var harmony=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="0Harmony").GetType("HarmonyLib.Harmony");
            var method=typeof(InventoryGui).GetMethod("OnOpenSkills",F);
            object patches=harmony.GetMethod("GetPatchInfo").Invoke(null,new object[]{method});
            var owners=(System.Collections.IEnumerable)patches.GetType().GetProperty("Owners").GetValue(patches);
            Check(owners.Cast<string>().Contains("plopyy.valheim.Overhaul.UI"),"Skills button patched by integrated UI");
            File.WriteAllText("../../../Tools/OverhaulV2Work/bootstrap-check-results.txt","PASS: single plugin, embedded dependencies/prefabs, dedicated gameplay without UI, integrated client startup, skills patch, mandatory modules.\n");
        }
        finally {ui.GetMethod("Stop",F).Invoke(null,null);UnityEngine.Object.DestroyImmediate(host);}
    }
}
