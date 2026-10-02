using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Playables;
using UnityEditor;
public static class MistlandsBossRoomAuthoring
{
    const string Output="../../../Tools/BossDungeonWork/Mistlands/";
    static Dictionary<string,string> paths=new Dictionary<string,string>();
    static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    const string Reference=@"D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Reference+"Bundles/"+id);if(!bundles[id])throw new Exception(id);}
    static GameObject Get(string path){Load(paths[path]);return bundles[paths[path]].LoadAsset<GameObject>(path);}
    static void Sources()
    {
        Directory.CreateDirectory(Output);
        AssetDatabase.Refresh();string current=null,bundle=null;bool section=false;
        foreach(var line in File.ReadAllLines(Reference+"manifest_extended").Concat(File.ReadAllLines(Reference+"manifest")))
        {if(line=="bundle dependencies:"){section=false;continue;}if(line.StartsWith("asset locations:")){section=true;continue;}if(!section){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;}
    }
    static string PathOf(string name)=>paths.Keys.Single(p=>p.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));
    static void Capture(Camera camera,string name,Vector3 pos,Vector3 target)
    {
        camera.transform.position=pos;camera.transform.LookAt(target);var rt=new RenderTexture(1800,1200,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
        var tex=new Texture2D(1800,1200,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1800,1200),0,0);tex.Apply();
        var pixels=tex.GetPixels32();int visible=pixels.Count(p=>Math.Max(p.r,Math.Max(p.g,p.b))>16);
        if(visible<pixels.Length/100)throw new Exception("Capture is black; published images preserved: "+name);
        Directory.CreateDirectory(Output+"Staging");File.WriteAllBytes(Output+"Staging/"+name+".png",tex.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);
    }

    public static void Build()
    {
        Sources();AppDomain.CurrentDomain.AssemblyResolve+=(sender,a)=>{var path=Path.GetFullPath("../../../Libs/"+new System.Reflection.AssemblyName(a.Name).Name+".dll");return File.Exists(path)?System.Reflection.Assembly.LoadFrom(path):null;};
        var mod=System.Reflection.Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var native=paths.Keys.Where(p=>p.EndsWith(".prefab")&&p.Contains("/Rooms/mistlands/")).Select(Get).Where(g=>g.GetComponent<Room>()&&(g.GetComponent<Room>().m_theme&Room.Theme.DvergerTown)!=0).ToArray();
        var host=new GameObject("Inactive mine templates");host.SetActive(false);
        var root=(GameObject)mod.GetType("Overhaul.Dungeons.MistlandsBossRoom").GetMethod("Build",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{host.transform,native});
        UnityEngine.Random.InitState(270926);foreach(var spawn in root.GetComponentsInChildren<RandomSpawn>(true)){spawn.Prepare();spawn.Randomize(spawn.transform.position+Vector3.up*5000);}foreach(var random in root.GetComponentsInChildren<RandomObject>(true))random.Randomize(random.transform.position+Vector3.up*5000);
        var preview=NativePreview(root);
        var boss=NativePreview(Get(PathOf("SeekerBrute")));boss.transform.position=root.transform.Find("BossSpawn").localPosition;boss.transform.localScale=Vector3.one*2;boss.transform.rotation=Quaternion.Euler(0,180,0);
        var bossNative=Get(PathOf("SeekerBrute"));
        var level=bossNative.GetComponentInChildren<LevelEffects>(true);float starScale=1;
        if(level&&level.m_levelSetups.Count>=2){starScale=level.m_levelSetups[1].m_scale*(bossNative.GetComponent<MonsterAI>().m_pathAgentType==Pathfinding.AgentType.TrollSize?1f:1.15f);var target=preview.transform;var visual=boss.transform.Find(AnimationUtility.CalculateTransformPath(level.transform,bossNative.transform));if(visual)visual.localScale=Vector3.one*starScale;}
        File.WriteAllText(Output+"boss-scale.txt","Native three-star visual scale="+starScale+"; root Scale=2; native path agent="+bossNative.GetComponent<MonsterAI>().m_pathAgentType);
        MeasureBoss(boss,bossNative);
        var chest=NativePreview(Get(PathOf("TreasureChest_dvergrtown")));chest.transform.position=root.transform.Find("RewardChestSpawn").localPosition;
        NativeEnvironment();
        var camera=new GameObject("Preview camera").AddComponent<Camera>();camera.depthTextureMode=DepthTextureMode.Depth|DepthTextureMode.DepthNormals;camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.fieldOfView=78;camera.nearClipPlane=.05f;camera.farClipPlane=150;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.02f,.025f,.03f);
        Capture(camera,"01-entree",new Vector3(0,1.65f,-30),new Vector3(0,2,12));
        Capture(camera,"02-salle",new Vector3(-6,2.5f,-19),new Vector3(2,2,10));
        Capture(camera,"03-promontoire",new Vector3(7,3,-2),new Vector3(0,2,10));
        Capture(camera,"13-coffre-promontoire",new Vector3(5,3,15),new Vector3(0,2.5f,21));
        Capture(camera,"04-retour-entree",new Vector3(6,4,12),new Vector3(0,2,-20));
        Capture(camera,"05-secret-gauche",new Vector3(-16,1.65f,-3),new Vector3(-28,1.5f,0));
        Capture(camera,"06-secret-droite",new Vector3(16,1.65f,3),new Vector3(28,1.5f,0));
        Capture(camera,"07-interieur-secret-gauche",new Vector3(-26.7f,1.5f,-2),new Vector3(-29,1,1));
        Capture(camera,"08-interieur-secret-droite",new Vector3(26.7f,1.5f,2),new Vector3(29,1,-1));
        foreach(var door in root.GetComponentsInChildren<Door>(true))
        {
            var animator=door.GetComponent<Animator>();if(!animator||!animator.runtimeAnimatorController)continue;
            var clip=animator.runtimeAnimatorController.animationClips.FirstOrDefault(c=>c.name.IndexOf("open",StringComparison.OrdinalIgnoreCase)>=0);
            var target=preview.transform.Find(AnimationUtility.CalculateTransformPath(door.transform,root.transform));
            if(clip&&target)
            {
                // The original secret door clip drives its kinematic Rigidbody.
                foreach(var body in door.GetComponentsInChildren<Rigidbody>(true))
                {var t=target.Find(AnimationUtility.CalculateTransformPath(body.transform,door.transform));var copy=t.GetComponent<Rigidbody>();if(!copy)copy=t.gameObject.AddComponent<Rigidbody>();EditorUtility.CopySerialized(body,copy);copy.isKinematic=true;copy.useGravity=false;}
                var copyAnimator=target.GetComponent<Animator>();if(!copyAnimator)copyAnimator=target.gameObject.AddComponent<Animator>();copyAnimator.avatar=animator.avatar;copyAnimator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                var graph=PlayableGraph.Create("NativeDoorOpen");var playable=UnityEngine.Animations.AnimationClipPlayable.Create(graph,clip);var output=UnityEngine.Animations.AnimationPlayableOutput.Create(graph,"Pose",copyAnimator);output.SetSourcePlayable(playable);graph.Play();playable.SetTime(clip.length);graph.Evaluate(0);graph.Destroy();
                clip.SampleAnimation(target.gameObject,clip.length);Physics.SyncTransforms();
                File.AppendAllLines(Output+"door-preview.txt",AnimationUtility.GetCurveBindings(clip).Select(b=>b.path+" "+b.type+" "+b.propertyName));
            }
        }
        Capture(camera,"10-passage-secret-gauche-ouvert",new Vector3(-21,1.65f,0),new Vector3(-29,1.2f,0));
        Capture(camera,"11-passage-secret-droite-ouvert",new Vector3(21,1.65f,0),new Vector3(29,1.2f,0));
        Capture(camera,"12-raccord-entree",new Vector3(0,2,-12),new Vector3(0,3,-24));
        // Capture exact native geometry with a roof cut for plan review.
        foreach(var r in preview.GetComponentsInChildren<Renderer>())if(r.bounds.center.y>6)r.enabled=false;
        Capture(camera,"09-plan-coupe",new Vector3(-28,36,-28),new Vector3(0,0,0));
        foreach(var file in Directory.GetFiles(Output+"Staging","*.png"))File.Copy(file,Output+Path.GetFileName(file),true);
        File.WriteAllText(Output+"preview-notes.txt","Static Unity views of the actual assembled room, using original meshes, native materials/shaders, lights, simulated native particles and InfectedMine environment settings. Camera postprocessing (deferred fog, bloom and color grading) is unavailable in this isolated preview; it remains native in game. No live game session. Boss shown at scale x2 and native three-star visual scale, idle pose. Roof removed only in 09. All captures checked for black output before publication. Package "+mod.GetName().Version);
        File.WriteAllLines(Output+"assembled-components.txt",root.GetComponentsInChildren<Transform>(true).Select(t=>AnimationUtility.CalculateTransformPath(t,root.transform)+" pos="+root.transform.InverseTransformPoint(t.position)+" components="+string.Join(",",t.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name))));
    }
    public static void InspectEnvironment(){Sources();NativeEnvironment();}
    public static void InspectCameras()
    {
        Sources();var game=Get(PathOf("_GameMain"));
        File.WriteAllLines(Output+"cameras.txt",game.GetComponentsInChildren<Component>(true).Where(c=>c&&(c is Camera||c.GetType().Name.Contains("PostProcessing")||c is CameraEffects)).Select(c=>c.GetType()+" "+AnimationUtility.CalculateTransformPath(c.transform,game.transform)+" "+EditorJsonUtility.ToJson(c,true)));
        File.AppendAllLines(Output+"cameras.txt",Resources.FindObjectsOfTypeAll<UnityEngine.PostProcessing.PostProcessingProfile>().Select(p=>p.name+" "+EditorJsonUtility.ToJson(p,true)));
    }
    static void NativeEnvironment()
    {
        var location=Get(PathOf("Mistlands_DvergrTownEntrance1")).GetComponent<Location>();
        var manager=Get(PathOf("_LocationList_Mistlands")).GetComponent<LocationList>();
        File.WriteAllText(Output+"env-manager.json",JsonUtility.ToJson(manager,true));
        File.WriteAllText(Output+"location-environment.txt",location.m_interiorEnvironment+"\n"+string.Join("\n",manager.m_environments.Select(e=>e.m_name)));
        var env=manager.m_environments.Single(e=>e.m_name==location.m_interiorEnvironment);
        File.WriteAllText(Output+"native-environment.json",JsonUtility.ToJson(env,true));
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=env.m_ambColorDay;
        var probe=new SphericalHarmonicsL2();probe.AddAmbientLight(env.m_ambColorDay);RenderSettings.ambientProbe=probe;
        RenderSettings.fog=true;RenderSettings.fogMode=FogMode.Exponential;RenderSettings.fogColor=env.m_fogColorDay;RenderSettings.fogDensity=env.m_fogDensityDay;
        Shader.SetGlobalFloat("_GlobalAlpha",1);Shader.SetGlobalFloat("_WaterLevel",-1000);
        Shader.SetGlobalColor("_AmbientColor",env.m_ambColorDay);Shader.SetGlobalColor("_SunFogColor",env.m_fogColorSunDay);Shader.SetGlobalColor("_SunColor",env.m_sunColorDay*env.m_lightIntensityDay);
        if(env.m_envObject)NativePreview(env.m_envObject);
        foreach(var particles in env.m_psystems??new GameObject[0])if(particles)NativePreview(particles);
    }
    internal static GameObject NativePreview(GameObject source)
    {
        var holder=new GameObject("Inactive visual clone");holder.SetActive(false);
        var clone=UnityEngine.Object.Instantiate(source,holder.transform,false);
        foreach(var water in clone.GetComponentsInChildren<WaterVolume>(true)){water.DetectWaterDepth();water.SetupMaterial();water.m_waterSurface.material.SetFloat("_WaterTime",6);}
        foreach(var b in clone.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(b);
        foreach(var a in clone.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(a);
        foreach(var body in clone.GetComponentsInChildren<Rigidbody>(true)){body.isKinematic=true;body.useGravity=false;}
        clone.transform.SetParent(null,false);clone.transform.position=Vector3.zero;clone.transform.rotation=Quaternion.identity;clone.SetActive(true);
        foreach(var lod in clone.GetComponentsInChildren<LODGroup>(true))lod.ForceLOD(0);
        foreach(var particles in clone.GetComponentsInChildren<ParticleSystem>()){var emission=particles.emission;emission.enabled=true;particles.Simulate(6,true,true);}
        UnityEngine.Object.DestroyImmediate(holder);return clone;
    }
    static void MeasureBoss(GameObject visual,GameObject source)
    {
        var native=source.GetComponentInChildren<Animator>(true);var target=visual.transform.Find(AnimationUtility.CalculateTransformPath(native.transform,source.transform));
        var animator=target.gameObject.AddComponent<Animator>();animator.avatar=native.avatar;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        var results=new List<string>();
        foreach(var clip in native.runtimeAnimatorController.animationClips.Distinct().Where(c=>c.name.IndexOf("death",StringComparison.OrdinalIgnoreCase)<0))
        {
            var graph=UnityEngine.Playables.PlayableGraph.Create("SeekerClearance");var playable=UnityEngine.Animations.AnimationClipPlayable.Create(graph,clip);var output=UnityEngine.Animations.AnimationPlayableOutput.Create(graph,"Pose",animator);output.SetSourcePlayable(playable);graph.Play();Bounds bounds=new Bounds();bool first=true;float radius=0;
            for(int i=0;i<=24;i++){playable.SetTime(clip.length*i/24d);graph.Evaluate(0);foreach(var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>()){var mesh=new Mesh();skin.BakeMesh(mesh,true);foreach(var v in mesh.vertices){var point=skin.transform.TransformPoint(v);var offset=point-visual.transform.position;radius=Mathf.Max(radius,new Vector2(offset.x,offset.z).magnitude);if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);}UnityEngine.Object.DestroyImmediate(mesh);}}
            graph.Destroy();results.Add(clip.name+" min="+bounds.min.ToString("F3")+" max="+bounds.max.ToString("F3")+" horizontal radius="+radius);
            if((clip.name.StartsWith("Walk")||clip.name.StartsWith("Turn"))&&(radius>7.2f||bounds.max.y-visual.transform.position.y>6))throw new Exception("Guardian navigation margin is smaller than moving mesh: "+clip.name+" radius="+radius);
        }
        File.WriteAllLines(Output+"boss-animation-clearance.txt",results);UnityEngine.Object.DestroyImmediate(animator);
        // Restore the native idle pose for the captures after measuring attack animations.
        var idle=native.runtimeAnimatorController.animationClips.First(c=>c.name.IndexOf("idle",StringComparison.OrdinalIgnoreCase)>=0);idle.SampleAnimation(target.gameObject,.3f);
    }
    public static void InspectParts()
    {
        Sources();var report=new List<string>();
        foreach(var name in new[]{"dvergrtown_wall_large","dvergrtown_floor_large","dvergrtown_stair","dvergrtown_gate","dvergrtown_gate_stair","dvergrtown_secretdoor","dvergrtown_2x2x2","DG_DvergrTown"})
        {
            var go=Get(PathOf(name));report.Add("PART "+name+" "+EditorJsonUtility.ToJson(go.GetComponent<Transform>()));
            foreach(var c in go.GetComponentsInChildren<Collider>(true))report.Add(" COLLIDER "+AnimationUtility.CalculateTransformPath(c.transform,go.transform)+" "+EditorJsonUtility.ToJson(c)+" pos="+c.transform.position);
            foreach(var r in go.GetComponentsInChildren<MeshRenderer>(true))if(r.enabled)report.Add(" RENDER "+AnimationUtility.CalculateTransformPath(r.transform,go.transform)+" bounds="+r.bounds+" active="+r.gameObject.activeSelf);
            if(go.GetComponent<DungeonGenerator>())report.Add(EditorJsonUtility.ToJson(go.GetComponent<DungeonGenerator>(),true));
        }
        File.WriteAllLines(Output+"parts.txt",report);
    }
    public static void Inspect()
    {
        Sources();var report=new List<string>();
        foreach(var path in paths.Keys.Where(p=>p.EndsWith(".prefab")&&(p.Contains("/Rooms/mistlands/")||p.EndsWith("/DG_DvergrTown.prefab")||p.EndsWith("/Mistlands_DvergrTownEntrance1.prefab")||p.EndsWith("/SeekerBrute.prefab"))).OrderBy(p=>p))
        {
            var go=Get(path);var room=go.GetComponent<Room>();report.Add("ASSET "+go.name+" "+(room?"theme="+room.m_theme+" size="+room.m_size+" enabled="+room.m_enabled:""));
            foreach(var c in go.GetComponentsInChildren<RoomConnection>(true))report.Add(" PORT "+c.name+" pos="+go.transform.InverseTransformPoint(c.transform.position)+" rot="+c.transform.eulerAngles+" type="+c.m_type);
            if(room&&(room.m_theme&Room.Theme.DvergerTown)==0)continue;
            foreach(var t in go.GetComponentsInChildren<Transform>(true))
            {
                var mf=t.GetComponent<MeshFilter>();var comps=t.GetComponents<Component>().Where(c=>c&&!(c is Transform)&&!(c is MeshFilter)&&!(c is MeshRenderer)).Select(c=>c.GetType().Name);
                report.Add(" NODE "+AnimationUtility.CalculateTransformPath(t,go.transform)+" pos="+go.transform.InverseTransformPoint(t.position).ToString("F2")+" rot="+t.eulerAngles.ToString("F0")+" scale="+t.lossyScale.ToString("F2")+" active="+t.gameObject.activeSelf+" components="+string.Join(",",comps)+(mf&&mf.sharedMesh?" mesh="+mf.sharedMesh.name+" bounds="+mf.sharedMesh.bounds:""));
            }
        }
        File.WriteAllLines(Output+"native-inventory.txt",report);
    }
}


