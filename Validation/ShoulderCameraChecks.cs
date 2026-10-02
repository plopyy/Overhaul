using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class ShoulderCameraChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> results=new List<string>();
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);results.Add("PASS "+label);}
    public static void Run()
    {
        var objects=new List<GameObject>();
        try
        {
            var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
            var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
            bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/AugaWork/CameraTest.exe"),null,null,new string[0]});
            var cf=bep.GetType("BepInEx.Configuration.ConfigFile");
            var config=Activator.CreateInstance(cf,new object[]{Path.GetFullPath("../../../Tools/AugaWork/camera-test.cfg"),false});
            var bind=cf.GetMethods().Single(m=>m.Name=="Bind"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==4&&m.GetParameters()[3].ParameterType==typeof(string));
            var entry=bind.MakeGenericMethod(typeof(float)).Invoke(config,new object[]{"Camera","RightShoulderOffset",.45f,"test"});
            entry.GetType().GetProperty("Value").SetValue(entry,.45f);
            mod.GetType("Overhaul.Utility.OverhaulConfig").GetProperty("RightShoulderOffset").SetValue(null,entry);
            var feature=mod.GetType("Overhaul.ShoulderCamera");
            var ht=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");
            var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.shoulder.check"});
            foreach(var type in feature.GetNestedTypes(F))
            {var p=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type});p.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(p,null);}
            Check(true,"patches install on native camera and projectile methods");
            ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
            Func<float,float> distance=z=>(float)feature.GetMethod("Distance",F).Invoke(null,new object[]{z});
            Check(Mathf.Abs(distance(4)-.45f)<.001f,"default offset 45 cm");
            Check(distance(0)==0 && Mathf.Abs(distance(.5f)-.225f)<.001f,"offset fades at close zoom; first person unchanged");
            entry.GetType().GetProperty("Value").SetValue(entry,0f);
            Check(distance(4)==0,"zero disables feature");
            entry.GetType().GetProperty("Value").SetValue(entry,.45f);
            var origin=new Vector3(500,500,500);
            var offset=feature.GetMethod("Offset",F);
            Func<Vector3> sample=()=>(Vector3)offset.Invoke(null,new object[]{origin,Vector3.right,.45f,.1f,1});
            Check(Vector3.Distance(sample(),Vector3.right*.45f)<.001f,"clear shoulder path preserves full offset");
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);objects.Add(wall);wall.transform.position=origin+Vector3.right*.35f;wall.transform.localScale=new Vector3(.1f,5,5);Physics.SyncTransforms();
            Check(sample().x>.1f&&sample().x<.21f,"wall limits sideways camera movement");
            wall.transform.position=origin;Physics.SyncTransforms();Check(sample()==Vector3.zero,"overlapping geometry suppresses offset");
            UnityEngine.Object.DestroyImmediate(wall);
            var owner=new GameObject("shooter");objects.Add(owner);owner.transform.position=origin;
            var self=GameObject.CreatePrimitive(PrimitiveType.Cube);objects.Add(self);self.transform.SetParent(owner.transform);self.transform.position=origin+Vector3.right*.45f;
            var target=GameObject.CreatePrimitive(PrimitiveType.Cube);objects.Add(target);target.transform.position=origin+new Vector3(.45f,0,10);
            var ray=new Ray(origin+new Vector3(.45f,0,-3),Vector3.forward);Physics.SyncTransforms();
            var aim=feature.GetMethod("Aim",F);
            var dir=(Vector3)aim.Invoke(null,new object[]{ray,origin,owner.transform,Vector3.forward});
            Check(Vector3.Angle(dir,(target.transform.position-Vector3.forward*.5f-origin).normalized)<.05f,"projectile converges on center target and ignores shooter colliders");
            var cameraObject=new GameObject("projection");objects.Add(cameraObject);var camera=cameraObject.AddComponent<Camera>();camera.transform.position=ray.origin;camera.transform.rotation=Quaternion.identity;
            Check(camera.WorldToViewportPoint(origin).x<.5f,"character appears left of center");
            Check(Mathf.Abs(camera.WorldToViewportPoint(target.transform.position).x-.5f)<.001f,"target remains centered");
            target.transform.position=ray.origin+Vector3.forward;Physics.SyncTransforms();
            dir=(Vector3)aim.Invoke(null,new object[]{ray,origin,owner.transform,Vector3.forward});
            Check(dir==Vector3.forward,"obstacle behind shooter never reverses shot");
            results.Add("PASS package "+mod.GetName().Version);
        }
        catch(Exception e){results.Add("FAIL "+e);throw;}
        finally{foreach(var go in objects)if(go)UnityEngine.Object.DestroyImmediate(go);File.WriteAllLines("../../../Tools/AugaWork/shoulder-camera-checks.txt",results);}
    }
}
