using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

public static class MistlandsNativeAudit
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    const string Output="../../../Tools/BossDungeonWork/Mistlands/NativeAudit/";
    static object Call(string n,params object[] a)=>typeof(MistlandsBossRoomAuthoring).GetMethod(n,F).Invoke(null,a);
    public static void Run()
    {
        Directory.CreateDirectory(Output);Call("Sources");
        var paths=(Dictionary<string,string>)typeof(MistlandsBossRoomAuthoring).GetField("paths",F).GetValue(null);
        var host=new GameObject("Inactive source inspection");host.SetActive(false);
        var camera=new GameObject("Audit camera").AddComponent<Camera>();camera.fieldOfView=85;camera.nearClipPlane=.05f;camera.farClipPlane=200;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.055f);
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.2f,.23f,.25f);RenderSettings.fog=false;
        Shader.SetGlobalFloat("_GlobalAlpha",1);Shader.SetGlobalFloat("_Wet",0);Shader.SetGlobalFloat("_WaterLevel",-1000);
        var report=new List<string>();
        foreach(var path in paths.Keys.Where(p=>p.Contains("/Rooms/mistlands/")&&p.EndsWith(".prefab")).OrderBy(p=>p))
        {
            var source=(GameObject)Call("Get",path);var room=source.GetComponent<Room>();if(!room||(room.m_theme&Room.Theme.DvergerTown)==0)continue;
            report.Add("ROOM "+source.name+" "+room.m_size+" music="+room.m_musicPrefab);
            foreach(Transform t in source.transform)report.Add(" GROUP "+t.name+" "+t.localPosition+" "+t.localEulerAngles+" "+t.localScale);
            foreach(var l in source.GetComponentsInChildren<Light>(true))report.Add(" LIGHT "+AnimationUtility.CalculateTransformPath(l.transform,source.transform)+" "+l.color+" range="+l.range+" intensity="+l.intensity);
            foreach(var p in source.GetComponentsInChildren<ParticleSystem>(true))report.Add(" PARTICLES "+AnimationUtility.CalculateTransformPath(p.transform,source.transform)+" "+p.main.startColor.color+" max="+p.main.maxParticles);
            foreach(var m in source.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m).Distinct())report.Add(" MATERIAL "+m.name+" shader="+m.shader.name+" supported="+m.shader.isSupported);
            var clone=UnityEngine.Object.Instantiate(source,host.transform,false);
            foreach(var b in clone.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(b);
            foreach(var l in clone.GetComponentsInChildren<LODGroup>(true))l.ForceLOD(0);
            clone.transform.SetParent(null,false);clone.transform.position=Vector3.zero;
            foreach(var p in clone.GetComponentsInChildren<ParticleSystem>())p.Simulate(5,true,true);
            var port=source.GetComponentsInChildren<RoomConnection>().FirstOrDefault();
            var position=port?port.transform.localPosition+port.transform.forward*1.5f+Vector3.up*1.6f:new Vector3(0,1.6f,-3);
            var target=new Vector3(0,position.y,0);if(Vector3.Distance(position,target)<1)target+=Vector3.forward*3;
            Capture(camera,Output+source.name+"-native.png",position,target);
            clone.SetActive(false);
            var diagnostic=DungeonBossPreview.CopyVisual(source);
            foreach(var lod in source.GetComponentsInChildren<LODGroup>(true))
            {var levels=lod.GetLODs();if(levels.Length==0)continue;var high=new HashSet<Renderer>(levels[0].renderers);foreach(var r in levels.SelectMany(l=>l.renderers).Where(r=>r).Distinct()){var t=diagnostic.transform.Find(AnimationUtility.CalculateTransformPath(r.transform,source.transform));if(t&&t.GetComponent<Renderer>())t.GetComponent<Renderer>().enabled=high.Contains(r);}}
            Capture(camera,Output+source.name+"-structure.png",position,target);
            UnityEngine.Object.DestroyImmediate(clone);UnityEngine.Object.DestroyImmediate(diagnostic);
        }
        File.WriteAllLines(Output+"catalogue.txt",report);
    }
    public static void Capture(Camera camera,string file,Vector3 position,Vector3 target)
    {
        camera.transform.position=position;camera.transform.LookAt(target);var rt=new RenderTexture(1200,800,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
        var tex=new Texture2D(1200,800,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1200,800),0,0);tex.Apply();File.WriteAllBytes(file,tex.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);
    }
}
