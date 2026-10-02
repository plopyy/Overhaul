using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
public static class CircletFogInspect
{
    const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
    public static void Run()
    {
        var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
        foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest"))) {
            if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
            else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
            else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
        }
        var output=new List<string>();
        foreach(var path in paths.Where(p=>p.Key.StartsWith("Assets/Effects/materials/",StringComparison.OrdinalIgnoreCase)&&p.Key.EndsWith(".mat")&&(p.Key.ToLowerInvariant().Contains("mist")||p.Key.ToLowerInvariant().Contains("fog")||p.Key.ToLowerInvariant().Contains("smoke")||p.Key.ToLowerInvariant().Contains("dust")))) {
            Load(path.Value);var mat=bundles[path.Value].LoadAsset<Material>(path.Key);if(!mat)continue;
            output.Add(path.Key+" shader="+mat.shader.name);
            for(int i=0;i<ShaderUtil.GetPropertyCount(mat.shader);i++) {
                var name=ShaderUtil.GetPropertyName(mat.shader,i);var type=ShaderUtil.GetPropertyType(mat.shader,i);
                if(type==ShaderUtil.ShaderPropertyType.Color)output.Add("  "+name+" = "+mat.GetColor(name));
                if(type==ShaderUtil.ShaderPropertyType.Float||type==ShaderUtil.ShaderPropertyType.Range)output.Add("  "+name+" = "+mat.GetFloat(name));
            }
        }
        File.WriteAllLines("../../../Tools/AugaWork/circlet-fog-materials.txt",output);
    }
}
