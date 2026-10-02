using System; using UnityEditor; using System.Reflection;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class HoeIconExtract {
const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
static Sprite Icon(Sprite original){foreach(var b in bundles.Values.Where(x=>x))foreach(var n in b.GetAllAssetNames().Where(n=>n.EndsWith(".spriteatlas"))){var atlas=b.LoadAsset<UnityEngine.U2D.SpriteAtlas>(n);var sprite=atlas.GetSprite(original.name);if(!sprite)continue;var entries=(Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(atlas))["SpriteAtlas"]["m_RenderDataMap"];var key=Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(sprite))["Sprite"]["m_RenderDataKey"]["first"].ToString();int index=Enumerable.Range(0,entries.Count).First(i=>entries[i]["first"]["first"].ToString()==key);var r=entries[index]["second"]["textureRect"];var texture=(Texture2D)new SerializedObject(atlas).FindProperty("m_RenderDataMap").GetArrayElementAtIndex(index).FindPropertyRelative("second.texture").objectReferenceValue;return Sprite.Create(texture,new Rect((float)r["x"],(float)r["y"],(float)r["width"],(float)r["height"]),new Vector2(.5f,.5f),100);}return original;}
public static void Run(){ AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new System.Reflection.AssemblyName(a.Name).Name+".dll");return File.Exists(p)?System.Reflection.Assembly.LoadFrom(p):null;};
var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest"))){
if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
}
GameObject Native(string name){var p=paths.First(p=>p.Key.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));Load(p.Value);return bundles[p.Value].LoadAsset<GameObject>(p.Key);}
foreach(var pair in new[]{new[]{"mud_road_v2","hoe-level-icon.png"},new[]{"paved_road_v2","hoe-pave-icon.png"}}){var piece=Native(pair[0]).GetComponent<Piece>();var sprite=Icon(piece.m_icon);var r=sprite.rect;var rt=RenderTexture.GetTemporary((int)r.width,(int)r.height,0,RenderTextureFormat.ARGB32);Graphics.Blit(sprite.texture,rt,new Vector2(r.width/sprite.texture.width,r.height/sprite.texture.height),new Vector2(r.x/sprite.texture.width,r.y/sprite.texture.height));RenderTexture.active=rt;var texture=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();File.WriteAllBytes("../../../Tools/AugaWork/"+pair[1],texture.EncodeToPNG());RenderTexture.active=null;RenderTexture.ReleaseTemporary(rt);}}
}
