using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

public static class OverhaulV2LevelingChecks
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;static Type system,dataType,config,character;
    static object Call(Type type,string name,params object[] args)=>type.GetMethod(name,All).Invoke(null,args);
    static T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,All).GetValue(obj);
    static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,All).SetValue(obj,value);
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Debug.Log("LEVELING PASS "+message);}
    static object Data()=>Activator.CreateInstance(dataType);
    static T Component<T>(string name) where T:Component {var go=new GameObject(name);go.SetActive(false);return go.AddComponent<T>();}
    public static void Run(Assembly plugin)
    {
        var window=plugin.GetType("Overhaul.Leveling.LevelingWindow");
        Check((int)Call(window,"ClickDelta",1,false,false,10,10,50,20)==1,"plain stat click adds one point");
        Check((int)Call(window,"ClickDelta",1,false,true,10,10,50,20)==5,"control adds five points");
        Check((int)Call(window,"ClickDelta",1,false,true,10,10,50,3)==3,"control respects three remaining points");
        Check((int)Call(window,"ClickDelta",1,true,false,48,40,50,20)==2,"shift respects stat cap");
        Check((int)Call(window,"ClickDelta",1,true,true,10,10,50,12)==12,"shift takes precedence and uses remaining budget");
        Check((int)Call(window,"ClickDelta",1,false,true,49,40,50,20)==1,"control clamps at stat cap");
        Check((int)Call(window,"ClickDelta",1,true,false,50,40,50,20)==0,"capped stat cannot gain points");
        Check((int)Call(window,"ClickDelta",1,true,false,10,10,50,0)==0,"empty budget cannot add points");
        Check((int)Call(window,"ClickDelta",-1,true,false,20,7,50,0)==-13,"shift removes all unvalidated points only");
        Check((int)Call(window,"ClickDelta",-1,false,true,20,7,50,0)==-5,"control removes five unvalidated points");
        Check((int)Call(window,"ClickDelta",-1,false,true,10,7,50,0)==-3,"control cannot remove committed points");
        Check((int)Call(window,"ClickDelta",-1,true,false,7,7,50,0)==0,"validated allocation cannot be removed");
        // Keep platform preferences entirely in this fixture; never initialize Steam or write prefs.
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony");
        object harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.dungeon.check"});
        var patchArgs=new object[5];patchArgs[0]=typeof(PlatformPrefs).GetMethod("GetString",new[]{typeof(string),typeof(string)});
        patchArgs[1]=Activator.CreateInstance(harmonyAssembly.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulV2LevelingChecks).GetMethod("ReadTestPreference",All)});
        harmonyType.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,patchArgs);
        checks=0;system=plugin.GetType("Overhaul.Leveling.LevelingSystem");dataType=plugin.GetType("Overhaul.Leveling.OverhaulCharacterData");config=plugin.GetType("Overhaul.Leveling.LevelingConfig");character=plugin.GetType("Overhaul.Leveling.OverhaulCharacter");
        Check((long)Call(system,"GetExperienceToNextLevel",1)==100 && (long)Call(system,"GetExperienceToNextLevel",2)==250,"central experience curve starts at 100 and 250");
        object data=Data();Call(system,"AddExperience",data,349L);Check(Field<int>(data,"Level")==2 && Field<long>(data,"CurrentExperience")==249 && Field<int>(data,"AvailableStatPoints")==4,"experience below next boundary");
        Call(system,"AddExperience",data,1L);Check(Field<int>(data,"Level")==3 && Field<long>(data,"CurrentExperience")==0 && Field<long>(data,"TotalExperience")==350,"exact level boundary preserves lifetime experience");
        var allocation=new Dictionary<string,int>{{"carry",4},{"element_fire",4}};
        Check((bool)Call(system,"Allocate",data,allocation,false)&&Field<int>(data,"AvailableStatPoints")==0,"valid allocation commits exact budget");
        Check(!(bool)Call(system,"Allocate",data,new Dictionary<string,int>{{"carry",3}},false),"committed points cannot be removed with minus");
        Check(!(bool)Call(system,"Reset",data,true)&&Field<Dictionary<string,int>>(data,"AllocatedStats").Count==2,"combat blocks resetting without mutation");
        Check(!(bool)Call(system,"ValidAllocation",new Dictionary<string,int>{{"element_fire",1},{"element_frost",1}},8),"elemental specializations are exclusive");
        Check(!(bool)Call(system,"ValidAllocation",new Dictionary<string,int>{{"carry",101}},1000),"stat rank cap enforced");
        Check(!(bool)Call(system,"ValidAllocation",new Dictionary<string,int>{{"element_fire",51}},1000)&&(bool)Call(system,"ValidAllocation",new Dictionary<string,int>{{"element_fire",50}},1000),"element cap accepts exactly fifty and rejects fifty-one");
        var legacy=Data();Call(system,"AddExperience",legacy,long.MaxValue);Field<Dictionary<string,int>>(legacy,"AllocatedStats")["element_fire"]=100;Call(system,"Reconcile",legacy);
        Check(Field<Dictionary<string,int>>(legacy,"AllocatedStats").Count==0&&Field<int>(legacy,"AvailableStatPoints")==796,"old over-cap elemental investment refunds all allocated points without losing XP");
        Check(!(bool)Call(system,"ValidAllocation",new Dictionary<string,int>{{"invalid",1}},8),"unknown stats rejected");
        Check((bool)Call(system,"Reset",data,false)&&Field<int>(data,"AvailableStatPoints")==8,"free reset restores whole level budget");
        Call(system,"Allocate",data,new Dictionary<string,int>{{"carry",8}},false);Set(data,"Level",99);
        Check((bool)Call(system,"Reconcile",data)&&Field<int>(data,"Level")==3&&Field<int>(data,"AvailableStatPoints")==8&&Field<Dictionary<string,int>>(data,"AllocatedStats").Count==0,"changed curve repairs level and refunds all points from total XP");
        Call(system,"AddExperience",data,long.MaxValue);
        Check(Field<int>(data,"Level")==200&&Field<int>(data,"AvailableStatPoints")==796&&Field<long>(data,"CurrentExperience")==0&&Field<long>(data,"TotalExperience")==long.MaxValue,"cap retains lifetime XP with saturating overflow protection");
        long now=DateTime.UtcNow.Ticks;
        Check((bool)Call(system,"ChoosePassive",data,"vitality",false,now),"first passive choice allowed at sufficient level");
        Check(!(bool)Call(system,"ChoosePassive",data,"artisan",false,now+TimeSpan.FromMinutes(29).Ticks),"first passive choice starts cooldown");
        Check(!(bool)Call(system,"ChoosePassive",data,"artisan",true,now+TimeSpan.FromHours(1).Ticks),"combat prevents passive swap after cooldown");
        Check((bool)Call(system,"ChoosePassive",data,"artisan",false,now+TimeSpan.FromMinutes(30).Ticks),"passive swap allowed exactly at cooldown boundary");
        string saved=JsonConvert.SerializeObject(data);object restored=JsonConvert.DeserializeObject(saved,dataType);
        Check(Field<long>(restored,"PassiveChangeAfterUtc")==Field<long>(data,"PassiveChangeAfterUtc")&&Field<long>(restored,"TotalExperience")==long.MaxValue,"offline serialization retains experience and absolute passive cooldown");
        Check((long)Call(config,"Reward","Greydwarf_Shaman",1)==63&&(long)Call(config,"Reward","Greydwarf",3)==75&&(long)Call(config,"Reward","Greyling",1)==10,"biome/category/stars rewards round once as requested");
        Check((long)Call(config,"Reward","UnknownModCreature",1)==0,"unconfigured mobs give no guessed rewards");
        SkillMilestoneChecks();
        PersistenceAndEffects(plugin);
        FeedChecks(plugin);

        OverhaulV2UiChecks.Run(plugin);
        File.WriteAllText("../../../Tools/OverhaulWork/Leveling/check-results.txt","PASS "+checks+" leveling checks. Packaged "+plugin.GetName().Version+". Synthetic Unity fixtures; no player worlds or profiles opened.\n");
    }
    static void SkillMilestoneChecks()
    {
        var player=Component<Player>("skill-milestone-player");
        var skills=player.gameObject.AddComponent<Skills>();skills.m_player=player;player.m_skills=skills;
        float oldRate=Game.m_skillReductionRate;
        try
        {
            Game.m_skillReductionRate=1f;skills.m_DeathLowerFactor=.05f;
            foreach(float initial in new[]{0f,9.9f,10f,10.5f,17f,20f,99f,100f})
            {
                skills.m_skillData.Clear();
                var skill=new Skills.Skill(new Skills.SkillDef{m_skill=Skills.SkillType.Bows});skill.m_level=initial;skill.m_accumulator=12;
                skills.m_skillData[Skills.SkillType.Bows]=skill;
                float floor=Mathf.Floor(initial/10f)*10f;
                skills.OnDeath();
                Check(Mathf.Abs(skill.m_level-Mathf.Max(initial*.95f,floor))<.0001f&&skill.m_accumulator==0,"native death respects decade and resets XP at "+initial);
                for(int i=0;i<50;i++)skills.OnDeath();
                Check(skill.m_level>=floor,"repeated deaths never cross earned decade at "+initial);
            }
            var kept=skills.m_skillData[Skills.SkillType.Bows];kept.m_level=37;
            skills.m_DeathLowerFactor=1f;skills.OnDeath();
            Check(kept.m_level==30,"maximum death penalty still preserves level thirty");
            kept.m_level=37;Game.m_skillReductionRate=0;skills.OnDeath();
            Check(kept.m_level==37,"zero world death penalty does not lower skills");
            Game.m_skillReductionRate=1;skills.OnDeath();
            var package=new ZPackage();skills.Save(package);
            var target=Component<Player>("skill-milestone-load");var restored=target.gameObject.AddComponent<Skills>();restored.m_player=target;
            restored.m_skills.Add(new Skills.SkillDef{m_skill=Skills.SkillType.Bows});restored.Load(new ZPackage(package.GetArray()));
            restored.OnDeath();
            Check(restored.m_skillData[Skills.SkillType.Bows].m_level==30,"milestone survives native save/load and another death");
            UnityEngine.Object.DestroyImmediate(target.gameObject);
            skills.LowerAllSkills(1f);
            Check(kept.m_level==0,"explicit non-death skill reduction remains unchanged");
        }
        finally{Game.m_skillReductionRate=oldRate;UnityEngine.Object.DestroyImmediate(player.gameObject);}
    }
    static bool ReadTestPreference(string defaultValue,ref string __result){__result=defaultValue=="English"?"French":defaultValue;return false;}
    static void PersistenceAndEffects(Assembly plugin)
    {
        var player=Component<Player>("leveling-player");var state=Call(character,"Get",player);Set(state,"Ready",true);
        object data=Field<object>(state,"Data");Call(system,"AddExperience",data,10000L);Call(system,"Allocate",data,new Dictionary<string,int>{{"carry",10},{"eitr_regen",5},{"fall_resist",3}},false);
        character.GetMethod("Store",All).Invoke(state,null);
        Check(player.m_customData.ContainsKey("Overhaul.Character"),"character data writes into native custom-data save field");
        var loaded=Component<Player>("leveling-loaded-player");loaded.m_customData=new Dictionary<string,string>(player.m_customData);var other=Call(character,"Get",loaded);character.GetMethod("Load",All).Invoke(other,null);
        Check(Field<long>(Field<object>(other,"Data"),"TotalExperience")==10000L&&Field<Dictionary<string,int>>(Field<object>(other,"Data"),"AllocatedStats")["carry"]==10,"custom-data reload retains progress and allocated stats");
        string original=loaded.m_customData["Overhaul.Character"]="{broken";character.GetMethod("Load",All).Invoke(other,null);character.GetMethod("Store",All).Invoke(other,null);
        Check(loaded.m_customData["Overhaul.Character"]==original&&Field<bool>(other,"InvalidSave"),"corrupt save remains intact instead of being overwritten");
        var view=player.gameObject.AddComponent<ZNetView>();player.m_nview=view;
        view.m_zdo=ZDOMan.instance.CreateNewZDO(Vector3.zero,0);view.m_zdo.SetOwner(ZNet.GetUID());
        player.m_skills=player.gameObject.AddComponent<Skills>();
        var seman=new SEMan(player,view);player.m_seman=seman;
        var package=new ZPackage();player.Save(package);
        Check(System.Text.Encoding.UTF8.GetString(package.GetArray()).Contains("Overhaul.Character")&&System.Text.Encoding.UTF8.GetString(package.GetArray()).Contains("TotalExperience"),"native Player.Save includes progression in binary character payload");
        var roundtrip=Component<Player>("leveling-binary-roundtrip");
        var roundtripView=roundtrip.gameObject.AddComponent<ZNetView>();roundtrip.m_nview=roundtripView;
        roundtripView.m_zdo=ZDOMan.instance.CreateNewZDO(Vector3.zero,0);roundtripView.m_zdo.SetOwner(ZNet.GetUID());
        roundtrip.m_skills=roundtrip.gameObject.AddComponent<Skills>();roundtrip.m_seman=new SEMan(roundtrip,roundtripView);
        var previousDb=ObjectDB.instance;var testDb=Component<ObjectDB>("leveling-object-db");ObjectDB.m_instance=testDb;
        roundtrip.Load(new ZPackage(package.GetArray()));
        var roundtripData=Field<object>(Call(character,"Get",roundtrip),"Data");
        Check(Field<long>(roundtripData,"TotalExperience")==10000L&&Field<Dictionary<string,int>>(roundtripData,"AllocatedStats")["carry"]==10,"native binary Player.Save/Load restores progression through actual Harmony hooks");
        UnityEngine.Object.DestroyImmediate(roundtrip.gameObject);
        ObjectDB.m_instance=previousDb;UnityEngine.Object.DestroyImmediate(testDb.gameObject);
        float baseline=2;seman.ModifyEitrRegen(ref baseline);Check(Math.Abs(baseline-2.05f)<0.0001f,"actual Eitr regeneration hook applies invested points");
        float damage=20;seman.ModifyFallDamage(20,ref damage);Check(Math.Abs(damage-19.4f)<0.0001f,"fall resistance multiplies native result");
        var allocations=Field<Dictionary<string,int>>(data,"AllocatedStats");
        foreach(string element in new[]{"poison","frost","fire","spirit"})
        {
            allocations.Clear();allocations["element_"+element]=10;
            var hit=new HitData();hit.m_variant=-1;hit.m_damage.m_slash=100;
            seman.ModifyAttack(Skills.SkillType.Swords,ref hit);
            float added=(float)typeof(HitData.DamageTypes).GetField("m_"+(element=="spirit"?"lightning":element)).GetValue(hit.m_damage);
            Check(added==10&&hit.m_damage.m_slash==100,"native attack adds exactly ten flat "+element+" damage without changing physical damage");
            Check(hit.m_variant==0,"added element selects standard visual instead of every variant");
            Check(hit.m_damage.m_poison+hit.m_damage.m_frost+hit.m_damage.m_fire+hit.m_damage.m_spirit+hit.m_damage.m_lightning==10,"one specialization does not add other elemental damage types");
            var special=new HitData();special.m_variant=2;seman.ModifyAttack(Skills.SkillType.Swords,ref special);
            Check(special.m_variant==2,"explicit native weapon visual variant is preserved");
            hit=new HitData();hit.m_damage.m_slash=1;seman.ModifyAttack(Skills.SkillType.Bows,ref hit);
            Check((float)typeof(HitData.DamageTypes).GetField("m_"+(element=="spirit"?"lightning":element)).GetValue(hit.m_damage)==10,"element bonus independent of weapon base damage for projectile attack");
            var serialized=new ZPackage();hit.Serialize(ref serialized);var received=new HitData();serialized.SetPos(0);received.Deserialize(ref serialized);
            Check((float)typeof(HitData.DamageTypes).GetField("m_"+(element=="spirit"?"lightning":element)).GetValue(received.m_damage)==10,"element damage survives native HitData network serialization");
        }
        var burning=ScriptableObject.CreateInstance<SE_Burning>();burning.name="Burning";burning.m_ttl=5;seman.m_statusEffects.Add(burning);seman.m_statusEffectsHashSet.Add(burning.NameHash());
        player.AddFireDamage(10,0);Check(burning.m_fireDamageLeft==10&&burning.m_fireDamagePerHit==2,"native fire damage feeds real burning status and damage ticks");
        seman.m_statusEffects.Remove(burning);seman.m_statusEffectsHashSet.Remove(burning.NameHash());UnityEngine.Object.DestroyImmediate(burning);
        allocations.Clear();var ordinary=new HitData();ordinary.m_variant=-1;seman.ModifyAttack(Skills.SkillType.Swords,ref ordinary);
        Check(ordinary.m_variant==-1,"attack without elemental investment keeps its native variant");
        var visual0=new GameObject("standard-fire-test");var visual1=new GameObject("alternate-fire-test");
        var effects=new EffectList{m_effectPrefabs=new[]{new EffectList.EffectData{m_prefab=visual0,m_enabled=true,m_variant=0},new EffectList.EffectData{m_prefab=visual1,m_enabled=true,m_variant=1}}};
        var all=effects.Create(Vector3.zero,Quaternion.identity,null,1,-1);var standard=effects.Create(Vector3.zero,Quaternion.identity,null,1,0);
        Check(all.Length==2&&standard.Length==1&&standard[0].name.StartsWith("standard-fire-test"),"native effect list reproduces all-variants bug and filters correctly with standard variant");
        foreach(var effect in all.Concat(standard))UnityEngine.Object.DestroyImmediate(effect);
        UnityEngine.Object.DestroyImmediate(visual0);UnityEngine.Object.DestroyImmediate(visual1);
        Set(data,"Level",200);Set(data,"Passive","feather");damage=20;seman.ModifyFallDamage(20,ref damage);Check(damage==0,"feather passive removes fall damage");
        UnityEngine.Object.DestroyImmediate(player.gameObject);UnityEngine.Object.DestroyImmediate(loaded.gameObject);
    }
    static void FeedChecks(Assembly plugin)
    {
        var type=plugin.GetType("Overhaul.Leveling.ExperienceFeed");
        var canvasObject=new GameObject("ExperiencePreview",typeof(Canvas));var canvas=canvasObject.GetComponent<Canvas>();
        var font=TMPro.TMP_FontAsset.CreateFontAsset(UnityEditor.AssetDatabase.LoadAssetAtPath<Font>("Assets/OverhaulLeveling/Native/AveriaSerifLibre-Bold.ttf"));
        var feed=(Component)Call(type,"Create",canvas.transform,font);
        var pending=Field<Queue<string>>(feed,"pending");var visible=Field<System.Collections.IList>(feed,"visible");
        for(int i=0;i<13;i++)pending.Enqueue(i==0?"Nain gris (*): 38":"Bourgeon nain gris: 10");
        Action<float> advance=t=>type.GetMethod("Advance",All).Invoke(feed,new object[]{t});
        advance(0);Check(visible.Count==10&&pending.Count==3,"feed displays at most ten rewards and queues excess");
        Check(Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y==-300&&feed.GetComponent<RectMask2D>(),"entry starts at clipped lower widget edge");
        advance(.5f);Check(Math.Abs(Field<TMPro.TMP_Text>(visible[0],"Text").alpha-.5f)<.01f&&Math.Abs(Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y+150)<.01f,"one second entry animates upward with fade");
        CaptureFeed(canvas,feed.gameObject,"experience-entry-fr");
        advance(1);Check(Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y==0,"entry reaches assigned slot after one second");
        advance(3);Check(Field<TMPro.TMP_Text>(visible[0],"Text").alpha==1,"line remains fully visible for two seconds after arrival");
        CaptureFeed(canvas,feed.gameObject,"experience-feed-fr");
        advance(3.5f);Check(Math.Abs(Field<TMPro.TMP_Text>(visible[0],"Text").alpha-.5f)<.01f,"exit fades over one full second");
        advance(4);Check(visible.Count==3&&pending.Count==0&&Field<float>(visible[0],"Born")==4,"queued gains start complete four-second lifetime only when space becomes free");
        advance(8);Check(visible.Count==0,"all rewards expire after entry hold and exit phases");
        pending.Enqueue("Premier: 10");advance(10);pending.Enqueue("Second: 10");advance(11);advance(13.99f);
        Check(Field<TMPro.TMP_Text>(visible[1],"Text").rectTransform.anchoredPosition.y==-28,"second row keeps its slot until first row fully disappears");
        advance(14);Check(visible.Count==1&&Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y==-28,"removing first row begins reflow without snapping");
        advance(14.15f);Check(Math.Abs(Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y+14)<.02f,"remaining row smoothly moves upward into freed slot");
        advance(14.31f);Check(Field<TMPro.TMP_Text>(visible[0],"Text").rectTransform.anchoredPosition.y==0,"remaining row reaches freed slot");
        var previous=MessageHud.instance;var hud=Component<MessageHud>("level-up-test");MessageHud.m_instance=hud;
        Call(type,"Reward","Greyling",0,10L,19,21);
        Check(hud.m_biomeFoundQueue.Count==2,"level gains use native biome notification queue once per earned level");
        Call(type,"Reward","Greyling",0,10L,21,21);Check(hud.m_biomeFoundQueue.Count==2,"no duplicate level notification when level is unchanged");
        MessageHud.m_instance=previous;UnityEngine.Object.DestroyImmediate(hud.gameObject);
        UnityEngine.Object.DestroyImmediate(canvasObject);UnityEngine.Object.DestroyImmediate(font);
    }
    static void CaptureFeed(Canvas canvas,GameObject root,string name)
    {
        var cameraObject=new GameObject("PreviewCamera",typeof(Camera));var camera=cameraObject.GetComponent<Camera>();
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.044f,.048f);camera.orthographic=true;camera.orthographicSize=540;camera.transform.position=new Vector3(0,0,-10);camera.cullingMask=1<<30;
        canvas.gameObject.layer=30;canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;
        foreach(var t in root.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        var render=new RenderTexture(1920,1080,24);camera.targetTexture=render;Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=render;
        var image=new Texture2D(1920,1080,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1920,1080),0,0);image.Apply();
        File.WriteAllBytes("../../../Tools/OverhaulWork/Leveling/"+name+".png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(cameraObject);UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(render);
    }
}

