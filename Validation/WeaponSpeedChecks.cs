using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

using UnityEngine;

public static class WeaponSpeedChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type config;static readonly List<string> lines=new List<string>();
    static object Call(string name,params object[] args)=>config.GetMethod(name,F).Invoke(null,args);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);lines.Add("PASS "+label);}
    public static void Run()
    {
        try{Test();lines.Add("PASS total="+lines.Count);File.WriteAllLines("../../../Tools/AugaWork/weapon-speed-checks.txt",lines);}
        catch(Exception e){lines.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/weapon-speed-checks.txt",lines);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));config=mod.GetType("Overhaul.Utility.WeaponAttackSpeeds");
        var text=File.ReadAllText("../../../Packages/Overhaul/WeaponRework.cfg");var rates=(Dictionary<string,float[]>)Call("Parse",text);
        Check(rates.Count==21,"all twenty-one weapon categories present");
        Check(rates["Swords1H"][0]==1.5f&&rates["Swords1H"][1]==1f,"one hand sword rates match server defaults");
        foreach(var pair in rates.Where(p=>p.Key!="Swords1H"&&p.Key!="Tankards"&&p.Key!="Unarmed"))Check(pair.Value[0]==pair.Value[1],pair.Key+" heavy defaults to light rate");
        foreach(var invalid in new[]{"0","-1","NaN","Infinity","11","0,5"}){
            bool rejected=false;try{Call("Parse","[Swords1H]\nLightAttackSpeed = "+invalid);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"invalid multiplier rejected: "+invalid);
        }
        var roundTrip=(Dictionary<string,float[]>)Call("Parse",Call("Format",rates));Check(roundTrip.All(p=>p.Value.SequenceEqual(rates[p.Key])),"server format roundtrip preserves all settings");
        var previous=(Dictionary<string,float[]>)Call("Parse","[Knives]\nLightAttackSpeed = 2\nHeavyAttackSpeed = 3\n[Axes2H]\nLightAttackSpeed = 1.7\n");
        Check(previous["DualKnives"].Take(2).SequenceEqual(new[]{2f,3f})&&previous["DualAxes"][0]==1.7f,"new dual categories inherit personalized previous categories when absent");
        var directory=Path.GetFullPath("../../../Tools/AugaWork/weapon-speed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll")).GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(directory,"TestGame.exe"),null,null,new string[0]});var legacyPath=Path.Combine(directory,"Overhaul.cfg");File.WriteAllText(legacyPath,"[AttackSpeed]\nSwords1HAttackSpeed = 0.8\nBowsAttackSpeed = 0.4\n[Other]\nKeep = 123\n");
        var configType=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll")).GetType("BepInEx.Configuration.ConfigFile");object Legacy()=>Activator.CreateInstance(configType,new object[]{legacyPath,true,null});var legacy=Legacy();Call("Initialize",legacy,directory);
        var migrated=(Dictionary<string,float[]>)config.GetField("Current",F).GetValue(null);
        Check(Mathf.Approximately(migrated["Swords1H"][0],1.8f)&&Mathf.Approximately(migrated["Swords1H"][1],.9f)&&Mathf.Approximately(migrated["Bows"][1],1.4f),"old additive personalized values migrate to light and heavy multipliers");
        Check(!File.ReadAllText(legacyPath).Contains("AttackSpeed")&&File.ReadAllText(legacyPath).Contains("Keep = 123"),"old entries removed, unrelated settings preserved");
        File.WriteAllText(Path.Combine(directory,"WeaponRework.cfg"),"[Swords1H]\nLightAttackSpeed = 2\nHeavyAttackSpeed = 0.6\n");Call("Initialize",Legacy(),directory);
        Check(((Dictionary<string,float[]>)config.GetField("Current",F).GetValue(null))["Swords1H"][1]==.6f,"existing dedicated file has priority on restart");
        config.GetField("Current",F).SetValue(null,rates);
        var examples=new Dictionary<string,string>{{"FistFenrirClaw","Claws"},{"FistBjornClaw","Claws"},{"KnifeSkollAndHati","DualKnives"},{"AxeBerzerkr","DualAxes"},{"CrossbowArbalest","Crossbows"},{"BombOoze","Throwables"},{"Torch","Torches"}};
        var distinct=rates.ToDictionary(p=>p.Key,p=>new[]{1f+Array.IndexOf(rates.Keys.ToArray(),p.Key)*.1f,4f+Array.IndexOf(rates.Keys.ToArray(),p.Key)*.1f});config.GetField("Current",F).SetValue(null,distinct);
        foreach(var line in File.ReadAllLines("../../../Tools/AugaWork/native-weapons.tsv")){
            var parts=line.Split('\t');if(!examples.TryGetValue(parts[0],out var category))continue;
            var item=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_itemType=(ItemDrop.ItemData.ItemType)Enum.Parse(typeof(ItemDrop.ItemData.ItemType),parts[1]),m_skillType=(Skills.SkillType)Enum.Parse(typeof(Skills.SkillType),parts[2])}};item.m_shared.m_attack=new Attack{m_attackAnimation=parts[3]};
            Check((float)Call("Rate",item,false)==distinct[category][0]&&(float)Call("Rate",item,true)==distinct[category][1],"native weapon classification and independent rates: "+parts[0]+" -> "+category);
        }
        config.GetField("Current",F).SetValue(null,rates);
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.weapon.speed.check"});
        var patchType=mod.GetType("Overhaul.Patches.CharacterAnimEventPatches");var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{patchType});processor.GetType().GetMethod("Patch").Invoke(processor,null);
        foreach(var nested in patchType.GetNestedTypes(F).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var proc=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{nested});proc.GetType().GetMethod("Patch").Invoke(proc,null);}
        var host=new GameObject("inactive speed fixtures");host.SetActive(false);var net=host.AddComponent<ZNet>();ZNet.m_instance=net;typeof(Game).GetProperty("instance",F).SetValue(null,host.AddComponent<Game>());new ZRoutedRpc(true);var manager=new ZDOMan(512);
        var go=new GameObject("player");go.transform.SetParent(host.transform);var player=go.AddComponent<Player>();Player.m_localPlayer=player;
        player.m_cachedFrame=MonoUpdaters.UpdateCount;player.m_cachedAttack=true;player.m_cachedAnimHashFrame=MonoUpdaters.UpdateCount;player.m_cachedCurrentAnimHash=Humanoid.s_animatorTagAttack;player.m_cachedNextAnimHash=0;
        player.m_animator=go.AddComponent<Animator>();var events=go.AddComponent<CharacterAnimEvent>();events.m_character=player;events.m_animator=player.m_animator;events.m_nview=go.AddComponent<ZNetView>();events.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,0);events.m_nview.m_zdo.SetOwner(ZNet.GetUID());player.m_nview=events.m_nview;
        var weapon=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon,m_skillType=Skills.SkillType.Swords}};player.m_rightItem=weapon;weapon.m_shared.m_attack=new Attack();
        try{
            player.m_currentAttackIsSecondary=false;events.Speed(1);Check(player.m_animator.speed==1.5f,"native Speed event selects light multiplier");
            player.m_currentAttackIsSecondary=true;events.Speed(1);Check(player.m_animator.speed==1f,"native Speed event selects heavy multiplier");
            events.Speed(2);Check(player.m_animator.speed==2f,"native clip tempo multiplied, not added");
            for(int i=0;i<10;i++)events.Speed(2);Check(player.m_animator.speed==2f,"repeated events do not compound");
            events.Speed(1);for(int i=0;i<10;i++)events.CustomFixedUpdate(.02f);Check(player.m_animator.speed==1f,"native ticks never compound the event multiplier");
            events.FreezeFrame(.1f);Check(player.m_animator.speed==.0001f,"native hit freeze preserved");events.CustomFixedUpdate(.11f);Check(player.m_animator.speed==1f,"freeze resumes the configured speed once");
            weapon.m_shared.m_itemType=ItemDrop.ItemData.ItemType.TwoHandedWeapon;weapon.m_shared.m_skillType=Skills.SkillType.Axes;weapon.m_shared.m_attack.m_attackAnimation="dualaxes";events.Speed(1);player.m_animator.speed=1;events.CustomFixedUpdate(.02f);Check(player.m_animator.speed==1.5f,"dual axes clips without Speed events receive configured rate");
            weapon.m_shared.m_skillType=Skills.SkillType.Swords;weapon.m_shared.m_attack.m_attackAnimation="";            weapon.m_shared.m_itemType=ItemDrop.ItemData.ItemType.TwoHandedWeapon;events.Speed(2);Check(player.m_animator.speed==3,"two hand sword uses imported rate");
            weapon.m_shared.m_itemType=ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;Check((float)Call("Rate",weapon,true)==1.5f,"left hand two handed classification");
            weapon.m_shared.m_skillType=Skills.SkillType.ElementalMagic;events.Speed(1);Check(Mathf.Approximately(player.m_animator.speed,1.2f),"staff heavy uses staff rate");
            weapon.m_shared.m_skillType=Skills.SkillType.Unarmed;events.Speed(1);Check(player.m_animator.speed==2,"claw rate matches imported defaults");
            weapon.m_shared.m_skillType=Skills.SkillType.Swords;weapon.m_shared.m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon;
            player.m_cachedAttack=false;player.m_cachedCurrentAnimHash=0;events.Speed(1);Check(player.m_animator.speed==1,"outside attacks unchanged");
            player.m_cachedAttack=true;player.m_cachedCurrentAnimHash=Humanoid.s_animatorTagAttack;Player.m_localPlayer=null;events.Speed(1);Check(player.m_animator.speed==1,"remote player is not scaled twice");
        }finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
        lines.Add("PACKAGE "+mod.GetName().Version);
    }
}
