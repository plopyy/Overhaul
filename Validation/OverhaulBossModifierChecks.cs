using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class OverhaulBossModifierChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly mod;
    static readonly List<string> report=new List<string>();
    static object Call(Type t,string name,params object[] args)=>t.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);report.Add("PASS "+message);}
    static float Number(object entry,string field)=>(float)entry.GetType().GetField(field).GetValue(entry);
    static void OtherHealth(ref float __result)=>__result*=1.5f;
    static bool SkipEquip(ref bool __result){__result=true;return false;}
    public static void Run()
    {
        try { Test();report.Add("PASS total="+report.Count+" package="+mod.GetName().Version);File.WriteAllLines("../../../Tools/AugaWork/boss-modifier-checks.txt",report); }
        catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/boss-modifier-checks.txt",report);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        Call(mod.GetType("Overhaul.IntegratedUi"),"LoadDependencies");
        var data=mod.GetType("Overhaul.Dungeons.BossData");var modifiers=mod.GetType("Overhaul.Dungeons.BossModifiers");
        string path=Path.GetFullPath("../../../Packages/Overhaul/DungeonBosses.cfg");var text=File.ReadAllText(path);
        var entries=(IDictionary)Call(data,"Read",path);
        Check(entries.Values.Cast<object>().All(e=>Number(e,"AttackSpeed")==2),"default attack speed is x2 for all twelve guardians");
        Check(entries.Count==13 && (int)entries["Abomination"].GetType().GetField("Stars").GetValue(entries["Abomination"])==1,"13 boss sections preserve existing star counts");
        // Current distribution can have configured bonuses. Normalize only this
        // in-memory fixture; never rewrite the user's configuration.
        foreach(var value in entries.Values) { value.GetType().GetField("HealthBonusPercent").SetValue(value,0f);value.GetType().GetField("Scale").SetValue(value,1f); }
        text=System.Text.RegularExpressions.Regex.Replace(text,@"(?m)^HealthBonusPercent\s*=.*$","HealthBonusPercent = 0");
        text=System.Text.RegularExpressions.Regex.Replace(text,@"(?m)^Scale\s*=.*$","Scale = 1");
        var temp=Path.GetFullPath("../../../Tools/AugaWork/boss-data-"+Guid.NewGuid().ToString("N")+".cfg");
        File.WriteAllText(temp,"[Stars]\n"+string.Join("\n",entries.Keys.Cast<string>().Select(k=>k+" = 2")));
        var migrated=(IDictionary)Call(data,"ReadAndMigrate",temp);
        Check(migrated.Values.Cast<object>().All(e=>(int)e.GetType().GetField("Stars").GetValue(e)==2),"migration preserves customized old stars");
        Check(File.Exists(temp+".legacy.bak")&&!File.ReadAllText(temp).Contains("[Stars]")&&File.ReadAllText(temp).Contains("HealthBonusPercent = 0"),"old data backed up and rewritten by boss");
        string once=File.ReadAllText(temp);Call(data,"ReadAndMigrate",temp);Check(File.ReadAllText(temp)==once,"migration idempotent");
        var oldText=System.Text.RegularExpressions.Regex.Replace(text,@"(?m)^AttackSpeed\s*=.*\r?\n?","");
        File.WriteAllText(temp,oldText);var upgraded=(IDictionary)Call(data,"ReadAndMigrate",temp);
        Check(upgraded.Values.Cast<object>().All(e=>Number(e,"AttackSpeed")==2)&&File.Exists(temp+".attack-speed.bak"),"existing CFG gains attack speed with backup");
        Check(File.ReadAllLines(temp).Select(l=>l.Trim()).Where(l=>l.Length>0&&!l.StartsWith("AttackSpeed =")).SequenceEqual(oldText.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(l=>l.Trim()).Where(l=>l.Length>0)),"migration preserves other settings and comments");
        once=File.ReadAllText(temp);Call(data,"ReadAndMigrate",temp);Check(File.ReadAllText(temp)==once,"attack speed migration idempotent");
        foreach(var value in new[]{"0","-2","NaN","Infinity","11","0.09","2\nAttackSpeed = 3"}){
            File.WriteAllText(temp,text.Replace("AttackSpeed = 2","AttackSpeed = "+value));bool rejected=false;try{Call(data,"ReadAndMigrate",temp);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"reject invalid attack speed "+value);
        }
        File.WriteAllText(temp,text.Replace("AttackSpeed = 2","AttackSpeed = 1.5"));var custom=(IDictionary)Call(data,"ReadAndMigrate",temp);
        Check(custom.Values.Cast<object>().All(e=>Number(e,"AttackSpeed")==1.5f),"custom attack speed preserved");
        foreach(var bad in new[]{text.Replace("Scale = 1","Scale = 0"),text.Replace("HealthBonusPercent = 0","HealthBonusPercent = NaN"),text.Replace("Stars = 3","Stars = 4"),text+"\n[Ulv]\nStars = 1",text.Replace("HealthBonusPercent = 0", ""),text.Replace("Scale = 1","Scale = Infinity"),text.Replace("Scale = 1","Scale = 1\nScale = 2")})
        {File.WriteAllText(temp,bad);bool rejected=false;try{Call(data,"Read",temp);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"reject malformed/incomplete/duplicate/nonfinite data");}

        var host=new GameObject("Boss modifier fixtures");host.SetActive(false);
        var net=host.AddComponent<ZNet>();typeof(ZNet).GetField("m_instance",F).SetValue(null,net);
        var game=host.AddComponent<Game>();typeof(Game).GetProperty("instance",F).SetValue(null,game);new ZRoutedRpc(true);var manager=new ZDOMan(512);
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=harmonyAssembly.GetType("HarmonyLib.Harmony");var hm=harmonyAssembly.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.boss.modifier.checks"});
        foreach(var t in modifiers.GetNestedTypes(F)){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});processor.GetType().GetMethod("Patch").Invoke(processor,null);}
        foreach(var name in new[]{"CreatureBaseHealthPatch","CreatureHealthFractionPatch","LoadedCreatureHealthPatch"}){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{mod.GetType("Overhaul.Storage."+name)});processor.GetType().GetMethod("Patch").Invoke(processor,null);}
        var encounter=mod.GetType("Overhaul.Dungeons.BossEncounter");int bossKey=(int)encounter.GetField("BossKey",F).GetValue(null);
        Character Make(string name,ZDO saved=null){var obj=new GameObject(name);obj.transform.SetParent(host.transform);var view=obj.AddComponent<ZNetView>();view.m_zdo=saved??manager.CreateNewZDO(Vector3.zero,name.GetStableHashCode());view.m_zdo.SetOwner(ZNet.GetUID());var c=obj.AddComponent<Character>();c.m_nview=view;c.m_health=100;return c;}
        var ordinary=Make("Greydwarf_Shaman");ordinary.SetLevel(4);ordinary.SetHealth(ordinary.GetMaxHealth());
        Check(ordinary.GetMaxHealth()==600,"ordinary three-star shaman receives global 50 percent health boost");
        var creature=Make("ordinary animal");creature.SetLevel(1);
        Check(creature.GetMaxHealth()==150&&creature.GetHealth()==150,"100 base HP becomes 150 and new creature starts full");
        creature.m_nview.GetZDO().Set(ZDOVars.s_maxHealth,100f);creature.SetHealth(40);
        Call(mod.GetType("Overhaul.Storage.LoadedCreatureHealthPatch"),"Postfix",creature);
        Check(creature.GetMaxHealth()==150&&creature.GetHealth()==60,"wounded saved creature receives boost while preserving health fraction");
        Call(mod.GetType("Overhaul.Storage.LoadedCreatureHealthPatch"),"Postfix",creature);
        Check(creature.GetMaxHealth()==150&&creature.GetHealth()==60,"reloading does not compound health or heal injuries");
        creature.m_tamed=true;creature.SetLevel(1);Check(creature.GetMaxHealth()==150,"tamed creatures also receive global health boost");
        var playerObject=new GameObject("player excluded");playerObject.transform.SetParent(host.transform);var player=playerObject.AddComponent<Player>();player.m_health=100;
        Check(player.GetMaxHealthBase()==100,"player base health unchanged");
        var entry=entries["Greydwarf_Shaman"];entry.GetType().GetField("HealthBonusPercent").SetValue(entry,100f);entry.GetType().GetField("Scale").SetValue(entry,1.5f);
        Call(modifiers,"Configure",ordinary,entry);Check(!ordinary.GetComponent(mod.GetType("Overhaul.Dungeons.BossFinalScale")),"unmarked normal creature cannot receive boss modifiers");
        var boss=Make("Greydwarf_Shaman");boss.m_nview.GetZDO().Set(bossKey,true);Call(modifiers,"Configure",boss,entry);boss.SetLevel(4);boss.SetHealth(boss.GetMaxHealth());
        Check(boss.GetMaxHealth()==1200&&boss.GetHealth()==1200,"100 percent bonus doubles final star/other-modifier HP");
        boss.SetHealth(420);boss.SetLevel(4);Check(boss.GetMaxHealth()==1200&&Mathf.Abs(boss.GetHealth()-420)<.01f,"repeated health setup neither compounds nor heals damaged boss");
        var component=boss.GetComponent(mod.GetType("Overhaul.Dungeons.BossFinalScale"));boss.transform.localScale=new Vector3(2,3,4);component.GetType().GetMethod("Apply",F).Invoke(component,null);
        Check(boss.transform.localScale==new Vector3(3,4.5f,6),"final scale multiplies existing nonuniform scale");
        component.GetType().GetMethod("Apply",F).Invoke(component,null);Check(boss.transform.localScale==new Vector3(3,4.5f,6),"scale does not compound across frames");
        var visual=boss.gameObject.AddComponent<LevelEffects>();visual.m_levelSetups.Add(new LevelEffects.LevelSetup{m_scale=2});visual.SetupLevelVisualization(2);
        Check(boss.transform.localScale==Vector3.one*3,"scale reapplied after native root level visuals");visual.SetupLevelVisualization(2);Check(boss.transform.localScale==Vector3.one*3,"repeated native level visuals stay stable");
        var packed=new ZPackage();boss.m_nview.GetZDO().Serialize(packed);var copy=new ZDO();copy.Deserialize(new ZPackage(packed.GetArray()));
        Check(copy.GetFloat((int)modifiers.GetField("HealthKey",F).GetValue(null))==100&&copy.GetFloat((int)modifiers.GetField("ScaleKey",F).GetValue(null))==1.5f,"modifiers survive native network/world serialization");
        var reloaded=Make("Greydwarf_Shaman",copy);Call(modifiers,"Attach",reloaded);reloaded.SetLevel(4);
        Check(reloaded.GetMaxHealth()==1200&&Mathf.Abs(reloaded.GetHealth()-420)<.01f,"reload preserves bonus and wounded health");
        var restoredScale=reloaded.GetComponent(mod.GetType("Overhaul.Dungeons.BossFinalScale"));reloaded.transform.localScale=Vector3.one*2;restoredScale.GetType().GetMethod("Apply",F).Invoke(restoredScale,null);Check(reloaded.transform.localScale==Vector3.one*3,"reload applies scale exactly once");
        entry.GetType().GetField("HealthBonusPercent").SetValue(entry,900f);reloaded.SetLevel(4);Check(reloaded.GetMaxHealth()==1200,"existing guardian uses saved data rather than changed config");
        var old=Make("Old guardian");old.m_nview.GetZDO().Set(bossKey,true);old.SetLevel(4);Check(old.GetMaxHealth()==600,"legacy guardian without modifier keys keeps old HP");
        Check(ordinary.GetMaxHealth()==600&&ordinary.transform.localScale==Vector3.one,"normal creature still unaffected");
        AttackChecks(modifiers,entries,host,manager,bossKey,ht,harmony);
        ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
        // Inactive native components lack their full game lifecycle; Unity exits the fixture scene.
    }
    static void AttackChecks(Type modifiers,IDictionary entries,GameObject host,ZDOMan manager,int bossKey,Type ht,object harmony)
    {
        var speed=mod.GetType("Overhaul.Dungeons.BossAttackSpeed");
        foreach(var patch in speed.GetNestedTypes(F).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{patch});processor.GetType().GetMethod("Patch").Invoke(processor,null);}
        var obj=new GameObject("Greydwarf_Shaman");obj.transform.SetParent(host.transform);
        var view=obj.AddComponent<ZNetView>();view.m_zdo=manager.CreateNewZDO(Vector3.zero,obj.name.GetStableHashCode());view.m_zdo.SetOwner(ZNet.GetUID());
        var boss=obj.AddComponent<Humanoid>();boss.m_nview=view;
        var animator=obj.AddComponent<Animator>();boss.m_animator=animator;
        var events=obj.AddComponent<CharacterAnimEvent>();events.m_character=boss;events.m_nview=view;events.m_animator=animator;
        Action<bool> attack=active=>{boss.m_cachedAnimHashFrame=MonoUpdaters.UpdateCount;boss.m_cachedCurrentAnimHash=active?Humanoid.s_animatorTagAttack:0;boss.m_cachedNextAnimHash=0;};
        attack(true);events.Speed(.75f);Check(animator.speed==.75f,"ordinary attack animation unchanged");
        view.m_zdo.Set(bossKey,true);Call(modifiers,"Configure",boss,entries["Greydwarf_Shaman"]);
        events.Speed(.75f);Check(animator.speed==1.5f,"native Speed event multiplies attack speed once");
        for(int i=0;i<10;i++)events.CustomFixedUpdate(.02f);Check(animator.speed==1.5f,"repeated native ticks never compound speed");
        events.Speed(1.8f);events.CustomFixedUpdate(.02f);Check(Mathf.Abs(animator.speed-3.6f)<.0001f,"native mid-attack tempo changes retained");
        events.FreezeFrame(.1f);Check(animator.speed==.0001f,"native hit pause preserved");
        events.CustomFixedUpdate(.05f);Check(animator.speed==.0001f,"hit pause remains frozen before deadline");
        events.CustomFixedUpdate(.06f);Check(Mathf.Abs(animator.speed-3.6f)<.0001f,"hit pause restores scaled tempo without compounding");
        attack(false);events.CustomFixedUpdate(.02f);Check(animator.speed==1,"leaving attack restores normal movement animation");
        attack(true);events.CustomFixedUpdate(.02f);Check(animator.speed==2,"attack without Speed event receives multiplier");
        var sync=obj.AddComponent<ZSyncAnimation>();sync.m_nview=view;sync.m_animator=animator;
        sync.m_boolHashes=new int[0];sync.m_floatHashes=new int[0];sync.m_intHashes=new int[0];
        sync.CustomFixedUpdate(.02f);Check(view.m_zdo.GetFloat(ZDOVars.s_animationSpeed)==2,"native ZSyncAnimation publishes scaled attack speed");
        view.m_zdo.SetOwner(ZNet.GetUID()+100);animator.speed=1;sync.CustomFixedUpdate(.02f);events.CustomFixedUpdate(.02f);
        Check(animator.speed==2,"native remote animation sync reads multiplier without applying it twice");
        view.m_zdo.SetOwner(ZNet.GetUID());events.CustomFixedUpdate(.02f);events.CustomFixedUpdate(.02f);
        Check(animator.speed==2,"network ownership transfer preserves speed without multiplying again");
        Check((float)Call(speed,"Interval",8f,boss)==4,"native attack cooldown divided by multiplier");
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var args=new object[5];
        args[0]=typeof(Humanoid).GetMethod("EquipItem",new[]{typeof(ItemDrop.ItemData),typeof(bool)});
        args[1]=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulBossModifierChecks).GetMethod("SkipEquip",F)});
        ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,args);
        boss.m_baseAI=obj.AddComponent<MonsterAI>();boss.m_baseAI.m_character=boss;
        boss.m_inventory.GetAllItems().Clear();
        var weapon=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon,m_aiAttackInterval=8,m_aiAttackRange=10,m_aiWhenWalking=true}};
        boss.m_inventory.GetAllItems().Add(weapon);attack(false);
        var staticObject=new GameObject("Attack target");staticObject.transform.SetParent(host.transform);var target=staticObject.AddComponent<StaticTarget>();
        foreach(var type in new[]{ItemDrop.ItemData.AiTarget.Enemy,ItemDrop.ItemData.AiTarget.Friend,ItemDrop.ItemData.AiTarget.FriendHurt}){
            weapon.m_shared.m_aiTargetType=type;weapon.m_lastAttackTime=Time.time-3.9f;boss.EquipBestWeapon(null,target,boss,boss);
            Check(!Humanoid.optimalWeapons.Contains(weapon),"native weapon selection respects shortened cooldown before boundary: "+type);
            weapon.m_lastAttackTime=Time.time-4.1f;boss.EquipBestWeapon(null,target,boss,boss);
            Check(Humanoid.optimalWeapons.Contains(weapon),"native weapon selection allows attack at half original cooldown: "+type);
            view.m_zdo.Set(bossKey,false);boss.EquipBestWeapon(null,target,boss,boss);
            Check(!Humanoid.optimalWeapons.Contains(weapon)&&weapon.m_shared.m_aiAttackInterval==8,"ordinary weapon cooldown and shared asset unchanged: "+type);view.m_zdo.Set(bossKey,true);
        }
        attack(true);
        var package=new ZPackage();view.m_zdo.Serialize(package);var restored=new ZDO();restored.Deserialize(new ZPackage(package.GetArray()));
        int key=(int)modifiers.GetField("AttackSpeedKey",F).GetValue(null);
        Check(restored.GetFloat(key)==2,"attack speed persists in native world/network serialization");
        entries["Greydwarf_Shaman"].GetType().GetField("AttackSpeed").SetValue(entries["Greydwarf_Shaman"],3f);
        Check((float)Call(modifiers,"AttackSpeed",boss)==2,"saved guardian retains speed after config changes");
        // Legacy guardian adopts the current config once, on its owning peer.
        mod.GetType("Overhaul.Dungeons.BossData").GetField("entries",F).SetValue(null,entries);
        var legacy=manager.CreateNewZDO(Vector3.zero,obj.name.GetStableHashCode());legacy.SetOwner(ZNet.GetUID());legacy.Set(bossKey,true);view.m_zdo=legacy;
        Check((float)Call(modifiers,"AttackSpeed",boss)==3&&legacy.GetFloat(key)==3,"legacy guardian acquires configured speed once");
        entries["Greydwarf_Shaman"].GetType().GetField("AttackSpeed").SetValue(entries["Greydwarf_Shaman"],4f);
        Check((float)Call(modifiers,"AttackSpeed",boss)==3,"legacy adoption is not cumulative");
        legacy.SetOwner(ZNet.GetUID()+100);animator.speed=3;events.CustomFixedUpdate(.02f);
        Check(animator.speed==3,"remote peer never multiplies replicated animator speed again");
        legacy.SetOwner(ZNet.GetUID());legacy.Set(bossKey,false);events.Speed(.8f);events.CustomFixedUpdate(.02f);
        Check(animator.speed==.8f&&(float)Call(speed,"Interval",8f,boss)==8,"unmarked creature speed and cooldown remain native");
    }
}
