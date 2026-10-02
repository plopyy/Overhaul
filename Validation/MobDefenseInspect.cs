using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
public static class MobDefenseInspect
{
    public static void Run()
    {
        const BindingFlags f=BindingFlags.Static|BindingFlags.NonPublic;
        var loader=typeof(MistlandsBossRoomAuthoring);loader.GetMethod("Sources",f).Invoke(null,null);
        var paths=(Dictionary<string,string>)loader.GetField("paths",f).GetValue(null);var lines=new List<string>();
        foreach(var name in File.ReadAllLines("../../../Tools/AugaWork/native-mob-names.txt"))
        {
            var path=paths.Keys.FirstOrDefault(p=>p.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));if(path==null)continue;
            var prefab=(GameObject)loader.GetMethod("Get",f).Invoke(null,new object[]{path});
            var holder=new GameObject("inactive");holder.SetActive(false);var clone=UnityEngine.Object.Instantiate(prefab,holder.transform);
            foreach(var script in clone.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(script);
            clone.transform.SetParent(null);clone.SetActive(true);
            var anim=clone.GetComponentInChildren<Animator>(true);if(!anim||!anim.runtimeAnimatorController){UnityEngine.Object.DestroyImmediate(clone);UnityEngine.Object.DestroyImmediate(holder);continue;}
            anim.Rebind();anim.Update(0);
            lines.Add(name+" | "+string.Join(",",anim.parameters.Where(p=>p.type==AnimatorControllerParameterType.Trigger||p.name.Contains("block")).Select(p=>p.name+":"+p.type))+" | clips="+string.Join(",",anim.runtimeAnimatorController.animationClips.Select(c=>c.name).Distinct().Where(n=>n.IndexOf("dodg",StringComparison.OrdinalIgnoreCase)>=0||n.IndexOf("evad",StringComparison.OrdinalIgnoreCase)>=0||n.IndexOf("block",StringComparison.OrdinalIgnoreCase)>=0)));
            UnityEngine.Object.DestroyImmediate(clone);UnityEngine.Object.DestroyImmediate(holder);
        }
        File.WriteAllLines("../../../Tools/AugaWork/mob-defense-animations.txt",lines);
    }
}
