using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;

public static class MistlandsBossChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    const string Output="../../../Tools/BossDungeonWork/Mistlands/";
    static readonly List<string> report=new List<string>();
    static object Call(Type t,string n,params object[] args)=>t.GetMethod(n,F).Invoke(null,args);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);report.Add("PASS "+message);}
    static GameObject Native(string name)=>(GameObject)Call(typeof(MistlandsBossRoomAuthoring),"Get",Call(typeof(MistlandsBossRoomAuthoring),"PathOf",name));
    public static void Run()
    {
        try{Test();File.WriteAllLines(Output+"checks.txt",report);}
        catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines(Output+"checks.txt",report);throw;}
    }
    static void Test()
    {
        Call(typeof(MistlandsBossRoomAuthoring),"Sources");
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var layout=mod.GetType("Overhaul.Dungeons.BossDungeonLayout");
        var paths=(Dictionary<string,string>)typeof(MistlandsBossRoomAuthoring).GetField("paths",F).GetValue(null);
        var natives=paths.Keys.Where(p=>p.EndsWith(".prefab")&&p.Contains("/Rooms/mistlands/")).Select(p=>(GameObject)Call(typeof(MistlandsBossRoomAuthoring),"Get",p)).Where(g=>g.GetComponent<Room>()&&(g.GetComponent<Room>().m_theme&Room.Theme.DvergerTown)!=0).ToArray();
        var host=new GameObject("Inactive checks");host.SetActive(false);
        var room=(GameObject)Call(mod.GetType("Overhaul.Dungeons.MistlandsBossRoom"),"Build",host.transform,natives);
        Check(room.GetComponentsInChildren<Room>(true).Length==1&&room.GetComponentsInChildren<RoomConnection>(true).Length==1,"one terminal room and one exact dvergr entrance");
        Check(room.GetComponentsInChildren<Door>(true).Count(d=>d.name.StartsWith("dvergrtown_secretdoor"))==2,"two original working secret doors retained");
        var sourceMaterials=new HashSet<Material>(natives.SelectMany(g=>g.GetComponentsInChildren<Renderer>(true)).SelectMany(r=>r.sharedMaterials));
        Check(room.GetComponentsInChildren<Renderer>(true).All(r=>r.sharedMaterials.All(m=>sourceMaterials.Contains(m))),"all runtime materials are exact native mine material instances");
        Check(room.GetComponentsInChildren<ParticleSystem>(true).Length>0&&room.GetComponentsInChildren<Light>(true).Length>0,"native particles and lights retained");
        foreach(var view in room.GetComponentsInChildren<ZNetView>(true))
            Check(natives.SelectMany(g=>g.GetComponentsInChildren<ZNetView>(true)).Any(v=>Utils.GetPrefabName(v.gameObject)==Utils.GetPrefabName(view.gameObject)),"network prefab name remains native: "+view.name);
        var meta=room.GetComponent<Room>();
        var net=host.AddComponent<ZNet>();ZNet.m_instance=net;typeof(Game).GetProperty("instance",F).SetValue(null,host.AddComponent<Game>());new ZRoutedRpc(true);var manager=new ZDOMan(512);ZNetScene.s_instance=host.AddComponent<ZNetScene>();
        var db=host.AddComponent<DungeonDB>();DungeonDB.m_instance=db;
        var assets=new DungeonTestAssets();typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",F).SetValue(null,assets);
        DungeonDB.RoomData Metadata(Room source)
        {
            var go=new GameObject(source.name);go.transform.SetParent(host.transform,false);var copy=go.AddComponent<Room>();EditorUtility.CopySerialized(source,copy);copy.m_musicPrefab=null;copy.m_roomConnections=null;
            foreach(var c in source.GetComponentsInChildren<RoomConnection>(true)){var child=new GameObject(c.name);child.transform.SetParent(go.transform,false);child.transform.localPosition=c.transform.localPosition;child.transform.localRotation=c.transform.localRotation;EditorUtility.CopySerialized(c,child.AddComponent<RoomConnection>());}
            var data=new DungeonDB.RoomData{m_prefab=assets.Add(go),m_enabled=source.m_enabled,m_theme=source.m_theme};db.m_rooms.Add(data);db.m_roomByHash[data.Hash]=data;return data;
        }
        foreach(var native in natives.Where(g=>g.GetComponent<Room>().m_enabled))Metadata(native.GetComponent<Room>());
        foreach(var name in new[]{"Mistlands_DvergrTownEntrance1","Mistlands_DvergrTownEntrance2"})
        {
            var location=Native(name);var loc=location.GetComponent<Location>();var interior=(Transform)Call(mod.GetType("Overhaul.Dungeons.BossInteriorPlacement"),"FindInterior",loc);var old=interior.localPosition;string environment=loc.m_interiorEnvironment;
            var position=new Vector3(-128,37,256);object[] args={new ZoneSystem.ZoneLocation{m_prefab=assets.Add(location)},position,ZoneSystem.SpawnMode.Ghost,null};Call(mod.GetType("Overhaul.Dungeons.BossInteriorPlacement"),"Prefix",args);
            Check(args[3]!=null&&Mathf.Abs(interior.localPosition.y+position.y-(float)Call(layout,"Height",position))<.1f,"native mine interior moved to isolated lane: "+name);
            Check(!string.IsNullOrEmpty(environment)&&loc.m_interiorEnvironment==environment,"native mine ambiance retained: "+environment);
            Call(mod.GetType("Overhaul.Dungeons.BossInteriorPlacement"),"Finalizer",args[3],null);Check(interior.localPosition==old,"source mine location restored: "+name);
        }
        Check(!(bool)Call(layout,"IsSupported","Mistlands_DvergrBossEntrance1"),"Queen biome dungeon excluded");
        var arena=Metadata(meta);arena.m_enabled=false;
        var field=layout.GetField("mistlandsArena",F);var custom=FormatterServices.GetUninitializedObject(field.FieldType);field.FieldType.GetField("<RoomData>k__BackingField",F).SetValue(custom,arena);field.SetValue(null,custom);
        var generatorObject=new GameObject("DG_DvergrTown");generatorObject.transform.SetParent(host.transform,false);var nv=generatorObject.AddComponent<ZNetView>();nv.m_zdo=manager.CreateNewZDO(Vector3.up*12000,0);nv.m_zdo.SetOwner(ZNet.GetUID());
        var dg=generatorObject.AddComponent<DungeonGenerator>();EditorUtility.CopySerialized(Native("DG_DvergrTown").GetComponent<DungeonGenerator>(),dg);dg.m_nview=nv;dg.transform.position=Vector3.up*12000;dg.m_zoneCenter=dg.transform.position;dg.SetupColliders();dg.SetupAvailableRooms();
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.mistlands.check"});
        foreach(var name in new[]{"BossPlanningPatch"}){var proc=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{mod.GetType("Overhaul.Dungeons."+name)});proc.GetType().GetMethod("Patch").Invoke(proc,null);}
        ZNetView.m_initZDO=nv.m_zdo;generatorObject.transform.SetParent(null,false);
        try
        {
            int minDepth=int.MaxValue;var plans=new HashSet<string>();
            for(int seed=0;seed<200;seed++)
            {
                dg.m_generatedSeed=31+7919*seed;Call(layout,"Plan",dg);
                var boss=DungeonGenerator.m_placedRooms.Single(r=>r.name=="Overhaul_Mistlands_BossRoom");var depths=(Dictionary<Room,int>)Call(layout,"DistancesFromEntrance");
                Check(depths.ContainsKey(boss)&&depths[boss]>=4,"connected unique terminal arena seed "+seed);minDepth=Math.Min(minDepth,depths[boss]);
                DungeonGenerator.m_placedRooms.Remove(boss);Check(!dg.TestCollision(boss,boss.transform.position,boss.transform.rotation),"reserved arena incl secrets has no room overlap seed "+seed);DungeonGenerator.m_placedRooms.Add(boss);
                Check(DungeonGenerator.m_placedRooms.All(r=>r.name.StartsWith("dvergr_")||r==boss),"only native mine connectors seed "+seed);
                dg.Save();plans.Add(Convert.ToBase64String(nv.GetZDO().GetByteArray(ZDOVars.s_roomData,null)));
                if(seed<5){int count=manager.m_objectsByID.Count;dg.Clear();DungeonGenerator.m_placedRooms.Clear();dg.Load();Check(dg.m_loadedRooms.Count(r=>r.m_roomData.m_prefab.Name=="Overhaul_Mistlands_BossRoom")==1,"saved mine retains one boss room "+seed);dg.Spawn();Check(manager.m_objectsByID.Count==count,"client reconstruction creates no new network objects "+seed);}
            }
            Check(plans.Count==200,"200 distinct native plans, minimum boss depth "+minDepth);
        }
        finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);dg.Clear();DungeonGenerator.m_placedRooms.Clear();}
        var secretDoors=room.GetComponentsInChildren<Door>(true).Where(d=>d.name.StartsWith("dvergrtown_secretdoor")).Select(d=>new{go=d.gameObject,clip=d.GetComponent<Animator>().runtimeAnimatorController.animationClips.First(c=>c.name.IndexOf("open",StringComparison.OrdinalIgnoreCase)>=0)}).ToArray();
        // Remove gameplay scripts from this test-only copy before enabling physics.
        foreach(var b in room.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(b);
        room.transform.SetParent(null,false);room.SetActive(true);Physics.SyncTransforms();
        var mask=room.GetComponentsInChildren<Collider>(true).Aggregate(0,(m,c)=>m|(1<<c.gameObject.layer));
        for(float x=-23.5f;x<=23.5f;x+=.5f)for(float z=-23.5f;z<=23.5f;z+=.5f)
            Check(Physics.Raycast(new Vector3(x,6,z),Vector3.down,10,mask,QueryTriggerInteraction.Ignore),"floor sealed "+x+","+z);
        for(float x=-23;x<=23;x+=1)for(float z=-23;z<=23;z+=1)
            Check(Physics.Raycast(new Vector3(x,3,z),Vector3.up,16,mask,QueryTriggerInteraction.Ignore),"vault sealed "+x+","+z);
        for(int angle=0;angle<360;angle++)for(float y=.25f;y<13.9f;y+=.5f)
        {
            if(y<4&&Mathf.Abs(Mathf.DeltaAngle(angle,180))<6)continue;
            Check(Physics.Raycast(new Vector3(0,y,0),Quaternion.Euler(0,angle,0)*Vector3.forward,40,mask,QueryTriggerInteraction.Ignore),"perimeter sealed "+angle+","+y);
        }
        var stairTrace=new List<string>();for(float z=14;z<=23;z+=.1f){Physics.Raycast(new Vector3(0,5,z),Vector3.down,out var hit,10,mask,QueryTriggerInteraction.Ignore);stairTrace.Add(z+" "+hit.point.y+" "+hit.collider?.name);}
        File.WriteAllLines(Output+"stair-trace.txt",stairTrace);
        // Synchronous native Unity navmesh construction against the actual colliders.
        var sources=new List<NavMeshBuildSource>();NavMeshBuilder.CollectSources(room.transform,mask,NavMeshCollectGeometry.PhysicsColliders,0,new List<NavMeshBuildMarkup>(),sources);
        var settings=NavMesh.GetSettingsByIndex(0);settings.agentRadius=.35f;settings.agentHeight=1.8f;settings.agentClimb=.5f;settings.agentSlope=50;
        var nav=NavMeshBuilder.BuildNavMeshData(settings,sources,new Bounds(Vector3.zero,new Vector3(80,40,80)),Vector3.zero,Quaternion.identity);var handle=NavMesh.AddNavMeshData(nav);
        bool Route(Vector3 a,Vector3 b){var route=new NavMeshPath();return NavMesh.CalculatePath(a,b,NavMesh.AllAreas,route)&&route.status==NavMeshPathStatus.PathComplete;}
        Check(Route(new Vector3(0,.1f,-31),new Vector3(0,.1f,0)),"player walks native entrance into arena");
        Check(Route(new Vector3(0,.1f,12),new Vector3(0,2.1f,21)),"player climbs native staircase to chest-only dais");
        Check(!Route(new Vector3(0,.1f,0),new Vector3(-28,.1f,0))&&!Route(new Vector3(0,.1f,0),new Vector3(28,.1f,0)),"closed secret doors block player access");
        NavMesh.RemoveNavMeshData(handle);UnityEngine.Object.DestroyImmediate(nav);
        foreach(var door in secretDoors){door.clip.SampleAnimation(door.go,door.clip.length);report.Add("Native secret door animation "+door.clip.name);}
        Physics.SyncTransforms();sources.Clear();NavMeshBuilder.CollectSources(room.transform,mask,NavMeshCollectGeometry.PhysicsColliders,0,new List<NavMeshBuildMarkup>(),sources);
        nav=NavMeshBuilder.BuildNavMeshData(settings,sources,new Bounds(Vector3.zero,new Vector3(80,40,80)),Vector3.zero,Quaternion.identity);handle=NavMesh.AddNavMeshData(nav);
        Check(Route(new Vector3(0,.1f,0),new Vector3(-28,.1f,0)),"player enters left secret chamber through open native door");
        Check(Route(new Vector3(0,.1f,0),new Vector3(28,.1f,0)),"player enters right secret chamber through open native door");
        NavMesh.RemoveNavMeshData(handle);UnityEngine.Object.DestroyImmediate(nav);
        var capsule=Native("SeekerBrute").GetComponent<CapsuleCollider>();settings.agentRadius=capsule.radius*2;settings.agentHeight=capsule.height*2;settings.agentClimb=.5f;
        report.Add("Boss capsule radius="+settings.agentRadius+" height="+settings.agentHeight);
        nav=NavMeshBuilder.BuildNavMeshData(settings,sources,new Bounds(Vector3.zero,new Vector3(80,40,80)),Vector3.zero,Quaternion.identity);handle=NavMesh.AddNavMeshData(nav);
        var spawn=room.transform.Find("BossSpawn").position;
        Check(spawn.y<.2f&&room.transform.Find("RewardChestSpawn").position.y>2,"boss starts on arena floor and chest alone is elevated");
        foreach(var point in new[]{new Vector3(0,.1f,-16),new Vector3(-10,.1f,0),new Vector3(10,.1f,0),new Vector3(-10,.1f,10),new Vector3(10,.1f,10),new Vector3(-10,.1f,-10),new Vector3(10,.1f,-10)})
            Check(Route(spawn,point),"boss physical capsule reaches arena sector "+point);
        NavMesh.RemoveNavMeshData(handle);UnityEngine.Object.DestroyImmediate(nav);
        var navigation=mod.GetType("Overhaul.Dungeons.BossMistlandsNavigation");
        var pathfinder=host.AddComponent<Pathfinding>();Pathfinding.m_instance=pathfinder;pathfinder.SetupAgents();
        var guardian=UnityEngine.Object.Instantiate(Native("SeekerBrute"),host.transform);guardian.name="SeekerBrute";guardian.transform.localScale=Vector3.one*2;
        var character=guardian.GetComponent<Character>();character.m_collider=guardian.GetComponent<CapsuleCollider>();character.m_nview=guardian.GetComponent<ZNetView>();character.m_nview.m_zdo=manager.CreateNewZDO(Vector3.zero,"SeekerBrute".GetStableHashCode());character.m_nview.m_zdo.Set("overhaul_boss_character_v1".GetStableHashCode(),true);
        var ai=guardian.GetComponent<MonsterAI>();ai.m_character=character;int originalAgents=pathfinder.m_agentSettings.Count;var ordinary=ai.m_pathAgentType;
        Call(navigation,"Ensure",ai);settings=pathfinder.m_agentSettings[(int)ai.m_pathAgentType].m_build;
        Check(ai.m_pathAgentType!=ordinary&&Mathf.Approximately(settings.agentRadius,7.2f)&&Mathf.Approximately(settings.agentHeight,6),"live guardian pathfinding uses measured visual clearance radius7.2 height6");
        Call(navigation,"Ensure",ai);Check(pathfinder.m_agentSettings.Count==originalAgents+1,"guardian navigation profile reused without growing per request");
        report.Add("Visual walking/turning clearance radius="+settings.agentRadius+" height="+settings.agentHeight);
        nav=NavMeshBuilder.BuildNavMeshData(settings,sources,new Bounds(Vector3.zero,new Vector3(80,40,80)),Vector3.zero,Quaternion.identity);handle=NavMesh.AddNavMeshData(nav);
        var filter=new NavMeshQueryFilter{agentTypeID=settings.agentTypeID,areaMask=NavMesh.AllAreas};
        File.WriteAllLines(Output+"wide-nav-debug.txt",room.GetComponentsInChildren<Collider>().Where(c=>!c.isTrigger&&c.bounds.max.y>.5f&&c.bounds.min.y<6&&Vector2.Distance(new Vector2(c.bounds.center.x,c.bounds.center.z),new Vector2(spawn.x,spawn.z))<18).Select(c=>AnimationUtility.CalculateTransformPath(c.transform,room.transform)+" "+c.bounds));
        File.AppendAllLines(Output+"wide-nav-debug.txt",NavMesh.CalculateTriangulation().vertices.Select(v=>v.ToString()));
        var points=new[]{spawn,new Vector3(0,.1f,-14),new Vector3(-10,.1f,0),new Vector3(10,.1f,0),new Vector3(0,.1f,0)};
        foreach(var a in points)foreach(var b in points){var route=new NavMeshPath();Check(NavMesh.CalculatePath(a,b,filter,route)&&route.status==NavMeshPathStatus.PathComplete,"wide boss route between pillars "+a+" -> "+b);}
        NavMesh.RemoveNavMeshData(handle);UnityEngine.Object.DestroyImmediate(nav);
        report.Add("PASS assertions="+report.Count+" package="+mod.GetName().Version);
    }
}
