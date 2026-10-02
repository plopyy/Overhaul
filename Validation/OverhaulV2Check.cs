using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;

// Isolated in-memory network state. Never opens or saves a user's world.
public static class OverhaulV2Check
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly plugin;
    static Type runtime, policy;
    static int checks;
    static readonly List<GameObject> roots = new List<GameObject>();
    static object Call(Type t, string name, params object[] args) { return t.GetMethod(name, All).Invoke(null, args); }
    static int Key(string name) { return (int)runtime.GetField(name, All).GetValue(null); }
    static ZDO NewZdo(Vector3 position, int prefab) { var z=ZDOMan.instance.CreateNewZDO(position,prefab);z.SetPrefab(prefab);z.Persistent=true;return z; }
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Debug.Log("DUNGEON PASS " + text); }
    static T Component<T>(string name) where T : Component
    {
        GameObject go = new GameObject(name); go.SetActive(false); roots.Add(go); return go.AddComponent<T>();
    }
    static void Singleton(Type type, string field, object value) { type.GetField(field, All).SetValue(null, value); }
    [InitializeOnLoadMethod]
    static void InstallPlayCallback()
    {
        EditorApplication.playModeStateChanged += state => {
            if(state==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("OverhaulV2Check",false)) {
                SessionState.SetBool("OverhaulV2Check",false);
                EditorApplication.delayCall += async () => { await Run(); EditorApplication.Exit(0); };
            }
        };
    }
    public static void Begin()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        SessionState.SetBool("OverhaulV2Check",true);EditorApplication.EnterPlaymode();
    }
    public static async System.Threading.Tasks.Task Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => { string p = Path.GetFullPath("../../../Libs/" + new AssemblyName(a.Name).Name + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; };
        plugin = Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        Call(plugin.GetType("Overhaul.IntegratedUi"), "LoadDependencies");
        runtime = plugin.GetType("Overhaul.Dungeons.DungeonRuntime", true);
        policy = plugin.GetType("Overhaul.Dungeons.DungeonPolicy", true);
        var bep = Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath", All).Invoke(null, new object[] { Path.GetFullPath("../../../Tools/DungeonResearch/TestGame.exe"), null, null, new string[0] });
        var logType = bep.GetType("BepInEx.Logging.ManualLogSource");
        Call(plugin.GetType("Overhaul.Utility.Log"), "Init", Activator.CreateInstance(logType, new object[] { "DungeonCheck" }));
        Call(plugin.GetType("Overhaul.Leveling.LevelingConfig"), "Initialize");
        Type configType = bep.GetType("BepInEx.Configuration.ConfigFile");
        object config = Activator.CreateInstance(configType, new object[] { Path.GetFullPath("../../../Tools/DungeonResearch/check-config.cfg"), false });
        Call(plugin.GetType("Overhaul.Utility.OverhaulConfig"), "Bind", config);
        var harmonyType = Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");
        object harmony = Activator.CreateInstance(harmonyType, new object[] { "overhaul.dungeon.check" });
        try
        {
            foreach (Type patch in plugin.GetTypes().Where(t => (t.Namespace == "Overhaul.Dungeons" || t.Namespace == "Overhaul.Commands" || t.Namespace == "Overhaul.Leveling" || t.Namespace == "Overhaul.Storage" || t.Namespace == "Overhaul.AI") && t.GetCustomAttributes(false).Any(a => a.GetType().Name == "HarmonyPatch")))
            {
                if (patch.Name == "BossInteriorPlacement") continue; // Synthetic legacy fixture; real forest assets exercised separately.
                object processor = harmonyType.GetMethod("CreateClassProcessor", new[] { typeof(Type) }).Invoke(harmony, new object[] { patch });
                processor.GetType().GetMethod("Patch", Type.EmptyTypes).Invoke(processor, null);
                Check(true, "Harmony " + patch.Name);
            }
            foreach (var item in new[] { new[] {20,40,30,60}, new[] {16,96,24,96}, new[] {3,64,5,96}, new[] {100,200,96,96}, new[] {4,5,6,8} })
            {
                object[] args = { item[0], item[1], 1.5d, 0, 0 }; Call(policy, "Scale", args);
                Check((int)args[3] == item[2] && (int)args[4] == item[3], "room scale " + item[0] + "/" + item[1]);
            }
            long now = DateTime.UtcNow.Ticks;
            Check(!(bool)Call(policy, "IsDue", 0L, now, 120d), "legacy has no immediate reset");
            Check(!(bool)Call(policy, "IsDue", now - TimeSpan.FromDays(5).Ticks + 1, now, 120d), "before real deadline");
            Check((bool)Call(policy, "IsDue", now - TimeSpan.FromDays(5).Ticks, now, 120d), "at real deadline");
            Check(!(bool)Call(policy, "IsDue", now - TimeSpan.FromDays(6).Ticks, now, 0d), "disabled interval");
            foreach(int value in new[] { int.MinValue, -1, 0, 10, int.MaxValue })
            { int seed = (int)Call(policy, "NextSeed", value, value); Check(seed != value && seed != int.MinValue, "fresh seed " + value); }
            var eligible = (HashSet<string>)policy.GetField("Supported", All).GetValue(null);
            Check(!eligible.Contains("Mistlands_DvergrBossEntrance1") && !eligible.Contains("WoodVillage1") && !eligible.Contains("WoodVillage2") && !eligible.Contains("WoodFarm1"), "reset exclusions");
            Check(eligible.Contains("Hildir_plainsfortress") && eligible.Contains("GoblinCamp2") && eligible.Contains("TrollCave02"), "tower outdoor and fixed cave included");
            NetworkChecks();
            if(Environment.GetCommandLineArgs().Contains("-adminExpOnly"))
            {
                var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));
                var patchArgs=new object[5];patchArgs[0]=typeof(SyncedList).GetMethod("CheckLoad",All);
                patchArgs[1]=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulV2Check).GetMethod("SkipAdminFileReload",All)});
                harmonyType.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,patchArgs);
                ZNet.instance.m_adminList=(SyncedList)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SyncedList));
                ZNet.instance.m_adminList.m_list=new List<string>();
                Call(plugin.GetType("Overhaul.AI.MobBehaviorConfig"),"Initialize");LevelingNetworkChecks();
                File.WriteAllText("../../../Tools/AugaWork/admin-exp-checks.txt","PASS "+checks+" checks. Packaged "+plugin.GetName().Version+". Isolated Unity/native RPC state; no user save opened.");
                return;
            }
            OverhaulStorageChecks.Run(plugin);
            if(Environment.GetCommandLineArgs().Contains("-storageOnly"))
            {
                File.WriteAllText("../../../Tools/OverhaulV2Work/check-results.txt", "PASS storage checks"+(Environment.GetCommandLineArgs().Contains("-resourceRangeOnly")?" (resource range scope)":" and boss loot checks")+". Packaged " + plugin.GetName().Version + ". Isolated Unity/network state.\n");
                return;
            }
            GenerationChecks();
            InteriorReservationChecks();
            ReconnectChecks();
            DashChecks(harmony, harmonyType);

            OverhaulV2LevelingChecks.Run(plugin);
            OverhaulV2BootstrapChecks.Run(plugin);
            Call(plugin.GetType("Overhaul.AI.MobBehaviorConfig"),"Initialize");LevelingNetworkChecks();
            PersistenceChecks();
            if(!Environment.GetCommandLineArgs().Contains("-mountainOnly")){await OverhaulBossChecks.Run(plugin);await SwampBossChecks.Run(plugin);}
            if(!Environment.GetCommandLineArgs().Contains("-skipBossPlans")) await MountainBossChecks.Run(plugin);
            File.WriteAllText("../../../Tools/OverhaulV2Work/check-results.txt", "PASS " + checks + " checks. Packaged " + plugin.GetName().Version + ". Isolated Unity/network state; no real world opened.\n");
            Debug.Log("DUNGEON CHECKS PASSED: " + checks);
        }
        catch(Exception e) { Debug.LogException(e); File.WriteAllText("../../../Tools/OverhaulV2Work/check-results.txt", "FAIL " + e); EditorApplication.Exit(1); }
        finally { harmonyType.GetMethod("UnpatchSelf").Invoke(harmony, null); }
    }
    static void InteriorReservationChecks()
    {
        var reservation=plugin.GetType("Overhaul.Dungeons.BossInteriorReservation",true);
        var layout=plugin.GetType("Overhaul.Dungeons.BossDungeonLayout",true);
        var reconnect=plugin.GetType("Overhaul.Dungeons.DungeonReconnect",true);
        int heightKey=(int)reservation.GetField("HeightKey",All).GetValue(null);
        int historyKey=(int)reservation.GetField("HistoryKey",All).GetValue(null);
        int layoutKey=(int)layout.GetField("LayoutKey",All).GetValue(null);
        var created=new List<ZDOID>();
        ZDO Add(Vector3 p,int prefab){var z=NewZdo(p,prefab);created.Add(z.m_uid);return z;}
        try
        {
            int proxyHash=ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            var target=Add(new Vector3(28000,0,28000),proxyHash);target.Set(ZDOVars.s_location,"MountainCave02".GetStableHashCode());Call(runtime,"Identity",target);target.Set(layoutKey,1);
            var original=(Bounds)Call(reservation,"Bounds",target);
            Check((float)Call(reservation,"ChooseHeight",target)==original.center.y,"clear reservation retains existing altitude");
            var neighbor=Add(target.GetPosition()+Vector3.right*64,proxyHash);neighbor.Set(ZDOVars.s_location,"Crypt2".GetStableHashCode());neighbor.Set(layoutKey,1);neighbor.Set(heightKey,original.center.y);
            float chosen=(float)Call(reservation,"ChooseHeight",target);
            Check(chosen!=original.center.y&&Mathf.Abs(chosen-original.center.y)>=128,"neighbor reservation causes vertical relocation rather than refusal");
            Call(reservation,"Remember",target,new List<Bounds>{original});target.Set(heightKey,chosen);
            Check((ZDO)Call(reconnect,"FindReconnectProxy",original.center)==target,"offline logout inside old reservation still resolves original exterior proxy");
            Check(!(bool)Call(layout,"InLane",target,original.center)&&(bool)Call(layout,"InLane",target,new Vector3(original.center.x,chosen,original.center.z)),"current occupancy uses persisted new altitude");
            var bytes=target.GetByteArray(historyKey,null);target.Set(historyKey,new byte[0]);target.Set(historyKey,bytes);
            Check(((List<Bounds>)Call(reservation,"History",target)).Single().Contains(original.center),"reservation history survives native ZDO byte serialization");
            var third=Add(target.GetPosition()+Vector3.forward*64,proxyHash);third.Set(ZDOVars.s_location,"Crypt3".GetStableHashCode());third.Set(layoutKey,1);third.Set(heightKey,original.center.y);
            Check((float)Call(reservation,"ChooseHeight",third)!=original.center.y,"another dungeon cannot reuse an offline player's former reservation");
            var foreign=Add(original.center,123);foreign.Set(Key("OwnerKey"),"other-owner");
            Check(!(bool)Call(runtime,"InResetInterior",target,foreign,new List<Bounds>{original},true),"overlapping foreign-owned objects are never collected for deletion");
            var playerPrefab=Component<Player>("offline-reset-player");ZNetScene.instance.m_namedPrefabs[playerPrefab.name.GetStableHashCode()]=playerPrefab.gameObject;
            var offline=Add(original.center,playerPrefab.name.GetStableHashCode());
            Check((bool)Call(runtime,"PlayersClear",new List<Bounds>{original}),"persistent player without a connected peer never blocks reset");
            var peer=(ZNetPeer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ZNetPeer));peer.m_uid=781;peer.m_characterID=offline.m_uid;
            ZNet.instance.m_peers.Add(peer);
            Check(!(bool)Call(runtime,"PlayersClear",new List<Bounds>{original}),"same character blocks reset while connected inside");
            ZNet.instance.m_peers.Remove(peer);
            var serverSocket=new AdminSocket();var clientSocket=new AdminSocket();var querying=new ZNetPeer(serverSocket,false){m_uid=782};
            ZNet.instance.m_peers.Add(querying);Call(reconnect,"Register",querying);
            var client=new ZRpc(clientSocket);ZDOID answer=ZDOID.None;Vector3 surface=Vector3.zero;
            client.Register<Vector3,ZDOID,Vector3>("Overhaul_DungeonReconnectAnswer",(rpc,saved,id,point)=>{answer=id;surface=point;});
            client.Invoke("Overhaul_DungeonReconnectQuery",original.center);querying.m_rpc.HandlePackage(clientSocket.Sent);
            Check(serverSocket.Sent!=null,"server answers reconnect query before character spawn");client.HandlePackage(serverSocket.Sent);
            Check(answer==target.m_uid&&surface==target.GetPosition(),"native RPC resolves offline old position to unchanged exterior entrance");
            ZNet.instance.m_peers.Remove(querying);
            var oldObject=Add(original.center+Vector3.right,123);var freshObject=Add(original.center+Vector3.up*128,123);
            string targetOwner=target.GetString(Key("IdentityKey"),"");oldObject.Set(Key("OwnerKey"),targetOwner);oldObject.Set(Key("EpochKey"),0);
            freshObject.Set(Key("OwnerKey"),targetOwner);freshObject.Set(Key("EpochKey"),1);
            var moves=Activator.CreateInstance(typeof(List<>).MakeGenericType(runtime.GetNestedType("TombMove",All)));
            var journal=(byte[])Call(runtime,"WriteResetJournal",new List<ZDOID>{oldObject.m_uid},new List<ZDOID>{freshObject.m_uid},873,1,DateTime.UtcNow.Ticks,new List<Bounds>{original},moves);
            Check((bool)Call(runtime,"FinishCommit",target,journal),"reset journal commits with offline character in old interior");
            Check(ZDOMan.instance.GetZDO(offline.m_uid)==offline&&!Character.InInterior(offline.GetPosition()),"offline character survives reset and is moved to exterior");
            Check(ZDOMan.instance.GetZDO(foreign.m_uid)==foreign,"neighbor dungeon object survives completed reset");
            var nav=plugin.GetType("Overhaul.Dungeons.BossNavigation",true);
            Check((int)Call(nav,"Lane",12000f+50*128f)==51,"navigation supports relocated interiors beyond original thirty-six lanes");
        }
        finally{foreach(var id in created)ZDOMan.instance.HandleDestroyedZDO(id);}
    }
    static void DashChecks(object harmony, Type harmonyType)
    {
        Type combat=plugin.GetType("Overhaul.DynamicCombat",true);
        var baseAim=(Vector3)Call(combat,"InclineDash",Vector3.forward,2f);
        var jumpAim=(Vector3)Call(combat,"InclineDash",baseAim,40f);
        Check(Mathf.Abs(Vector3.Angle(Vector3.forward,baseAim)-2f)<.01f&&Mathf.Abs(baseAim.magnitude-1f)<.001f,"base dash has two degree inclination at unchanged total speed");
        Check(Mathf.Abs(Vector3.Angle(Vector3.forward,jumpAim)-40f)<.01f,"jump dash remains forty degrees rather than adding base angle");
        Type swim=plugin.GetType("Overhaul.Patches.PlayerPatches+Character_UpdateSwimming_Patch",true);
        Type walk=plugin.GetType("Overhaul.Patches.PlayerPatches+Character_UpdateWalking_Patch",true);
        foreach(Type patch in new[]{swim,walk})
        {
            object processor=harmonyType.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{patch});
            processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);
        }
        Func<bool,bool,bool> press=(held,input)=>(bool)Call(combat,"ConsumeDashPress",held,input);
        press(false,true);
        Check(press(true,true),"dash accepts fresh press");
        Check(!press(true,true)&&!press(true,false)&&!press(true,true),"held dash cannot restart when controls disappear and return");
        press(false,false);
        Check(!press(true,false)&&!press(true,true),"press during blocked controls is not deferred");
        press(false,true);Check(press(true,true),"real release and new press rearm dash");
        var local=Player.m_localPlayer;var player=Component<Player>("dash-test-player");Player.m_localPlayer=player;
        var bodyObject=new GameObject("dash-test-body");var body=bodyObject.AddComponent<Rigidbody>();body.useGravity=false;player.m_body=body;
        combat.GetField("dashDirection",All).SetValue(null,Vector3.forward);
        combat.GetField("dashIsUpward",All).SetValue(null,false);
        combat.GetField("dashTimeRemaining",All).SetValue(null,0.2f);
        player.UpdateWalking(0.05f);
        Check(Math.Abs((float)combat.GetField("dashTimeRemaining",All).GetValue(null)-0.15f)<0.0001f,"land movement advances dash once");
        player.UpdateSwimming(0.05f);
        Check(Math.Abs((float)combat.GetField("dashTimeRemaining",All).GetValue(null)-0.10f)<0.0001f&&body.linearVelocity.z>0,"water entry continues same dash rather than suspending timer");
        player.UpdateSwimming(0.11f);
        Check(!(bool)Call(combat,"IsDashing",player)&&Math.Abs(body.linearVelocity.z)<0.0001f,"dash expires in water and stops propulsion");
        Check((bool)Call(swim,"Prefix",player,0.02f)&&(bool)Call(walk,"Prefix",player,0.02f),"water exit after expiry resumes native movement without another dash");
        combat.GetField("dashTimeRemaining",All).SetValue(null,0.2f);
        player.UpdateSwimming(0.05f);player.UpdateWalking(0.16f);
        Check(!(bool)Call(combat,"IsDashing",player),"water to land transition also shares a single dash duration");
        var capsule=bodyObject.AddComponent<CapsuleCollider>();capsule.radius=0.4f;capsule.height=1.8f;
        body.position=new Vector3(24000,100,24000);
        var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=body.position+Vector3.forward;wall.transform.localScale=new Vector3(5,5,0.02f);
        Physics.SyncTransforms();
        Func<Vector3,float,Vector3> limit=(velocity,dt)=>(Vector3)Call(combat,"LimitDashVelocity",body,velocity,dt);
        Vector3 fast=Vector3.forward*100;
        Vector3 clipped=limit(fast,0.02f);
        Check(clipped.z>0&&clipped.z*0.02f<0.59f,"thin wall limits full capsule travel at maximum configured dash speed");
        Check(limit(fast,0.1f).z*0.1f<0.59f,"long physics step cannot tunnel through thin wall");
        Check(limit(-fast,0.02f)==-fast,"dash away from wall stays unrestricted");
        wall.transform.position=body.position+Vector3.forward*.44f;Physics.SyncTransforms();
        
        combat.GetField("dashTimeRemaining",All).SetValue(null,.2f);
        Call(combat,"UpdateDash",player,.02f);
        Check((bool)Call(combat,"IsDashing",player)&&Math.Abs((float)combat.GetField("dashTimeRemaining",All).GetValue(null)-.18f)<.0001f,"solid obstacle does not cancel or extend dash timer");
        wall.transform.position=body.position+Vector3.forward;Physics.SyncTransforms();
        wall.GetComponent<Collider>().isTrigger=true;Physics.SyncTransforms();
        Check(limit(fast,0.02f)==fast,"trigger volumes do not block dash");
        wall.GetComponent<Collider>().isTrigger=false;Physics.IgnoreCollision(capsule,wall.GetComponent<Collider>(),true);
        Check(limit(fast,0.02f)==fast,"explicitly ignored collider does not block dash");
        Physics.IgnoreCollision(capsule,wall.GetComponent<Collider>(),false);
        UnityEngine.Object.DestroyImmediate(wall.GetComponent<BoxCollider>());
        var meshCollider=wall.AddComponent<MeshCollider>();meshCollider.sharedMesh=wall.GetComponent<MeshFilter>().sharedMesh;
        Physics.SyncTransforms();
        Check(limit(fast,0.02f).z*0.02f<0.59f,"solid mesh obstacle is swept as well as box walls");
        Vector3 before=body.position;
        var simulationMode=Physics.simulationMode;Physics.simulationMode=SimulationMode.Script;
        try { body.linearVelocity=limit(fast,0.02f);Physics.Simulate(0.02f); }
        finally { Physics.simulationMode=simulationMode; }
        Check(body.position.z-before.z<0.59f,"real physics step keeps dash capsule in front of thin mesh wall");
        body.position=before;body.linearVelocity=Vector3.zero;
        wall.transform.position=body.position+Vector3.up*2;wall.transform.localScale=new Vector3(5,0.02f,5);Physics.SyncTransforms();
        Check(limit(Vector3.up*100,0.02f).y*0.02f<1.1f,"upward dash respects thin ceiling");
        UnityEngine.Object.DestroyImmediate(wall);
        Check(limit(fast,0.02f)==fast,"unobstructed dash retains configured speed");
        var paving=GameObject.CreatePrimitive(PrimitiveType.Cube);paving.transform.localScale=new Vector3(8,.5f,8);
        paving.transform.position=body.position+Vector3.down*1.14f;Physics.SyncTransforms();
        var slight=(Vector3)Call(combat,"InclineDash",Vector3.forward,2f)*20f;
        Check(limit(slight,.02f).magnitude>19.9f,"two degree dash is not blocked by one centimetre overlap with supporting paving");
        UnityEngine.Object.DestroyImmediate(paving);
        var downTread=GameObject.CreatePrimitive(PrimitiveType.Cube);downTread.transform.localScale=new Vector3(4,.5f,4);
        downTread.transform.position=body.position+new Vector3(0,-1.25f,1);Physics.SyncTransforms();
        object[] descendingArgs={body,new Vector3(0,-10,20),.02f,false};
        var descendingMotion=(Vector3)Call(combat,"LimitDashMotion",descendingArgs);
        Check(!(bool)descendingArgs[3]&&descendingMotion.z>19.9f&&Mathf.Abs(descendingMotion.y)<.01f,"descending dash landing on a tread slides forward instead of cancelling or freezing");
        var downWall=GameObject.CreatePrimitive(PrimitiveType.Cube);downWall.transform.position=body.position+Vector3.forward*.43f;downWall.transform.localScale=new Vector3(4,4,.02f);Physics.SyncTransforms();
        object[] blockedDescending={body,new Vector3(0,-10,20),.02f,false};Call(combat,"LimitDashMotion",blockedDescending);
        Check((bool)blockedDescending[3],"wall in descending staircase still stops dash");
        UnityEngine.Object.DestroyImmediate(downWall);UnityEngine.Object.DestroyImmediate(downTread);
        var stairs=new List<GameObject>();var stairsStart=body.position;
        for(int i=0;i<6;i++)
        {
            var tread=GameObject.CreatePrimitive(PrimitiveType.Cube);tread.transform.localScale=new Vector3(4,.5f,.8f);
            tread.transform.position=stairsStart+new Vector3(0,-1.25f-i*.3f,i*.8f);stairs.Add(tread);
        }
        Physics.SyncTransforms();var stairSimulation=Physics.simulationMode;var stairRotation=body.rotation;var stairConstraints=body.constraints;
        body.constraints=RigidbodyConstraints.FreezeRotation;Physics.simulationMode=SimulationMode.Script;
        bool cancelledOnStairs=false;
        try
        {
            for(int frame=0;frame<20;frame++)
            {
                object[] movement={body,new Vector3(0,-8,10),.02f,false};
                body.linearVelocity=(Vector3)Call(combat,"LimitDashMotion",movement);cancelledOnStairs|=(bool)movement[3];
                Physics.Simulate(.02f);
            }
            Check(!cancelledOnStairs&&body.position.z>stairsStart.z+3f&&body.position.y<stairsStart.y-.7f,"successive real physics steps descend staircase without cancelling dash");
        }
        finally
        {
            Physics.simulationMode=stairSimulation;body.position=stairsStart;body.rotation=stairRotation;body.linearVelocity=Vector3.zero;body.angularVelocity=Vector3.zero;body.constraints=stairConstraints;
            foreach(var tread in stairs)UnityEngine.Object.DestroyImmediate(tread);Physics.SyncTransforms();
        }
        var step=GameObject.CreatePrimitive(PrimitiveType.Cube);step.transform.position=body.position+new Vector3(0,-.75f,1.3f);step.transform.localScale=new Vector3(3,.3f,1.5f);
        int oldMask=ZoneSystem.instance.m_solidRayMask;ZoneSystem.instance.m_solidRayMask=~0;Physics.SyncTransforms();
        var initial=body.position;
        Check((bool)Call(combat,"TryDashStep",body,Vector3.forward*20,.02f)&&body.position.y>initial.y,"dash climbs a 30cm step with clear headroom");
        body.position=initial;step.transform.position=initial+new Vector3(0,-1.135f,1.2f);step.transform.localScale=new Vector3(3,.5f,1.5f);Physics.SyncTransforms();
        Check((bool)Call(combat,"TryDashStep",body,Vector3.forward*20,.02f)&&body.position.y>initial.y,"dash can clear a 15mm paving seam without treating it as a wall");
        body.position=initial;step.transform.position=initial+new Vector3(0,.1f,1.3f);step.transform.localScale=new Vector3(3,2,1.5f);Physics.SyncTransforms();
        Check(!(bool)Call(combat,"TryDashStep",body,Vector3.forward*20,.02f)&&body.position==initial,"dash does not climb a tall wall");
        ZoneSystem.instance.m_solidRayMask=oldMask;UnityEngine.Object.DestroyImmediate(step);
        var roofNormal=Quaternion.Euler(-45,0,0)*Vector3.up;
        var roofDirection=Vector3.ProjectOnPlane(Vector3.forward,roofNormal).normalized;
        var roofFoot=body.position-roofNormal*(.4f+.5f*roofNormal.y);
        var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.rotation=Quaternion.Euler(-45,0,0);roof.transform.localScale=new Vector3(6,.2f,8);roof.transform.position=roofFoot-roofNormal*.1f;
        var beam=GameObject.CreatePrimitive(PrimitiveType.Cube);beam.transform.rotation=roof.transform.rotation;beam.transform.localScale=new Vector3(4,.2f,.5f);beam.transform.position=roofFoot+roofDirection*.6f+roofNormal*.1f;
        ZoneSystem.instance.m_solidRayMask=~0;Physics.SyncTransforms();
        var roofStart=body.position;
        Check((bool)Call(combat,"TryDashRelief",body,Vector3.forward*20,.02f,roofNormal),"small horizontal beam protruding from roof is crossed relative to roof slope");
        body.position=roofStart;UnityEngine.Object.DestroyImmediate(beam);
        var post=GameObject.CreatePrimitive(PrimitiveType.Cube);post.transform.position=roofFoot+roofDirection*.9f+Vector3.up;post.transform.localScale=new Vector3(.3f,3,.3f);Physics.SyncTransforms();
        Check(!(bool)Call(combat,"TryDashRelief",body,Vector3.forward*20,.02f,roofNormal)&&body.position==roofStart,"upright post on roof cannot be bypassed as a shallow protrusion");
        UnityEngine.Object.DestroyImmediate(post);UnityEngine.Object.DestroyImmediate(roof);ZoneSystem.instance.m_solidRayMask=oldMask;Physics.SyncTransforms();
        Func<Vector3,Vector3> follow=n=>(Vector3)Call(combat,"FollowDashGround",body,Vector3.forward*20,.02f,n);
        var flat=(Vector3)Call(combat,"FollowDashGround",body,new Vector3(0,12,20),.02f,Vector3.up);
        Check(flat==Vector3.forward*20,"flat ground clears residual uphill velocity instead of bouncing");
        Check((float)Call(combat,"RemoveGroundDashLift",12f,true)==0f,"leaving slope and ending ground dash cannot retain launch impulse");
        Check((float)Call(combat,"RemoveGroundDashLift",-5f,true)==-5f,"ground dash recovery preserves falling velocity");
        Check((float)Call(combat,"RemoveGroundDashLift",12f,false)==12f,"airborne motion unrelated to ground dash is preserved");
        Check((Vector3)Call(combat,"FollowDashGround",body,new Vector3(0,-5,20),.02f,Vector3.zero)==new Vector3(0,-5,20),"air dash without ground preserves gravity");
        foreach(float angle in new[]{30f,55f,75f})
        {
            var normal=Quaternion.Euler(-angle,0,0)*Vector3.up;
            var climbing=follow(normal);
            Check(climbing.y>0 && Mathf.Abs(climbing.magnitude-20)<.01f && Mathf.Abs(Vector3.Dot(climbing,normal))<.01f,"dash follows slope at "+angle+" degrees at constant speed");
        }
        Check(follow(Vector3.back)==Vector3.forward*20,"vertical surface never redirects dash upward");
        var ramp=GameObject.CreatePrimitive(PrimitiveType.Cube);
        ramp.transform.localScale=new Vector3(6,.2f,8);
        ramp.transform.rotation=Quaternion.Euler(-55,0,0);
        var rampNormal=ramp.transform.up;
        ramp.transform.position=body.position-Vector3.up*.5f-rampNormal*.52f;
        Physics.SyncTransforms();
        var projected=follow(Vector3.up);
        Check(projected.y>5,"low forward slope contact redirects horizontal dash uphill");
        Check(limit(projected,.02f).magnitude>19,"redirected trajectory clears actual steep ramp collider");
        UnityEngine.Object.DestroyImmediate(ramp);
        var terrainData=new TerrainData{heightmapResolution=33,size=new Vector3(8,10,8)};
        var heights=new float[33,33];for(int z=0;z<33;z++)for(int x=0;x<33;x++)heights[z,x]=Mathf.Min(1f,z*.3f);
        terrainData.SetHeights(0,0,heights);
        var terrainObject=Terrain.CreateTerrainGameObject(terrainData);terrainObject.transform.position=body.position+new Vector3(-4,-1f,.7f);
        capsule.radius=.5f;capsule.height=2f;Physics.SyncTransforms();
        var terrainMotion=(Vector3)Call(combat,"FollowDashGround",body,Vector3.forward*20,.02f,Vector3.up);
        File.WriteAllText("../../../Tools/OverhaulV2Work/dash-terrain-probe.txt","motion="+terrainMotion+" foot="+capsule.bounds.min+" hits="+string.Join(";",body.SweepTestAll(Vector3.forward,.42f,QueryTriggerInteraction.Ignore).Select(h=>h.collider.name+" d="+h.distance+" p="+h.point+" n="+h.normal)));
        Check(terrainMotion.y>15f&&Mathf.Abs(terrainMotion.magnitude-20)<.01f,"horizontal dash redirects along actual steep terrain without relying on two degree bias");
        object[] terrainLimit={body,Vector3.forward*20,.02f,false};Call(combat,"LimitDashMotion",terrainLimit);
        Check(!(bool)terrainLimit[3],"terrain collision does not cancel dash as a wall");
        var barrier=GameObject.CreatePrimitive(PrimitiveType.Cube);barrier.transform.position=body.position+Vector3.forward*.52f;barrier.transform.localScale=new Vector3(3,4,.02f);Physics.SyncTransforms();
        object[] barrierLimit={body,Vector3.forward*20,.02f,false};Call(combat,"LimitDashMotion",barrierLimit);
        Check((bool)barrierLimit[3],"wall still cancels dash even when terrain is also in contact");
        UnityEngine.Object.DestroyImmediate(barrier);UnityEngine.Object.DestroyImmediate(terrainObject);UnityEngine.Object.DestroyImmediate(terrainData);
        capsule.radius=.4f;capsule.height=1.8f;
        var sapling=Template("dash-sapling");var plant=sapling.AddComponent<Plant>();var plantCollider=sapling.AddComponent<BoxCollider>();
        var grown=Template("dash-grown-tree");grown.AddComponent<TreeBase>();var treeCollider=grown.AddComponent<BoxCollider>();
        plant.m_grownPrefabs=new[]{grown};
        Check((bool)Call(combat,"IsDashVegetation",plantCollider),"growing tree is traversable vegetation");
        Check(!(bool)Call(combat,"IsDashVegetation",treeCollider),"mature tree is not traversable vegetation");
        plant.m_grownPrefabs=new GameObject[0];
        Check(!(bool)Call(combat,"IsDashVegetation",plantCollider),"unrelated plants are not classified as growing trees");
        UnityEngine.Object.DestroyImmediate(sapling);UnityEngine.Object.DestroyImmediate(grown);
        Player.m_localPlayer=local;UnityEngine.Object.DestroyImmediate(bodyObject);press(false,true);
    }

    static void ReconnectChecks()
    {
        Type reconnect=plugin.GetType("Overhaul.Dungeons.DungeonReconnect");
        var scene=ZNetScene.instance;var zone=ZoneSystem.instance;var game=Game.instance;
        var oldWorld=ZNet.m_world;var oldProfile=game.m_playerProfile;
        ZNet.m_world=(World)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(World));ZNet.m_world.m_uid=87654;
        var profile=new PlayerProfile(null,FileHelpers.FileSource.Local);game.m_playerProfile=profile;
        Vector3 surface=new Vector3(18000,40,18000), saved=surface+Vector3.up*5000+Vector3.right*8;
        var root=Template("reconnect-proxy");root.transform.position=surface;
        var view=root.AddComponent<ZNetView>();var proxy=root.AddComponent<LocationProxy>();
        ZDO zdo=NewZdo(surface,root.name.GetStableHashCode());view.m_zdo=zdo;scene.m_instances[zdo]=view;
        var instance=new GameObject("reconnect-loaded-location");instance.transform.position=surface;proxy.m_instance=instance;
        var outsideObject=new GameObject("entrance");outsideObject.transform.SetParent(instance.transform,false);var outside=outsideObject.AddComponent<Teleport>();
        var insideObject=new GameObject("current-start-room");insideObject.transform.SetParent(instance.transform,false);insideObject.transform.localPosition=new Vector3(2,5000,3);var inside=insideObject.AddComponent<Teleport>();outside.m_targetPoint=inside;inside.m_targetPoint=outside;
        object[] targetArgs={proxy,Vector3.zero};
        Check((bool)Call(reconnect,"TryGetEntrance",targetArgs)&&(Vector3)targetArgs[1]==inside.GetTeleportPoint(),"reconnect uses actual current entrance teleport destination");
        object[] graveArgs={saved,Vector3.zero};
        Check((bool)Call(plugin.GetType("Overhaul.Dungeons.DungeonDeathGrave"),"TryExterior",graveArgs)&&!Character.InInterior((Vector3)graveArgs[1]),"death grave resolves exterior without filtering dungeon biome");
        var graveRoot=Template("death-grave");graveRoot.transform.position=saved;var graveView=graveRoot.AddComponent<ZNetView>();
        var graveData=NewZdo(saved,"TombStone".GetStableHashCode());graveData.SetOwner(ZNet.GetUID());graveData.Set("items","inventory-preserved");graveData.Set(ZDOVars.s_ownerName,"Test owner");graveView.m_zdo=graveData;
        graveRoot.AddComponent<Rigidbody>();var grave=graveRoot.AddComponent<TombStone>();
        Call(plugin.GetType("Overhaul.Dungeons.DungeonDeathGrave"),"Postfix",grave);
        Check(!Character.InInterior(graveData.GetPosition())&&graveData.GetVec3(ZDOVars.s_spawnPoint,Vector3.zero)==graveData.GetPosition(),"death grave position and native recovery point move outside together");
        Check(graveData.GetString("items","")=="inventory-preserved"&&graveData.GetString(ZDOVars.s_ownerName,"")=="Test owner","death relocation preserves grave inventory and owner");
        UnityEngine.Object.DestroyImmediate(graveRoot);
        inside.transform.position+=Vector3.right*4;
        targetArgs=new object[]{proxy,Vector3.zero};
        Check((bool)Call(reconnect,"TryGetEntrance",targetArgs)&&(Vector3)targetArgs[1]==inside.GetTeleportPoint(),"moved start room resolves new destination rather than old coordinates");
        profile.SetLogoutPoint(saved);game.m_respawnAfterDeath=false;game.m_respawnWait=100;
        Vector3 point;bool used;
        Check(!game.FindSpawnPoint(out point,out used,1f)&&profile.HaveLogoutPoint(),"reconnect waits for native area loading without consuming saved position");
        zone.m_zones[ZoneSystem.GetZone(saved)]=new ZoneSystem.ZoneData();
        proxy.m_locationNeedsSpawn=true;
        Check(!game.FindSpawnPoint(out point,out used,1f)&&profile.HaveLogoutPoint(),"reconnect waits for location instance completion");
        proxy.m_locationNeedsSpawn=false;insideObject.SetActive(false);
        Check(!game.FindSpawnPoint(out point,out used,1f),"inactive start teleport cannot be used");
        insideObject.SetActive(true);
        int mask=zone.m_solidRayMask;zone.m_solidRayMask=1;
        Check(!game.FindSpawnPoint(out point,out used,1f)&&profile.HaveLogoutPoint(),"loaded teleport without floor keeps player on loading screen");
        Vector3 destination=outside.GetTeleportPoint();
        var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=destination-Vector3.up*0.3f;floor.transform.localScale=new Vector3(6,0.5f,6);
        Physics.SyncTransforms();
        Check(game.FindSpawnPoint(out point,out used,1f)&&used&&point==destination+Vector3.up*0.25f&&!profile.HaveLogoutPoint(),"patched native spawn resolves exterior entrance only after outside floor is loaded");
        int terrainMask=zone.m_terrainRayMask;zone.m_terrainRayMask=1;
        object[] surfaceArgs={destination,Vector3.zero};
        Check((bool)Call(reconnect,"TrySurfacePosition",surfaceArgs),"surface recovery accepts dry flat ground with space for player");
        var obstacle=GameObject.CreatePrimitive(PrimitiveType.Cube);obstacle.layer=8;zone.m_solidRayMask=1|(1<<8);obstacle.transform.position=destination+Vector3.up;
        Check(!(bool)Call(reconnect,"TrySurfacePosition",new object[]{destination,Vector3.zero}),"surface recovery rejects occupied player capsule");
        UnityEngine.Object.DestroyImmediate(obstacle);
        float water=zone.m_waterLevel;zone.m_waterLevel=destination.y+1;
        Check(!(bool)Call(reconnect,"TrySurfacePosition",new object[]{destination,Vector3.zero}),"surface recovery rejects submerged terrain");zone.m_waterLevel=water;
        Check(!(bool)Call(reconnect,"TrySurfacePosition",new object[]{destination+Vector3.right*20,Vector3.zero}),"surface recovery rejects missing ground");
        profile.SetLogoutPoint(saved);game.m_respawnAfterDeath=false;
        Call(reconnect,"Prefix",new object[]{game,Vector3.zero,false,1f,false});
        reconnect.GetField("reconnectStarted",All).SetValue(null,Time.realtimeSinceStartup-11f);
        // Start directly above the test floor: timeout must return to surface, not old room.
        profile.SetLogoutPoint(new Vector3(destination.x,saved.y,destination.z));
        reconnect.GetField("reconnectGame",All).SetValue(null,game);
        reconnect.GetField("reconnectSaved",All).SetValue(null,profile.GetLogoutPoint());
        Check(game.FindSpawnPoint(out point,out used,1f)&&used&&!Character.InInterior(point)&&!profile.HaveLogoutPoint(),"unresolved dungeon timeout actually spawns player on verified surface and consumes logout only on success");
        zone.m_terrainRayMask=terrainMask;
        profile.SetLogoutPoint(surface);
        object[] bypass={game,Vector3.zero,false,1f,false};
        Check((bool)Call(reconnect,"Prefix",bypass)&&profile.GetLogoutPoint()==surface,"surface reconnection keeps native spawn behavior");
        profile.SetLogoutPoint(saved);game.m_respawnAfterDeath=true;bypass=new object[]{game,Vector3.zero,false,1f,false};
        Check((bool)Call(reconnect,"Prefix",bypass)&&profile.HaveLogoutPoint(),"death respawn is not redirected into dungeon");
        game.m_respawnAfterDeath=false;game.m_playerProfile=oldProfile;ZNet.m_world=oldWorld;zone.m_solidRayMask=mask;
        scene.m_instances.Remove(zdo);UnityEngine.Object.DestroyImmediate(instance);proxy.m_instance=null;UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(floor);
    }

    static void NetworkChecks()
    {
        var net = Component<ZNet>("test-net"); Singleton(typeof(ZNet), "m_instance", net);
        var game = Component<Game>("test-game"); typeof(Game).GetProperty("instance", All).SetValue(null, game);
        var scene = Component<ZNetScene>("test-scene"); Singleton(typeof(ZNetScene), "s_instance", scene);
        var rpc = new ZRoutedRpc(true);
        var manager = new ZDOMan(512); rpc.SetUID(ZNet.GetUID());
        runtime.GetField("session", All).SetValue(null, manager);
        var plain = Component<ZNetView>("test-object").gameObject;
        scene.m_namedPrefabs.Add(plain.name.GetStableHashCode(), plain);
        ZDO proxy = NewZdo(Vector3.zero, plain.name.GetStableHashCode());
        ZDO old = NewZdo(Vector3.one, plain.name.GetStableHashCode());
        string owner=(string)Call(runtime,"Identity",proxy);
        old.Set(Key("OwnerKey"), owner); old.Set(Key("EpochKey"), 0);
        ZDO fresh = NewZdo(Vector3.one * 2, plain.name.GetStableHashCode());
        fresh.Set(Key("OwnerKey"), owner); fresh.Set(Key("EpochKey"), 1);
        ZDOID oldId = old.m_uid, freshId = fresh.m_uid;
        byte[] journal = (byte[])Call(runtime, "WriteJournal", new List<ZDOID>{oldId}, new List<ZDOID>{freshId}, 456, 1, DateTime.UtcNow.Ticks);
        proxy.Set(Key("JournalKey"), journal);
        Call(runtime, "FinishCommit", proxy, journal);
        Check(manager.GetZDO(oldId) == null && manager.GetZDO(freshId) != null, "native network destruction removes only old objects");
        Check(proxy.GetInt(Key("SeedKey"), 0) == 456 && proxy.GetByteArray(Key("JournalKey"), null).Length == 0, "commit seed and journal persisted on anchor");
        Call(runtime, "FinishCommit", proxy, journal);
        Check(manager.GetZDO(freshId) != null, "idempotent interrupted commit replay");
        ZDO retained = NewZdo(Vector3.one * 3, plain.name.GetStableHashCode());
        retained.Set(Key("OwnerKey"), owner); retained.Set(Key("EpochKey"), 1);
        ZDO missing=NewZdo(Vector3.zero,plain.name.GetStableHashCode());var missingId=missing.m_uid;
        byte[] bad = (byte[])Call(runtime, "WriteJournal", new List<ZDOID>{retained.m_uid}, new List<ZDOID>{missingId}, 789, 2, DateTime.UtcNow.Ticks);
        Call(runtime,"DestroyObjects",new[]{missingId});
        bool threw = false; try { Call(runtime,"FinishCommit",proxy,bad); } catch(TargetInvocationException) { threw=true; }
        Check(threw && manager.GetZDO(retained.m_uid) != null, "missing replacement retains original");
        ZPackage serialized = new ZPackage(); fresh.Serialize(serialized);
        Check(serialized.Size() > 0, "native ZDO serialization includes custom metadata");
        var generator = Component<DungeonGenerator>("DG_DvergrTown"); var view=generator.gameObject.AddComponent<ZNetView>();
        view.m_zdo=NewZdo(new Vector3(0,5000,0),generator.name.GetStableHashCode()); generator.m_nview=view;
        generator.m_minRooms=16;generator.m_maxRooms=96;
        object[] parameters={generator,123};Call(runtime,"ConfigureGeneration",parameters);
        Check(generator.m_minRooms==24 && generator.m_maxRooms==96,"actual generator configuration capped");
        generator.m_hasGeneratedSeed=true;generator.m_generatedSeed=999;
        Check(generator.GetSeed()==123,"native GetSeed patch restores persisted seed for reconstruction");
        var occupied = new List<Bounds>{new Bounds(new Vector3(0,5000,0),new Vector3(128,256,128))};
        Check((bool)Call(runtime,"PlayersClear",occupied),"empty dedicated server eligible");
        var peer=(ZNetPeer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
        net.m_peers.Add(peer);
        Check((bool)Call(runtime,"PlayersClear",occupied),"connection outside dungeon does not defer reset");
        peer.m_uid=1234;var remote=NewZdo(new Vector3(0,5000,0),plain.name.GetStableHashCode());peer.m_characterID=remote.m_uid;peer.m_refPos=new Vector3(2048,0,2048);
        Check(!(bool)Call(runtime,"PlayersClear",occupied),"interior character blocks reset despite distant reference position");
        remote.SetPosition(new Vector3(2048,5000,2048));peer.m_refPos=new Vector3(0,5000,0);
        Check((bool)Call(runtime,"PlayersClear",occupied),"actual exterior character overrides stale interior loading reference");
        peer.m_characterID=ZDOID.None;Check(!(bool)Call(runtime,"PlayersClear",occupied),"connecting peer without a character still protects its interior loading area");peer.m_characterID=remote.m_uid;
        remote.SetPosition(Vector3.zero);peer.m_refPos=Vector3.zero;
        Check((bool)Call(runtime,"PlayersClear",occupied),"player at exterior entrance permits interior reset");
        net.m_peers.Clear();
    }

    static GameObject Template(string name)
    {
        var holder=new GameObject("template-holder");holder.SetActive(false);roots.Add(holder);
        var go=new GameObject(name);go.transform.SetParent(holder.transform,false);return go;
    }
    static void Connection(Room room, Vector3 pos, float yaw, bool entrance=false)
    {
        var go=new GameObject("connection");go.transform.SetParent(room.transform,false);go.transform.localPosition=pos;go.transform.localRotation=Quaternion.Euler(0,yaw,0);
        var connection=go.AddComponent<RoomConnection>();connection.m_entrance=entrance;connection.m_allowDoor=false;
    }
    static void GenerationChecks()
    {
        var assets=new DungeonTestAssets();
        typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",All).SetValue(null,assets);
        var db=Component<DungeonDB>("test-db");Singleton(typeof(DungeonDB),"m_instance",db);
        var zone=Component<ZoneSystem>("test-zone");Singleton(typeof(ZoneSystem),"s_instance",zone);
        var world=(World)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(World));world.m_seed=923;
        var generatorWorld=(WorldGenerator)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(WorldGenerator));generatorWorld.m_world=world;
        Singleton(typeof(WorldGenerator),"m_instance",generatorWorld);
        var scene=ZNetScene.instance;
        for(int i=0;i<4;i++)
        {
            var go=Template("fixture-room-"+i);var room=go.AddComponent<Room>();room.m_size=new Vector3Int(4,4,4);room.m_theme=Room.Theme.ForestCrypt;
            room.m_entrance=i==0;room.m_endCap=i==3;
            Connection(room,new Vector3(0,0,-2),180,i==0);
            if(i!=3) Connection(room,new Vector3(0,0,2),0);
            if(i==2) Connection(room,new Vector3(2,0,0),90);
            var loot=new GameObject("fixture-loot");loot.transform.SetParent(go.transform,false);var net=loot.AddComponent<ZNetView>();net.m_persistent=true;
            scene.m_namedPrefabs[loot.name.GetStableHashCode()]=loot;
            var data=new DungeonDB.RoomData { m_enabled=true,m_theme=Room.Theme.ForestCrypt,m_prefab=assets.Add(go) };
            db.m_rooms.Add(data);db.m_roomByHash.Add(data.Hash,data);
        }
        var locationObject=Template("Crypt2");var loc=locationObject.AddComponent<Location>();loc.m_hasInterior=true;
        var interior=new GameObject("Interior");interior.transform.SetParent(locationObject.transform,false);interior.transform.localPosition=new Vector3(0,5000,0);loc.m_interiorTransform=interior.transform;
        var dgObject=new GameObject("DG_ForestCrypt");dgObject.transform.SetParent(interior.transform,false);
        dgObject.AddComponent<ZNetView>().m_persistent=true;var dg=dgObject.AddComponent<DungeonGenerator>();dg.m_themes=Room.Theme.ForestCrypt;dg.m_minRooms=10;dg.m_maxRooms=40;dg.m_minRequiredRooms=1;dg.m_requiredRooms.Add("fixture-room-1");loc.m_generator=dg;
        scene.m_namedPrefabs[dgObject.name.GetStableHashCode()]=dgObject;
        var location=new ZoneSystem.ZoneLocation { m_prefab=assets.Add(locationObject),m_prefabName="Crypt2" };
        var proxy=NewZdo(new Vector3(1024,0,1024),"test-object".GetStableHashCode());proxy.Set(ZDOVars.s_seed,345);proxy.Set(ZDOVars.s_location,location.Hash);
        var previous=new Dictionary<int,ZDO>();
        var capture=Call(runtime,"Stage",proxy,location,previous,false,100);
        var ids=(List<ZDOID>)capture.GetType().GetField("Objects",All).GetValue(capture);
        proxy.Set(Key("TrackedKey"),true);proxy.Set(Key("EpochKey"),1);proxy.Set(Key("SeedKey"),100);proxy.Set(Key("LastKey"),DateTime.UtcNow.AddDays(-6).Ticks);
        var original=ids.Select(ZDOMan.instance.GetZDO).Single(z=>z.GetPrefab()==dgObject.name.GetStableHashCode());
        byte[] first=original.GetByteArray(ZDOVars.s_roomData,null);
        Check(first!=null&&BitConverter.ToInt32(first,0)>2,"native generation produces a connected multi-room fixture");
        Check(dg.m_minRooms==10&&dg.m_maxRooms==40,"prefab parameters not compounded or mutated");
        int oldCount=ZDOMan.instance.m_objectsByID.Count;
        Call(runtime,"Reset",proxy,location);
        Check(proxy.GetInt(Key("EpochKey"),0)==2,"full reset commits next epoch");
        Check(ids.All(id=>ZDOMan.instance.GetZDO(id)==null),"full reset leaves no old room objects");
        var second=ZDOMan.instance.m_objectsByID.Values.Single(z=>z.GetPrefab()==dgObject.name.GetStableHashCode());
        Check(!first.SequenceEqual(second.GetByteArray(ZDOVars.s_roomData,null)),"new seed produces a different native room plan");
        Check(proxy.GetInt(ZDOVars.s_seed,0)==345,"entrance/location decoration seed remains stable");
        int beforeClient=ZDOMan.instance.m_objectsByID.Count;
        typeof(ZNet).GetField("m_isServer",All).SetValue(null,false);
        GameObject client=null;
        try
        {
            client=scene.CreateObject(second);
            Check(client!=null&&client.GetComponentsInChildren<Room>(true).Length==BitConverter.ToInt32(second.GetByteArray(ZDOVars.s_roomData,null),0),"native client reconstructs every saved room");
            Check(client.GetComponent<DungeonGenerator>().GetSeed()==second.GetInt(Key("SeedKey"),0),"client reconstruction uses new persisted seed");
            Check(ZDOMan.instance.m_objectsByID.Count==beforeClient,"client reconstruction creates no duplicate network objects");
        }
        finally
        {
            typeof(ZNet).GetField("m_isServer",All).SetValue(null,true);
            scene.m_instances.Remove(second);
            if(client)UnityEngine.Object.DestroyImmediate(client);
        }
        // Migrate an untagged existing location with matching saved plan.
        proxy.Set(Key("TrackedKey"),false);
        foreach(var z in ZDOMan.instance.m_objectsByID.Values.Where(z=>z.GetString(Key("OwnerKey"),"")==(string)Call(runtime,"Identity",proxy)).ToArray())z.Set(Key("OwnerKey"),"");
        Call(runtime,"Reset",proxy,location);
        Check(proxy.GetInt(Key("EpochKey"),0)==3,"legacy plan replay safely migrates matching objects");
        // Old interior layouts no longer have to be reproduced to be replaced.
        proxy.Set(Key("TrackedKey"),false);
        var third=ZDOMan.instance.m_objectsByID.Values.Single(z=>z.GetPrefab()==dgObject.name.GetStableHashCode());
        var changed=third.GetByteArray(ZDOVars.s_roomData,null).ToArray();changed[4]^=1;third.Set(ZDOVars.s_roomData,changed);
        var thirdId=third.m_uid;
        string migratedOwner=(string)Call(runtime,"Identity",proxy);
        var priorIds=ZDOMan.instance.m_objectsByID.Values.Where(z=>z.GetString(Key("OwnerKey"),"")==migratedOwner).Select(z=>z.m_uid).ToArray();
        foreach(var id in priorIds)ZDOMan.instance.GetZDO(id).Set(Key("OwnerKey"),"");
        var spawner=NewZdo(third.GetPosition()+Vector3.right,"test-object".GetStableHashCode());
        var child=NewZdo(proxy.GetPosition()+Vector3.forward*200,"test-object".GetStableHashCode());var childId=child.m_uid;
        spawner.SetConnection(ZDOExtraData.ConnectionType.Spawned,childId);
        var neighbor=NewZdo(third.GetPosition()+Vector3.right*64,"test-object".GetStableHashCode());var neighborId=neighbor.m_uid;
        Call(runtime,"Reset",proxy,location);
        Check(proxy.GetInt(Key("EpochKey"),0)==4&&ZDOMan.instance.GetZDO(thirdId)==null&&priorIds.All(id=>ZDOMan.instance.GetZDO(id)==null),"different untracked legacy interior successfully replaced with all old content removed");
        Check(ZDOMan.instance.GetZDO(childId)==null,"legacy interior spawner discovers and removes its roaming creature");
        Check(ZDOMan.instance.GetZDO(neighborId)!=null,"legacy migration preserves adjacent dungeon contents");
        Check(ZDOMan.instance.m_objectsByID.Values.Where(z=>z.GetString(Key("OwnerKey"),"")==migratedOwner).All(z=>z.GetInt(Key("EpochKey"),0)==4&&z.GetInt(Key("PendingKey"),0)==0),"legacy reference replay leaves no extra staged generation");
        var proxyTemplate=Template("fixture-location-proxy");proxyTemplate.AddComponent<ZNetView>().m_persistent=true;proxyTemplate.AddComponent<LocationProxy>();zone.m_locationProxyPrefab=proxyTemplate;
        scene.m_namedPrefabs[proxyTemplate.name.GetStableHashCode()]=proxyTemplate;
        var ghosts=new List<GameObject>();
        zone.SpawnLocation(location,928,new Vector3(3072,0,3072),Quaternion.identity,ZoneSystem.SpawnMode.Ghost,ghosts,false);
        foreach(var ghost in ghosts)if(ghost)UnityEngine.Object.DestroyImmediate(ghost);
        var initial=ZDOMan.instance.m_objectsByID.Values.Single(z=>z.GetPrefab()==proxyTemplate.name.GetStableHashCode());
        Check(initial.GetLong(Key("VisitStartedKey"),0)==0&&initial.GetBool(Key("TrackedKey"),false),"native first generation leaves visit timestamp at zero");
        string owner=(string)Call(runtime,"Identity",initial);
        var initialObjects=ZDOMan.instance.m_objectsByID.Values.Where(z=>z.GetString(Key("OwnerKey"),"")==owner).ToArray();
        Check(initialObjects.Length>2&&initialObjects.All(z=>z.GetString(Key("IdentityKey"),"").Length==32),"native initial scope tags all generated objects");
        // A fixed cave uses the same replacement engine but has no room plan to scale.
        var cave=Template("TrollCave02");cave.AddComponent<Location>();var fixedLoot=new GameObject("fixture-fixed-loot");fixedLoot.transform.SetParent(cave.transform,false);fixedLoot.AddComponent<ZNetView>().m_persistent=true;
        scene.m_namedPrefabs[fixedLoot.name.GetStableHashCode()]=fixedLoot;
        var caveLocation=new ZoneSystem.ZoneLocation { m_prefab=assets.Add(cave),m_prefabName="TrollCave02" };
        var caveProxy=NewZdo(new Vector3(6144,0,6144),proxyTemplate.name.GetStableHashCode());caveProxy.Set(ZDOVars.s_seed,778);caveProxy.Set(ZDOVars.s_location,caveLocation.Hash);
        var caveCapture=Call(runtime,"Stage",caveProxy,caveLocation,new Dictionary<int,ZDO>(),false,900);
        caveProxy.Set(Key("TrackedKey"),true);caveProxy.Set(Key("EpochKey"),1);
        var caveIds=(List<ZDOID>)caveCapture.GetType().GetField("Objects",All).GetValue(caveCapture);
        Call(runtime,"Reset",caveProxy,caveLocation);
        Check(caveProxy.GetInt(Key("EpochKey"),0)==2&&caveIds.All(id=>ZDOMan.instance.GetZDO(id)==null),"fixed location content reset without DungeonGenerator");
        VisitChecks(zone, location, caveLocation, initial, caveProxy);
        AdminChecks(zone, location, caveLocation, initial, caveProxy, assets);
        ResetSafetyChecks(zone, location);
    }

    sealed class AdminSocket : ISocket
    {
        public ZPackage Sent;
        public readonly List<ZPackage> Packets=new List<ZPackage>();
        public bool IsConnected() { return true; }
        public void Send(ZPackage p) { Sent=new ZPackage(p.GetArray());Packets.Add(new ZPackage(p.GetArray())); }
        public ZPackage Recv() { return null; }
        public int GetSendQueueSize() { return 0; }
        public int GetCurrentSendRate() { return 0; }
        public bool IsHost() { return false; }
        public void Dispose() { }
        public bool GotNewData() { return false; }
        public void Close() { }
        public string GetEndPointString() { return "fixture"; }
        public void GetAndResetStats(out int a,out int b) { a=b=0; }
        public void GetConnectionQuality(out float a,out float b,out int c,out float d,out float e) { a=b=d=e=0;c=0; }
        public ISocket Accept() { return null; }
        public int GetHostPort() { return 0; }
        public bool Flush() { return true; }
        public string GetHostName() { return "Steam_76561198000000001"; }
        public void VersionMatch() { }
    }

    static void LevelingNetworkChecks()
    {
        var network=plugin.GetType("Overhaul.Leveling.LevelingNetwork");
        var serverSocket=new AdminSocket();var clientSocket=new AdminSocket();var peer=new ZNetPeer(serverSocket,false){m_uid=871};
        var actor=NewZdo(new Vector3(22000,0,22000),"network-player".GetStableHashCode());actor.SetOwner(peer.m_uid);peer.m_characterID=actor.m_uid;
        ZNet.instance.m_peers.Add(peer);Call(network,"Register",peer);
        var client=new ZRpc(clientSocket);Newtonsoft.Json.Linq.JObject reply=null;
        client.Register<string>("Overhaul_Leveling",(rpc,json)=>reply=Newtonsoft.Json.Linq.JObject.Parse(json));
        Action<object> send=packet=>{serverSocket.Sent=null;client.Invoke("Overhaul_Leveling",Newtonsoft.Json.JsonConvert.SerializeObject(packet));peer.m_rpc.HandlePackage(clientSocket.Sent);if(serverSocket.Sent!=null)client.HandlePackage(serverSocket.Sent);};
        send(new{Op="hello"});Check((string)reply["Op"]=="rules"&&((string)reply["Json"]).Contains("MaxLevel"),"leveling native RPC handshake supplies server rules");
        var speeds=(Dictionary<string,float[]>)Call(plugin.GetType("Overhaul.Utility.WeaponAttackSpeeds"),"Parse",(string)reply["WeaponSpeeds"]);
        Check(speeds.Count==21&&speeds["Swords1H"][0]==1.5f&&speeds["Swords1H"][1]==1f,"native RPC supplies independent server light and heavy weapon speeds");
        var mobRules=(System.Collections.IDictionary)Call(plugin.GetType("Overhaul.AI.MobBehaviorConfig"),"Parse",(string)reply["MobBehaviors"]);Check(mobRules.Contains("Unbjorn")&&mobRules.Count>150,"native RPC supplies the per-mob behavior configuration");
        var weaponConfig=plugin.GetType("Overhaul.Utility.WeaponAttackSpeeds");var mobConfig=plugin.GetType("Overhaul.AI.MobBehaviorConfig");
        var savedWeapons=weaponConfig.GetField("Current",All).GetValue(null);var savedMobs=mobConfig.GetField("Current",All).GetValue(null);
        try{
            weaponConfig.GetField("Current",All).SetValue(null,Call(weaponConfig,"Parse","[Bows]\nBackstabBonus = 8\nLightFirstAttackMovement = non\nHeavyFirstAttackMovement = oui"));mobConfig.GetField("Current",All).SetValue(null,Call(mobConfig,"Parse","[Unbjorn]\nCharge = oui\nAggressiveness = agressif\nDumbChance = 10.5"));send(new{Op="hello"});
            var customWeapons=(Dictionary<string,float[]>)Call(weaponConfig,"Parse",(string)reply["WeaponSpeeds"]);var customMobs=(System.Collections.IDictionary)Call(mobConfig,"Parse",(string)reply["MobBehaviors"]);
            Check(customWeapons["Bows"][2]==8&&customWeapons["Bows"][3]==0&&customWeapons["Bows"][4]==1,"actual RPC carries customized backstab and independent combo movement settings");Check((bool)customMobs["Unbjorn"].GetType().GetField("Charge",All).GetValue(customMobs["Unbjorn"]),"actual RPC carries enabled charge");Check((float)customMobs["Unbjorn"].GetType().GetField("DumbChance",All).GetValue(customMobs["Unbjorn"])==10.5f,"actual RPC carries per-mob dumb percentage");
        }finally{weaponConfig.GetField("Current",All).SetValue(null,savedWeapons);mobConfig.GetField("Current",All).SetValue(null,savedMobs);}
        send(new{Op="profile",Data=new{Version=1,TotalExperience=0,Level=1,CurrentExperience=0,AllocatedStats=new Dictionary<string,int>()}});
        Check((string)reply["Op"]=="state"&&(int)reply["Data"]["AvailableStatPoints"]==0,"server reconciles initial portable profile");
        send(new{Op="allocate",Stats=new Dictionary<string,int>{{"carry",4}}});Check((string)reply["Op"]=="refused","server refuses allocation above earned budget");
        var monster=Template("Troll");monster.AddComponent<Character>();ZNetScene.instance.m_namedPrefabs["Troll".GetStableHashCode()]=monster;
        var victim=NewZdo(actor.GetPosition()+Vector3.forward,"Troll".GetStableHashCode());victim.SetOwner(peer.m_uid);victim.Set(ZDOVars.s_level,1);victim.Set(ZDOVars.s_health,0f);
        send(new{Op="death",Victim=victim.m_uid.ToString(),Participants=new[]{actor.m_uid.ToString()}});
        Check((long)reply["Data"]["TotalExperience"]==125&&(int)reply["Data"]["Level"]==2,"owned elite kill grants server-calculated XP through actual ZRpc");
        Check((long)reply["RewardAmount"]==125&&(int)reply["RewardStars"]==0&&reply["RewardName"]!=null,"server reward packet carries earned amount, creature name and stars for client feed");
        send(new{Op="death",Victim=victim.m_uid.ToString(),Participants=new[]{actor.m_uid.ToString()}});
        Check(serverSocket.Sent==null,"duplicate death report awards no second XP");
        var foreign=NewZdo(actor.GetPosition(),"Troll".GetStableHashCode());foreign.SetOwner(999);
        send(new{Op="death",Victim=foreign.m_uid.ToString(),Participants=new[]{actor.m_uid.ToString()}});
        Check(serverSocket.Sent==null,"client cannot report another peer's monster death");
        send(new{Op="allocate",Stats=new Dictionary<string,int>{{"carry",4}}});Check((string)reply["Op"]=="state"&&(int)reply["Data"]["AvailableStatPoints"]==0,"server commits valid allocation");
        send(new{Op="combat"});send(new{Op="reset"});Check((string)reply["Op"]=="refused","server enforces combat lock on reset");
        send(new{Op="profile",Data=new{Version=1,TotalExperience=99999999}});
        Check((long)reply["Data"]["TotalExperience"]==125,"second profile cannot overwrite authoritative in-session XP");
        var commands=plugin.GetType("Overhaul.Commands.AdminCommands");Call(commands,"Register",peer);peer.m_playerName="Leveling Test Player";
        var originalAdmins=new List<string>(ZNet.instance.m_adminList.m_list);ZNet.instance.m_adminList.m_list.Clear();
        string resetReply=null;client.Register<string>("Overhaul_DungeonResetResult",(rpc,message)=>resetReply=message);
        Action<string> resetRequest=target=>{serverSocket.Packets.Clear();client.Invoke("Overhaul_AdminResetLevel",target);peer.m_rpc.HandlePackage(clientSocket.Sent);foreach(var p in serverSocket.Packets)client.HandlePackage(p);};
        resetRequest("");Check(resetReply.Contains("refuse"),"full progression reset RPC rejects non-admin");
        send(new{Op="profile"});Check((long)reply["Data"]["TotalExperience"]==125,"unauthorized reset preserves XP and committed points");
        ZNet.instance.m_adminList.m_list.Add(serverSocket.GetHostName());
        Call(plugin.GetType("Overhaul.Commands.AdminCommandAccess"),"SetEnabled",peer.m_rpc,true);
        var sessions=(System.Collections.IDictionary)network.GetField("Sessions",All).GetValue(null);var session=sessions[peer.m_uid];var resetData=session.GetType().GetField("Data",All).GetValue(session);
        resetData.GetType().GetField("Passive").SetValue(resetData,"vitality");resetData.GetType().GetField("PassiveChangeAfterUtc").SetValue(resetData,DateTime.UtcNow.AddHours(1).Ticks);
        resetRequest("");
        Check(resetReply.Contains("remise a zero")&&(string)reply["Op"]=="admin_reset"&&(long)reply["Data"]["TotalExperience"]==0&&(long)reply["Data"]["CurrentExperience"]==0&&(int)reply["Data"]["Level"]==1&&(int)reply["Data"]["AvailableStatPoints"]==0&&!reply["Data"]["AllocatedStats"].HasValues&&(string)reply["Data"]["Passive"]==""&&(long)reply["Data"]["PassiveChangeAfterUtc"]==0,"authorized admin reset clears all progression including passive cooldown through native RPC");
        send(new{Op="profile",Data=new{Version=1,TotalExperience=99999999}});Check((long)reply["Data"]["TotalExperience"]==0,"stale profile cannot resurrect reset progression");
        resetRequest("Missing Player");Check(resetReply.Contains("absent"),"unknown reset target is rejected without fallback to caller");
        Check(((string)Call(commands,"ResetLevel",null,peer.m_playerName)).Contains("remise a zero"),"dedicated server console can reset exact connected player name with spaces");
        Check(((string)Call(commands,"ResetLevel",null,"")).Contains("Usage serveur dedie"),"dedicated server console without target returns usage");
        Action<string,long> expRequest=(target,amount)=>{serverSocket.Packets.Clear();client.Invoke("Overhaul_AdminExperience",target,amount);peer.m_rpc.HandlePackage(clientSocket.Sent);foreach(var p in serverSocket.Packets)client.HandlePackage(p);};
        ZNet.instance.m_adminList.m_list.Clear();expRequest("",10000);
        Check(resetReply.Contains("refuse"),"experience RPC refuses non-admin");
        send(new{Op="profile"});Check((long)reply["Data"]["TotalExperience"]==0,"unauthorized experience request changes nothing");
        ZNet.instance.m_adminList.m_list.Add(serverSocket.GetHostName());
        Call(plugin.GetType("Overhaul.Commands.AdminCommandAccess"),"SetEnabled",peer.m_rpc,true);
        expRequest("",10000);Check((string)reply["Op"]=="admin_exp"&&(long)reply["Data"]["TotalExperience"]==10000,"admin adds XP to authenticated self through native RPC");
        var expData=session.GetType().GetField("Data",All).GetValue(session);var dataType=expData.GetType();
        var system=plugin.GetType("Overhaul.Leveling.LevelingSystem");
        ((Dictionary<string,int>)dataType.GetField("AllocatedStats").GetValue(expData))["carry"]=1;
        expRequest(peer.m_playerName,-8750);
        Check((long)reply["Data"]["TotalExperience"]==1250&&(int)reply["Data"]["Level"]==4&&(long)reply["Data"]["CurrentExperience"]==450,"bear surplus correction removes 8750 and recalculates level and progress");
        Check(!reply["Data"]["AllocatedStats"].HasValues&&(int)reply["Data"]["AvailableStatPoints"]==12&&resetReply.Contains("remises a zero"),"every level decrease resets even a single affordable allocated point");
        ((Dictionary<string,int>)dataType.GetField("AllocatedStats").GetValue(expData))["carry"]=1;
        expRequest("",-1);Check((int)reply["Data"]["AllocatedStats"]["carry"]==1&&(int)reply["Data"]["AvailableStatPoints"]==11,"same-level XP removal retains allocations");
        expRequest("",1);Check((int)reply["Data"]["AllocatedStats"]["carry"]==1,"XP addition retains allocations");
        expRequest("Missing Player",100);Check(resetReply.Contains("absent"),"missing target never falls back to caller for experience");
        var duplicate=new ZNetPeer(new AdminSocket(),false){m_uid=872,m_playerName=peer.m_playerName};ZNet.instance.m_peers.Add(duplicate);
        expRequest(peer.m_playerName,100);Check(resetReply.Contains("ambigu"),"ambiguous exact player name refused");
        ZNet.instance.m_peers.Remove(duplicate);
        expRequest("",0);Check(resetReply.Contains("non nul"),"zero experience request rejected");
        expRequest("",long.MaxValue);Check((long)reply["Data"]["TotalExperience"]==long.MaxValue,"XP addition saturates without integer overflow");
        dataType.GetField("Passive").SetValue(expData,"vitality");dataType.GetField("PassiveChangeAfterUtc").SetValue(expData,123456L);
        expRequest("",long.MinValue);Check((long)reply["Data"]["TotalExperience"]==0&&(int)reply["Data"]["Level"]==1&&(int)reply["Data"]["AvailableStatPoints"]==0&&(string)reply["Data"]["Passive"]=="","maximum removal clamps to zero and removes unavailable passive");
        Check((long)reply["Data"]["PassiveChangeAfterUtc"]==123456L,"admin correction preserves passive cooldown");
        send(new{Op="profile",Data=new{Version=1,TotalExperience=99999999}});Check((long)reply["Data"]["TotalExperience"]==0,"stale profile cannot restore removed XP");
        Check(((string)Call(commands,"AdjustExperience",null,peer.m_playerName,100L)).Contains("total 100"),"dedicated console supports exact connected name with spaces");
        Check(((string)Call(commands,"AdjustExperience",null,"",100L)).Contains("Usage serveur dedie"),"dedicated console requires explicit player target");
        // Apply the actual server packet on a client, then reload portable custom data.
        var characterType=plugin.GetType("Overhaul.Leveling.OverhaulCharacter");var local=Component<Player>("admin-exp-client");
        local.m_nview=local.gameObject.AddComponent<ZNetView>();local.m_nview.m_zdo=NewZdo(Vector3.zero,0);local.m_nview.m_zdo.SetOwner(ZNet.GetUID());
        var previousLocal=Player.m_localPlayer;Player.m_localPlayer=local;
        var localState=Call(characterType,"Get",local);characterType.GetField("Ready").SetValue(localState,true);
        var authoritative=Newtonsoft.Json.JsonConvert.SerializeObject(new{Op="admin_exp",Data=expData,AdminMessage="test correction"});
        var serverFlag=typeof(ZNet).GetField("m_isServer",All);bool wasServer=(bool)serverFlag.GetValue(null);bool oldPeerServer=peer.m_server;
        var connectionFlag=typeof(ZNet).GetField("m_connectionStatus",All);var oldConnection=connectionFlag.GetValue(null);
        var savedPeers=new List<ZNetPeer>(ZNet.instance.m_peers);
        try{
            serverFlag.SetValue(null,false);peer.m_server=true;
            connectionFlag.SetValue(null,ZNet.ConnectionStatus.Connected);ZNet.instance.m_peers.Clear();ZNet.instance.m_peers.Add(peer);
            Call(network,"Receive",new ZRpc(new AdminSocket()),authoritative);
            Check(!local.m_customData.ContainsKey("Overhaul.Character"),"client rejects admin state from a foreign connection");
            Call(network,"Receive",peer.m_rpc,authoritative);
            Check(local.m_customData.ContainsKey("Overhaul.Character")&&(long)Newtonsoft.Json.Linq.JObject.Parse(local.m_customData["Overhaul.Character"])["TotalExperience"]==100,"approved admin state is stored in player custom data");
            serverFlag.SetValue(null,true);characterType.GetMethod("Load",All).Invoke(localState,null);
            var reloaded=characterType.GetField("Data").GetValue(localState);
            Check((long)dataType.GetField("TotalExperience").GetValue(reloaded)==100&&(int)dataType.GetField("AvailableStatPoints").GetValue(reloaded)==4,"corrected XP and point budget survive character reload");
            ((Dictionary<string,int>)dataType.GetField("AllocatedStats").GetValue(reloaded))["carry"]=1;
            Check(((string)Call(commands,"AdjustExperience",null,"",-1L)).Contains("total 99"),"host command without name corrects local player");
            Check((int)dataType.GetField("Level").GetValue(reloaded)==1&&((Dictionary<string,int>)dataType.GetField("AllocatedStats").GetValue(reloaded)).Count==0,"host level decrease resets points");
            Check((long)Newtonsoft.Json.Linq.JObject.Parse(local.m_customData["Overhaul.Character"])["TotalExperience"]==99,"host correction stored immediately");
        }finally{serverFlag.SetValue(null,wasServer);connectionFlag.SetValue(null,oldConnection);ZNet.instance.m_peers.Clear();ZNet.instance.m_peers.AddRange(savedPeers);peer.m_server=oldPeerServer;Player.m_localPlayer=previousLocal;UnityEngine.Object.DestroyImmediate(local.gameObject);}
        foreach(string birdName in new[]{"Crow","Seagal"})
        {
            var bird=Template(birdName);bird.AddComponent<Destructible>();ZNetScene.instance.m_namedPrefabs[birdName.GetStableHashCode()]=bird;
            var birdData=NewZdo(actor.GetPosition()+Vector3.forward,birdName.GetStableHashCode());birdData.SetOwner(peer.m_uid);
            send(new{Op="death",Victim=birdData.m_uid.ToString(),Participants=new[]{actor.m_uid.ToString()}});
            Check((long)reply["RewardAmount"]==5,"native bird without Character grants exactly five XP: "+birdName);
            send(new{Op="death",Victim=birdData.m_uid.ToString(),Participants=new[]{actor.m_uid.ToString()}});
            Check(serverSocket.Sent==null,"bird death cannot grant XP twice: "+birdName);
            Call(network,"BirdDeath",bird.GetComponent<Destructible>(),null);
            ZNetScene.instance.m_namedPrefabs.Remove(birdName.GetStableHashCode());
        }
        ZNet.instance.m_adminList.m_list=originalAdmins;
        Call(network,"Disconnect",peer);ZNet.instance.m_peers.Remove(peer);
    }

    static bool SkipAdminFileReload() { return false; }

    static void VisitChecks(ZoneSystem zone, ZoneSystem.ZoneLocation crypt, ZoneSystem.ZoneLocation cave, ZDO initial, ZDO second)
    {
        zone.m_locationsByHash[crypt.Hash]=crypt;zone.m_locationsByHash[cave.Hash]=cave;
        runtime.GetField("selected",All).SetValue(null,new HashSet<string>{"Crypt2","TrollCave02"});
        var interval=plugin.GetType("Overhaul.Utility.OverhaulConfig").GetProperty("ResetIntervalHours",All).GetValue(null);interval.GetType().GetProperty("Value").SetValue(interval,120f);
        long now=DateTime.UtcNow.Ticks, minute=now-now%TimeSpan.TicksPerMinute;
        initial.Set(Key("LastKey"),now-TimeSpan.FromDays(20).Ticks);
        Check(!(bool)Call(runtime,"EligibleForSweep",initial,now),"legacy generation timestamp is not treated as a visit");
        initial.Set(Key("RequestedKey"),true);
        Check(!(bool)Call(runtime,"EligibleForSweep",initial,now)&&initial.GetBool(Key("RequestedKey"),false),"automatic sweep preserves queued manual request without treating unvisited dungeon as due");initial.Set(Key("RequestedKey"),false);
        var neighbor=NewZdo(initial.GetPosition()+Vector3.right*64,zone.m_locationProxyPrefab.name.GetStableHashCode());
        neighbor.Set(ZDOVars.s_location,crypt.Hash);neighbor.Set(ZDOVars.s_seed,818);neighbor.Set(Key("TrackedKey"),true);
        Call(runtime,"Stage",neighbor,crypt,new Dictionary<int,ZDO>(),false,818);
        Call(runtime,"RegisterVisitSite",neighbor);
        Call(runtime,"RegisterVisitSite",initial);Call(runtime,"RegisterVisitSite",second);
        var character=NewZdo(initial.GetPosition(),"test-object".GetStableHashCode());
        var peer=new ZNetPeer(new AdminSocket(),false){m_uid=919,m_characterID=character.m_uid,m_refPos=initial.GetPosition()+Vector3.up*5000};ZNet.instance.m_peers.Add(peer);
        Call(runtime,"ObserveVisits",now);
        Check(initial.GetLong(Key("VisitStartedKey"),0)==0,"exterior character and interior camera reference do not start visit timer");
        character.SetPosition(initial.GetPosition()+Vector3.up*5000);
        Call(runtime,"ObserveVisits",now);
        Check(initial.GetLong(Key("VisitStartedKey"),0)==minute,"dedicated server observes real interior entry and stores UTC minute");
        Check(neighbor.GetLong(Key("VisitStartedKey"),0)==0,"visiting one interior does not mark a neighboring untouched dungeon");
        Call(runtime,"ObserveVisits",now+TimeSpan.FromHours(1).Ticks);
        Check(initial.GetLong(Key("VisitStartedKey"),0)==minute,"later visits do not extend first-visit deadline");
        character.SetPosition(second.GetPosition());peer.m_refPos=second.GetPosition();
        Call(runtime,"ObserveVisits",now+TimeSpan.FromMinutes(37).Ticks);
        Check(second.GetLong(Key("VisitStartedKey"),0)==minute+TimeSpan.FromMinutes(37).Ticks,"different locations acquire independent first-visit timestamps");
        Check(!(bool)Call(runtime,"EligibleForSweep",initial,minute+TimeSpan.FromDays(5).Ticks-1),"visited dungeon is not due before configured interval");
        Check((bool)Call(runtime,"EligibleForSweep",initial,minute+TimeSpan.FromDays(5).Ticks)&&!(bool)Call(runtime,"EligibleForSweep",second,minute+TimeSpan.FromDays(5).Ticks),"only dungeon whose own deadline elapsed becomes eligible");
        initial.Set(Key("VisitStartedKey"),now-TimeSpan.FromDays(5).Ticks);second.Set(Key("VisitStartedKey"),now);
        var pending=(Queue<ZDOID>)runtime.GetField("queue",All).GetValue(null);var queued=(HashSet<ZDOID>)runtime.GetField("queued",All).GetValue(null);pending.Clear();queued.Clear();
        runtime.GetField("nextSweep",All).SetValue(null,0f);
        Action<float> sweep=time=> { for(int i=0;i<10000;i++){Call(runtime,"SweepDueLocations",time,now);if(!(bool)runtime.GetField("scanning",All).GetValue(null))return;}throw new Exception("native sweep did not finish"); };
        sweep(1000f);
        Check(pending.Contains(initial.m_uid)&&!pending.Contains(second.m_uid),"global native sweep enqueues only due visited locations");
        second.Set(Key("VisitStartedKey"),now-TimeSpan.FromDays(5).Ticks);
        sweep(1299f);Check(!pending.Contains(second.m_uid),"expiry scan does not run again before five minutes");
        sweep(1300f);Check(pending.Contains(second.m_uid)&&pending.Count(id=>id==initial.m_uid)==1,"five-minute sweep admits new deadlines without duplicate resets");
        long first=initial.GetLong(Key("VisitStartedKey"),0);int epoch=initial.GetInt(Key("EpochKey"),0);
        character.SetPosition(initial.GetPosition()+Vector3.up*5000);peer.m_refPos=character.GetPosition();
        Call(runtime,"Reset",initial,crypt);
        Check(initial.GetLong(Key("VisitStartedKey"),0)==first&&initial.GetInt(Key("EpochKey"),0)==epoch,"occupied overdue dungeon retains original deadline while waiting");
        ZNet.instance.m_peers.Remove(peer);pending.Clear();queued.Clear();
    }

    static void AdminChecks(ZoneSystem zone, ZoneSystem.ZoneLocation crypt, ZoneSystem.ZoneLocation cave, ZDO initial, ZDO anchor, DungeonTestAssets assets)
    {
        var commands=plugin.GetType("Overhaul.Commands.AdminCommands",true);
        Call(commands,"Initialize");
        Check(Terminal.commands.ContainsKey("o_resetalldungeons")&&!Terminal.commands["o_resetalldungeons"].IsCheat&&Terminal.commands.ContainsKey("o_resetdungeon")&&!Terminal.commands.ContainsKey("o_resetdungeons"),"global and single admin commands registered without old alias");
        zone.m_locationsByHash[crypt.Hash]=crypt;zone.m_locationsByHash[cave.Hash]=cave;
        var forbidden=new ZoneSystem.ZoneLocation { m_prefab=assets.Add(Template("WoodVillage1")),m_prefabName="WoodVillage1" };
        zone.m_locationsByHash[forbidden.Hash]=forbidden;
        var excluded=NewZdo(Vector3.zero,zone.m_locationProxyPrefab.name.GetStableHashCode());excluded.Set(ZDOVars.s_location,forbidden.Hash);
        var neverVisited=NewZdo(new Vector3(12000,0,12000),zone.m_locationProxyPrefab.name.GetStableHashCode());neverVisited.Set(ZDOVars.s_location,crypt.Hash);neverVisited.Set(Key("LastKey"),DateTime.UtcNow.AddDays(-20).Ticks);
        // Keep the native permission check and list lookup, but isolate file reload/platform services.
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));
        var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony");
        object harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.dungeon.check"});
        var patch=harmonyType.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        var patchArgs=new object[patch.GetParameters().Length];patchArgs[0]=typeof(SyncedList).GetMethod("CheckLoad",All);
        patchArgs[1]=Activator.CreateInstance(harmonyAssembly.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulV2Check).GetMethod("SkipAdminFileReload",All)});
        patch.Invoke(harmony,patchArgs);
        ZNet.instance.m_adminList=(SyncedList)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SyncedList));
        ZNet.instance.m_adminList.m_list=new List<string>();
        var serverSocket=new AdminSocket();var clientSocket=new AdminSocket();
        var peer=new ZNetPeer(serverSocket,false) { m_uid=412 };
        var client=new ZRpc(clientSocket);string reply=null;
        client.Register<string>("Overhaul_DungeonResetResult",(rpc,message)=>reply=message);
        ZNet.instance.m_peers.Add(peer);Call(commands,"Register",peer);
        Action request=()=> { client.Invoke("Overhaul_DungeonReset");peer.m_rpc.HandlePackage(clientSocket.Sent);client.HandlePackage(serverSocket.Sent); };
        request();
        Check(reply.Contains("refuse")&&!anchor.GetBool(Key("RequestedKey"),false),"native admin RPC rejects non-admin without scheduling resets");
        ZNet.instance.m_adminList.m_list.Add(serverSocket.GetHostName());
        Call(plugin.GetType("Overhaul.Commands.AdminCommandAccess"),"SetEnabled",peer.m_rpc,true);
        Check((bool)Call(commands,"Authorized",peer.m_rpc),"server validates connected admin against native admin list");
        peer.m_uid=0;Check(!(bool)Call(commands,"Authorized",peer.m_rpc),"unready admin connection rejected");peer.m_uid=412;
        Check(!(bool)Call(commands,"Authorized",new ZRpc(new AdminSocket())),"unregistered connection cannot impersonate admin");
        var config=plugin.GetType("Overhaul.Utility.OverhaulConfig").GetProperty("ResetIntervalHours",All).GetValue(null);
        config.GetType().GetProperty("Value").SetValue(config,0f);
        int manualCount=ZDOMan.instance.m_objectsByID.Values.Count(z=>z.GetPrefab()==zone.m_locationProxyPrefab.name.GetStableHashCode()&&(z.GetInt(ZDOVars.s_location,0)==crypt.Hash||z.GetInt(ZDOVars.s_location,0)==cave.Hash));
        long initialVisit=initial.GetLong(Key("VisitStartedKey"),0);initial.Set(Key("VisitStartedKey"),0L);
        var queue=(Queue<ZDOID>)runtime.GetField("queue",All).GetValue(null);var queued=(HashSet<ZDOID>)runtime.GetField("queued",All).GetValue(null);queue.Clear();queued.Clear();
        request();
        Check(reply.Contains(manualCount+" lieux generes")&&queue.Count==manualCount&&initial.GetBool(Key("RequestedKey"),false)&&neverVisited.GetBool(Key("RequestedKey"),false),"manual bulk reset includes both visited and unvisited generated dungeons");
        request();Check(queue.Count==manualCount,"repeating one-target command does not multiply scheduled resets");
        int objectCount=ZDOMan.instance.m_objectsByID.Count;
        initial.Set(Key("RequestedKey"),false);neverVisited.Set(Key("RequestedKey"),false);Call(runtime,"Process",initial);Call(runtime,"Process",neverVisited);
        Check(ZDOMan.instance.m_objectsByID.Count==objectCount&&initial.GetLong(Key("VisitStartedKey"),0)==0&&neverVisited.GetLong(Key("VisitStartedKey"),0)==0,"processing never-visited locations performs no generation or deletion");
        initial.Set(Key("VisitStartedKey"),initialVisit);
        request();
        Check(reply.Contains(manualCount+" lieux")&&anchor.GetBool(Key("RequestedKey"),false)&&initial.GetBool(Key("RequestedKey"),false),"native admin request schedules generated locations with automatic resets disabled");
        Check(!excluded.GetBool(Key("RequestedKey"),false),"manual request preserves excluded village");
        Check(neverVisited.GetBool(Key("RequestedKey"),false),"admin reset includes never-visited location");
        long recentVisit=DateTime.UtcNow.Ticks;anchor.Set(Key("VisitStartedKey"),recentVisit);
        config.GetType().GetProperty("Value").SetValue(config,120f);
        Check(!(bool)Call(runtime,"EligibleForSweep",anchor,recentVisit+TimeSpan.FromMinutes(5).Ticks),"pending manual request cannot bypass first-visit deadline in global sweep");
        config.GetType().GetProperty("Value").SetValue(config,0f);
        queue.Clear();queued.Clear();
        Call(runtime,"Process",anchor);
        Check(!anchor.GetBool(Key("RequestedKey"),false)&&queue.Count==0&&anchor.GetInt(Key("EpochKey"),0)==2&&anchor.GetLong(Key("VisitStartedKey"),0)==recentVisit,"unavailable location is skipped without retaining or requeueing manual request");
        request();
        anchor.Set(Key("LastKey"),0L);
        runtime.GetField("selected",All).SetValue(null,new HashSet<string>{"TrollCave02","Crypt2"});
        peer.m_refPos=anchor.GetPosition();
        var prepField=runtime.GetField("preparation",All);
        var reportMap=(Dictionary<ZDOID,string>)runtime.GetField("reports",All).GetValue(null);
        var anchorZone=ZoneSystem.GetZone(anchor.GetPosition());
        zone.SetZoneGenerated(anchorZone);
        queue.Clear();queued.Clear();
        assets.DelayLoads=true;
        int loadsBefore=assets.AsyncLoads, releasesBefore=assets.Releases;
        var cachedBounds=(Dictionary<ZDOID,List<Bounds>>)runtime.GetField("visitBounds",All).GetValue(null);
        cachedBounds[anchor.m_uid]=new List<Bounds>{new Bounds(anchor.GetPosition(),Vector3.one*20)};
        Call(runtime,"Process",anchor);
        Check(prepField.GetValue(null)==null&&assets.AsyncLoads==loadsBefore&&!anchor.GetBool(Key("RequestedKey"),false)&&anchor.GetLong(Key("VisitStartedKey"),0)==recentVisit,"occupied cached dungeon skips asset and terrain loading and consumes command");
        cachedBounds[anchor.m_uid]=new List<Bounds>{new Bounds(anchor.GetPosition()+Vector3.up*5000,Vector3.one*20)};
        Check((bool)Call(runtime,"KnownPlayersClear",anchor,cave),"outside entrance player does not block cached interior precheck");
        peer.m_refPos=anchor.GetPosition()+Vector3.up*5000;
        Check(!(bool)Call(runtime,"KnownPlayersClear",anchor,cave),"inside player blocks cached interior without loading assets");
        peer.m_refPos=anchor.GetPosition();
        cachedBounds.Remove(anchor.m_uid);
        assets.DelayLoads=false;
        Check(!(bool)Call(runtime,"KnownPlayersClear",anchor,cave)&&assets.AsyncLoads==loadsBefore,"resident prefab detects occupancy without cached bounds or additional loading");
        assets.DelayLoads=true;
        anchor.Set(Key("RequestedKey"),true);
        Call(runtime,"Process",anchor);
        Check(prepField.GetValue(null)!=null&&!anchor.GetBool(Key("RequestedKey"),false)&&assets.AsyncLoads==loadsBefore+1,"cold assets start one held asynchronous load instead of rejecting reset");
        Call(runtime,"AdvancePreparation",Time.realtimeSinceStartup);
        request();
        Check(assets.AsyncLoads==loadsBefore+1&&!queue.Contains(anchor.m_uid)&&!anchor.GetBool(Key("RequestedKey"),false),"loading does not restart assets or duplicate target when command repeats");
        Call(runtime,"AdvancePreparation",Time.realtimeSinceStartup+61f);
        Check(prepField.GetValue(null)==null&&!queued.Contains(anchor.m_uid)&&assets.Releases==releasesBefore+1&&anchor.GetLong(Key("VisitStartedKey"),0)==recentVisit,"loading timeout releases reference and abandons attempt without changing visit");
        request();
        Check(!reportMap.ContainsKey(anchor.m_uid),"new command clears suppressed failure reason so outcome is visible again");
        queue.Clear();queued.Clear();
        Call(runtime,"Process",anchor);
        anchor.Set(Key("VisitStartedKey"),0L);
        zone.m_zones[anchorZone]=new ZoneSystem.ZoneData();
        assets.DelayLoads=false;
        Call(runtime,"AdvancePreparation",Time.realtimeSinceStartup);
        Check(prepField.GetValue(null)==null&&anchor.GetInt(Key("EpochKey"),0)==2&&!anchor.GetBool(Key("RequestedKey"),false)&&reportMap[anchor.m_uid].Contains("joueur"),"manual preparation ignores zero visit timestamp and reaches occupied-player check");anchor.Set(Key("VisitStartedKey"),recentVisit);
        Call(runtime,"AdvancePreparation",Time.realtimeSinceStartup);
        Check(prepField.GetValue(null)==null&&!queued.Contains(anchor.m_uid),"blocked reset has no preparation retry remaining");
        Call(runtime,"Reset",anchor,cave);
        Check(!anchor.GetBool(Key("RequestedKey"),false)&&anchor.GetLong(Key("LastKey"),0)==0&&anchor.GetInt(Key("EpochKey"),0)==2,"blocked manual attempt is consumed while preserving player protection");
        config.GetType().GetProperty("Value").SetValue(config,120f);
        Check(!(bool)Call(runtime,"EligibleForSweep",anchor,recentVisit+TimeSpan.FromMinutes(10).Ticks)&&anchor.GetLong(Key("VisitStartedKey"),0)==recentVisit,"failed manual reset does not schedule early retry or alter first visit");
        anchor.Set(Key("RequestedKey"),true);
        Check(!(bool)Call(runtime,"EligibleForSweep",anchor,recentVisit+TimeSpan.FromMinutes(15).Ticks),"persisted request from older version cannot force periodic retries");
        anchor.Set(Key("RequestedKey"),false);
        Check((bool)Call(runtime,"EligibleForSweep",anchor,recentVisit+TimeSpan.FromDays(5).Ticks),"automatic reset becomes eligible normally at first-visit deadline after manual failure");
        ZNet.instance.m_peers.Remove(peer);
        Call(runtime,"RequestManualReset");
        queue.Clear();queued.Clear();
        assets.DelayLoads=true;
        Call(runtime,"Process",anchor);
        Check(prepField.GetValue(null)!=null&&anchor.GetInt(Key("EpochKey"),0)==2,"fresh command can prepare previously blocked dungeon again");
        assets.DelayLoads=false;
        Call(runtime,"AdvancePreparation",Time.realtimeSinceStartup);
        Check(!anchor.GetBool(Key("RequestedKey"),false)&&anchor.GetInt(Key("EpochKey"),0)==3&&anchor.GetLong(Key("VisitStartedKey"),0)==0,"successful manual reset consumes request and disarms timer until next visit");
        Check(prepField.GetValue(null)==null&&!queued.Contains(anchor.m_uid),"successful reset after delayed load releases preparation and target");
        Check(!(bool)Call(runtime,"EligibleForSweep",anchor,DateTime.UtcNow.AddDays(100).Ticks),"fresh reset remains ineligible indefinitely without another visit");
        var bounds=(List<Bounds>)Call(runtime,"GetVisitBounds",anchor);
        Check((bool)Call(runtime,"StartFirstVisit",anchor,anchor.GetPosition(),bounds,DateTime.UtcNow.Ticks)&&anchor.GetLong(Key("VisitStartedKey"),0)>0,"new visit after reset rearms a new independent deadline");
        TargetedAdminChecks(commands,zone,crypt,assets,neverVisited,queue,queued);
        config.GetType().GetProperty("Value").SetValue(config,120f);
    }

    static void TargetedAdminChecks(Type commands, ZoneSystem zone, ZoneSystem.ZoneLocation crypt, DungeonTestAssets assets, ZDO neverVisited, Queue<ZDOID> queue, HashSet<ZDOID> queued)
    {
        queue.Clear();queued.Clear();
        Check(Call(runtime,"FindManualResetTarget",neverVisited.GetPosition())==neverVisited,"nearest selection pins unvisited nearest instead of skipping to distant visited dungeon");
        Check(((string)Call(runtime,"RequestManualResetAt",neverVisited.GetPosition())).Contains("reset demande uniquement")&&queue.Count==1&&queue.Peek()==neverVisited.m_uid,"targeted reset schedules the unvisited nearest dungeon");queue.Clear();queued.Clear();neverVisited.Set(Key("RequestedKey"),false);
        var target=NewZdo(new Vector3(20000,30,20000),zone.m_locationProxyPrefab.name.GetStableHashCode());target.Set(ZDOVars.s_location,crypt.Hash);
        target.Set(Key("VisitStartedKey"),DateTime.UtcNow.Ticks);
        var inside=target.GetPosition()+Vector3.up*5000;
        var cached=(Dictionary<ZDOID,List<Bounds>>)runtime.GetField("visitBounds",All).GetValue(null);
        cached[target.m_uid]=new List<Bounds>{new Bounds(inside,Vector3.one*20)};
        Check(Call(runtime,"FindManualResetTarget",inside)==target,"interior selection resolves containing dungeon");
        var distantRoom=inside+Vector3.right*110;
        cached[target.m_uid]=new List<Bounds>{new Bounds(distantRoom,Vector3.one*20)};
        Check(Call(runtime,"FindManualResetTarget",distantRoom)==target,"interior in another terrain zone still resolves its entrance");
        Check(Call(runtime,"FindManualResetTarget",distantRoom+Vector3.forward*30)==null,"nearby position outside saved rooms never selects a dungeon");
        var reconnect=plugin.GetType("Overhaul.Dungeons.DungeonReconnect");
        int layoutKey=(int)plugin.GetType("Overhaul.Dungeons.BossDungeonLayout").GetField("LayoutKey",All).GetValue(null);
        var isolated=NewZdo(new Vector3(1600,40,-1024),zone.m_locationProxyPrefab.name.GetStableHashCode());
        isolated.Set(layoutKey,1);isolated.Set(ZDOVars.s_location,crypt.Hash);
        var obsolete=new Vector3(1688.61f,13693.81f,-1088.32f);
        cached[isolated.m_uid]=new List<Bounds>{new Bounds(new Vector3(1600,13664,-1024),Vector3.one*20)};
        Check(Call(runtime,"FindManualResetTarget",obsolete)==null,"old logout point outside current rooms remains ineligible for destructive reset targeting");
        Check(Call(reconnect,"FindReconnectProxy",obsolete)==isolated,"reconnect resolves reported logout position from isolated reservation without room containment");
        var duplicate=NewZdo(isolated.GetPosition(),zone.m_locationProxyPrefab.name.GetStableHashCode());duplicate.Set(layoutKey,1);
        Check(Call(reconnect,"FindReconnectProxy",obsolete)==null,"ambiguous reconnect reservation is rejected");
        duplicate.Set(layoutKey,0);isolated.Set(layoutKey,0);cached.Remove(isolated.m_uid);
        cached[target.m_uid]=new List<Bounds>{new Bounds(inside,Vector3.one*20)};
        var outer=Template("admin-exterior");outer.transform.SetParent(crypt.m_prefab.Asset.transform,false);outer.transform.localPosition=new Vector3(3,1,4);
        var inner=Template("admin-interior");inner.transform.SetParent(crypt.m_prefab.Asset.transform,false);inner.transform.localPosition=Vector3.up*5000;
        var ot=outer.AddComponent<Teleport>();var it=inner.AddComponent<Teleport>();it.m_targetPoint=ot;ot.m_targetPoint=it;
        object[] exitArgs={crypt.m_prefab.Asset,target,Vector3.zero,Quaternion.identity};
        Check((bool)commands.GetMethod("TryGetExterior",All).Invoke(null,exitArgs),"native paired teleport resolves exterior exit");
        var exit=(Vector3)exitArgs[2];Check(Vector3.Distance(exit,target.GetPosition()+new Vector3(3,0,5))<0.01f,"exterior position includes native teleport forward and height offset");
        var socket=new AdminSocket();var peer=new ZNetPeer(socket,false){m_uid=915,m_refPos=inside};
        var character=NewZdo(inside,"Player".GetStableHashCode());peer.m_characterID=character.m_uid;ZNet.instance.m_peers.Add(peer);
        ZNet.instance.m_adminList.m_list.Clear();
        Check(((string)Call(commands,"ResetTarget",peer.m_rpc)).Contains("refuse"),"single target rejects non-admin");
        ZNet.instance.m_adminList.m_list.Add(socket.GetHostName());
        var evacuations=(System.Collections.IList)commands.GetField("evacuations",All).GetValue(null);
        target.Set(Key("VisitStartedKey"),0L);
        Call(commands,"ResetTarget",peer.m_rpc);
        Check(evacuations.Count==1&&queue.Count==0&&target.GetLong(Key("VisitStartedKey"),0)>0,"inside admin starts evacuation and records real first visit without scheduling reset");
        Call(commands,"ResetTarget",peer.m_rpc);Check(evacuations.Count==1,"repeated command cannot duplicate evacuation");
        Call(commands,"Tick");Call(commands,"Tick");
        Check(queue.Count==0&&evacuations.Count==1,"teleport request alone never authorizes reset while character remains inside");
        character.SetPosition(exit);Call(commands,"Tick");
        Check(queue.Count==1,"confirmed exterior character permits reset despite stale loading reference");
        peer.m_refPos=exit;Call(commands,"Tick");
        Check(evacuations.Count==0&&queue.Count==1&&queue.Peek()==target.m_uid,"confirmed exterior position schedules only originally pinned dungeon");
        queue.Clear();queued.Clear();target.Set(Key("RequestedKey"),false);
        character.SetPosition(inside);peer.m_refPos=inside;Call(commands,"ResetTarget",peer.m_rpc);
        var work=evacuations[0];work.GetType().GetField("Deadline",All).SetValue(work,Time.realtimeSinceStartup-1);
        Call(commands,"Tick");Check(evacuations.Count==0&&queue.Count==0,"evacuation timeout cancels without any reset or retry");
        Call(commands,"ResetTarget",peer.m_rpc);ZNet.instance.m_peers.Remove(peer);Call(commands,"Tick");
        Check(evacuations.Count==0&&queue.Count==0,"disconnect cancels pending evacuation without resetting");
        cached.Remove(target.m_uid);UnityEngine.Object.DestroyImmediate(outer);UnityEngine.Object.DestroyImmediate(inner);
        ZDOMan.instance.HandleDestroyedZDO(character.m_uid);ZDOMan.instance.HandleDestroyedZDO(target.m_uid);
    }

    static void ResetSafetyChecks(ZoneSystem zone, ZoneSystem.ZoneLocation location)
    {
        var scene=ZNetScene.instance;var manager=ZDOMan.instance;
        Vector3 entrance=new Vector3(8192,40,8192), inside=entrance+Vector3.up*5000;
        var anchor=NewZdo(entrance,zone.m_locationProxyPrefab.name.GetStableHashCode());
        anchor.Set(ZDOVars.s_location,location.Hash);anchor.Set(ZDOVars.s_seed,812);anchor.Set(Key("EpochKey"),1);anchor.Set(Key("TrackedKey"),true);
        var staged=Call(runtime,"Stage",anchor,location,new Dictionary<int,ZDO>(),false,444);
        var oldIds=((List<ZDOID>)staged.GetType().GetField("Objects",All).GetValue(staged)).ToArray();
        var tombPrefab=Component<TombStone>("safety-tomb").gameObject;scene.m_namedPrefabs[tombPrefab.name.GetStableHashCode()]=tombPrefab;
        var item=Template("safety-item");var drop=item.AddComponent<ItemDrop>();drop.m_itemData=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData()};drop.m_itemData.m_shared.m_name="keepsake";drop.m_itemData.m_shared.m_maxStackSize=50;
        byte[] inventory=InventoryBytes(item,7,true);
        var tomb1=NewZdo(inside,tombPrefab.name.GetStableHashCode());var tomb2=NewZdo(inside+Vector3.right*2,tombPrefab.name.GetStableHashCode());
        foreach(var tomb in new[]{tomb1,tomb2}) { tomb.Set(ZDOVars.s_items,inventory);tomb.Set(ZDOVars.s_owner,123456L);tomb.Set(ZDOVars.s_ownerName,"fixture owner");tomb.Set(ZDOVars.s_timeOfDeath,123456789L);tomb.Set(ZDOVars.s_spawnPoint,tomb.GetPosition()); }
        var outsideTomb=NewZdo(entrance+Vector3.right*4,tombPrefab.name.GetStableHashCode());Vector3 outsideTombPosition=outsideTomb.GetPosition();
        var chestPrefab=Component<Container>("safety-chest").gameObject;scene.m_namedPrefabs[chestPrefab.name.GetStableHashCode()]=chestPrefab;
        var chest=NewZdo(inside+Vector3.forward*3,chestPrefab.name.GetStableHashCode());chest.Set(ZDOVars.s_items,inventory);chest.Set(ZDOVars.s_inUse,1);chest.Set("overhaul_dungeon_player_storage",true);
        var piecePrefab=Component<Piece>("safety-piece").gameObject;scene.m_namedPrefabs[piecePrefab.name.GetStableHashCode()]=piecePrefab;
        var piece=NewZdo(inside+Vector3.left*3,piecePrefab.name.GetStableHashCode());piece.Set(ZDOVars.s_creator,456L);
        var outsidePiece=NewZdo(entrance+Vector3.left*3,piecePrefab.name.GetStableHashCode());outsidePiece.Set(ZDOVars.s_creator,456L);
        var chestId=chest.m_uid;var pieceId=piece.m_uid;var outsidePieceId=outsidePiece.m_uid;var tomb1Id=tomb1.m_uid;var tomb2Id=tomb2.m_uid;
        var loadedChest=Component<ZNetView>("loaded-old-chest");loadedChest.m_zdo=chest;scene.m_instances[chest]=loadedChest;
        manager.m_onZDODestroyed+=scene.OnZDODestroyed;
        // A real loaded transform/Rigidbody must follow the ZDO instead of overwriting it next tick.
        var view=Component<ZNetView>("loaded-tomb");view.m_zdo=tomb1;view.transform.position=inside;
        var body=view.gameObject.AddComponent<Rigidbody>();body.isKinematic=true;scene.m_instances[tomb1]=view;
        var peer=new ZNetPeer(new AdminSocket(),false){m_uid=713,m_refPos=inside};ZNet.instance.m_peers.Add(peer);
        Call(runtime,"Reset",anchor,location);
        Check(anchor.GetInt(Key("EpochKey"),0)==1&&tomb1.GetPosition()==inside&&manager.GetZDO(chestId)!=null,"player inside defers reset before moving graves or deleting chest");
        peer.m_refPos=entrance;
        var exteriorTeleport=new GameObject("safety-exterior-teleport");exteriorTeleport.transform.SetParent(location.m_prefab.Asset.transform,false);var outside=exteriorTeleport.AddComponent<Teleport>();
        var interiorTeleport=new GameObject("safety-interior-teleport");interiorTeleport.transform.SetParent(location.m_prefab.Asset.transform,false);interiorTeleport.transform.localPosition=Vector3.up*5000;var inner=interiorTeleport.AddComponent<Teleport>();outside.m_targetPoint=inner;inner.m_targetPoint=outside;
        int solidMask=zone.m_solidRayMask,terrainMask=zone.m_terrainRayMask;zone.m_solidRayMask=1;zone.m_terrainRayMask=1;
        Call(runtime,"Reset",anchor,location);
        Check(anchor.GetInt(Key("EpochKey"),0)==1&&tomb1.GetPosition()==inside&&manager.GetZDO(chestId)!=null,"no safe exterior ground retains graves and entire old dungeon");
        var floor=new GameObject("safety-ground");roots.Add(floor);floor.transform.position=entrance-Vector3.up;var collider=floor.AddComponent<BoxCollider>();collider.size=new Vector3(200,2,200);Physics.SyncTransforms();
        Call(runtime,"Reset",anchor,location);
        Check(anchor.GetInt(Key("EpochKey"),0)==2&&oldIds.All(id=>manager.GetZDO(id)==null),"player outside entrance permits native full reset");
        Check(manager.GetZDO(chestId)==null&&manager.GetZDO(pieceId)==null,"filled dungeon chest and interior player construction do not block and are deleted");
        Check(loadedChest.m_zdo==null,"native network destruction clears already loaded old instance");
        Check(manager.GetZDO(outsidePieceId)!=null&&outsideTomb.GetPosition()==outsideTombPosition,"unrelated outside construction and grave unchanged");
        Check(manager.GetZDO(tomb1Id)==tomb1&&manager.GetZDO(tomb2Id)==tomb2&&tomb1.GetPosition().y<3000&&tomb2.GetPosition().y<3000,"all interior graves moved outside without replacing ZDOs");
        Check(tomb1.GetByteArray(ZDOVars.s_items,null).SequenceEqual(inventory)&&tomb2.GetByteArray(ZDOVars.s_items,null).SequenceEqual(inventory)&&tomb1.GetLong(ZDOVars.s_owner,0)==123456&&tomb1.GetString(ZDOVars.s_ownerName,"")=="fixture owner"&&tomb1.GetLong(ZDOVars.s_timeOfDeath,0)==123456789,"grave inventories ownership and death time preserved exactly");
        Check(Vector3.Distance(tomb1.GetPosition(),entrance)>=8&&Vector3.Distance(tomb1.GetPosition(),tomb2.GetPosition())>=3,"graves spaced away from entrance and one another");
        Check(tomb1.GetVec3(ZDOVars.s_spawnPoint,Vector3.zero)==tomb1.GetPosition()&&view.transform.position==tomb1.GetPosition()&&body.position==tomb1.GetPosition(),"spawn anchor and loaded transform follow relocation");
        Check(tomb1.GetString(Key("OwnerKey"),"")==""&&anchor.GetByteArray(Key("JournalKey"),null).Length==0,"grave detached from dungeon and completed journal cleared");
        ZNet.instance.m_peers.Remove(peer);scene.m_instances.Remove(tomb1);view.m_zdo=null;UnityEngine.Object.DestroyImmediate(view.gameObject);
        manager.m_onZDODestroyed-=scene.OnZDODestroyed;
        UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(exteriorTeleport);UnityEngine.Object.DestroyImmediate(interiorTeleport);
        zone.m_solidRayMask=solidMask;zone.m_terrainRayMask=terrainMask;
    }

    static object TombMoves(string identity, Vector3 destination)
    {
        var type=runtime.GetNestedType("TombMove",All);var moves=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
        var move=Activator.CreateInstance(type,true);type.GetField("Identity",All).SetValue(move,identity);type.GetField("Destination",All).SetValue(move,destination);moves.Add(move);return moves;
    }

    static byte[] InventoryBytes(GameObject prefab, int stack, bool custom=false)
    {
        var inv=new Inventory("fixture",null,4,4);
        if(stack>0) { var item=prefab.GetComponent<ItemDrop>().m_itemData.Clone();item.m_dropPrefab=prefab;item.m_stack=stack;item.m_gridPos=new Vector2i(0,0);if(custom)item.m_customData["personal"]="yes";inv.m_inventory.Add(item); }
        var pkg=new ZPackage();inv.Save(pkg);return pkg.GetArray();
    }
    static void PersistenceChecks()
    {
        var manager=ZDOMan.instance;int hash="test-object".GetStableHashCode();
        ZDO anchor=NewZdo(new Vector3(4096,0,4096),hash);
        ZDO old=NewZdo(new Vector3(4097,0,4096),hash), fresh=NewZdo(new Vector3(4098,0,4096),hash);
        string owner=(string)Call(runtime,"Identity",anchor);
        long visited=DateTime.UtcNow.Ticks-TimeSpan.FromHours(4).Ticks;anchor.Set(Key("VisitStartedKey"),visited);
        old.Set(Key("OwnerKey"),owner);old.Set(Key("EpochKey"),0);
        fresh.Set(Key("OwnerKey"),owner);fresh.Set(Key("EpochKey"),1);fresh.Set(Key("PendingKey"),1);
        var tomb=NewZdo(new Vector3(4096,5000,4096),"safety-tomb".GetStableHashCode());byte[] contents=new byte[]{5,8,13,21};tomb.Set(ZDOVars.s_items,contents);tomb.Set(ZDOVars.s_owner,123L);
        Vector3 destination=new Vector3(4106,40.5f,4096);
        byte[] journal=(byte[])Call(runtime,"WriteResetJournal",new List<ZDOID>{old.m_uid},new List<ZDOID>{fresh.m_uid},999,1,DateTime.UtcNow.Ticks,
            new List<Bounds>{new Bounds(tomb.GetPosition(),Vector3.one*100)},TombMoves((string)Call(runtime,"Identity",tomb),destination));
        anchor.Set(Key("JournalKey"),journal);
        Check(ZNetScene.instance.CreateObject(fresh)==null,"uncommitted replacement is hidden from native scene creation");
        var originalIds=new[]{anchor.m_uid,old.m_uid,fresh.m_uid,tomb.m_uid};
        ZDOExtraData.PrepareSave();var saves=new List<byte[]>();
        foreach(var z in new[]{anchor,old,fresh,tomb}) { var pkg=new ZPackage();z.Save(pkg);saves.Add(pkg.GetArray()); }
        ZDOExtraData.ClearSave();
        Call(runtime,"DestroyObjects",originalIds);
        var loaded=new List<ZDO>();
        foreach(byte[] bytes in saves) { var z=ZDOPool.Create();z.Load(new ZPackage(bytes),global::Version.c_WorldVersion);manager.m_objectsByID.Add(z.m_uid,z);manager.InitialAddToSector(z,z.GetSectorIndex());loaded.Add(z); }
        Check(loaded[0].m_uid!=originalIds[0]&&loaded[1].m_uid!=originalIds[1],"native disk reader reassigns ZDO IDs");
        Check(loaded[0].GetString(Key("IdentityKey"),"")==owner&&loaded[2].GetString(Key("OwnerKey"),"")==owner,"stable identity survives native Save/Load");
        Check(loaded[0].GetLong(Key("VisitStartedKey"),0)==visited,"first-visit timestamp survives native world Save/Load");
        var oldId=loaded[1].m_uid;var freshId=loaded[2].m_uid;
        Call(runtime,"FinishCommit",loaded[0],loaded[0].GetByteArray(Key("JournalKey"),null));
        Check(manager.GetZDO(oldId)==null&&manager.GetZDO(freshId)!=null,"commit recovery resolves new native IDs after disk load");
        Check(loaded[2].GetInt(Key("PendingKey"),1)==0,"recovery releases replacement visibility");
        Check(loaded[0].GetLong(Key("VisitStartedKey"),0)==0,"successful recovery also resets visit timestamp to zero");
        Check(loaded[3].GetPosition()==destination&&loaded[3].GetByteArray(ZDOVars.s_items,null).SequenceEqual(contents)&&loaded[3].GetLong(ZDOVars.s_owner,0)==123,"journal recovery relocates saved grave with inventory and owner intact after native ID reassignment");
        Call(runtime,"FinishCommit",loaded[0],journal);
        Check(loaded[3].GetPosition()==destination&&manager.GetZDO(freshId)!=null,"repeated v3 recovery is idempotent for grave and replacement");
        ZDO child=NewZdo(Vector3.zero,hash);Call(runtime,"Inherit",loaded[2],child);
        Check(child.GetString(Key("OwnerKey"),"")==owner&&child.GetInt(Key("EpochKey"),0)==1,"spawned creature inherits stable owner and epoch");
    }
}

