using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using UnityEngine.AI;

// Executes production planning/native placement against room metadata extracted from game assets.
// No user world, game process, or on-disk world save is used.
public static class MountainBossChecks
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type layout,encounter;static int checks;static int nativePathLayers;
    static object Call(Type t,string method,params object[] args){return t.GetMethod(method,All).Invoke(null,args);}
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static readonly Dictionary<string,string> paths=new Dictionary<string,string>();
    static readonly Dictionary<string,List<string>> dependencies=new Dictionary<string,List<string>>();
    static readonly Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    const string Reference="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(dependencies.ContainsKey(id))foreach(var d in dependencies[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Reference+"Bundles/"+id);if(!bundles[id])throw new Exception(id);}
    static GameObject Get(string path){Load(paths[path]);return bundles[paths[path]].LoadAsset<GameObject>(path);}
    public static async System.Threading.Tasks.Task Run(Assembly plugin)
    {
        layout=plugin.GetType("Overhaul.Dungeons.BossDungeonLayout",true);encounter=plugin.GetType("Overhaul.Dungeons.BossEncounter",true);
        string current=null,bundle=null;bool section=false;
        foreach(var line in File.ReadAllLines(Reference+"manifest_extended").Concat(File.ReadAllLines(Reference+"manifest")))
        {if(line=="bundle dependencies:"){section=false;continue;}if(line.StartsWith("asset locations:")){section=true;continue;}if(!section){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();dependencies[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))dependencies[current].Add(line.Substring(4).Trim());}else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;}
        GameObject Native(string name)=>Get(paths.Keys.Single(p=>p.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase)));
        var oldLoader=typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",All).GetValue(null);var assets=new DungeonTestAssets();typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",All).SetValue(null,assets);
        var db=DungeonDB.instance;var oldRooms=db.m_rooms.ToArray();var oldMap=db.m_roomByHash.ToArray();db.m_rooms.Clear();db.m_roomByHash.Clear();
        var templates=new GameObject("MountainTemplates");templates.SetActive(false);var generatorObject=new GameObject("DG_Cave");
        var view=generatorObject.AddComponent<ZNetView>();view.m_persistent=true;var generator=generatorObject.AddComponent<DungeonGenerator>();generator.transform.position=new Vector3(0,12000,0);generator.m_zoneCenter=generator.transform.position;
        generator.m_useCustomInteriorTransform=true;generator.m_themes=Room.Theme.Cave;generator.m_minRooms=30;generator.m_maxRooms=60;generator.SetupColliders();
        DungeonDB.RoomData Copy(Room source)
        {
            var go=new GameObject(source.name);go.transform.SetParent(templates.transform,false);go.transform.position=new Vector3(10000,0,0);var room=go.AddComponent<Room>();EditorUtility.CopySerialized(source,room);room.m_musicPrefab=null;room.m_roomConnections=null;
            foreach(var c in source.GetComponentsInChildren<RoomConnection>(true))
            {var child=new GameObject(c.name);child.transform.SetParent(go.transform,false);child.transform.localPosition=c.transform.localPosition;child.transform.localRotation=c.transform.localRotation;EditorUtility.CopySerialized(c,child.AddComponent<RoomConnection>());}
            var data=new DungeonDB.RoomData{m_prefab=assets.Add(go),m_enabled=source.m_enabled,m_theme=source.m_theme};db.m_rooms.Add(data);db.m_roomByHash[data.Hash]=data;return data;
        }
        GameObject fixture=null;
        var binder=plugin.GetType("Overhaul.Dungeons.BossNativeMaterials",true);
        try
        {
            var location=Native("MountainCave02");var loc=location.GetComponent<Location>();var interior=(Transform)Call(plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement"),"FindInterior",loc);var before=interior.localPosition;
            File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/interior-flags.txt", "hasInterior="+loc.m_hasInterior+" custom="+loc.m_useCustomInteriorTransform+" interior="+interior.localPosition);
            var placement=plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement");var position=new Vector3(-128,37,256);object[] args={new ZoneSystem.ZoneLocation{m_prefab=assets.Add(location)},position,ZoneSystem.SpawnMode.Ghost,null};Call(placement,"Prefix",args);
            Check(Math.Abs((loc.m_useCustomInteriorTransform?interior.localPosition.y:interior.GetComponentInChildren<DungeonGenerator>(true).transform.position.y-location.transform.position.y)+position.y-(float)Call(layout,"Height",position))<.01f,"Mountain generator moves to isolated lane: custom="+loc.m_useCustomInteriorTransform+" interior="+interior.position+" local="+interior.localPosition+" root="+location.transform.position+" generator="+interior.GetComponentInChildren<DungeonGenerator>(true).transform.position+" target="+Call(layout,"Height",position)+" state="+(args[3]!=null));
            Check(loc.m_interiorEnvironment=="Caves","Native mountain environment retained");Call(placement,"Finalizer",args[3],null);Check(interior.localPosition==before,"Native location restored");
            var runtime=plugin.GetType("Overhaul.Dungeons.DungeonRuntime");
            var proxy=ZDOMan.instance.CreateNewZDO(position,0);
            var fixedCave=Native("TrollCave02");
            var fixedVolumes=(List<Bounds>)Call(runtime,"BuildVolumes",proxy,fixedCave,new ZDO[0],40f);
            var fixedOccupied=(List<Bounds>)Call(runtime,"OccupiedVolumes",fixedCave.GetComponent<Location>(),fixedVolumes);
            Check(fixedOccupied.Count>0 && fixedOccupied.All(b=>b.center.y>3000),"Native fixed cave without generator supplies interior bounds and does not abort nearby targeting");
            object[] exitArgs={location,proxy,Vector3.zero,Quaternion.identity};
            Check((bool)Call(plugin.GetType("Overhaul.Commands.AdminCommands"),"TryGetExterior",exitArgs),"Native mountain exterior teleport resolves");
            var exit=(Vector3)exitArgs[2];
            var occupied=(List<Bounds>)Call(runtime,"OccupiedVolumes",loc,new List<Bounds>{new Bounds(position,Vector3.one*300),(Bounds)Call(layout,"BoundsFor",position)});
            Check(!occupied.Any(b=>b.Contains(exit)),"Native mountain exit is outside reset occupancy volumes");
            var remote=ZDOMan.instance.CreateNewZDO(occupied[0].center,0);
            var peer=(ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));peer.m_uid=987654;peer.m_characterID=remote.m_uid;peer.m_refPos=occupied[0].center;
            ZNet.instance.m_peers.Add(peer);
            try
            {
                Check(!(bool)Call(runtime,"PlayersClear",occupied),"Mountain player inside blocks reset");
                remote.SetPosition(exit);
                Check((bool)Call(runtime,"PlayersClear",occupied),"Mountain exterior character overrides stale interior loading reference");
                peer.m_refPos=exit;
                Check((bool)Call(runtime,"PlayersClear",occupied),"Mountain reset allowed after character and loading reference exit");
            }
            finally { ZNet.instance.m_peers.Remove(peer);ZDOMan.instance.HandleDestroyedZDO(remote.m_uid);ZDOMan.instance.HandleDestroyedZDO(proxy.m_uid); }
            var nativeRooms=paths.Keys.Where(p=>p.EndsWith(".prefab")&&p.IndexOf("/cave/",StringComparison.OrdinalIgnoreCase)>=0).Select(Get).Where(p=>p.GetComponent<Room>()&&(p.GetComponent<Room>().m_theme&Room.Theme.Cave)!=0).ToArray();
            foreach(var p in nativeRooms)db.m_rooms.Add(new DungeonDB.RoomData{m_prefab=assets.Add(p),m_theme=Room.Theme.Cave});
            fixture=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulMountainBossRoom/Overhaul_MountainCave_BossRoom.prefab"),templates.transform,false);
            Call(binder,"Bind",new object[]{new[]{fixture}});
            var nativeMaterials=new HashSet<Material>(nativeRooms.SelectMany(p=>p.GetComponentsInChildren<Renderer>(true)).SelectMany(r=>r.sharedMaterials).Where(m=>m));
            Check(fixture.GetComponentsInChildren<Renderer>(true).All(r=>r.sharedMaterials.All(m=>!m||nativeMaterials.Contains(m))),"Every arena material is an exact native crypt material: "+string.Join(",",fixture.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m&&!nativeMaterials.Contains(m)).Select(m=>m.name+"/"+m.shader.name).Distinct()));
            nativePathLayers=Native("_GameMain").GetComponentInChildren<Pathfinding>(true).m_layers.value;
            Check(fixture.GetComponentsInChildren<Collider>(true).Where(c=>!c.isTrigger).All(c=>(nativePathLayers&(1<<c.gameObject.layer))!=0),"Every solid collider participates in native navigation");
            fixture.transform.SetParent(null,false);fixture.transform.position=new Vector3(400,12004.5f,400);fixture.SetActive(true);Physics.SyncTransforms();
            int floorMask=fixture.GetComponentsInChildren<Collider>().Where(c=>!c.isTrigger).Aggregate(0,(m,c)=>m|(1<<c.gameObject.layer));
            for(int x=-19;x<=19;x+=2)for(int z=-19;z<=19;z+=2)
                if(x*x+z*z<19*19)Check(Physics.Raycast(new Vector3(400+x,12004,400+z),Vector3.down,out var hit,10,floorMask,QueryTriggerInteraction.Ignore),"Continuous arena floor "+x+","+z);
            for(float z=-34;z<=-17;z+=.25f)
                Check(Physics.Raycast(new Vector3(400,12001,400+z),Vector3.down,out var hit,2,floorMask,QueryTriggerInteraction.Ignore),"Native entrance has no floor gap "+z);
            for(int x=-16;x<=16;x+=4)for(int z=-16;z<=16;z+=4)
                if(x*x+z*z<18*18)Check(Physics.Raycast(new Vector3(400+x,12004,400+z),Vector3.up,22,floorMask,QueryTriggerInteraction.Ignore),"Rock vault covers arena "+x+","+z);
            for(float z=-33;z<=-18;z+=.5f)
            {
                Check(Physics.Raycast(new Vector3(400,12002,400+z),Vector3.down,out var foot,4,floorMask,QueryTriggerInteraction.Ignore),"Entrance walking floor");
                if(z==-33)Check(!Physics.CheckCapsule(foot.point+Vector3.up*.35f,foot.point+Vector3.up*1.65f,.25f,floorMask,QueryTriggerInteraction.Ignore),"Player fits at native doorway");
            }
            // Trace outward through the organic shell at multiple heights. Entrance is the only opening.
            for(int angle=0;angle<360;angle+=3)for(float y=.5f;y<9;y+=1)
            {
                if(Mathf.Abs(Mathf.DeltaAngle(angle,180))<14)continue;
                var direction=Quaternion.Euler(0,angle,0)*Vector3.forward;
                Check(Physics.Raycast(new Vector3(400,12000+y,400),direction,36,floorMask,QueryTriggerInteraction.Ignore),"Closed rock shell at "+angle+" height "+y);
            }
            fixture.SetActive(false);
            await NavigationChecks(fixture);
            UnityEngine.Object.DestroyImmediate(fixture);fixture=null;Call(binder,"Release");db.m_rooms.Clear();
            foreach(var source in nativeRooms.Where(p=>p.GetComponent<Room>().m_enabled))Copy(source.GetComponent<Room>());
            var arena=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulMountainBossRoom/Overhaul_MountainCave_BossRoom.prefab");var arenaData=Copy(arena.GetComponent<Room>());arenaData.m_enabled=false;
            var field=layout.GetField("mountainArena",All);var custom=FormatterServices.GetUninitializedObject(field.FieldType);field.FieldType.GetField("<RoomData>k__BackingField",All).SetValue(custom,arenaData);field.SetValue(null,custom);
            generator.SetupAvailableRooms();var seen=new HashSet<string>();int minDepth=int.MaxValue,maxRooms=0;
            
            
            for(int seed=0;seed<200;seed++)
            {
                generator.m_zoneCenter.y=generator.transform.position.y-110f;generator.m_originalPosition=Vector3.up*110f;generator.m_generatedSeed=seed*7919+31;int objects=ZDOMan.instance.m_objectsByID.Count;try{Call(layout,"Plan",generator);}catch{var depths=(Dictionary<Room,int>)Call(layout,"DistancesFromEntrance");File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/plan-failure.txt","Seed "+generator.m_generatedSeed+" available="+DungeonGenerator.m_availableRooms.Count+"\n"+string.Join("\n",DungeonGenerator.m_placedRooms.Select(r=>r.name+" pos="+r.transform.position+" depth="+(depths.ContainsKey(r)?depths[r]:-1)+" size="+r.m_size))+"\nOPEN\n"+string.Join("\n",DungeonGenerator.m_openConnections.Select(c=>c.GetComponentInParent<Room>().name+" type="+c.m_type+" order="+c.m_placeOrder+" pos="+c.transform.position+" entry="+c.m_entrance+" forward="+c.transform.forward)));var diag=new System.Text.StringBuilder();var copyRooms=DungeonGenerator.m_placedRooms.ToArray();foreach(var c in DungeonGenerator.m_openConnections.Where(c=>c.m_type=="")){generator.CalculateRoomPosRot(arenaData.RoomInPrefab.GetConnections()[0],c.transform.position,c.transform.rotation*Quaternion.Euler(0,180,0),out var bp,out var br);bool collision=generator.TestCollision(arenaData.RoomInPrefab,bp,br);DungeonGenerator.m_placedRooms.Clear();bool empty=generator.TestCollision(arenaData.RoomInPrefab,bp,br);DungeonGenerator.m_placedRooms.AddRange(copyRooms);diag.AppendLine(c.GetComponentInParent<Room>().name+" target="+bp+" collision="+collision+" empty="+empty+" bounds="+generator.m_zoneSize+" center="+generator.m_zoneCenter);}File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/collision-failure.txt",diag.ToString());throw;}
                var rooms=DungeonGenerator.m_placedRooms.ToArray();var boss=rooms.Single(r=>r.name=="Overhaul_MountainCave_BossRoom");
                var distances=(Dictionary<Room,int>)Call(layout,"DistancesFromEntrance");
                if(!distances.ContainsKey(boss))File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/broken-graph.txt",string.Join("\n",rooms.SelectMany(r=>r.GetConnections().Select(c=>r.name+" reachable="+distances.ContainsKey(r)+" "+c.transform.position.ToString("F5")+" "+c.transform.forward.ToString("F5")))));
                Check(distances.ContainsKey(boss)&&distances[boss]>=4,"One boss room connected far from entrance");minDepth=Math.Min(minDepth,distances[boss]);maxRooms=Math.Max(maxRooms,rooms.Length);
                Check(rooms.All(r=>!r.name.StartsWith("Overhaul_Forest")),"Mountain uses only native mountain connector rooms");
                DungeonGenerator.m_placedRooms.Remove(boss);if(generator.TestCollision(boss,boss.transform.position,boss.transform.rotation)){var report=new System.Text.StringBuilder("seed="+generator.m_generatedSeed+" boss="+boss.transform.position+"\n");generator.m_colliderA.size=(Vector3)boss.m_size-Vector3.one*.1f;foreach(var other in DungeonGenerator.m_placedRooms){generator.m_colliderB.size=other.m_size;if(Physics.ComputePenetration(generator.m_colliderA,boss.transform.position,boss.transform.rotation,generator.m_colliderB,other.transform.position,other.transform.rotation,out _,out var distance))report.AppendLine(other.name+" size="+other.m_size+" pos="+other.transform.position+" cap="+other.m_endCap+" divider="+other.m_divider+" depth="+distance);}File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/overlap.txt",report.ToString());throw new Exception("Arena overlap: "+report);}checks++;DungeonGenerator.m_placedRooms.Add(boss);
                Check(ZDOMan.instance.m_objectsByID.Count==objects,"Planning does not spawn monsters or loot");generator.Save();seen.Add(Convert.ToBase64String(view.GetZDO().GetByteArray(ZDOVars.s_roomData,null)));
                var visitRooms=(List<Bounds>)Call(runtime,"ReadVisitRooms",view.GetZDO());
                Check(visitRooms.Count==DungeonGenerator.m_placedRooms.Count,"All native saved mountain rooms can be read by command targeting");
                if(seed<5){generator.Clear();DungeonGenerator.m_placedRooms.Clear();generator.Load();Check(generator.m_loadedRooms.Count(r=>r.m_roomData.m_prefab.Name=="Overhaul_MountainCave_BossRoom")==1,"Saved mountain plan retains one arena");generator.Spawn();Check(ZDOMan.instance.m_objectsByID.Count==objects,"Client reconstruction spawns no new network objects");}
            }
            Check(seen.Count==200,"All mountain seeds generate distinct plans");
            int ek=(int)encounter.GetField("EncounterKey",All).GetValue(null),bk=(int)encounter.GetField("BossKey",All).GetValue(null),dk=(int)encounter.GetField("DefeatedKey",All).GetValue(null);
            var id=Guid.NewGuid().ToString("N");var bossZdo=ZDOMan.instance.CreateNewZDO(Vector3.up*12000,123);bossZdo.SetOwner(742);bossZdo.Set(ek,id);bossZdo.Set(bk,true);
            var chest=ZDOMan.instance.CreateNewZDO(Vector3.up*12000,"Overhaul_MountainBossChest".GetStableHashCode());chest.SetPrefab("Overhaul_MountainBossChest".GetStableHashCode());chest.Set(ek,id);
            Call(encounter,"Unlock",999L,id);Check(!chest.GetBool(dk,false),"Unauthorized peer cannot unlock mountain chest");Call(encounter,"Unlock",742L,id);Check(chest.GetBool(dk,false),"Guardian owner death unlocks mountain chest");
            foreach(var name in ((System.Array)plugin.GetType("Overhaul.Dungeons.BossLootData").GetMethod("For",All).Invoke(null,new object[]{"Mountain"})).Cast<object>().Select(e=>(string)e.GetType().GetField("Prefab",All).GetValue(e)))Check(Native(name).GetComponent<ItemDrop>(),"Mountain reward prefab exists: "+name);
            foreach(var name in (string[])encounter.GetField("MountainRoster",All).GetValue(null))Check(Native(name).GetComponent<Character>(),"Mountain boss prefab exists: "+name);
            foreach(var name in new[]{"Fenring_Cultist","StoneGolem","Ulv"})
                Check((int)Call(encounter,"StarsFor",name)==(name=="StoneGolem"?1:3),"Mountain guardian stars: "+name);
            File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/check-results.txt","PASS "+checks+" checks; 200 distinct native mountain plans; minimum boss graph depth "+minDepth+"; maximum rooms "+maxRooms+". Native materials, floor continuity, native navigation, save/load and network authorization verified.\n");
        }
        finally
        {
            if(fixture)UnityEngine.Object.DestroyImmediate(fixture);Call(binder,"Release");layout.GetField("Planning",All).SetValue(null,false);UnityEngine.Object.DestroyImmediate(generatorObject);UnityEngine.Object.DestroyImmediate(templates);
            db.m_rooms.Clear();db.m_rooms.AddRange(oldRooms);db.m_roomByHash.Clear();foreach(var p in oldMap)db.m_roomByHash.Add(p.Key,p.Value);typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",All).SetValue(null,oldLoader);foreach(var b in bundles.Values.Reverse())if(b)b.Unload(true);
        }
    }
    static async System.Threading.Tasks.Task NavigationChecks(GameObject arena)
    {
        arena.transform.position=new Vector3(400,12004.5f,400);arena.SetActive(true);Physics.SyncTransforms();
        var old=Pathfinding.instance;var go=new GameObject("NativeBossNavigationCheck");var path=go.AddComponent<Pathfinding>();path.enabled=false;
        path.m_layers=nativePathLayers;path.m_waterLayers=0;path.m_layers=path.m_layers.value & ~path.m_waterLayers.value;
        try
        {
            foreach(var agent in new[]{Pathfinding.AgentType.Humanoid,Pathfinding.AgentType.HumanoidBig,Pathfinding.AgentType.TrollSize})
            {
                var lower=path.GetTile(new Vector3(400,0,400),agent);var upper=path.GetTile(new Vector3(400,12000,400),agent);var other=path.GetTile(new Vector3(400,12128,400),agent);
                Check(lower!=upper&&upper!=other&&path.GetTilePos(lower).y==2500&&path.GetTilePos(upper).y==12000,"Surface and vertical dungeon lanes have distinct native navmesh tiles");
                for(int x=-1;x<=1;x++)for(int z=-1;z<=1;z++)
                {
                    var id=upper+new Vector3Int(x,z,0);var tile=path.GetNavTile(id);path.BuildTile(tile);
                    var timeout=DateTime.UtcNow.AddSeconds(20);while(!path.m_buildOperation.isDone&&DateTime.UtcNow<timeout)await System.Threading.Tasks.Task.Yield();
                    if(!path.m_buildOperation.isDone)throw new Exception("Navigation build timeout");path.UpdateAsyncBuild();
                }
                var route=new List<Vector3>();
                Check(path.GetPath(new Vector3(400,12000.2f,413),new Vector3(400,12000.2f,385),route,agent,true)&&route.Count>=2,"Real native navmesh provides complete boss-to-entrance path for "+agent);
                if(agent==Pathfinding.AgentType.Humanoid)Check(path.GetPath(new Vector3(400,12000.2f,367),new Vector3(400,12000.2f,409),route,agent,true)&&route.Count>=2,"Complete player route through the native passage into arena");
                if(agent==Pathfinding.AgentType.Humanoid)
                {
                    bool reaches=path.GetPath(new Vector3(400,12000.2f,412),new Vector3(400,12001f,417),route,agent,true)&&route.Count>=2;
                    if(!reaches){var report=new System.Text.StringBuilder();for(float z=412;z<=418;z+=.25f){Physics.Raycast(new Vector3(400,12005,z),Vector3.down,out var h,10,path.m_layers,QueryTriggerInteraction.Ignore);report.AppendLine(z+" ground="+h.point+" normal="+h.normal+" object="+h.collider?.name);}report.AppendLine("route="+string.Join(";",route));File.WriteAllText("../../../Tools/BossDungeonWork/Mountain/stair-navigation.txt",report.ToString());}
                    Check(reaches,"Player can climb native steps to compact reward dais");
                }
                var hits=new List<Vector3>();path.FindGround(new Vector3(400,12000,400),false,hits,path.GetSettings(agent));
                Check(Physics.Raycast(new Vector3(400,12004,400),Vector3.down,out var ground,10,path.m_layers,QueryTriggerInteraction.Ignore),"Centre floor exists");
                Check(hits.Any(h=>Mathf.Abs(h.y-ground.point.y)<.25f)&&hits.All(h=>h.y>11952&&h.y<12048),"Native navmesh stitching finds the uneven cave floor only in correct vertical lane");
            }
        }
        finally{UnityEngine.Object.DestroyImmediate(go);Pathfinding.m_instance=old;arena.SetActive(false);}
    }
}




