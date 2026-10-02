using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class WeaponReworkChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> report=new List<string>();
    static object Call(Type t,string name,params object[] args)=>t.GetMethod(name,F).Invoke(null,args);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);report.Add("PASS "+label);}
    public static void Run()
    {
        try{Test();report.Add("PASS assertions="+report.Count);File.WriteAllLines("../../../Tools/AugaWork/weapon-rework-checks.txt",report);}
        catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/weapon-rework-checks.txt",report);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var config=mod.GetType("Overhaul.Utility.WeaponAttackSpeeds");var effects=mod.GetType("Overhaul.Utility.WeaponReworkEffects");
        var template=File.ReadAllText("../../../Packages/Overhaul/WeaponRework.cfg");var defaults=(Dictionary<string,float[]>)Call(config,"Parse",template);config.GetField("Current",F).SetValue(null,defaults);
        int weapons=0;
        foreach(var line in File.ReadAllLines("../../../Tools/AugaWork/native-weapons.tsv"))
        {
            var p=line.Split('\t');if(p.Length<7)continue;
            var item=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name=p[6],m_itemType=(ItemDrop.ItemData.ItemType)Enum.Parse(typeof(ItemDrop.ItemData.ItemType),p[1]),m_skillType=(Skills.SkillType)Enum.Parse(typeof(Skills.SkillType),p[2]),m_backstabBonus=float.Parse(p[5],System.Globalization.CultureInfo.InvariantCulture),m_tamedOnly=p[0]=="KnifeButcher",m_attack=new Attack{m_attackAnimation=p[3]},m_secondaryAttack=new Attack{m_attackAnimation=p[4]}}};
            var category=(string)Call(config,"Category",item);var expected=category==null?-1:defaults[category][2];Check((float)Call(config,"Backstab",item)==(expected<0?item.m_shared.m_backstabBonus:expected),"configured backstab applied to native weapon: "+p[0]);if(category==null)Check((float)Call(config,"Rate",item,false)==1&&(float)Call(config,"Rate",item,true)==1&&!(bool)Call(config,"Movement",item,false)&&!(bool)Call(config,"Movement",item,true),"excluded tool or NPC attack keeps native speed and no added lunge: "+p[0]);weapons++;
        }
        Check(weapons>100,"more than one hundred native weapon definitions checked");
        var sections=mod.GetType("Overhaul.Utility.ConfigSections");
        var old="# Personal note\n[Swords1H]\nLightAttackSpeed = 2\nHeavyAttackSpeed = 0.6\n";
        var complete=(string)Call(sections,"Complete",old,template);
        Check(complete.Contains("# Personal note")&&complete.Contains("LightAttackSpeed = 2")&&complete.Contains("HeavyAttackSpeed = 0.6")&&complete.Contains("BackstabBonus = 3"),"completion preserves custom values and adds new options");
        Check((string)Call(sections,"Complete",complete,template)==complete,"config completion is idempotent");
        foreach(var file in new[]{"WeaponRework.cfg","MobBehaviors.cfg"})
        {
            var lines=File.ReadAllLines("../../../Packages/Overhaul/"+file);int start=0;
            for(int i=0;i<lines.Length;i++)if(lines[i].StartsWith("[")){var comments=string.Join("\n",lines.Skip(start).Take(i-start));Check(comments.Contains("# FR :")&&comments.Contains("# EN :"),file+" bilingual comments before "+lines[i]);start=i+1;}
        }
        foreach(var bad in new[]{"BackstabBonus = -2","BackstabBonus = NaN","BackstabBonus = 101","LightFirstAttackMovement = maybe","HeavyFirstAttackMovement = 2"})
        {bool fail=false;try{Call(config,"Parse","[Bows]\n"+bad);}catch(TargetInvocationException e){fail=e.InnerException is InvalidDataException;}Check(fail,"reject invalid "+bad);}
        var directory=Path.GetFullPath("../../../Tools/AugaWork/weapon-rework-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var bepin=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));bepin.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(directory,"TestGame.exe"),null,null,new string[0]});
        var legacy=Activator.CreateInstance(bepin.GetType("BepInEx.Configuration.ConfigFile"),new object[]{Path.Combine(directory,"Overhaul.cfg"),true,null});
        File.WriteAllText(Path.Combine(directory,"WeaponAttackSpeeds.cfg"),old);Call(config,"Initialize",legacy,directory);
        var migrated=(Dictionary<string,float[]>)config.GetField("Current",F).GetValue(null);
        Check(!File.Exists(Path.Combine(directory,"WeaponAttackSpeeds.cfg"))&&File.Exists(Path.Combine(directory,"WeaponRework.cfg"))&&migrated["Swords1H"][0]==2&&migrated["Swords1H"][1]==.6f,"old filename migrates with customized rates");
        Check(!migrated.ContainsKey("Tankards")&&migrated["Swords1H"][0]==2,"removed tankards do not change retained weapon speeds");
        var saved=File.ReadAllText(Path.Combine(directory,"WeaponRework.cfg"));Call(config,"Initialize",legacy,directory);Check(saved==File.ReadAllText(Path.Combine(directory,"WeaponRework.cfg")),"second initialization leaves config unchanged");
        config.GetField("Current",F).SetValue(null,defaults);
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.weapon.rework.check"});
        try
        {
            foreach(var t in effects.GetNestedTypes(F).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch")))
            {var proc=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});proc.GetType().GetMethod("Patch").Invoke(proc,null);Check(true,"native Harmony patch installs: "+t.Name);}
            var host=new GameObject("inactive weapon fixtures");host.SetActive(false);var go=new GameObject("player");go.transform.SetParent(host.transform);var player=go.AddComponent<Player>();Player.m_localPlayer=player;player.m_animator=go.AddComponent<Animator>();
            var other=new GameObject("mob");other.transform.SetParent(host.transform);var mob=other.AddComponent<Humanoid>();
            var weapon=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_sword_bronze",m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon,m_skillType=Skills.SkillType.Swords,m_backstabBonus=3,m_attack=new Attack{m_attackAnimation="swing"},m_secondaryAttack=new Attack{m_attackAnimation="swing"}}};
            var attack=new Attack{m_character=player,m_weapon=weapon,m_attackType=Attack.AttackType.Horizontal,m_attackAnimation="swing"};
            defaults["Swords1H"][2]=7;
            Check((float)Call(effects,"HitBonus",weapon.m_shared,attack)==7&&(float)Call(effects,"TooltipBonus",weapon.m_shared)==7,"configured player hit and tooltip use the same backstab multiplier");
            attack.m_character=mob;Check((float)Call(effects,"HitBonus",weapon.m_shared,attack)==3&&weapon.m_shared.m_backstabBonus==3,"creature attacks and shared vanilla data remain intact");attack.m_character=player;
            var context=effects.GetNestedType("StartContext",F);var combat=mod.GetType("Overhaul.DynamicCombat");var patch=combat.GetNestedType("AttackLungeStartPatch",F);var active=combat.GetField("lungingAttack",F);
            void Lunge(bool secondary,bool enabled,int combo)
            {
                defaults["Swords1H"][3]=secondary?1:(enabled?1:0);defaults["Swords1H"][4]=secondary?(enabled?1:0):1;attack.m_currentAttackCainLevel=combo;active.SetValue(null,null);
                var args=new object[]{player,secondary,null};context.GetMethod("Prefix",F).Invoke(null,args);
                try{Check((bool)Call(effects,"IsSecondary",attack,weapon)==secondary,"same-animation attack context identifies "+(secondary?"heavy":"light"));patch.GetMethod("Postfix",F).Invoke(null,new object[]{attack,true});Check((active.GetValue(null)==attack)==(enabled&&combo==0),"lunge secondary="+secondary+" enabled="+enabled+" combo="+combo);}
                finally{context.GetMethod("Finalizer",F).Invoke(null,new[]{args[2]});}
            }
            Lunge(false,false,0);Lunge(false,true,0);Lunge(true,false,0);Lunge(true,true,0);Lunge(false,true,1);Lunge(true,true,1);
        }
        finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
        report.Add("PACKAGE "+mod.GetName().Version);
    }
}
