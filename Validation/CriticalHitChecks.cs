using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public static class CriticalHitChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> report=new List<string>();static Assembly mod;static Type critical;static int sounds,texts;static DamageText.TextType lastType;static string lastText;static EffectList critEffects;
    static object Call(Type t,string method,params object[] args)=>t.GetMethod(method,F).Invoke(null,args);
    static void Check(bool value,string message){if(!value)throw new Exception(message);report.Add("PASS "+message);}
    static bool Text(DamageText.TextType type,string text){texts++;lastType=type;lastText=text;return false;}
    static bool Effect(EffectList __instance,ref GameObject[] __result){if(__instance==critEffects)sounds++;__result=new GameObject[0];return false;}
    static bool Scale(ref float __result){__result=1;return false;}
    static T New<T>(GameObject host,string name) where T:Component{var go=new GameObject(name);go.transform.SetParent(host.transform);return go.AddComponent<T>();}
    public static void Run()
    {
        try{Test();report.Add("PASS total="+report.Count+" package="+mod.GetName().Version);File.WriteAllLines("../../../Tools/AugaWork/critical-checks.txt",report);}
        catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/critical-checks.txt",report);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));Call(mod.GetType("Overhaul.IntegratedUi"),"LoadDependencies");critical=mod.GetType("Overhaul.Leveling.CriticalHits");
        var config=mod.GetType("Overhaul.Leveling.LevelingConfig");var rules=Activator.CreateInstance(mod.GetType("Overhaul.Leveling.LevelingRules"));
        var read=config.GetMethod("Read",F); // Use the existing package configuration parser through Initialize.
        Call(config,"Initialize");rules=config.GetField("Current",F).GetValue(null);
        var stats=(System.Collections.IDictionary)rules.GetType().GetField("Stats").GetValue(rules);var stat=stats["critical"];stat.GetType().GetField("PerPoint").SetValue(stat,1d);
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.critical.check"});
        var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        void Hook(MethodBase method,string handler){patch.Invoke(harmony,new object[]{method,Activator.CreateInstance(hm,new object[]{typeof(CriticalHitChecks).GetMethod(handler,F)}),null,null,null});}
        try{
            foreach(var t in critical.GetNestedTypes(F).Concat(new[]{mod.GetType("Overhaul.Leveling.LevelingAttackPatch")}).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});processor.GetType().GetMethod("Patch").Invoke(processor,null);}
            Hook(typeof(DamageText).GetMethod("ShowText",new[]{typeof(DamageText.TextType),typeof(Vector3),typeof(string),typeof(bool)}),"Text");
            Hook(typeof(EffectList).GetMethod("Create"),"Effect");Hook(typeof(Game).GetMethod("GetDifficultyDamageScaleEnemy"),"Scale");
            var host=new GameObject("Inactive critical fixtures");host.SetActive(false);
            var net=New<ZNet>(host,"net");ZNet.m_instance=net;new ZRoutedRpc(true);var manager=new ZDOMan(512);
            var game=New<Game>(host,"game");typeof(Game).GetProperty("instance",F).SetValue(null,game);
            var scene=New<ZNetScene>(host,"scene");ZNetScene.s_instance=scene;
            CinematicsManager.s_instance=New<CinematicsManager>(host,"cinematics");CinematicsManager.s_instance.m_videoPlayer=CinematicsManager.s_instance.gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
            var player=New<Player>(host,"critical attacker");player.m_nview=player.gameObject.AddComponent<ZNetView>();player.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,0);player.m_nview.m_zdo.SetOwner(ZNet.GetUID());
            var stateType=mod.GetType("Overhaul.Leveling.OverhaulCharacter");var state=Call(stateType,"Get",player);stateType.GetField("Ready").SetValue(state,true);var data=stateType.GetField("Data").GetValue(state);var allocation=(Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data);
            var seman=new SEMan(player,player.m_nview);var hit=new HitData(20);
            seman.ModifyAttack(Skills.SkillType.Swords,ref hit);Check(!(bool)Call(critical,"IsCritical",hit)&&hit.GetTotalDamage()==20,"zero stat gives no critical and unchanged damage");
            allocation["critical"]=1;seman.ModifyAttack(Skills.SkillType.Swords,ref hit);
            Check((bool)Call(critical,"IsCritical",hit)&&hit.GetTotalDamage()==30,"guaranteed stat proc uses configured 1.5 multiplier and records critical");
            Call(critical,"Roll",player,hit);Check(hit.GetTotalDamage()==30,"same marked hit never receives multiplier twice");
            var clone=hit.Clone();Check((bool)Call(critical,"IsCritical",clone),"native hit cloning preserves critical");
            uint used=0;foreach(var flag in Enum.GetValues(typeof(HitData.HitDefaults.SerializeFlags)))used|=Convert.ToUInt32(flag);
            Check((used&0x80000000u)==0,"critical transport flag does not overlap any native flag");
            var package=new ZPackage();package.Write(123);hit.Serialize(ref package);new HitData(7).Serialize(ref package);package.Write(987);
            var incoming=new ZPackage(package.GetArray());Check(incoming.ReadInt()==123,"hit can be embedded after other RPC arguments");
            var received=new HitData();received.Deserialize(ref incoming);Check((bool)Call(critical,"IsCritical",received)&&received.GetTotalDamage()==30,"critical flag and damage survive native serialization");
            var ordinary=new HitData();ordinary.Deserialize(ref incoming);Check(!(bool)Call(critical,"IsCritical",ordinary)&&ordinary.GetTotalDamage()==7&&incoming.ReadInt()==987,"following hit and RPC arguments keep exact boundaries");
            var ordinaryPackage=new ZPackage();ordinary.Serialize(ref ordinaryPackage);ordinaryPackage.SetPos(0);received.Deserialize(ref ordinaryPackage);Check(!(bool)Call(critical,"IsCritical",received),"reusing HitData clears old critical marker");
            var projectile=New<Projectile>(host,"projectile");projectile.m_originalHitData=hit;
            var impact=(HitData)Call(critical,"ProjectileHit",new HitData(30),projectile);Check((bool)Call(critical,"IsCritical",impact)&&impact.GetTotalDamage()==30,"projectile impact inherits proc without multiplying again");
            var victim=New<Character>(host,"target");victim.m_nview=victim.gameObject.AddComponent<ZNetView>();victim.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,0);victim.m_nview.m_zdo.SetOwner(ZNet.GetUID());victim.m_staggerDamageFactor=0;victim.m_health=1000;victim.SetMaxHealth(1000);victim.SetHealth(1000);victim.m_seman=new SEMan(victim,victim.m_nview);victim.m_animator=victim.gameObject.AddComponent<Animator>();victim.m_collider=victim.gameObject.AddComponent<CapsuleCollider>();
            victim.m_cachedAnimHashFrame=MonoUpdaters.UpdateCount;victim.m_cachedCurrentAnimHash=0;victim.m_cachedNextAnimHash=0;
            critEffects=victim.m_critHitEffects;
            var damageText=New<DamageText>(host,"damage text");DamageText.m_instance=damageText;
            sounds=texts=0;victim.ApplyDamage(impact,true,false);
            Check(victim.GetHealth()==970&&sounds==1&&texts==1&&lastType==DamageText.TextType.Weak&&lastText=="30","real ApplyDamage deals critical damage, uses yellow native text and one native critical effect");
            sounds=texts=0;victim.ApplyDamage(new HitData(20),true,false);
            Check(victim.GetHealth()==950&&sounds==0&&texts==1&&lastType==DamageText.TextType.Normal,"normal hit keeps normal damage text and no critical sound");
            var zero=new HitData();Call(critical,"Mark",zero);sounds=texts=0;victim.ApplyDamage(zero,true,false);
            Check(sounds==0&&texts==0&&victim.GetHealth()==950,"zero damage cannot emit critical feedback");
            victim.m_isDead=true;sounds=texts=0;victim.ApplyDamage(impact,true,false);Check(sounds==0&&texts==0,"dead target rejects critical feedback");
            // Verify the actual vanilla text component uses yellow, not a damage multiplier hack.
            var template=new GameObject("Native damage text",typeof(RectTransform),typeof(TextMeshProUGUI));template.SetActive(false);template.transform.SetParent(host.transform);damageText.m_worldTextBase=template;
            var loc=(Localization)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Localization));
            foreach(var field in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic))
            {
                if(field.FieldType==typeof(char[]))field.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
                else if(field.FieldType==typeof(System.Text.StringBuilder))field.SetValue(loc,new System.Text.StringBuilder());
                else if(field.FieldType.IsGenericType&&(field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||field.FieldType.GetGenericTypeDefinition()==typeof(List<>)))field.SetValue(loc,Activator.CreateInstance(field.FieldType));
                else if(field.Name=="m_cache")field.SetValue(loc,Activator.CreateInstance(field.FieldType,new object[]{100}));
            }
            typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
            damageText.AddInworldText(DamageText.TextType.Weak,Vector3.zero,1,"30",false);
            var color=damageText.m_worldTexts.Last().m_textField.color;
            Check(color.r>.9f&&color.g>.8f&&color.b<.2f,"native floating damage text is yellow: "+color);
        }finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
    }
}
