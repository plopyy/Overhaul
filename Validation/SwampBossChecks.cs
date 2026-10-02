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
public static class SwampBossChecks
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type layout,encounter;static int checks;
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
        var templates=new GameObject("SwampTemplates");templates.SetActive(false);var generatorObject=new GameObject("DG_SunkenCrypt");
        var view=generatorObject.AddComponent<ZNetView>();view.m_persistent=true;var generator=generatorObject.AddComponent<DungeonGenerator>();generator.transform.position=new Vector3(0,12000,0);generator.m_zoneCenter=generator.transform.position;
        generator.m_useCustomInteriorTransform=true;generator.m_themes=Room.Theme.SunkenCrypt;generator.m_minRooms=30;generator.m_maxRooms=60;generator.SetupColliders();
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
            var location=Native("SunkenCrypt4");var loc=location.GetComponent<Location>();var interior=(Transform)Call(plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement"),"FindInterior",loc);var before=interior.localPosition;
            var placement=plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement");var position=new Vector3(-128,37,256);object[] args={new ZoneSystem.ZoneLocation{m_prefab=assets.Add(location)},position,ZoneSystem.SpawnMode.Ghost,null};Call(placement,"Prefix",args);
            Check(Math.Abs(interior.GetComponentInChildren<DungeonGenerator>(true).transform.position.y+position.y-(float)Call(layout,"Height",position))<.01f,"Swamp generator moves to isolated lane");
            Check(loc.m_interiorEnvironment=="SunkenCrypt","Native swamp environment retained");Call(placement,"Finalizer",args[3],null);Check(interior.localPosition==before,"Native location restored");
            var nativeRooms=paths.Keys.Where(p=>p.EndsWith(".prefab")&&p.IndexOf("/sunkencrypt_new_",StringComparison.OrdinalIgnoreCase)>=0).Select(Get).Where(p=>p.GetComponent<Room>()).ToArray();
            foreach(var p in nativeRooms)db.m_rooms.Add(new DungeonDB.RoomData{m_prefab=assets.Add(p),m_theme=Room.Theme.SunkenCrypt});
            fixture=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulSwampBossRoom/Overhaul_SunkenCrypt_BossRoom.prefab"),templates.transform,false);
            Call(binder,"Bind",new object[]{new[]{fixture}});
            var nativeMaterials=new HashSet<Material>(nativeRooms.SelectMany(p=>p.GetComponentsInChildren<Renderer>(true)).SelectMany(r=>r.sharedMaterials).Where(m=>m));
            Check(fixture.GetComponentsInChildren<Renderer>(true).All(r=>r.sharedMaterials.All(m=>!m||nativeMaterials.Contains(m))),"Every arena material is an exact native crypt material: "+string.Join(",",fixture.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m&&!nativeMaterials.Contains(m)).Select(m=>m.name+"/"+m.shader.name).Distinct()));
            Check(fixture.GetComponentsInChildren<WaterVolume>(true).Length==1,"Native shallow water retained");
            var podium=fixture.transform.Find("NativePodium");
            Check(Quaternion.Angle(podium.localRotation,Quaternion.Euler(0,90,0))<.01f,"Podium stairs face the entrance");
            Check(Mathf.Abs(podium.localPosition.z-19)<.01f,"Rear stairs embedded in back wall");
            Check(fixture.transform.Find("RewardChestSpawn").localRotation==Quaternion.identity,"Chest orientation unchanged");
            foreach(var bank in fixture.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("MudBank_")))
            foreach(var filter in bank.GetComponentsInChildren<MeshFilter>())
            {
                var points=filter.sharedMesh.vertices.Select(v=>bank.InverseTransformPoint(filter.transform.TransformPoint(v))).ToArray();
                var bounds=new Bounds(points[0],Vector3.zero);foreach(var point in points)bounds.Encapsulate(point);
                Check(points.Where(v=>Mathf.Abs(v.x-bounds.center.x)>bounds.extents.x*.98f||Mathf.Abs(v.z-bounds.center.z)>bounds.extents.z*.98f).All(v=>v.y<-.5f),"Square mud border buried below floor");
            }
            Call(layout,"InstallMushrooms",fixture,Native("Pickable_Mushroom_yellow"));
            var mushrooms=fixture.GetComponentsInChildren<Pickable>(true);Check(mushrooms.Length==22&&mushrooms.All(p=>Mathf.Abs(fixture.transform.InverseTransformPoint(p.transform.position).x)>=12.75f),"Harvestable mushrooms only in outer ring");
            foreach(var mushroom in mushrooms)UnityEngine.Object.DestroyImmediate(mushroom.gameObject);
            fixture.transform.SetParent(null,false);fixture.transform.position=new Vector3(400,12004.5f,400);fixture.SetActive(true);Physics.SyncTransforms();
            int waterMask=fixture.transform.Find("NativeShallowWater").GetComponentsInChildren<Collider>().Aggregate(0,(m,c)=>m|(1<<c.gameObject.layer));
            int floorMask=fixture.GetComponentsInChildren<Collider>().Where(c=>!c.isTrigger).Aggregate(0,(m,c)=>m|(1<<c.gameObject.layer)) & ~waterMask;
            for(int x=-19;x<=19;x+=2)for(int z=-19;z<=19;z+=2)
                Check(Physics.Raycast(new Vector3(400+x,12001.5f,400+z),Vector3.down,out var hit,3,floorMask,QueryTriggerInteraction.Ignore),"Continuous arena floor");
            for(float z=-22;z<=-17;z+=.25f)
                Check(Physics.Raycast(new Vector3(400,12001,400+z),Vector3.down,out var hit,2,floorMask,QueryTriggerInteraction.Ignore),"Native entrance has no floor gap");
            foreach(int side in new[]{-1,1})for(float x=2.1f;x<5.5f;x+=.2f)for(float y=.5f;y<6;y+=.5f)
                Check(Physics.Raycast(new Vector3(400+side*x,12000+y,382),Vector3.back,5,floorMask,QueryTriggerInteraction.Ignore),"Jamb and front wall overlap without a gap");
            fixture.SetActive(false);
            await NavigationChecks(fixture);
            UnityEngine.Object.DestroyImmediate(fixture);fixture=null;Call(binder,"Release");db.m_rooms.Clear();
            foreach(var source in nativeRooms.Where(p=>p.GetComponent<Room>().m_enabled))Copy(source.GetComponent<Room>());
            var arena=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulSwampBossRoom/Overhaul_SunkenCrypt_BossRoom.prefab");var arenaData=Copy(arena.GetComponent<Room>());arenaData.m_enabled=false;
            var field=layout.GetField("swampArena",All);var custom=FormatterServices.GetUninitializedObject(field.FieldType);field.FieldType.GetField("<RoomData>k__BackingField",All).SetValue(custom,arenaData);field.SetValue(null,custom);
            generator.SetupAvailableRooms();var seen=new HashSet<string>();int minDepth=int.MaxValue,maxRooms=0;
            var stairsReport=new System.Text.StringBuilder("seed,stair,port,height,neighbor,endcap\n");
            int stairIndex=0;
            for(int seed=0;seed<200;seed++)
            {
                generator.m_generatedSeed=seed*7919+31;int objects=ZDOMan.instance.m_objectsByID.Count;Call(layout,"Plan",generator);
                var rooms=DungeonGenerator.m_placedRooms.ToArray();var boss=rooms.Single(r=>r.name=="Overhaul_SunkenCrypt_BossRoom");
                var bossPort=boss.GetConnections().Single();
                Check(rooms.Where(r=>r.name=="sunkencrypt_new_Stair1").Any(r=>Vector3.Distance(r.GetConnections().OrderByDescending(c=>c.transform.position.y).First().transform.position,bossPort.transform.position)<.1f),"Boss entrance always connects directly to upper native stair exit");
                Check(rooms.All(r=>Mathf.Abs(r.transform.position.y-generator.transform.position.y)+r.m_size.y*.5f<=40.01f),"Every room stays within the 80m generation height and 96m isolation lane");
                foreach(var stair in rooms.Where(r=>r.name=="sunkencrypt_new_Stair1"))
                {
                    stairIndex++;var ports=stair.GetConnections().OrderBy(c=>c.transform.position.y).ToArray();
                    foreach(var port in ports)
                    {
                        var neighbors=rooms.Where(r=>r!=stair).Where(r=>r.GetConnections().Any(c=>
                            Vector3.Distance(c.transform.position,port.transform.position)<.1f &&
                            Vector3.Dot(c.transform.forward,port.transform.forward)<-.99f && c.m_type==port.m_type)).ToArray();
                        if(neighbors.Length==0)stairsReport.AppendLine(generator.m_generatedSeed+","+stairIndex+","+(port==ports[0]?"bottom":"top")+","+port.transform.position.y.ToString(System.Globalization.CultureInfo.InvariantCulture)+",NONE,false");
                        foreach(var neighbor in neighbors)stairsReport.AppendLine(generator.m_generatedSeed+","+stairIndex+","+(port==ports[0]?"bottom":"top")+","+port.transform.position.y.ToString(System.Globalization.CultureInfo.InvariantCulture)+","+neighbor.name+","+neighbor.m_endCap);
                    }
                }
                var distances=(Dictionary<Room,int>)Call(layout,"DistancesFromEntrance");
                if(!distances.ContainsKey(boss))File.WriteAllText("../../../Tools/BossDungeonWork/Swamp/broken-graph.txt",string.Join("\n",rooms.SelectMany(r=>r.GetConnections().Select(c=>r.name+" reachable="+distances.ContainsKey(r)+" "+c.transform.position.ToString("F5")+" "+c.transform.forward.ToString("F5")))));
                Check(distances.ContainsKey(boss)&&distances[boss]>=4,"One boss room connected far from entrance");minDepth=Math.Min(minDepth,distances[boss]);maxRooms=Math.Max(maxRooms,rooms.Length);
                Check(rooms.All(r=>!r.name.StartsWith("Overhaul_Forest")),"Swamp uses only native swamp connector rooms");
                DungeonGenerator.m_placedRooms.Remove(boss);Check(!generator.TestCollision(boss,boss.transform.position,boss.transform.rotation),"Arena never intersects native rooms");DungeonGenerator.m_placedRooms.Add(boss);
                Check(ZDOMan.instance.m_objectsByID.Count==objects,"Planning does not spawn monsters or loot");generator.Save();seen.Add(Convert.ToBase64String(view.GetZDO().GetByteArray(ZDOVars.s_roomData,null)));
                if(seed<5){generator.Clear();DungeonGenerator.m_placedRooms.Clear();generator.Load();Check(generator.m_loadedRooms.Count(r=>r.m_roomData.m_prefab.Name=="Overhaul_SunkenCrypt_BossRoom")==1,"Saved swamp plan retains one arena");generator.Spawn();Check(ZDOMan.instance.m_objectsByID.Count==objects,"Client reconstruction spawns no new network objects");}
            }
            Check(seen.Count==200,"All swamp seeds generate distinct plans");
            File.WriteAllText("../../../Tools/BossDungeonWork/Swamp/stair-connections.csv",stairsReport.ToString());
            var probe=new System.Text.StringBuilder();
            var stairSource=db.GetRoom("sunkencrypt_new_Stair1".GetStableHashCode()).RoomInPrefab;
            var corridorSource=db.GetRoom("sunkencrypt_new_Corridor1".GetStableHashCode()).RoomInPrefab;
            var originalPlaced=DungeonGenerator.m_placedRooms.ToArray();DungeonGenerator.m_placedRooms.Clear();
            var stairProbe=UnityEngine.Object.Instantiate(stairSource.gameObject);stairProbe.transform.position=new Vector3(0,12000,0);stairProbe.transform.rotation=Quaternion.identity;
            var probeRoom=stairProbe.GetComponent<Room>();DungeonGenerator.m_placedRooms.Add(probeRoom);
            foreach(var port in probeRoom.GetConnections().OrderBy(c=>c.transform.position.y))
            {
                generator.CalculateRoomPosRot(corridorSource.GetConnections()[0],port.transform.position,port.transform.rotation*Quaternion.Euler(0,180,0),out var pos,out var rot);
                bool blocked=generator.TestCollision(corridorSource,pos,rot);
                DungeonGenerator.m_placedRooms.Clear();bool without=generator.TestCollision(corridorSource,pos,rot);DungeonGenerator.m_placedRooms.Add(probeRoom);
                var offset=Vector3.ProjectOnPlane(port.transform.position-stairProbe.transform.position,Vector3.up).normalized*.25f;bool shifted=generator.TestCollision(corridorSource,pos+offset,rot);
                probe.AppendLine("Port "+port.name+" local="+stairProbe.transform.InverseTransformPoint(port.transform.position)+" blocked="+blocked+" withoutStair="+without+" shiftedOut025="+shifted);
            }
            DungeonGenerator.m_placedRooms.Clear();DungeonGenerator.m_placedRooms.AddRange(originalPlaced);UnityEngine.Object.DestroyImmediate(stairProbe);
            File.WriteAllText("../../../Tools/BossDungeonWork/Swamp/stair-collision-probe.txt",probe.ToString());
            int ek=(int)encounter.GetField("EncounterKey",All).GetValue(null),bk=(int)encounter.GetField("BossKey",All).GetValue(null),dk=(int)encounter.GetField("DefeatedKey",All).GetValue(null);
            var id=Guid.NewGuid().ToString("N");var bossZdo=ZDOMan.instance.CreateNewZDO(Vector3.up*12000,123);bossZdo.SetOwner(742);bossZdo.Set(ek,id);bossZdo.Set(bk,true);
            var chest=ZDOMan.instance.CreateNewZDO(Vector3.up*12000,"Overhaul_SwampBossChest".GetStableHashCode());chest.SetPrefab("Overhaul_SwampBossChest".GetStableHashCode());chest.Set(ek,id);
            Call(encounter,"Unlock",999L,id);Check(!chest.GetBool(dk,false),"Unauthorized peer cannot unlock swamp chest");Call(encounter,"Unlock",742L,id);Check(chest.GetBool(dk,false),"Guardian owner death unlocks swamp chest");
            foreach(var name in ((System.Array)plugin.GetType("Overhaul.Dungeons.BossLootData").GetMethod("For",All).Invoke(null,new object[]{"Swamp"})).Cast<object>().Select(e=>(string)e.GetType().GetField("Prefab",All).GetValue(e)))Check(Native(name).GetComponent<ItemDrop>(),"Swamp reward prefab exists: "+name);
            foreach(var name in (string[])encounter.GetField("SwampRoster",All).GetValue(null))Check(Native(name).GetComponent<Character>(),"Swamp boss prefab exists: "+name);
            foreach(var name in new[]{"Greydwarf_Shaman","Greydwarf_Elite","Troll","Draugr_Elite","BlobElite","Abomination","Wraith","Ghost"})
            {
                int expected=name=="BlobElite"||name=="Abomination"?1:name=="Wraith"||name=="Ghost"?2:3;
                Check((int)Call(encounter,"StarsFor",name)==expected,"Per-creature guardian stars: "+name+" "+expected);
                Check(Native(name).GetComponent<Character>(),"Guardian asset available: "+name);
            }
            File.WriteAllText("../../../Tools/BossDungeonWork/Swamp/check-results.txt","PASS "+checks+" checks; 200 distinct native swamp plans; minimum boss graph depth "+minDepth+"; maximum rooms "+maxRooms+". Native materials, harvestables, floor continuity, native navigation, save/load and network authorization verified.\n");
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
        path.m_layers=arena.GetComponentsInChildren<Collider>(true).Where(c=>!c.isTrigger).Aggregate(0,(mask,c)=>mask|(1<<c.gameObject.layer));path.m_waterLayers=arena.transform.Find("NativeShallowWater").GetComponentsInChildren<Collider>().Aggregate(0,(m,c)=>m|(1<<c.gameObject.layer));path.m_layers=path.m_layers.value & ~path.m_waterLayers.value;
        try
        {
            foreach(var agent in new[]{Pathfinding.AgentType.Humanoid,Pathfinding.AgentType.HumanoidBigNoSwim,Pathfinding.AgentType.Abomination})
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
                var hits=new List<Vector3>();path.FindGround(new Vector3(400,12000,400),false,hits,path.GetSettings(agent));
                Check(hits.Any(h=>Mathf.Abs(h.y-12000)<.25f)&&hits.All(h=>h.y>11952&&h.y<12048),"Native navmesh stitching finds dungeon floor only in correct vertical lane");
            }
        }
        finally{UnityEngine.Object.DestroyImmediate(go);Pathfinding.m_instance=old;arena.SetActive(false);}
    }
}
