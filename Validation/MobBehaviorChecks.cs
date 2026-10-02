using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class MobBehaviorChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Assembly mod;static Type config,behavior;static readonly List<string> report=new List<string>();static GameObject host;static ZDOMan manager;static int attacks,moves,straightMoves;static Vector3 destination;static bool path=true,baseRun=true;static float seenView,seenCircle;static ItemDrop.ItemData selected;
    static object Call(Type type,string name,params object[] args)=>type.GetMethod(name,F).Invoke(null,args);
    static Animator DefenseAnimator(Transform parent,bool blocking,bool dodge)
    {
        var controller=new UnityEditor.Animations.AnimatorController();controller.AddLayer("Base");controller.layers[0].stateMachine.AddState("idle");
        if(blocking)controller.AddParameter("blocking",AnimatorControllerParameterType.Bool);
        if(dodge)controller.AddParameter("dodge",AnimatorControllerParameterType.Trigger);
        var go=new GameObject("defense animator");var animator=go.AddComponent<Animator>();animator.runtimeAnimatorController=controller;animator.Rebind();animator.Update(0);go.transform.SetParent(parent,false);return animator;
    }
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);report.Add("PASS "+label);}
    static bool BaseUpdate(BaseAI __instance,ref bool __result){seenView=__instance.m_viewRange;seenCircle=__instance is MonsterAI m?m.m_circleTargetInterval:-1;__result=baseRun;return false;}
    static bool Visible(ref bool __result){__result=true;return false;}
    static Func<Vector3,bool> escapePath;
    static bool HasPath(Vector3 target,ref bool __result){__result=path&&(escapePath==null||escapePath(target));return false;}
    static bool Move(Vector3 point,ref bool __result){moves++;destination=point;__result=false;return false;}
    static bool Straight(Vector3 dir,bool run){straightMoves++;destination=dir;return false;}
    static bool Start(ref bool __result){attacks++;__result=true;return false;}
    static bool Target(ref bool canHearTarget,ref bool canSeeTarget){canHearTarget=canSeeTarget=true;return false;}
    static bool Select(ref ItemDrop.ItemData __result){__result=selected;return false;}
    static int horns;
    static bool Horn(){horns++;return false;}
    static int orbits;
    static bool Orbit(){orbits++;return false;}
    static bool Equip(Humanoid __instance,ItemDrop.ItemData item,ref bool __result){__instance.m_rightItem=item;__result=true;return false;}
    static bool Skip()=>false;
    static Character swingVictim;static bool swingHits;
    static bool Swing(Attack __instance){if(swingHits){var hit=new HitData(10);hit.SetAttacker(__instance.m_character);swingVictim.Damage(hit);}return false;}
    static T New<T>(string name)where T:Component{var go=new GameObject(name);go.transform.SetParent(host.transform);return go.AddComponent<T>();}
    static Humanoid Body(string name,Character.Faction faction)
    {
        var body=New<Humanoid>(name);body.m_nview=body.gameObject.AddComponent<ZNetView>();body.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,name.GetStableHashCode());body.m_nview.m_zdo.SetOwner(ZNet.GetUID());body.m_nview.m_zdo.SetPrefab(name.GetStableHashCode());body.m_faction=faction;body.m_animator=body.gameObject.AddComponent<Animator>();body.m_cachedAnimHashFrame=MonoUpdaters.UpdateCount;body.m_cachedCurrentAnimHash=0;body.m_cachedNextAnimHash=0;body.m_timeSinceLastAttack=100;body.m_collider=body.gameObject.AddComponent<CapsuleCollider>();body.m_eye=body.transform;body.m_visual=body.gameObject;body.m_health=100;body.SetMaxHealth(100);body.SetHealth(100);body.m_seman=new SEMan(body,body.m_nview);return body;
    }
    static MonsterAI Mob(string name,Humanoid target)
    {
        var body=Body(name,Character.Faction.ForestMonsters);var ai=body.gameObject.AddComponent<MonsterAI>();ai.m_character=body;ai.m_nview=body.m_nview;body.m_baseAI=ai;ai.m_animator=body.gameObject.AddComponent<ZSyncAnimation>();ai.m_animator.m_animator=body.m_animator;ai.m_animator.m_nview=body.m_nview;ai.m_targetCreature=target;ai.m_alerted=true;ai.m_viewRange=50;ai.m_hearRange=40;ai.m_alertRange=20;ai.m_circleTargetInterval=4;ai.m_circulateWhileCharging=true;ai.m_circulateWhileChargingFlying=true;body.m_rightItem=selected;BaseAI.m_instances.Add(ai);ZNetScene.instance.m_instances[body.m_nview.GetZDO()]=body.m_nview;return ai;
    }
    static void Rules(string text){config.GetField("Current",F).SetValue(null,Call(config,"Parse",text));foreach(var ai in BaseAI.GetAllInstances())if(ai&&ai.m_nview&&ai.m_nview.IsValid())ai.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",-1);}
    static object State(MonsterAI ai){var table=behavior.GetField("States",F).GetValue(null);return table.GetType().GetMethod("GetOrCreateValue").Invoke(table,new object[]{ai});}
    static void Set(object state,string field,object value)=>state.GetType().GetField(field,F).SetValue(state,value);
    static double Now=>(double)behavior.GetProperty("Now",F).GetValue(null);
    public static void Run(){try{Test();report.Add("PASS assertions="+report.Count+" package="+mod.GetName().Version);File.WriteAllLines("../../../Tools/AugaWork/mob-behavior-checks.txt",report);}catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/mob-behavior-checks.txt",report);throw;}}
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));config=mod.GetType("Overhaul.AI.MobBehaviorConfig");behavior=mod.GetType("Overhaul.AI.MobBehavior");
        var defaults=(IDictionary)Call(config,"Parse",File.ReadAllText("../../../Packages/Overhaul/MobBehaviors.cfg"));
        var english="[Wolf]\nAggressiveness = aggressive\nIntelligence = dumb\nGroupBehavior = pack\nCharge = true\n[Boar]\nAggressiveness = cunning\nIntelligence = smart";
        var french="[Wolf]\nAggressiveness = agressif\nIntelligence = debile\nGroupBehavior = meute\nCharge = oui\n[Boar]\nAggressiveness = vicieux\nIntelligence = intelligent";
        var formatted=(string)Call(config,"Format",Call(config,"Parse",english));Check(formatted==(string)Call(config,"Format",Call(config,"Parse",french)),"legacy French and canonical English settings have identical behavior");Check(formatted.Contains("Charge = true")&&formatted.Contains("Aggressiveness = aggressive")&&formatted.Contains("GroupBehavior = pack")&&formatted.Contains("Intelligence = smart"),"serialization writes only English options");
        var names=File.ReadAllLines("../../../Tools/AugaWork/native-mob-names.txt");Check(defaults.Count==names.Length&&names.All(n=>defaults.Contains(n)),"every inventoried native creature has a section");
        Check(defaults.Keys.Cast<string>().All(n=>(float)defaults[n].GetType().GetField("DumbChance",F).GetValue(defaults[n])==(n=="Deathsquito"?0:n=="Greyling"?15:7)),"all shipped dumb chances match species defaults");
        Rules("[Greydwarf]\nAggressiveness = vanilla\nIntelligence = vanilla\nGroupBehavior = vanilla");Check(!(bool)config.GetProperty("Enabled",F).GetValue(null),"explicit vanilla leaves native intelligence unchanged");
        foreach(string bad in new[]{"[Wolf]\nIntelligence = genius","[Wolf]\nGroupBehavior = mega","[Wolf]\nAggressiveness = violent","[Wolf]\n[Wolf]"}){bool fail=false;try{Call(config,"Parse",bad);}catch(TargetInvocationException e){fail=e.InnerException is InvalidDataException;}Check(fail,"invalid setting refused: "+bad.Replace('\n',' '));}
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.mob.check"});
        var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        void Hook(MethodBase method,string handler){patch.Invoke(harmony,new object[]{method,Activator.CreateInstance(hm,new object[]{typeof(MobBehaviorChecks).GetMethod(handler,F)}),null,null,null});}
        foreach(var t in mod.GetTypes().Where(t=>t.Namespace=="Overhaul.AI"&&t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var p=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});p.GetType().GetMethod("Patch").Invoke(p,null);}
        // Include guardian speed patches: both transpilers must coexist on UpdateAI.
        foreach(var t in mod.GetType("Overhaul.Dungeons.BossAttackSpeed").GetNestedTypes(F).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var p=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});p.GetType().GetMethod("Patch").Invoke(p,null);}
        Hook(typeof(BaseAI).GetMethod("IdleMovement",F),"Skip");Hook(typeof(BaseAI).GetMethod("UpdateAI"),"BaseUpdate");Hook(typeof(BaseAI).GetMethod("CanSeeTarget",new[]{typeof(Character)}),"Visible");Hook(typeof(BaseAI).GetMethod("HavePath",F),"HasPath");Hook(typeof(BaseAI).GetMethod("MoveTo"),"Move");Hook(typeof(BaseAI).GetMethod("MoveTowards"),"Straight");Hook(typeof(MonsterAI).GetMethod("UpdateTarget",F),"Target");Hook(typeof(MonsterAI).GetMethod("UpdateSleep",F),"Skip");Hook(typeof(MonsterAI).GetMethod("SelectBestAttack",F),"Select");Hook(typeof(Humanoid).GetMethod("StartAttack",new[]{typeof(Character),typeof(bool)}),"Start");
        host=new GameObject("inactive AI fixtures");host.SetActive(false);var net=New<ZNet>("net");ZNet.m_instance=net;typeof(Game).GetProperty("instance",F).SetValue(null,New<Game>("game"));new ZRoutedRpc(true);manager=new ZDOMan(512);ZNetScene.s_instance=New<ZNetScene>("scene");
        selected=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon,m_skillType=Skills.SkillType.Clubs,m_aiAttackRange=3,m_aiAttackMaxAngle=181,m_aiAttackInterval=1,m_blockPower=10}};selected.m_lastAttackTime=-100;
        var target=Body("player-target",Character.Faction.Players);ZNetScene.instance.m_instances[target.m_nview.GetZDO()]=target.m_nview;
        var ai=Mob("Greydwarf",target);ai.transform.position=Vector3.forward*2;ai.transform.rotation=Quaternion.LookRotation(Vector3.back);
        try{
            Rules("[Greydwarf]\nIntelligence = smart\nDumbChance = 100");Check(Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)).ToString()=="Dumb","100 percent chance assigns dumb");
            var savedPacket=new ZPackage();ai.m_nview.GetZDO().Serialize(savedPacket);var restored=manager.CreateNewZDO(Vector3.zero,0);restored.Deserialize(new ZPackage(savedPacket.GetArray()));Check(restored.GetInt("overhaul_ai_intelligence_v1",-1)==1,"native ZDO serialization preserves assigned intelligence");
            config.GetField("Current",F).SetValue(null,Call(config,"Parse","[Greydwarf]\nIntelligence = smart\nDumbChance = 0"));Check(ai.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==1&&Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)).ToString()=="Dumb","stored intelligence survives configuration changes without reroll");
            ai.m_nview.GetZDO().SetOwner(ZNet.GetUID()+1);Check(Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)).ToString()=="Dumb","remote owner reads persisted intelligence");ai.m_nview.GetZDO().SetOwner(ZNet.GetUID());
            ai.m_nview.GetZDO().Set("overhaul_boss_character_v1".GetStableHashCode(),true);Check(Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)).ToString()=="Smart"&&ai.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==2,"dungeon boss replaces prior dumb intelligence with persisted smart");ai.m_nview.GetZDO().Set("overhaul_boss_character_v1".GetStableHashCode(),false);
            Rules("[Greydwarf]\nIntelligence = smart\nDumbChance = 10");UnityEngine.Random.InitState(341);int dumbCount=0;for(int i=0;i<10000;i++){ai.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",-1);Call(config,"Rule",ai.m_character);if(ai.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==1)dumbCount++;}Check(dumbCount>850&&dumbCount<1150,"ten percent chance observed across independent spawns: "+dumbCount);
            foreach(var invalid in new[]{"-1","101","NaN","Infinity"}){bool rejected=false;try{Call(config,"Parse","[Wolf]\nDumbChance = "+invalid);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"invalid DumbChance refused: "+invalid);}
            var chanceText=(string)Call(config,"Format",Call(config,"Parse","[Wolf]\nIntelligence = smart\nDumbChance = 10.5"));Check(chanceText.Contains("DumbChance = 10.5"),"chance survives server serialization");
            string IntelligenceOf()=>Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)).ToString();
            foreach(int level in new[]{1,2,3})
            {
                Rules("[Greydwarf]\nIntelligence = normal\nDumbChance = 7");ai.m_character.m_level=level;int smart=0,dumbStars=0;
                for(int i=0;i<10000;i++){ai.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",-1);ai.m_nview.GetZDO().Set("overhaul_ai_star_roll_v1",-1f);var intelligence=IntelligenceOf();if(intelligence=="Smart")smart++;if(intelligence=="Dumb")dumbStars++;}
                Check(dumbStars>580&&dumbStars<820,"dumb stays at seven percent regardless of stars, level="+level+" count="+dumbStars);
                Check(level==1?smart==0:level==2?smart>1700&&smart<2020:smart>4440&&smart<4850,"star promotion with dumb priority, level="+level+" smart="+smart);
            }
            ai.m_character.m_level=1;Rules("[Greydwarf]\nIntelligence = normal");ai.m_nview.GetZDO().Set("overhaul_ai_star_roll_v1",.1f);Check(IntelligenceOf()=="Normal","zero-star initial Awake stays normal");ai.m_character.m_level=2;Check(IntelligenceOf()=="Smart","star assignment after Awake uses the persisted roll");
            ai.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",1);ai.m_character.m_level=3;Check(IntelligenceOf()=="Dumb","stars never promote a stored dumb mob");ai.m_character.m_level=1;

            baseRun=false;Rules("[Greydwarf]\nAggressiveness = normal");ai.UpdateAI(.1f);Check(seenView==50&&seenCircle==4&&ai.m_circulateWhileCharging,"normal leaves native AI settings untouched");
            Rules("[Greydwarf]\nAggressiveness = agressif\nIntelligence = debile");ai.UpdateAI(.1f);Check(Mathf.Approximately(seenView,30)&&seenCircle==0,"aggressive disables circling and dumb reduces perception by forty percent: view="+seenView+" circle="+seenCircle+" active="+Call(behavior,"Active",ai)+" name="+Utils.GetPrefabName(ai.gameObject)+" rule="+Call(config,"Rule",ai.m_character).GetType().GetField("Intelligence",F).GetValue(Call(config,"Rule",ai.m_character)));Check(ai.m_viewRange==50&&ai.m_hearRange==40&&ai.m_circleTargetInterval==4&&ai.m_circulateWhileCharging,"native AI settings restored after update");
            ai.m_nview.GetZDO().SetOwner(ZNet.GetUID()+1);ai.UpdateAI(.1f);Check(seenView==50&&seenCircle==4,"remote owner never applies modifiers twice");ai.m_nview.GetZDO().SetOwner(ZNet.GetUID());
            ai.m_targetCreature=target;Rules("[Greydwarf]\nAggressiveness = agressif");baseRun=true;attacks=0;ai.UpdateAI(.1f);Check(attacks==1,"aggressive native UpdateAI proceeds to direct attack with both transpilers installed");
            Rules("[Greydwarf]\nAggressiveness = vicieux");var state=State(ai);Set(state,"FlankUntil",Now+2.5);moves=attacks=0;ai.UpdateAI(.1f);Check(moves==1&&attacks==0&&destination.z<target.transform.position.z,"cunning native UpdateAI chooses a reachable rear flank");
            path=false;ai.UpdateAI(.1f);Check(attacks==1,"unreachable flank falls back to native direct attack");path=true;
            Rules("[Greydwarf]\nCharge = oui");Set(state,"Target",target);Set(state,"ReactUntil",0d);ai.transform.position=Vector3.forward*12;moves=straightMoves=attacks=0;
            ai.UpdateAI(.1f);Check(straightMoves==1&&moves==0&&attacks==0&&destination.z<0,"charge at twelve metres runs straight toward target");
            selected.m_lastAttackTime=Time.time;ai.m_minAttackInterval=999;((Humanoid)ai.m_character).m_timeSinceLastAttack=0;ai.transform.position=Vector3.forward*2;ai.UpdateAI(.1f);Check(attacks==1,"charge arrival triggers native attack despite ordinary cooldown");
            Check(!(bool)state.GetType().GetField("Charging",F).GetValue(state),"successful attack ends charge");
            Set(state,"ChargeReady",true);straightMoves=0;Call(behavior,"Combat",ai,.1f);Check(straightMoves==0,"charge does not start below ten metres");
            ai.transform.position=Vector3.forward*10;Call(behavior,"Combat",ai,.1f);Check(straightMoves==1,"charge starts at exactly ten metres");Set(state,"ChargeUntil",Now-1);Call(behavior,"Combat",ai,.1f);Check(!(bool)state.GetType().GetField("Charging",F).GetValue(state),"blocked charge times out to ordinary behavior");
            ai.transform.position=Vector3.forward*2;selected.m_lastAttackTime=-100;ai.m_minAttackInterval=0;((Humanoid)ai.m_character).m_timeSinceLastAttack=100;
            Rules("[Greydwarf]\nIntelligence = dumb\nAggressiveness = cunning\nGroupBehavior = helper\nCharge = true");Set(state,"Target",null);attacks=moves=0;ai.UpdateAI(.1f);Check(attacks==0&&ai.m_targetCreature==null,"dumb mob does not acquire a target or attack first");ai.SetAlerted(true);Check(!ai.IsAlerted(),"dumb mob ignores combat alert before direct damage");
            ai.DoAttack(target,false);Check(attacks==0,"direct native attack cannot bypass dumb non-aggression");
            var alarm=behavior.GetNestedType("Alarm",F).GetMethod("Postfix",F);alarm.Invoke(null,new object[]{ai,10f,target});ai.SetAlerted(true);Check(ai.m_targetCreature==target,"damage grants retaliation against the attacker");
            ai.UpdateAI(.1f);Check(attacks==1&&moves==0,"dumb retaliates immediately without added delay or cunning flank");
            ((Humanoid)ai.m_character).m_blocking=true;Check(!((Humanoid)ai.m_character).IsBlocking(),"dumb never blocks even when the native blocking flag is set");
            ai.UpdateAI(.1f);Check(!((Humanoid)ai.m_character).m_blocking,"dumb update clears native blocking intent");
            var innocent=Body("innocent-target",Character.Faction.Players);int before=attacks;ai.DoAttack(innocent,false);Check(attacks==before,"retaliation does not permit attacking an uninvolved target");
            ai.m_targetCreature=null;ai.UpdateAI(.1f);Check(ai.m_nview.GetZDO().GetZDOID("overhaul_ai_retaliation")==ZDOID.None,"losing combat target clears retaliation permission");
            Check(mod.GetType("Overhaul.AI.AnimalReaction")==null,"dumb animals have no added flee reaction delay");ai.m_targetCreature=target;ai.m_alerted=true;

            ai.m_character.m_animator=DefenseAnimator(ai.transform,true,false);
            Rules("[Greydwarf]\nIntelligence = intelligent");Set(state,"ReactUntil",0d);Set(state,"Target",target);target.m_cachedCurrentAnimHash=Humanoid.s_animatorTagAttack;UnityEngine.Random.InitState(5821);int blocks=0;
            for(int i=0;i<1000;i++){Set(state,"TargetAttacking",false);Set(state,"BlockUntil",0d);Call(behavior,"Combat",ai,.1f);if(((Humanoid)ai.m_character).m_blocking)blocks++;Call(behavior,"ReleaseBlock",ai.m_character,state);}
            Check(blocks>740&&blocks<860,"smart mob attempts native blocking about eighty percent of attack starts: "+blocks);
            Rules("[Greydwarf]\nIntelligence = normal");blocks=0;UnityEngine.Random.InitState(5821);for(int i=0;i<1000;i++){Set(state,"TargetAttacking",false);Set(state,"BlockUntil",0d);Call(behavior,"Combat",ai,.1f);if(((Humanoid)ai.m_character).m_blocking)blocks++;Call(behavior,"ReleaseBlock",ai.m_character,state);}Check(blocks>290&&blocks<410,"normal mob attempts blocking about thirty-five percent of starts: "+blocks);Rules("[Greydwarf]\nIntelligence = smart");
            Set(state,"BlockUntil",Now+1);Set(state,"TargetAttacking",true);Call(behavior,"Combat",ai,.1f);Set(state,"BlockUntil",Now-1);target.m_cachedCurrentAnimHash=0;Call(behavior,"Combat",ai,.1f);Check(!((Humanoid)ai.m_character).m_blocking,"blocking ends and movement can resume");
            var defense=mod.GetType("Overhaul.AI.MobDefense");var body=(Humanoid)ai.m_character;var blockAnimator=body.m_animator;
            body.m_animator=DefenseAnimator(ai.transform,false,false);
            Check(!(bool)Call(defense,"CanBlock",body),"weapon block power without blocking animation uses retreat");
            target.m_cachedCurrentAnimHash=Humanoid.s_animatorTagAttack;int retreats=0;
            foreach(var mode in new[]{"normal","smart"})
            {
                Rules("[Greydwarf]\nIntelligence = "+mode);UnityEngine.Random.InitState(5821);retreats=0;
                for(int i=0;i<1000;i++){Set(state,"TargetAttacking",false);Call(behavior,"Combat",ai,.1f);if((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)>Now)retreats++;Call(behavior,"ReleaseBlock",body,state);}
                Check(mode=="normal"?retreats>290&&retreats<410:retreats>740&&retreats<860,mode+" defense probability also applies to retreat: "+retreats);
            }
            Call(defense,"Start",ai,body,target,state);Check((bool)Call(defense,"Retreat",ai,body,target,state)&&Vector3.Dot(body.m_moveDir,(body.transform.position-target.transform.position).normalized)>.99f,"fallback moves away from enemy");
            Check((bool)typeof(Character).GetMethod("AlwaysRotateCamera",F).Invoke(body,null),"retreat keeps facing enemy via native rotation");
            Check(Mathf.Approximately((float)typeof(Character).GetMethod("GetRunSpeedFactor",F).Invoke(body,null),1.5f),"retreat speed boost is scoped to active dodge");
            Check(!ai.DoAttack(target,false),"cannot start an attack while retreating");
            ai.m_nview.GetZDO().SetOwner(ZNet.GetUID()+1);Check(!(bool)Call(defense,"Retreating",body),"remote owner never applies retreat speed or rotation");ai.m_nview.GetZDO().SetOwner(ZNet.GetUID());
            Call(behavior,"ReleaseBlock",body,state);Check(body.m_moveDir==Vector3.zero&&Mathf.Approximately((float)typeof(Character).GetMethod("GetRunSpeedFactor",F).Invoke(body,null),1),"cancel restores movement and speed");
            Set(state,"TargetAttacking",true);for(int i=0;i<100;i++)Call(behavior,"Combat",ai,.1f);Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)==0,"one probability roll per observed attack, no repeated dodge during held attack");
            Call(defense,"Start",ai,body,target,state);Call(defense,"Retreat",ai,body,target,state);Set(state,"EvadeUntil",Now-1);Call(behavior,"Combat",ai,.1f);Check(body.m_moveDir==Vector3.zero&&!(bool)Call(defense,"Retreating",body),"expired retreat ends without leaving movement intent");
            Call(defense,"Start",ai,body,target,state);ai.m_targetCreature=null;Call(behavior,"Combat",ai,.1f);Check(!(bool)Call(defense,"Retreating",body)&&body.m_moveDir==Vector3.zero,"losing target cancels retreat");ai.m_targetCreature=target;Set(state,"Target",target);
            path=false;Call(defense,"Start",ai,body,target,state);Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)==0,"no retreat onto inaccessible terrain");path=true;
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=body.GetCenterPoint()+Vector3.forward;wall.transform.localScale=new Vector3(4,4,.2f);Physics.SyncTransforms();Call(defense,"Start",ai,body,target,state);
            Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)>Now&&Mathf.Abs(((Vector3)state.GetType().GetField("EvadeDirection",F).GetValue(state)).x)>.99f,"blocked rear chooses a clear lateral dodge");Call(behavior,"ReleaseBlock",body,state);
            var left=GameObject.CreatePrimitive(PrimitiveType.Cube);left.transform.position=body.GetCenterPoint()+Vector3.left;left.transform.localScale=new Vector3(.2f,4,4);Physics.SyncTransforms();
            for(int i=0;i<20;i++){Call(defense,"Start",ai,body,target,state);Check(((Vector3)state.GetType().GetField("EvadeDirection",F).GetValue(state)).x>.99f,"only right side open chooses right "+i);Call(behavior,"ReleaseBlock",body,state);}
            var right=GameObject.CreatePrimitive(PrimitiveType.Cube);right.transform.position=body.GetCenterPoint()+Vector3.right;right.transform.localScale=new Vector3(.2f,4,4);Physics.SyncTransforms();Call(defense,"Start",ai,body,target,state);Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)==0,"no movement when rear and both sides blocked");UnityEngine.Object.DestroyImmediate(left);UnityEngine.Object.DestroyImmediate(right);UnityEngine.Object.DestroyImmediate(wall);
            var dodgeAnimator=DefenseAnimator(ai.transform,false,true);Check((string)Call(defense,"EvadeTrigger",dodgeAnimator)=="dodge","available native dodge trigger selected");Check(Call(defense,"EvadeTrigger",body.m_animator)==null,"missing dodge animation selects movement fallback");
            Rules("[Greydwarf]\nIntelligence = dumb");Set(state,"TargetAttacking",false);Call(behavior,"Combat",ai,.1f);Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)==0&&!body.m_blocking,"dumb never defends");
            Rules("[Greydwarf]\nIntelligence = vanilla");Call(behavior,"Combat",ai,.1f);Check((double)state.GetType().GetField("EvadeUntil",F).GetValue(state)==0,"vanilla has no added defense");
            body.m_animator=blockAnimator;Rules("[Greydwarf]\nIntelligence = smart");target.m_cachedCurrentAnimHash=0;
            target.m_nview.GetZDO().Set("overhaul_ai_miss",(long)(Now*1000));Set(state,"LastMiss",0d);selected.m_lastAttackTime=Time.time;ai.m_minAttackInterval=20;((Humanoid)ai.m_character).m_timeSinceLastAttack=0;attacks=0;ai.UpdateAI(.1f);Check(attacks==1,"intelligent mob exploits a recent missed swing despite its ordinary cooldown");Check(ai.m_minAttackInterval==20,"punish window restores configured attack interval");
            selected.m_lastAttackTime=-100;ai.m_minAttackInterval=0;((Humanoid)ai.m_character).m_timeSinceLastAttack=100;
            var friend=Mob("Greydwarf_Elite",target);friend.transform.position=Vector3.right*3;
            Rules("[Greydwarf]\nGroupBehavior = helper");ai.m_targetCreature=null;Set(state,"NextHelp",0d);friend.m_nview.GetZDO().Set("overhaul_ai_alarm",target.GetZDOID());friend.m_nview.GetZDO().Set("overhaul_ai_alarm_time",(long)((Now+4)*1000));Call(behavior,"Help",ai,state);Check(ai.m_targetCreature==target,"helper acquires the attacker of a nearby ally");
            Check(!(bool)Call(behavior,"Allies",ai.m_character,target),"hostile target is never treated as ally");
            var third=Mob("Greydwarf_Shaman",target);third.transform.position=Vector3.left*3;
            Rules("[Greydwarf]\nGroupBehavior = meute\n[Greydwarf_Elite]\nGroupBehavior = meute\n[Greydwarf_Shaman]\nGroupBehavior = meute");
            foreach(var m in new[]{ai,friend,third}){Set(State(m),"NextPack",0d);Call(behavior,"ObserveTarget",m,State(m),Call(config,"Rule",m.m_character));}
            var packType=mod.GetType("Overhaul.AI.MobPack");var members=new[]{ai,friend,third};
            foreach(var m in members){m.transform.position=target.transform.position+Vector3.forward*8;Call(behavior,"RefreshPack",m,State(m));}
            var leader=((IEnumerable)Call(behavior,"Members",ai,target)).Cast<MonsterAI>().First();var data=leader.m_nview.GetZDO();
            Check(members.All(m=>!(bool)Call(packType,"Allowed",m,State(m))),"pack gathers before opening an assault");
            moves=0;Call(behavior,"Combat",ai,.1f);Check(moves==1,"pack approaches encirclement before anyone attacks");
            var slots=members.Select(m=>(Vector3)Call(packType,"Slot",m,State(m))).ToArray();
            Check(Vector3.Distance(slots[0],slots[1])>2&&Vector3.Distance(slots[1],slots[2])>2,"members receive separated positions around prey");
            target.transform.rotation=Quaternion.Euler(0,120,0);Check((Vector3)Call(packType,"Slot",ai,state)==slots[0],"prey rotation does not rotate encirclement slots");
            foreach(var m in members){m.transform.position=(Vector3)Call(packType,"Slot",m,State(m));Set(State(m),"NextPackReady",0d);Call(behavior,"Combat",m,.1f);}
            Call(packType,"Allowed",leader,State(leader));Check(data.GetLong("overhaul_pack_end",0)/1000d>Now,"two positioned allies trigger a shared assault window");
            Check(members.All(m=>!(bool)Call(packType,"Allowed",m,State(m))),"replicated assault has a brief synchronization lead time");
            ZNet.instance.m_netTime+=.3;attacks=0;foreach(var m in members)m.DoAttack(target,false);Check(attacks==3,"all three pack members can attack during the same opening");
            friend.m_character.m_cachedCurrentAnimHash=Humanoid.s_animatorTagAttack;Check((bool)Call(packType,"Allowed",ai,state),"an ally attacking no longer locks other members");friend.m_character.m_cachedCurrentAnimHash=0;
            ZNet.instance.m_netTime+=3;foreach(var m in members){m.transform.position=target.transform.position+Vector3.forward*8;m.m_nview.GetZDO().Set("overhaul_pack_ready",(long)((Now-2)*1000));}
            data.Set("overhaul_pack_gather",(long)((Now+4)*1000));Check(!(bool)Call(packType,"Allowed",ai,state),"expired readiness cannot trigger another coordinated attack");
            path=false;moves=0;Call(behavior,"Combat",ai,.1f);Check(moves==0,"blocked encirclement does not force movement through obstacles");
            ZNet.instance.m_netTime+=4.1;Call(packType,"Allowed",leader,State(leader));ZNet.instance.m_netTime+=.3;
            Check(members.All(m=>(bool)Call(packType,"Allowed",m,State(m))),"gathering timeout releases whole pack on blocked terrain");path=true;
            var remote=members.First(m=>m!=leader);data.SetOwner(ZNet.GetUID()+77);data.Set("overhaul_pack_end",0L);var grant=data.GetLong("overhaul_pack_start",0);Set(State(remote),"PackFallback",Now-1);
            Check((bool)Call(packType,"Allowed",remote,State(remote))&&data.GetLong("overhaul_pack_start",0)==grant,"remote leader is never written and stalled coordination has a fallback");data.SetOwner(ZNet.GetUID());
            var dumbMember=third;dumbMember.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",1);moves=0;Call(behavior,"Combat",dumbMember,.1f);Check(moves==0,"dumb member does not encircle or initiate combat");
            Set(state,"NextPack",0d);Check((bool)Call(packType,"Allowed",ai,state),"two tactical members plus a dumb mob do not start an encirclement");
            dumbMember.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",0);third.m_character.m_isDead=true;Set(state,"NextPack",0d);Check((bool)Call(packType,"Allowed",ai,state),"fewer than three living pack members return to ordinary attacks");
            baseRun=false;ai.UpdateAI(.1f);Check(seenCircle==4,"two-member pack retains vanilla circling");
            Rules("[Greydwarf]\nAggressiveness = normal");Check(!(bool)Call(behavior,"Combat",ai,.1f),"returning all settings to normal leaves native combat in control");
            Rules("[Greydwarf]\nIntelligence = intelligent");
            var attacker=New<Player>("swing-player");attacker.m_nview=attacker.gameObject.AddComponent<ZNetView>();attacker.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,0);attacker.m_nview.m_zdo.SetOwner(ZNet.GetUID());ZNetScene.instance.m_instances[attacker.m_nview.GetZDO()]=attacker.m_nview;
            var swing=new Attack{m_character=attacker};var melee=typeof(Attack).GetMethod("DoMeleeAttack",F);Hook(melee,"Swing");Hook(typeof(Character).GetMethod("Damage"),"Skip");swingVictim=ai.m_character;
            swingHits=false;melee.Invoke(swing,null);Check(attacker.m_nview.GetZDO().GetLong("overhaul_ai_miss",0)>0,"missed melee swing publishes a network opening");
            attacker.m_nview.GetZDO().Set("overhaul_ai_miss",0L);swingHits=true;melee.Invoke(swing,null);Check(attacker.m_nview.GetZDO().GetLong("overhaul_ai_miss",0)==0,"melee contact does not publish a false missed attack");
            attacker.m_nview.GetZDO().SetOwner(ZNet.GetUID()+77);swingHits=false;melee.Invoke(swing,null);Check(attacker.m_nview.GetZDO().GetLong("overhaul_ai_miss",0)==0,"remote peer cannot publish another player's missed attack");
            Check(mod.GetType("Overhaul.AI.MissedAttacks").GetField("Current",F).GetValue(null)==null,"melee scope cleared after hit processing");

            Rules("[DumbTest]\nIntelligence = dumb\nAggressiveness = aggressive");
            var victim=Body("followup-victim",Character.Faction.Players);var dumb=Mob("DumbTest",victim);dumb.transform.position=Vector3.back*2;
            var follow=mod.GetType("Overhaul.AI.DumbFollowup");var table=follow.GetField("States",F).GetValue(null);
            object FollowState()=>table.GetType().GetMethod("GetOrCreateValue").Invoke(table,new object[]{dumb});
            int Remaining()=>(int)FollowState().GetType().GetField("Remaining",F).GetValue(FollowState());
            dumb.m_nview.GetZDO().Set("overhaul_ai_retaliation",victim.GetZDOID());
            UnityEngine.Random.InitState(452);int repeats=0;
            for(int i=0;i<10000;i++){Call(follow,"Record",dumb,victim);repeats+=Remaining();}
            Check(repeats>1800&&repeats<2200,"twenty percent follow-ups across 10000 attacks: "+repeats);
            Call(follow,"Record",dumb,victim);var remembered=victim.GetCenterPoint();Set(FollowState(),"Remaining",1);victim.transform.position=Vector3.right*10;attacks=0;
            selected.m_lastAttackTime=Time.time;Call(behavior,"Combat",dumb,.1f);Check(attacks==0&&Remaining()==1,"follow-up respects native weapon cooldown");
            selected.m_lastAttackTime=-100;Call(behavior,"Combat",dumb,.1f);Check(attacks==1&&Remaining()==0,"one native follow-up consumes queue without recursively rolling another");
            var db=(Humanoid)dumb.m_character;Check(Vector3.Dot(db.GetAimDir(db.transform.position),(remembered-db.transform.position).normalized)>.999f,"melee aim stays at remembered point after target moves");
            var shot=new Attack{m_character=db,m_baseAI=dumb,m_useCharacterFacing=true};var shotArgs=new object[]{Vector3.zero,Vector3.zero};typeof(Attack).GetMethod("GetProjectileSpawnPoint",F).Invoke(shot,shotArgs);
            Check(Vector3.Dot((Vector3)shotArgs[1],(remembered-(Vector3)shotArgs[0]).normalized)>.999f,"native projectile aim cannot retarget the moved enemy");
            Set(FollowState(),"Started",Now-1);Check(!(bool)Call(follow,"Tick",dumb,Call(config,"Rule",db)),"completed follow-up returns control to native AI");
            var death=typeof(Character).GetMethod("OnDeath",F);Hook(death,"Skip");bool sawOne=false,sawTwo=false;
            for(int i=0;i<30;i++)
            {
                Call(follow,"Record",dumb,victim);death.Invoke(victim,null);int count=Remaining();sawOne|=count==1;sawTwo|=count==2;Check(count>=1&&count<=2,"death queues one or two swings "+i);
                death.Invoke(victim,null);Check(Remaining()==count,"duplicate death event does not reroll "+i);
                dumb.m_targetCreature=null;victim.m_isDead=true;attacks=0;baseRun=true;
                for(int j=0;j<count;j++){Set(FollowState(),"Started",Now-1);dumb.UpdateAI(.1f);}
                Check(attacks==count&&Remaining()==0,"corpse follow-ups execute after native target cleared "+i);
                Set(FollowState(),"Started",Now-1);Check(!(bool)Call(behavior,"Combat",dumb,.1f),"corpse queue ends without endless attacks "+i);
                victim.m_isDead=false;dumb.m_targetCreature=victim;
            }
            Check(sawOne&&sawTwo,"corpse swing count includes both one and two");
            Call(follow,"Record",dumb,victim);Set(FollowState(),"Remaining",1);dumb.m_nview.GetZDO().SetOwner(ZNet.GetUID()+7);attacks=0;Call(behavior,"Combat",dumb,.1f);Check(attacks==0,"remote peer never executes follow-up");dumb.m_nview.GetZDO().SetOwner(ZNet.GetUID());
            db.m_boss=true;Check((bool)Call(config,"Excluded",db)&&!(bool)Call(behavior,"Active",dumb),"native boss excluded even with saved dumb intelligence");Call(behavior,"Combat",dumb,.1f);Check(attacks==0,"native boss never executes queued dumb follow-up");db.m_boss=false;
            dumb.m_nview.GetZDO().Set("overhaul_boss_character_v1".GetStableHashCode(),true);Check(!(bool)Call(config,"Excluded",db)&&(bool)Call(behavior,"Active",dumb),"dungeon guardian participates in behaviors");
            Rules("[DumbTest]\nIntelligence = dumb\nDumbChance = 100\nAggressiveness = cunning\nGroupBehavior = pack\nCharge = true");
            var guardian=Call(config,"Rule",db);var gt=guardian.GetType();
            Check(gt.GetField("Intelligence",F).GetValue(guardian).ToString()=="Smart"&&db.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==2,"guardian always smart despite 100 percent dumb chance");
            Check(gt.GetField("Aggression",F).GetValue(guardian).ToString()=="Cunning"&&gt.GetField("Group",F).GetValue(guardian).ToString()=="Pack"&&(bool)gt.GetField("Charge",F).GetValue(guardian),"guardian inherits species aggression group and charge");
            db.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",1);db.m_nview.GetZDO().SetOwner(ZNet.GetUID()+7);guardian=Call(config,"Rule",db);
            Check(gt.GetField("Intelligence",F).GetValue(guardian).ToString()=="Smart"&&db.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==1,"remote guardian uses smart without writing another owner ZDO");
            db.m_nview.GetZDO().SetOwner(ZNet.GetUID());Call(config,"Rule",db);
            Check(db.m_nview.GetZDO().GetInt("overhaul_ai_intelligence_v1",-1)==2,"guardian owner persists corrected smart intelligence");
            var species=mod.GetType("Overhaul.AI.MobSpecies");
            var mosquito=Mob("Deathsquito",target);var mosquitoBody=(Humanoid)mosquito.m_character;
            Rules("[Deathsquito]\nAggressiveness = vanilla\nIntelligence = normal\nDumbChance = 0");
            var originalWeapon=selected;selected=originalWeapon.Clone();selected.m_shared=(ItemDrop.ItemData.SharedData)typeof(object).GetMethod("MemberwiseClone",F).Invoke(originalWeapon.m_shared,null);selected.m_shared.m_aiAttackInterval=10;var nativeShared=selected.m_shared;mosquitoBody.m_rightItem=selected;
            mosquito.SelectBestAttack(mosquitoBody,.1f);mosquito.SelectBestAttack(mosquitoBody,.1f);
            Check(selected.m_shared.m_aiAttackInterval==5&&nativeShared.m_aiAttackInterval==10&&originalWeapon.m_shared.m_aiAttackInterval==1,"sting cooldown halved once without changing shared prefabs or other weapons");
            Hook(typeof(BaseAI).GetMethod("RandomMovementArroundPoint",F),"Orbit");mosquito.m_circleTargetInterval=0;mosquito.m_circulateWhileCharging=true;mosquito.m_circulateWhileChargingFlying=true;mosquito.transform.position=target.transform.position+Vector3.forward*2;mosquito.transform.rotation=Quaternion.LookRotation(Vector3.back);baseRun=true;
            selected.m_lastAttackTime=Time.time;orbits=attacks=0;mosquito.UpdateAI(.1f);Check(orbits==1&&attacks==0,"mosquito uses native circling while its sting is recovering");
            selected.m_lastAttackTime=Time.time-5.1f;orbits=attacks=0;mosquito.UpdateAI(.1f);Check(orbits==0&&attacks==1,"mosquito returns to attack after five seconds instead of ten");
            foreach(float armor in new[]{0f,20f,100f,200f})
            {
                var sting=new HitData();sting.m_damage.m_pierce=100;sting.SetAttacker(mosquitoBody);var packet=new ZPackage();sting.Serialize(ref packet);var received=new HitData();var receivedPacket=new ZPackage(packet.GetArray());received.Deserialize(ref receivedPacket);received.ApplyArmor(armor);
                var expected=new HitData.DamageTypes{m_pierce=100};expected.ApplyArmor(armor*.5f);Check(Mathf.Abs(received.m_damage.m_pierce-expected.m_pierce)<.001f,"networked mosquito hit ignores half armor "+armor);
                var ordinary=new HitData();ordinary.m_damage.m_pierce=100;ordinary.SetAttacker(ai.m_character);ordinary.ApplyArmor(armor);expected=new HitData.DamageTypes{m_pierce=100};expected.ApplyArmor(armor);Check(Mathf.Abs(ordinary.m_damage.m_pierce-expected.m_pierce)<.001f,"other attacker retains full armor "+armor);
            }
            var lox=Mob("Lox",target);var loxBody=(Humanoid)lox.m_character;Rules("[Lox]\nCharge = true\nIntelligence = vanilla");Hook(typeof(Humanoid).GetMethod("EquipItem",F),"Equip");
            var head=originalWeapon.Clone();head.m_dropPrefab=New<ItemDrop>("lox_bite").gameObject;var stomp=originalWeapon.Clone();stomp.m_dropPrefab=New<ItemDrop>("lox_stomp").gameObject;loxBody.GetInventory().GetAllItems().Add(head);loxBody.GetInventory().GetAllItems().Add(stomp);loxBody.m_rightItem=stomp;selected=stomp;
            lox.transform.position=target.transform.position+Vector3.forward*12;lox.transform.rotation=Quaternion.LookRotation(Vector3.back);Call(behavior,"Combat",lox,.1f);Check(loxBody.GetCurrentWeapon()==head,"lox charge equips native head strike even if stomp was selected");
            lox.transform.position=target.transform.position+Vector3.forward*2;attacks=0;Call(behavior,"Combat",lox,.1f);Check(attacks==1&&loxBody.GetCurrentWeapon()==head,"lox completes charge with head strike");selected=originalWeapon;
            var camp=mod.GetType("Overhaul.AI.GoblinCampAlarm");var campReceived=(IDictionary)camp.GetField("Received",F).GetValue(null);var participants=(IDictionary)camp.GetField("Participants",F).GetValue(null);
            var center=new Vector3(200,0,200);var guard=Mob("Goblin",target);guard.transform.position=center;guard.m_nview.GetZDO().SetPosition(center);guard.m_targetCreature=null;var shaman=Mob("GoblinShaman",target);shaman.transform.position=center+Vector3.right*5;shaman.m_targetCreature=null;
            var outsider=Mob("GoblinArcher",target);outsider.transform.position=center+Vector3.right*100;outsider.m_targetCreature=null;
            Rules("[Goblin]\nIntelligence = normal\n[GoblinShaman]\nIntelligence = dumb\n[GoblinArcher]\nIntelligence = normal");target.m_nview.GetZDO().SetPosition(center);
            var alert=Call(camp,"Record",center,50f,center,target.GetZDOID());double epoch=(double)alert.GetType().GetField("Epoch",F).GetValue(alert);ZNet.instance.m_netTime+=3;Call(camp,"Record",center,50f,center,target.GetZDOID());Check((double)alert.GetType().GetField("Epoch",F).GetValue(alert)==epoch,"reinforcement attacks keep the original camp alarm epoch");
            var member=Activator.CreateInstance(camp.GetNestedType("Participant",F),true);Set(member,"Center",center);Set(member,"Active",true);Set(member,"Updated",Now);participants[guard.m_character.GetZDOID()]=member;
            for(int round=0;round<6;round++){ZNet.instance.m_netTime+=20;Set(member,"Updated",Now);Call(camp,"TryCalm",center);Call(camp,"Record",center,50f,center,target.GetZDOID());}
            Check((double)alert.GetType().GetField("Epoch",F).GetValue(alert)==epoch,"ongoing camp battle never retriggers horn even minutes later");
            Set(member,"Active",false);Set(member,"Updated",Now);Call(camp,"TryCalm",center);Check((bool)alert.GetType().GetField("Fighting",F).GetValue(alert),"brief gap does not reset camp combat state");ZNet.instance.m_netTime+=6;Call(camp,"TryCalm",center);Check(!(bool)alert.GetType().GetField("Fighting",F).GetValue(alert),"whole camp leaving combat resets alert after grace period");
            Call(camp,"Record",center,50f,center,target.GetZDOID());double nextEpoch=(double)alert.GetType().GetField("Epoch",F).GetValue(alert);Check(nextEpoch>epoch,"new attack after calm creates exactly one new horn epoch");
            ZPackage CampPacket(){var packet=new ZPackage();packet.Write(center);packet.Write(50f);packet.Write(center);packet.Write(target.GetZDOID());packet.Write(nextEpoch);packet.SetPos(0);return packet;}
            Call(camp,"OnBroadcast",ZRoutedRpc.instance.GetServerPeerID()+99,CampPacket());Check(campReceived.Count==0,"camp broadcast from non-server is refused");Call(camp,"OnBroadcast",ZRoutedRpc.instance.GetServerPeerID(),CampPacket());Call(camp,"OnBroadcast",ZRoutedRpc.instance.GetServerPeerID(),CampPacket());
            Check(campReceived.Count==1&&(double)campReceived[center].GetType().GetField("Heard",F).GetValue(campReceived[center])==nextEpoch,"duplicate broadcasts share one consumed horn epoch");
            Call(camp,"Tick",guard);Call(camp,"Tick",shaman);Call(camp,"Tick",outsider);Check(guard.m_targetCreature==target&&shaman.m_targetCreature==null&&outsider.m_targetCreature==null,"camp goblin joins while dumb and outside goblins ignore horn");
            guard.m_targetCreature=null;var pollField=camp.GetField("polls",F);pollField.SetValue(null,Activator.CreateInstance(pollField.FieldType));ZNet.instance.m_netTime+=1.1;Call(camp,"Tick",guard);Check(guard.m_targetCreature==null,"stored camp epoch prevents reacquiring after disengagement and owner transfer");
            Check((bool)Call(camp,"Accept",guard.m_nview.GetZDO().GetOwner(),guard.m_nview.GetZDO(),target.m_nview.GetZDO()),"owner may report camp attack");Check(!(bool)Call(camp,"Accept",guard.m_nview.GetZDO().GetOwner()+99,guard.m_nview.GetZDO(),target.m_nview.GetZDO()),"another peer cannot report this goblin attack");guard.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",1);Check(!(bool)Call(camp,"Accept",guard.m_nview.GetZDO().GetOwner(),guard.m_nview.GetZDO(),target.m_nview.GetZDO()),"server refuses dumb caller");
            Hook(mod.GetType("Overhaul.AI.CampHorn").GetMethod("Play",F),"Horn");horns=0;
            var serverAlerts=(IDictionary)camp.GetField("Server",F).GetValue(null);serverAlerts.Clear();participants.Clear();campReceived.Clear();guard.m_nview.GetZDO().Set("overhaul_ai_intelligence_v1",0);
            var zone=New<ZoneSystem>("camp zones");ZoneSystem.s_instance=zone;zone.m_locationInstances[new Vector2s(3,3)]=new ZoneSystem.LocationInstance{m_position=center,m_placed=true,m_location=new ZoneSystem.ZoneLocation{m_prefabName="GoblinCamp2",m_exteriorRadius=40}};
            bool wasServer=ZNet.m_isServer;ZNet.m_isServer=true;
            Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),target.GetZDOID());Check(horns==1&&serverAlerts.Count==1,"real routed server request starts camp encounter and one horn");
            for(int n=0;n<8;n++){ZNet.instance.m_netTime+=1;Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),target.GetZDOID());}
            Check(horns==1,"repeated server broadcasts during combat never replay horn");
            guard.m_nview.GetZDO().SetPosition(center+Vector3.right*100);ZNet.instance.m_netTime+=1;Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),target.GetZDOID());Check(horns==1&&(double)serverAlerts[center].GetType().GetField("Contact",F).GetValue(serverAlerts[center])==Now,"pursuit outside camp keeps same encounter alive");
            Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),ZDOID.None);ZNet.instance.m_netTime+=6;Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),ZDOID.None);
            guard.m_nview.GetZDO().SetPosition(center);Call(camp,"OnRequest",guard.m_nview.GetZDO().GetOwner(),guard.m_character.GetZDOID(),target.GetZDOID());Check(horns==2,"server allows one new horn only after entire encounter ended");
            ZNet.m_isServer=wasServer;
            var horn=(AudioClip)Call(mod.GetType("Overhaul.AI.CampHorn"),"Clip");var soundSamples=new float[horn.samples];horn.GetData(soundSamples,0);Check(horn.length==3&&soundSamples.Any(v=>Mathf.Abs(v)>.1f)&&soundSamples.All(v=>!float.IsNaN(v)&&Mathf.Abs(v)<=1),"horn audio has three seconds of audible non-clipping samples");

            var escape=mod.GetType("Overhaul.AI.AnimalEscape");var deerBody=Body("Deer",Character.Faction.ForestMonsters);var deer=deerBody.gameObject.AddComponent<AnimalAI>();deer.m_character=deerBody;deer.m_nview=deerBody.m_nview;deerBody.m_baseAI=deer;deer.m_target=target;deer.m_alerted=true;deer.m_fleeRange=20;deer.m_avoidWater=false;deer.m_avoidLavaFlee=false;deer.transform.position=Vector3.zero;target.transform.position=Vector3.forward*5;
            moves=0;deer.Flee(.1f,target.transform.position);Check(moves==1&&Vector3.Distance(destination,Vector3.back*20)<.01f,"native animal flee patch chooses exactly opposite the predator");var firstEscape=destination;
            for(int step=0;step<20;step++){ZNet.instance.m_netTime+=1;deer.Flee(.1f,target.transform.position);}Check(destination==firstEscape,"animal escape waypoint stays stable across native flee intervals");
            deer.transform.position=Vector3.back*19;deer.Flee(.1f,target.transform.position);Check(destination.z<-30,"animal continues away immediately on reaching its waypoint");
            target.transform.position=Vector3.back*40;deer.Flee(.1f,target.transform.position);Check(destination.z>deer.transform.position.z,"escape turns away when predator crosses to the other side");
            deer.transform.position=Vector3.zero;target.transform.position=Vector3.forward*5;escapePath=p=>Mathf.Abs(p.x)>1&&p.z<0;ZNet.instance.m_netTime+=1;deer.Flee(.1f,target.transform.position);Check(Mathf.Abs(destination.x)>1&&destination.z<0,"blocked direct route selects accessible detour away from predator");var detour=destination;
            ZNet.instance.m_netTime+=1;deer.Flee(.1f,target.transform.position);Check(destination==detour,"detour keeps the same side instead of zigzagging");
            escapePath=p=>p.z<0&&p.magnitude<=3.1f;ZNet.instance.m_netTime+=1;deer.Flee(.1f,target.transform.position);Check(destination.magnitude<=3.1f&&Mathf.Abs(destination.x)<.01f,"shorter escape route is used when long routes are blocked");
            escapePath=p=>false;ZNet.instance.m_netTime+=1;moves=0;deer.Flee(.1f,target.transform.position);Check(moves==0,"fully blocked animal never chooses a random route toward predator");escapePath=null;
            Check(!(bool)Call(escape,"Applies",deer,Vector3.right*10),"fire flee destination is not mistaken for the predator");Check(!(bool)Call(escape,"Applies",ai,target.transform.position),"combat monster fleeing stays native");
            deer.m_nview.GetZDO().SetOwner(ZNet.GetUID()+99);Check(!(bool)Call(escape,"Applies",deer,target.transform.position),"remote owner does not steer animal escape");deer.m_nview.GetZDO().SetOwner(ZNet.GetUID());
            deer.gameObject.name="Goblin_Gem";Check(!(bool)Call(escape,"Applies",deer,target.transform.position),"gem goblin is not treated as a peaceful animal");deer.gameObject.name="Deer";
            deer.m_target=null;Check(!(bool)Call(escape,"Applies",deer,target.transform.position),"no predator leaves idle behavior untouched");
        }finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
    }
}
