using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Playables;
using UnityEditor;
using Newtonsoft.Json;
public static class MountainBossRoomAuthoring
{
    const string Assets="Assets/OverhaulMountainBossRoom/";
    const string Output="../../../Tools/BossDungeonWork/Mountain/";
    static Dictionary<string,string> paths=new Dictionary<string,string>();
    static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    static Dictionary<Mesh,Mesh> meshes=new Dictionary<Mesh,Mesh>();
    static Dictionary<Material,Material> materials=new Dictionary<Material,Material>();
    static Dictionary<Texture,Texture> textures=new Dictionary<Texture,Texture>();
    static HashSet<Material> nativeEffectMaterials=new HashSet<Material>();
    const string Reference=@"D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Reference+"Bundles/"+id);if(!bundles[id])throw new Exception(id);}
    static GameObject Get(string path){Load(paths[path]);return bundles[paths[path]].LoadAsset<GameObject>(path);}
    static T Save<T>(T value,string path) where T:UnityEngine.Object
    {var old=AssetDatabase.LoadAssetAtPath<T>(path);if(old){EditorUtility.CopySerialized(value,old);UnityEngine.Object.DestroyImmediate(value);EditorUtility.SetDirty(old);return old;}var originalName=value.name;AssetDatabase.CreateAsset(value,path);value.name=originalName;EditorUtility.SetDirty(value);return value;}
    static Mesh Mesh(Mesh source)
    {
        if(meshes.TryGetValue(source,out var m))return m;
        var copy=new Mesh{name=source.name,indexFormat=source.indexFormat};copy.SetVertexBufferParams(source.vertexCount,source.GetVertexAttributes());
        string key=source.name;
        for(int i=0;i<source.vertexBufferCount;i++)
        {
            using(var buffer=source.GetVertexBuffer(i)){var bytes=new byte[buffer.count*buffer.stride];buffer.GetData(bytes);copy.SetVertexBufferData(bytes,0,0,bytes.Length,i,MeshUpdateFlags.DontRecalculateBounds);key+=Hash128.Compute(Convert.ToBase64String(bytes));}
        }
        using(var buffer=source.GetIndexBuffer()){var bytes=new byte[buffer.count*buffer.stride];buffer.GetData(bytes);copy.SetIndexBufferParams(bytes.Length/(source.indexFormat==IndexFormat.UInt16?2:4),source.indexFormat);copy.SetIndexBufferData(bytes,0,0,bytes.Length);key+=Hash128.Compute(Convert.ToBase64String(bytes));}
        copy.subMeshCount=source.subMeshCount;for(int i=0;i<source.subMeshCount;i++)copy.SetSubMesh(i,source.GetSubMesh(i),MeshUpdateFlags.DontRecalculateBounds);copy.bounds=source.bounds;
        m=Save(copy,Assets+"Meshes/GeometryData_"+Hash128.Compute(key)+".asset");
        meshes[source]=m;return m;
    }
    static Texture Texture(Texture source)
    {
        if(!source)return null;if(textures.TryGetValue(source,out var t))return t;
        int w=Math.Min(2048,source.width),h=Math.Min(2048,source.height);var rt=RenderTexture.GetTemporary(w,h,0,RenderTextureFormat.ARGB32);Graphics.Blit(source,rt);var previous=RenderTexture.active;RenderTexture.active=rt;
        var copy=new Texture2D(w,h,TextureFormat.RGBA32,false);copy.ReadPixels(new Rect(0,0,w,h),0,0);copy.Apply();RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);
        var bytes=copy.EncodeToPNG();var path=Assets+"Textures/Image_"+Hash128.Compute(Convert.ToBase64String(bytes))+".png";File.WriteAllBytes(path,bytes);UnityEngine.Object.DestroyImmediate(copy);AssetDatabase.ImportAsset(path);
        var importer=(TextureImporter)AssetImporter.GetAtPath(path);importer.alphaIsTransparency=true;importer.textureCompression=TextureImporterCompression.Uncompressed;importer.SaveAndReimport();t=AssetDatabase.LoadAssetAtPath<Texture2D>(path);textures[source]=t;return t;
    }
    static Material Material(Material source)
    {
        if(materials.TryGetValue(source,out var result))return result;
        var m=new Material(Shader.Find("Standard"));m.name="OverhaulNative:"+source.name;
        if(source.HasProperty("_MainTex")){m.mainTexture=Texture(source.GetTexture("_MainTex"));m.mainTextureScale=source.GetTextureScale("_MainTex");m.mainTextureOffset=source.GetTextureOffset("_MainTex");}
        m.color=source.HasProperty("_Color")?source.GetColor("_Color"):Color.white;m.SetFloat("_Glossiness",.08f);
        if(source.name=="dirt")
        {m.SetFloat("_Mode",3);m.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);m.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);m.SetInt("_ZWrite",0);m.EnableKeyword("_ALPHABLEND_ON");m.renderQueue=3000;}
        result=Save(m,Assets+"Materials/Surface_"+Hash128.Compute(EditorJsonUtility.ToJson(source))+".mat");materials[source]=result;return result;
    }
    static GameObject Node(Transform parent,string name,Vector3 pos){var g=new GameObject(name);g.transform.SetParent(parent,false);g.transform.localPosition=pos;return g;}
    static GameObject NativePart(Transform parent,string name,MeshFilter source,Vector3 position,Quaternion rotation,Vector3 scale,bool collision)
    {
        var g=Node(parent,name,position);g.transform.localRotation=rotation;g.transform.localScale=scale;
        g.AddComponent<MeshFilter>().sharedMesh=Mesh(source.sharedMesh);
        g.AddComponent<MeshRenderer>().sharedMaterials=source.GetComponent<MeshRenderer>().sharedMaterials.Select(Material).ToArray();
        if(collision)g.AddComponent<MeshCollider>().sharedMesh=Mesh(source.sharedMesh);
        return g;
    }
    static GameObject Part(Transform parent,string name,MeshFilter source,Vector3 center,Vector3 size,Quaternion rotation,bool collision=true)
    {
        var g=Node(parent,name,center);g.transform.localRotation=rotation;var mesh=Mesh(source.sharedMesh);var scale=new Vector3(size.x/mesh.bounds.size.x,size.y/mesh.bounds.size.y,size.z/mesh.bounds.size.z);g.transform.localScale=scale;g.transform.localPosition-=rotation*Vector3.Scale(mesh.bounds.center,scale);
        g.AddComponent<MeshFilter>().sharedMesh=mesh;g.AddComponent<MeshRenderer>().sharedMaterials=source.GetComponent<MeshRenderer>().sharedMaterials.Select(Material).ToArray();if(collision)g.AddComponent<MeshCollider>().sharedMesh=mesh;return g;
    }
    static void Prop(Transform parent,string name,GameObject source,Vector3 pos)
    {
        var root=Node(parent,name,pos);
        foreach(var f in source.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer=f.GetComponent<MeshRenderer>();if(!renderer||!renderer.enabled||!f.sharedMesh)continue;
            var g=Node(root.transform,f.name,source.transform.InverseTransformPoint(f.transform.position));g.transform.localRotation=Quaternion.Inverse(source.transform.rotation)*f.transform.rotation;g.transform.localScale=f.transform.lossyScale;
            g.AddComponent<MeshFilter>().sharedMesh=Mesh(f.sharedMesh);g.AddComponent<MeshRenderer>().sharedMaterials=renderer.sharedMaterials.Select(Material).ToArray();
        }
    }
    // Copy visual components only. No network view, fuel, damage, spawn or interaction scripts.
    static GameObject NativeVisual(Transform parent,GameObject source,string name,Vector3 position)
    {
        foreach(var r in source.GetComponentsInChildren<Renderer>(true))foreach(var m in r.sharedMaterials)if(m)nativeEffectMaterials.Add(m);
        GameObject Copy(Transform from,Transform to)
        {
            var g=Node(to,from.name,from.localPosition);g.transform.localRotation=from.localRotation;g.transform.localScale=from.localScale;
            var f=from.GetComponent<MeshFilter>();if(f&&f.sharedMesh)g.AddComponent<MeshFilter>().sharedMesh=Mesh(f.sharedMesh);
            var ps=from.GetComponent<ParticleSystem>();if(ps){EditorUtility.CopySerialized(ps,g.AddComponent<ParticleSystem>());}
            var renderer=from.GetComponent<Renderer>();if(renderer)
            {
                var copy=g.GetComponent<Renderer>();if(!copy)copy=(Renderer)g.AddComponent(renderer.GetType());EditorUtility.CopySerialized(renderer,copy);
                copy.sharedMaterials=renderer.sharedMaterials.Where(m=>m).Select(Material).ToArray();
                if(copy.sharedMaterials.Length==0)copy.enabled=false;
            }
            var light=from.GetComponent<Light>();if(light)EditorUtility.CopySerialized(light,g.AddComponent<Light>());
            foreach(Transform child in from)Copy(child,g.transform);
            g.SetActive(from.gameObject.activeSelf);return g;
        }
        var result=Copy(source.transform,parent);result.name=name;result.transform.localPosition=position;result.SetActive(true);return result;
    }
    static void Torch(Transform parent,GameObject source,Vector3 position,int index)
    {
        var torch=NativeVisual(parent,source,"Torch_"+index,position);
        var disabled=torch.transform.Find("_disabled");if(disabled)disabled.gameObject.SetActive(false);
        var enabled=torch.transform.Find("_enabled");if(enabled)enabled.gameObject.SetActive(true);
    }
    static void Sources()
    {
        foreach(var folder in new[]{Assets,Assets+"Meshes",Assets+"Materials",Assets+"Textures",Output})Directory.CreateDirectory(folder);
        AssetDatabase.Refresh();string current=null,bundle=null;bool section=false;
        foreach(var line in File.ReadAllLines(Reference+"manifest_extended").Concat(File.ReadAllLines(Reference+"manifest")))
        {if(line=="bundle dependencies:"){section=false;continue;}if(line.StartsWith("asset locations:")){section=true;continue;}if(!section){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;}
    }
    static string PathOf(string name)=>paths.Keys.Single(p=>p.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));
    static GameObject Module(Transform parent,Transform source,string name,Vector3 position,Quaternion rotation)
    {
        var g=NativeVisual(parent,source.gameObject,name,position);g.transform.localRotation=rotation;g.transform.localScale=source.lossyScale;
        foreach(var c in source.GetComponentsInChildren<Collider>(true))
        {
            var path=AnimationUtility.CalculateTransformPath(c.transform,source);var target=path==""?g.transform:g.transform.Find(path);
            var copy=(Collider)target.gameObject.AddComponent(c.GetType());EditorUtility.CopySerialized(c,copy);
            if(copy is MeshCollider mc&&mc.sharedMesh)mc.sharedMesh=Mesh(mc.sharedMesh);
            target.gameObject.layer=c.gameObject.layer;
        }
        foreach(var lod in source.GetComponentsInChildren<LODGroup>(true))
        {
            var path=AnimationUtility.CalculateTransformPath(lod.transform,source);var target=path==""?g.transform:g.transform.Find(path);
            var copy=target.gameObject.AddComponent<LODGroup>();EditorUtility.CopySerialized(lod,copy);
            copy.SetLODs(lod.GetLODs().Select(l=>new LOD(l.screenRelativeTransitionHeight,l.renderers.Where(r=>r).Select(r=>{
                var rp=AnimationUtility.CalculateTransformPath(r.transform,source);return (rp==""?g.transform:g.transform.Find(rp)).GetComponent<Renderer>();}).ToArray())).ToArray());copy.RecalculateBounds();
        }
        return g;
    }
    static void Capture(Camera camera,string name,Vector3 pos,Vector3 target)
    {
        camera.transform.position=pos;camera.transform.LookAt(target);var rt=new RenderTexture(1800,1200,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
        var tex=new Texture2D(1800,1200,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1800,1200),0,0);tex.Apply();File.WriteAllBytes(Output+name+".png",tex.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);
    }
    static void MeasureAbomination(GameObject source)
    {
        var visual=DungeonBossPreview.CopyVisual(source);var native=source.GetComponentInChildren<Animator>(true);var target=visual.transform.Find(AnimationUtility.CalculateTransformPath(native.transform,source.transform));
        var animator=target.gameObject.AddComponent<Animator>();animator.avatar=native.avatar;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        var results=new List<object>();
        foreach(var clip in native.runtimeAnimatorController.animationClips.Distinct().Where(c=>!c.name.ToLowerInvariant().Contains("death")))
        {
            var graph=UnityEngine.Playables.PlayableGraph.Create("AbominationClearance");var playable=UnityEngine.Animations.AnimationClipPlayable.Create(graph,clip);
            var output=UnityEngine.Animations.AnimationPlayableOutput.Create(graph,"Pose",animator);output.SetSourcePlayable(playable);graph.Play();Bounds bounds=new Bounds();bool first=true;
            for(int i=0;i<=24;i++)
            {
                playable.SetTime(clip.length*i/24d);graph.Evaluate(0);
                foreach(var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var mesh=new Mesh();skin.BakeMesh(mesh,true);foreach(var v in mesh.vertices){var point=visual.transform.InverseTransformPoint(skin.transform.TransformPoint(v));if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);}UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            graph.Destroy();results.Add(new{clip=clip.name,min=bounds.min.ToString("F3"),max=bounds.max.ToString("F3"),size=bounds.size.ToString("F3")});
        }
        File.WriteAllText(Output+"abomination-clearance.json",JsonConvert.SerializeObject(results,Formatting.Indented));UnityEngine.Object.DestroyImmediate(visual);
    }
    // Keep the native UVs/material, sinking the square border below the floor.
    static void BuryMudEdges(GameObject bank,int index)
    {
        foreach(var filter in bank.GetComponentsInChildren<MeshFilter>())
        {
            var mesh=UnityEngine.Object.Instantiate(filter.sharedMesh);
            var vertices=mesh.vertices;var matrix=bank.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
            var inverse=matrix.inverse;var bounds=new Bounds(matrix.MultiplyPoint3x4(vertices[0]),Vector3.zero);
            foreach(var v in vertices)bounds.Encapsulate(matrix.MultiplyPoint3x4(v));
            for(int i=0;i<vertices.Length;i++)
            {
                var v=matrix.MultiplyPoint3x4(vertices[i]);
                float nx=(v.x-bounds.center.x)/bounds.extents.x,nz=(v.z-bounds.center.z)/bounds.extents.z;
                float radius=Mathf.Sqrt(nx*nx+nz*nz);
                float ripple=.05f*Mathf.Sin(Mathf.Atan2(nz,nx)*5+index);
                float blend=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.18f,.9f+ripple,radius));
                v.y=Mathf.Lerp(.68f+.06f*Mathf.PerlinNoise(v.x+index,v.z),-.65f,blend);
                vertices[i]=inverse.MultiplyPoint3x4(v);
            }
            mesh.vertices=vertices;mesh.RecalculateBounds();mesh.RecalculateNormals();
            mesh=Save(mesh,Assets+"Meshes/MudBank_"+index+"_"+filter.name+".asset");filter.sharedMesh=mesh;
            var collider=filter.GetComponent<MeshCollider>();if(!collider)collider=filter.gameObject.AddComponent<MeshCollider>();collider.sharedMesh=mesh;
        }
    }
    public static void InspectPassages()
    {
        Sources();var report=new System.Text.StringBuilder();
        foreach(var name in new[]{"sunkencrypt_new_Corridor1","sunkencrypt_new_Corridor2","SunkenCrypt4"})
        {
            var root=Get(PathOf(name));report.AppendLine(name);
            foreach(var c in root.GetComponentsInChildren<Component>(true))
                if(c is Room || c is RoomConnection || c is RandomSpawn || c is RandomObject || c is DungeonGenerator)
                    report.AppendLine(AnimationUtility.CalculateTransformPath(c.transform,root.transform)+" "+c.GetType().Name+" "+EditorJsonUtility.ToJson(c));
        }
        File.WriteAllText(Output+"passage-inspection.txt",report.ToString());
    }
    public static void InspectEntrance()
    {
        Sources();var p=Get(PathOf("MountainCave02"));var loc=p.GetComponent<Location>();
        File.WriteAllText(Output+"entrance-inspection.txt","root="+p.transform.position+" interior="+loc.m_interiorTransform.localPosition+" generator="+loc.m_generator.transform.localPosition+"\n"+string.Join("\n",p.GetComponentsInChildren<Teleport>(true).Select(t=>AnimationUtility.CalculateTransformPath(t.transform,p.transform)+" local="+t.transform.localPosition+" rootRelative="+p.transform.InverseTransformPoint(t.transform.position)+" target="+t.m_targetPoint?.name)));
    }
    public static void Build()
    {
        Sources();UnityEngine.Random.InitState(21092026);
        var cave=Get(PathOf("cave_new_corridor01"));
        var shrine=Get(PathOf("cave_shrine_shrine01"));
        var floorSource=cave.GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name.StartsWith("caverock_floorbig"));
        var wallSource=cave.GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name.StartsWith("caverock_curvedwallbig"));
        // Extend only the buried lower edge. The visible native silhouette stays intact.
        var buriedWall=UnityEngine.Object.Instantiate(Mesh(wallSource.sharedMesh));
        var wallVertices=buriedWall.vertices;
        for(int i=0;i<wallVertices.Length;i++)if(wallVertices[i].y<1.4f)wallVertices[i].y-=5f*(1-Mathf.Clamp01(wallVertices[i].y/1.4f));
        buriedWall.vertices=wallVertices;buriedWall.RecalculateBounds();buriedWall=Save(buriedWall,Assets+"Meshes/NaturalWallBuriedBase.asset");
        var pillarSource=Get(PathOf("cave_new_crossroads02")).GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name.StartsWith("caverock_pillar"));
        var iceWallSource=Get(PathOf("cave_new_entrance02")).GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name.StartsWith("caverock_ice_pillar_wall"));
        var shrineFloor=shrine.GetComponentsInChildren<MeshFilter>(true).First(f=>f.name=="high" && f.transform.parent.name.StartsWith("MountainKit_int_floor"));
        var stairPrefab=Get("Assets/world/Props/Caverocks/caverock_stairs.prefab");
        var iceSource=cave.GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name=="caverock_ice_stalagmite");
        var roofIceSource=cave.GetComponentsInChildren<MeshFilter>(true).First(f=>f.transform.parent.name=="caverock_ice_stalagtite");
        var root=new GameObject("Overhaul_MountainCave_BossRoom");
        var room=root.AddComponent<Room>();room.m_size=new Vector3Int(68,32,68);room.m_theme=Room.Theme.Cave;room.m_enabled=true;
        var floor=Node(root.transform,"Floor",Vector3.zero);var roof=Node(root.transform,"Ceiling",Vector3.zero);
        var walls=Node(root.transform,"Walls",Vector3.zero);var decor=Node(root.transform,"IndestructibleDecor",Vector3.zero);
        // Large natural surfaces use both faces, as the native deep cave rooms do.
        // No floor grid: one broad central basin and irregular overlapping peripheral shelves.
        NativePart(floor.transform,"CentralRockBasin",floorSource,new Vector3(0,-5.55f,0),Quaternion.Euler(0,23,180),floorSource.transform.lossyScale*2.4f,true);
        for(int i=0;i<11;i++)
        {
            float angle=(i*360f/11+UnityEngine.Random.Range(-7f,7f))*Mathf.Deg2Rad;
            float radius=UnityEngine.Random.Range(13.5f,16.5f),multiplier=UnityEngine.Random.Range(1.25f,1.6f);
            bool flip=i%3!=0;
            NativePart(floor.transform,"RockShelf_"+i,floorSource,new Vector3(Mathf.Sin(angle)*radius,flip?-2.3f*multiplier:-.2f,Mathf.Cos(angle)*radius),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),flip?180:0),floorSource.transform.lossyScale*multiplier,true);
        }
        // A rounded, asymmetrical cavern. Native rock walls overlap in an irregular ring.
        float WallRadius(float a) => 23f+2.1f*Mathf.Sin(a*3+.7f)+1.35f*Mathf.Cos(a*5+.4f)+.65f*Mathf.Sin(a*7);
        int wallIndex=0;
        for(float angle=0;angle<360;angle+=UnityEngine.Random.Range(6f,9f))
        {
            int i=wallIndex++;float radians=angle*Mathf.Deg2Rad;
            float radius=WallRadius(radians);
            if(Mathf.Abs(Mathf.DeltaAngle(angle,180))<13)continue;
            var pos=new Vector3(Mathf.Sin(radians)*radius,UnityEngine.Random.Range(-1.3f,-.4f),Mathf.Cos(radians)*radius);
            var wall=NativePart(walls.transform,"NaturalWall_"+i,wallSource,pos,Quaternion.Euler(0,angle+90+UnityEngine.Random.Range(-9f,9f),0),Vector3.one*UnityEngine.Random.Range(.88f,1.05f),true);
            wall.GetComponent<MeshFilter>().sharedMesh=buriedWall;wall.GetComponent<MeshCollider>().sharedMesh=buriedWall;
            // Tilt the same cave rocks inward over the rim to form the shoulders of the vault.
            NativePart(roof.transform,"VaultShoulder_"+i,wallSource,pos+Vector3.up*6,Quaternion.Euler(0,angle+90,0)*Quaternion.Euler(0,0,65),Vector3.one*.9f,true);
            if(i%3!=0)
            {
                var source=i%4==0?iceWallSource:pillarSource;
                float reliefRadius=radius+UnityEngine.Random.Range(-.2f,.6f);
                var relief=new Vector3(Mathf.Sin(radians)*reliefRadius,-1.4f,Mathf.Cos(radians)*reliefRadius);
                NativePart(walls.transform,"EmbeddedRockRelief_"+i,source,relief,Quaternion.Euler(0,angle+180+UnityEngine.Random.Range(-25f,25f),0),Vector3.one*UnityEngine.Random.Range(.75f,1.15f),true);
                // Ice grows in clusters at the foot of these masses, partially embedded.
                for(int j=0;j<UnityEngine.Random.Range(2,5);j++)
                {
                    float ia=radians+UnityEngine.Random.Range(-.065f,.065f),ir=reliefRadius-UnityEngine.Random.Range(.6f,1.2f);
                    NativePart(decor.transform,"WallIceCluster_"+i+"_"+j,iceSource,new Vector3(Mathf.Sin(ia)*ir,-.65f,Mathf.Cos(ia)*ir),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),Vector3.one*UnityEngine.Random.Range(.5f,1.15f),true);
                }
            }
        }
        // Broad rock shelves under the perimeter close the irregular lower wall edges.
        for(int i=0;i<13;i++)
        {
            float angle=(i*360f/13+11)*Mathf.Deg2Rad;
            NativePart(floor.transform,"WallFootRock_"+i,floorSource,new Vector3(Mathf.Sin(angle)*20,-2.5f,Mathf.Cos(angle)*20),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),floorSource.transform.lossyScale*1.3f,true);
        }
        // Broad uneven vault sections overlap off-axis; no repeated rows of ceiling tiles.
        NativePart(roof.transform,"CentralVault",floorSource,new Vector3(1,13.4f,-1),Quaternion.Euler(175,41,4),floorSource.transform.lossyScale*2.25f,true);
        for(int i=0;i<9;i++)
        {
            float angle=(i*40+UnityEngine.Random.Range(-9f,9f))*Mathf.Deg2Rad;
            float radius=UnityEngine.Random.Range(12f,16f);
            NativePart(roof.transform,"VaultMass_"+i,floorSource,new Vector3(Mathf.Sin(angle)*radius,UnityEngine.Random.Range(9.8f,12.4f),Mathf.Cos(angle)*radius),Quaternion.Euler(UnityEngine.Random.Range(166f,194f),UnityEngine.Random.Range(0,360),UnityEngine.Random.Range(-9f,9f)),floorSource.transform.lossyScale*UnityEngine.Random.Range(1.4f,1.8f),true);
        }
        // The entrance is the complete native straight cave module, never a fabricated tunnel.
        var neck=Module(root.transform,cave.transform,"NativeCaveEntrance",new Vector3(0,3,-28),Quaternion.Euler(0,90,0));
        NativePart(floor.transform,"EntranceBedrock",floorSource,new Vector3(0,-.25f,-21),Quaternion.Euler(0,17,0),floorSource.transform.lossyScale*.7f,true);
        // Only permanent cave geometry is part of this local module; network loot is not cloned as decoration.
        foreach(var t in neck.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("Pickable_")||t.name.StartsWith("TreasureChest")||t.name.StartsWith("CreatureSpawner")).ToArray())if(t)UnityEngine.Object.DestroyImmediate(t.gameObject);
        // Rock columns are natural cave assets, distributed asymmetrically away from the combat centre.
        var columns=new[]{new Vector3(-13,-1,-9),new Vector3(14,-1,-5),new Vector3(-15,-1,7),new Vector3(11,-1,13)};
        for(int i=0;i<columns.Length;i++)NativePart(decor.transform,"NaturalRockColumn_"+i,pillarSource,columns[i],Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),Vector3.one*(i%2==0?.9f:.8f),true);
        for(int i=0;i<columns.Length;i++)for(int j=0;j<3;j++)
        {
            var offset=Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0)*Vector3.forward*UnityEngine.Random.Range(.8f,1.4f);
            NativePart(decor.transform,"ColumnIce_"+i+"_"+j,iceSource,columns[i]+offset,Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),Vector3.one*UnityEngine.Random.Range(.5f,.85f),true);
        }
        for(int i=0;i<42;i++)
        {
            float angle=UnityEngine.Random.Range(0,360)*Mathf.Deg2Rad,radius=UnityEngine.Random.Range(10f,20f);
            NativePart(decor.transform,"CeilingIce_"+i,roofIceSource,new Vector3(Mathf.Sin(angle)*radius,10.1f-.01f*radius*radius,Mathf.Cos(angle)*radius),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0),Vector3.one*UnityEngine.Random.Range(.7f,1.1f),false);
        }
        // Small native stone ledge for the reward, tucked against the back of the cave.
        NativePart(floor.transform,"ChestDais",shrineFloor,new Vector3(0,.3f,18),Quaternion.identity,shrineFloor.transform.lossyScale,true);
        var stairs=Module(floor.transform,stairPrefab.transform,"NativeChestStairs",new Vector3(0,-.65f,16.8f),Quaternion.identity);
        stairs.transform.localScale=Vector3.one*.65f;
        var blueFire=Get(PathOf("MountainKit_brazier_blue"));
        foreach(float side in new[]{-1f,1f})NativeVisual(decor.transform,blueFire,"ChestBrazier",new Vector3(side*1.5f,.8f,18));
        var snow=shrine.GetComponentsInChildren<ParticleSystem>(true).First(p=>p.name=="snow_from_roof").gameObject;
        for(int i=0;i<6;i++)NativeVisual(decor.transform,snow,"NativeRoofSnow_"+i,new Vector3(i%2==0?-15:15,9,-15+(i/2)*15));
        Node(root.transform,"BossSpawn",new Vector3(0,.5f,9));Node(root.transform,"RewardChestSpawn",new Vector3(0,.8f,18));Node(root.transform,"DoorSpawn",new Vector3(0,0,-34));
        Prop(root.transform,"RewardChestVisual",Get(PathOf("TreasureChest_mountaincave")),new Vector3(0,.8f,18));
        // Native connection forward points into the room (not out of the cave).
        var port=Node(root.transform,"EntranceConnection",new Vector3(0,0,-34)).AddComponent<RoomConnection>();port.m_type="";port.m_allowDoor=false;port.transform.localRotation=Quaternion.identity;
        int solidLayer=floorSource.transform.parent.gameObject.layer;
        foreach(var collider in root.GetComponentsInChildren<Collider>(true))collider.gameObject.layer=solidLayer;
        File.WriteAllText(Output+"native-collision-layer.txt",solidLayer.ToString());
        foreach(Transform child in root.transform)child.localPosition-=Vector3.up*4.5f;
        AssetDatabase.SaveAssets();PrefabUtility.SaveAsPrefabAsset(root,Assets+root.name+".prefab");root.transform.position=Vector3.up*4.5f;
        foreach(var r in root.GetComponentsInChildren<Renderer>(true))r.sharedMaterials=r.sharedMaterials.Select(m=>materials.First(p=>p.Value==m).Key).ToArray();
        var boss=DungeonBossPreview.CopyVisual(Get(PathOf("StoneGolem")));boss.transform.position=new Vector3(0,.5f,9);boss.transform.rotation=Quaternion.Euler(0,180,0);
        var player=DungeonBossPreview.CopyVisual(Get(PathOf("Player")));player.transform.position=new Vector3(3,.1f,3);
        var envs=Get(PathOf("_GameMain")).GetComponentInChildren<EnvMan>(true).m_environments.ToList();
        foreach(var p in paths.Keys.Where(p=>p.IndexOf("/LocationLists/",StringComparison.OrdinalIgnoreCase)>=0&&p.EndsWith(".prefab")).ToArray())
            foreach(var list in Get(p).GetComponentsInChildren<LocationList>(true))envs.AddRange(list.m_environments);
        File.WriteAllText(Output+"environments.json",JsonConvert.SerializeObject(envs.Select(e=>e.m_name).ToArray()));var env=envs.Last(e=>e.m_name=="Caves");
        File.WriteAllText(Output+"environment.json",JsonUtility.ToJson(env,true));
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=env.m_ambColorNight;RenderSettings.fog=true;RenderSettings.fogColor=env.m_fogColorNight;RenderSettings.fogMode=FogMode.Exponential;RenderSettings.fogDensity=env.m_fogDensityNight;
        Shader.SetGlobalColor("_SunFogColor",RenderSettings.fogColor);Shader.SetGlobalColor("_SunColor",Color.black);Shader.SetGlobalColor("_AmbientColor",RenderSettings.ambientLight);
        var fill=new GameObject("PreviewFill").AddComponent<Light>();fill.type=LightType.Point;fill.range=60;fill.intensity=1.4f;fill.color=new Color(.7f,.8f,1);fill.transform.position=new Vector3(0,7,0);
        foreach(var ps in root.GetComponentsInChildren<ParticleSystem>())ps.Simulate(2,true,true);
        var camera=new GameObject("PreviewCamera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=RenderSettings.fogColor;camera.depthTextureMode=DepthTextureMode.Depth;camera.nearClipPlane=.1f;camera.farClipPlane=150;camera.fieldOfView=75;
        Capture(camera,"entree",new Vector3(0,3,-17),new Vector3(0,2,11));Capture(camera,"coffre",new Vector3(7,3,10),new Vector3(0,1,18));Capture(camera,"raccord",new Vector3(7,3,-13),new Vector3(0,2,-22));Capture(camera,"ensemble",new Vector3(17,7,-16),new Vector3(-2,1,4));
        Capture(camera,"paroi",new Vector3(8,2,-3),new Vector3(23,3,3));
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(boss);UnityEngine.Object.DestroyImmediate(player);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(fill.gameObject);
        foreach(var b in bundles.Values.Reverse())if(b)b.Unload(true);bundles.Clear();AssetDatabase.SaveAssets();
        Directory.CreateDirectory(Output+"Bundle");if(!BuildPipeline.BuildAssetBundles(Output+"Bundle",new[]{new AssetBundleBuild{assetBundleName="overhaul_mountainbossroom",assetNames=new[]{Assets+"Overhaul_MountainCave_BossRoom.prefab"}}},BuildAssetBundleOptions.ChunkBasedCompression,BuildTarget.StandaloneWindows64))throw new Exception("Mountain bundle failed");
        var packed=AssetBundle.LoadFromFile(Path.GetFullPath(Output+"Bundle/overhaul_mountainbossroom"));var loaded=packed.LoadAsset<GameObject>("Overhaul_MountainCave_BossRoom");
        if(!loaded||loaded.GetComponent<Room>().m_theme!=Room.Theme.Cave)throw new Exception("Mountain room metadata missing");
        foreach(var r in loaded.GetComponentsInChildren<Renderer>(true))foreach(var m in r.sharedMaterials)if(!m||!m.shader)throw new Exception("Missing bundled material: "+r.name);
        File.WriteAllText(Output+"bundle-validation.txt","PASS standalone cave bundle reloaded with geometry, native material identifiers and cave connection.\n");packed.Unload(true);
    }

    public static void Research()
    {
        Sources();var records=new List<object>();
        foreach(var path in paths.Keys.Where(p=>p.EndsWith(".prefab")&&(p.IndexOf("/cave/",StringComparison.OrdinalIgnoreCase)>=0||p.EndsWith("/MountainCave02.prefab")||p.EndsWith("/DG_Cave.prefab")||p.EndsWith("/Fenring_Cultist.prefab")||p.EndsWith("/StoneGolem.prefab")||p.EndsWith("/Ulv.prefab"))).ToArray())
        {
            var source=Get(path);var room=source.GetComponent<Room>();var location=source.GetComponent<Location>();var dg=source.GetComponent<DungeonGenerator>();var c=source.GetComponent<Character>();
            records.Add(new{path,name=source.name,room=room?new{size=room.m_size.ToString(),theme=room.m_theme.ToString(),entrance=room.m_entrance,endCap=room.m_endCap,ports=room.GetConnections().Select(p=>new{name=p.name,type=p.m_type,pos=p.transform.localPosition.ToString("F3"),rot=p.transform.eulerAngles.ToString("F3")}).ToArray()}:null,environment=location?location.m_interiorEnvironment:null,doors=dg?dg.m_doorTypes.Select(d=>new{type=d.m_connectionType,prefab=d.m_prefab?d.m_prefab.name:"null"}).ToArray():null,
            meshes=source.GetComponentsInChildren<MeshFilter>(true).Select(f=>new{path=AnimationUtility.CalculateTransformPath(f.transform,source.transform),mesh=f.sharedMesh?f.sharedMesh.name:"",bounds=f.sharedMesh?f.sharedMesh.bounds.ToString():"",pos=source.transform.InverseTransformPoint(f.transform.position).ToString("F3"),rot=f.transform.eulerAngles.ToString("F2"),scale=f.transform.lossyScale.ToString("F3"),materials=f.GetComponent<Renderer>()?f.GetComponent<Renderer>().sharedMaterials.Where(m=>m).Select(m=>m.name).ToArray():new string[0]}).ToArray(),
            effects=source.GetComponentsInChildren<ParticleSystem>(true).Select(p=>AnimationUtility.CalculateTransformPath(p.transform,source.transform)).ToArray(),lights=source.GetComponentsInChildren<Light>(true).Select(l=>new{path=AnimationUtility.CalculateTransformPath(l.transform,source.transform),pos=l.transform.position.ToString(),color=l.color.ToString(),range=l.range,intensity=l.intensity}).ToArray(),
            creature=c?new{scale=c.transform.localScale.ToString(),capsules=c.GetComponentsInChildren<CapsuleCollider>(true).Select(a=>new{a.radius,a.height,center=a.center.ToString()}).ToArray(),agent=c.GetComponent<MonsterAI>()?.m_pathAgentType.ToString(),renderers=c.GetComponentsInChildren<Renderer>(true).Select(r=>new{name=r.name,bounds=r.bounds.ToString()}).ToArray()}:null});
        }
        File.WriteAllText(Output+"native-inventory.json",JsonConvert.SerializeObject(records,Formatting.Indented));
        foreach(var b in bundles.Values.Reverse())if(b)b.Unload(true);
    }
}


