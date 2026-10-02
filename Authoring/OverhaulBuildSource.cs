using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Valheim.SettingsGui;
public static class OverhaulBuildSource
{
 public static readonly List<AssetBundle> Bundles=new List<AssetBundle>();
 public static AssetBundle Load(){
 var deps=new Dictionary<string,List<string>>();string current=null;
 foreach(var line in File.ReadAllLines(@"D:\Valheim\valheim_Data\StreamingAssets\SoftRef\manifest")){
 if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}
 else if(current!=null && line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());
 else if(line.StartsWith("assets:"))break;
 }
 var loaded=new Dictionary<string,AssetBundle>();
 Action<string> visit=null;visit=id=>{if(loaded.ContainsKey(id))return;loaded[id]=null;if(deps.ContainsKey(id))foreach(var dep in deps[id])visit(dep);var b=AssetBundle.LoadFromFile(@"D:\Valheim\valheim_Data\StreamingAssets\SoftRef\Bundles\"+id);if(!b)throw new Exception("Bundle rejected "+id);loaded[id]=b;Bundles.Add(b);};
 visit("d59cfac");return loaded["d59cfac"];
 }
 public static void InspectLines(){
foreach(var method in typeof(UnityEngine.U2D.SpriteAtlasManager).GetMethods(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static))Debug.Log("ATLAS METHOD "+method);var bundle=Load();foreach(var path in bundle.GetAllAssetNames().Where(n=>n.EndsWith(".spriteatlas") || n.ToLower().Contains("ps5_gamepad"))){var asset=bundle.LoadAsset(path);Debug.Log("ATLAS ASSET "+path+" "+asset.GetType()+" "+EditorJsonUtility.ToJson(asset));if(asset is UnityEngine.U2D.SpriteAtlas atlas){Debug.Log("ATLAS TEXTURE "+new SerializedObject(atlas).FindProperty("m_RenderDataMap").GetArrayElementAtIndex(0).FindPropertyRelative("second.texture").objectReferenceValue);var sprites=new Sprite[atlas.spriteCount];atlas.GetSprites(sprites);foreach(var sprite in sprites)Debug.Log("ATLAS SPRITE "+sprite.name+" texture="+sprite.texture);}}var map=bundle.LoadAsset<GameObject>("Assets/UI/Gamepad/Prefabs/XboxGamepadMap.prefab");foreach(var t in map.GetComponentsInChildren<Transform>(true))if(t.name=="Lines")Debug.Log("LINE NODE "+EditorJsonUtility.ToJson(t.gameObject,true));foreach(var script in Resources.FindObjectsOfTypeAll<MonoScript>().Where(s=>s.name.ToLower().Contains("line")))Debug.Log("SCRIPT "+EditorJsonUtility.ToJson(script,true)+" CLASS "+script.GetClass());
}
public static void Run(){
 var b=Load();var prefab=b.LoadAsset<GameObject>("Assets/UI/prefabs/Settings/GamepadTab.prefab");
 if(!prefab)throw new Exception("No native GamepadTab");
 var s=new System.Text.StringBuilder();
 foreach(var t in prefab.GetComponentsInChildren<Transform>(true))s.AppendLine(AnimationUtility.CalculateTransformPath(t,prefab.transform)+" | "+string.Join(",",Array.ConvertAll(t.GetComponents<Component>(),c=>c?c.GetType().Name:"MISSING")));
 File.WriteAllText("../../../Tools/AugaWork/gamepad-native-tree.txt",s.ToString());
 foreach(var c in prefab.GetComponentsInChildren<GamepadSettings>(true))File.WriteAllText("../../../Tools/AugaWork/gamepad-native-fields.json",EditorJsonUtility.ToJson(c,true));
 Debug.Log("PASS: native GamepadTab loaded read-only in Editor.");
 }
}
