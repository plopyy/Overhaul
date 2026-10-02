using UnityEditor;using System.Reflection;using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class SpiritArtCheck {
const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
public static void Run(){ AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new System.Reflection.AssemblyName(a.Name).Name+".dll");return File.Exists(p)?System.Reflection.Assembly.LoadFrom(p):null;};
var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest"))){
if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
}
var entry=paths.First(p=>p.Key.EndsWith("/HelmetDverger.prefab",StringComparison.OrdinalIgnoreCase));Load(entry.Value);var prefab=bundles[entry.Value].LoadAsset<GameObject>(entry.Key);
var sprite=Icon(prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons[0]);var r=sprite.rect;
Save(sprite.texture,"dverger-icon-original.png",new Vector2(r.width/sprite.texture.width,r.height/sprite.texture.height),new Vector2(r.x/sprite.texture.width,r.y/sprite.texture.height),(int)r.width,(int)r.height);
var report=new List<string>();
foreach(var renderer in prefab.GetComponentsInChildren<Renderer>(true)) {
report.Add("RENDERER "+renderer.name);
foreach(var mat in renderer.sharedMaterials) {
if(!mat)continue;report.Add("MATERIAL "+mat.name+" shader="+mat.shader.name);
foreach(var prop in mat.GetTexturePropertyNames()) {var tex=mat.GetTexture(prop);if(!tex)continue;report.Add(prop+" = "+tex.name+" "+tex.width+"x"+tex.height);if(prop=="_MainTex"||prop=="_EmissionMap")Save(tex,"dverger-"+tex.name+".png",Vector2.one,Vector2.zero,tex.width,tex.height);}
for(int i=0;i<ShaderUtil.GetPropertyCount(mat.shader);i++)if(ShaderUtil.GetPropertyType(mat.shader,i)==ShaderUtil.ShaderPropertyType.Color){var prop=ShaderUtil.GetPropertyName(mat.shader,i);report.Add(prop+"="+mat.GetColor(prop));}
}}
File.WriteAllLines("../../../Tools/AugaWork/spirit-art-materials.txt",report);Save(Png("dverger-dvergerhat_d.png"),"dverger-texture-reference-large.png",Vector2.one,Vector2.zero,1024,1024);
if(File.Exists("../../../Tools/AugaWork/spirit-icon-candidate.png"))Preview(prefab);
}
static Texture2D Png(string name){var t=new Texture2D(2,2,TextureFormat.RGBA32,false);ImageConversion.LoadImage(t,File.ReadAllBytes("../../../Tools/AugaWork/"+name));t.filterMode=FilterMode.Point;t.wrapMode=TextureWrapMode.Clamp;return t;}
static void Capture(Camera camera,string name,int width,int height){var rt=new RenderTexture(width,height,24);camera.targetTexture=rt;camera.Render();var old=RenderTexture.active;RenderTexture.active=rt;var t=new Texture2D(width,height,TextureFormat.RGBA32,false);t.ReadPixels(new Rect(0,0,width,height),0,0);t.Apply();File.WriteAllBytes("../../../Tools/AugaWork/"+name,t.EncodeToPNG());RenderTexture.active=old;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(t);}
static void FlatPair(Texture before,Texture after,string name,string title){
var go=new GameObject("Comparison camera");var camera=go.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.13f,.16f);camera.cullingMask=1<<30;
var canvas=new GameObject("Comparison",typeof(Canvas)).GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;
for(int i=0;i<2;i++){
var panel=new GameObject("Asset",typeof(RectTransform),typeof(UnityEngine.UI.RawImage));panel.transform.SetParent(canvas.transform,false);var rect=(RectTransform)panel.transform;rect.anchorMin=rect.anchorMax=new Vector2(i==0?.25f:.75f,.46f);rect.sizeDelta=new Vector2(256,256);panel.GetComponent<UnityEngine.UI.RawImage>().texture=i==0?before:after;
var label=new GameObject("Label",typeof(RectTransform),typeof(UnityEngine.UI.Text));label.transform.SetParent(canvas.transform,false);var lr=(RectTransform)label.transform;lr.anchorMin=lr.anchorMax=new Vector2(i==0?.25f:.75f,.92f);lr.sizeDelta=new Vector2(320,40);var text=label.GetComponent<UnityEngine.UI.Text>();text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.fontSize=22;text.alignment=TextAnchor.MiddleCenter;text.color=Color.white;text.text=i==0?"Avant — Dverger":"Après — Esprit";
}
foreach(var tr in canvas.GetComponentsInChildren<Transform>(true))tr.gameObject.layer=30;
Canvas.ForceUpdateCanvases();Capture(camera,name,640,360);UnityEngine.Object.DestroyImmediate(canvas.gameObject);UnityEngine.Object.DestroyImmediate(go);
}
static void Preview(GameObject prefab){
var icon=Png("spirit-icon-candidate.png");var texture=Png("spirit-texture-candidate.png");
FlatPair(Png("dverger-icon-original.png"),icon,"spirit-icon-before-after.png","Icône");
FlatPair(Png("dverger-dvergerhat_d.png"),texture,"spirit-texture-before-after.png","Texture");
RenderSettings.fog=false;RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.6f,.6f,.6f);
var roots=new List<GameObject>();var bounds=new Bounds();
for(int i=0;i<2;i++){
var source=UnityEngine.Object.Instantiate(prefab);source.SetActive(false);roots.Add(source);
if(i==1){source.name="Overhaul_SpiritCirclet";var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));plugin.GetType("Overhaul.DvergerCirclet").GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{source});}
var model=UnityEngine.Object.Instantiate(source.transform.Find("attach").gameObject);roots.Add(model);model.SetActive(true);model.transform.rotation=Quaternion.Euler(0,155,0);
foreach(var light in model.GetComponentsInChildren<Light>(true))light.enabled=false;
foreach(var renderer in model.GetComponentsInChildren<Renderer>(true)){
renderer.gameObject.layer=30;
}
var renderers=model.GetComponentsInChildren<Renderer>(true);var b=renderers[0].bounds;foreach(var r in renderers)b.Encapsulate(r.bounds);
model.transform.position+=new Vector3(i==0?-.19f:.19f,0,0)-b.center;
}
var lamp=new GameObject("Studio light").AddComponent<Light>();lamp.type=LightType.Directional;lamp.intensity=1.3f;lamp.transform.rotation=Quaternion.Euler(40,-35,0);
var camera=new GameObject("Model camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,.23f,-1);camera.transform.LookAt(Vector3.zero);camera.orthographic=true;camera.orthographicSize=.22f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.13f,.16f);camera.cullingMask=1<<30;
Capture(camera,"spirit-model-integrated.png",1024,512);
foreach(var root in roots)UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(lamp.gameObject);
}
static void Save(Texture texture,string name,Vector2 scale,Vector2 offset,int width,int height){var rt=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);Graphics.Blit(texture,rt,scale,offset);var old=RenderTexture.active;RenderTexture.active=rt;var image=new Texture2D(width,height,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/"+name,image.EncodeToPNG());RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);UnityEngine.Object.DestroyImmediate(image);}static Sprite Icon(Sprite original){foreach(var b in bundles.Values.Where(x=>x))foreach(var n in b.GetAllAssetNames().Where(n=>n.EndsWith(".spriteatlas"))){var atlas=b.LoadAsset<UnityEngine.U2D.SpriteAtlas>(n);var sprite=atlas.GetSprite(original.name);if(!sprite)continue;var entries=(Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(atlas))["SpriteAtlas"]["m_RenderDataMap"];var key=Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(sprite))["Sprite"]["m_RenderDataKey"]["first"].ToString();int index=Enumerable.Range(0,entries.Count).First(i=>entries[i]["first"]["first"].ToString()==key);var r=entries[index]["second"]["textureRect"];var texture=(Texture2D)new SerializedObject(atlas).FindProperty("m_RenderDataMap").GetArrayElementAtIndex(index).FindPropertyRelative("second.texture").objectReferenceValue;return Sprite.Create(texture,new Rect((float)r["x"],(float)r["y"],(float)r["width"],(float)r["height"]),new Vector2(.5f,.5f),100);}return original;}
}
