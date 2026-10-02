using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using AugaUnity;
using Object=UnityEngine.Object;

public static class OvenFoodTooltipCheck
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    static List<string> results=new List<string>();
    static void Check(bool ok,string text) { if(!ok)throw new Exception(text);results.Add("PASS "+text); }
    static void Load(string id) { if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id); }
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        plugin.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
        var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
        foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest"))) {
            if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
            else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
            else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
        }
        var path=paths.First(p=>p.Key.EndsWith("/piece_oven.prefab",StringComparison.OrdinalIgnoreCase));Load(path.Value);
        var oven=bundles[path.Value].LoadAsset<GameObject>(path.Key);
        var host=new GameObject("Oven tooltip fixture");host.SetActive(false);
        var scene=host.AddComponent<ZNetScene>();typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);scene.m_namedPrefabs["piece_oven".GetStableHashCode()]=oven;
        var player=host.AddComponent<Player>();Player.m_localPlayer=player;
        player.m_equipmentModifierValues=new float[0];
        player.m_nview=host.AddComponent<ZNetView>();player.m_nview.m_zdo=new ZDO();
        var skills=host.AddComponent<Skills>();skills.m_player=player;player.m_skills=skills;
        var seman=(SEMan)FormatterServices.GetUninitializedObject(typeof(SEMan));typeof(SEMan).GetField("m_statusEffects",F).SetValue(seman,new List<StatusEffect>());player.m_seman=seman;
        var loc=(Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var f in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic)) {
            if(f.FieldType==typeof(char[]))f.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(f.FieldType==typeof(System.Text.StringBuilder))f.SetValue(loc,new System.Text.StringBuilder());
            else if(f.FieldType.IsGenericType&&(f.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||f.FieldType.GetGenericTypeDefinition()==typeof(List<>)))f.SetValue(loc,Activator.CreateInstance(f.FieldType));
            else if(f.Name=="m_cache")f.SetValue(loc,Activator.CreateInstance(f.FieldType,new object[]{100}));
        }
        typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
        plugin.GetType("Auga.Auga").GetMethod("LoadTranslations",F).Invoke(null,new object[]{loc,"French"});
        loc.AddWord("item_food_health","Santé");loc.AddWord("item_food_stamina","Endurance");loc.AddWord("item_food_eitr","Eitr");loc.AddWord("item_food_regen","Régénération");loc.AddWord("item_food_duration","Durée");loc.AddWord("healing_tick","PV/tick");
        loc.AddWord("item_weight","Poids");
        var art=AssetBundle.LoadFromFile("AssetBundles/augaassets");
        var conversions=oven.GetComponent<CookingStation>().m_conversion.Where(c=>c.m_to&&c.m_to.m_itemData.m_shared.m_food>0).ToArray();
        Check(conversions.Length>0,"native oven food conversions found");
        foreach(var conversion in conversions) {
            var raw=conversion.m_from.m_itemData.Clone();var cooked=conversion.m_to.m_itemData;
            var originalFood=raw.m_shared.m_food;
            Check(ComplexTooltip.GetOvenFood(raw)==cooked,"native recipe "+conversion.m_from.name+" -> "+conversion.m_to.name);
            player.m_foods.Clear();player.m_foods.Add(new Player.Food{m_item=cooked,m_time=7,m_health=1,m_stamina=2,m_eitr=3});
            var go=Object.Instantiate(art.LoadAsset<GameObject>("InventoryTooltip"),host.transform,false);var tip=go.GetComponent<ComplexTooltip>();
            tip.SetItem(raw);
            string text=string.Join("\n",go.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>t.text));
            Check(text.Contains("Après cuisson au four")&&text.Contains(loc.Localize(cooked.m_shared.m_name)),"cooked result labelled "+conversion.m_from.name);
            Check(text.Contains($"<color=#FF8080>{cooked.m_shared.m_food:0}</color>")&&text.Contains($"<color=#FFFF80>{cooked.m_shared.m_foodStamina:0}</color>"),"cooked health and stamina "+conversion.m_from.name);
            Check(cooked.m_shared.m_foodEitr<=0||text.Contains($"<color=#9C5ACF>{cooked.m_shared.m_foodEitr:0}</color>"),"cooked eitr "+conversion.m_from.name);
            Check(text.Contains($"+{cooked.m_shared.m_foodRegen:0.#}<color="),"cooked regeneration "+conversion.m_from.name);
            Check(text.Contains(TimeSpan.FromSeconds(Mathf.CeilToInt(cooked.m_shared.m_foodBurnTime)).ToString(PlayerPanelFoodController.TimeFormat))&&!text.Contains("(1)")&&!text.Contains("(2)"),"full duration and no active meal decay "+conversion.m_from.name);
            Check(raw.m_shared.m_food==originalFood&&ComplexTooltip.GetOvenFood(cooked)==null,"raw gameplay values and cooked tooltip unchanged "+conversion.m_from.name);
            if(conversion==conversions[0]) { tip.SetIcon(PreviewIcon(raw.GetIcon()));Render(go); } else Object.DestroyImmediate(go);
        }
        Check(ComplexTooltip.GetOvenFood(new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_wood"}})==null,"non-oven material excluded");
        File.WriteAllLines("../../../Tools/AugaWork/oven-food-tooltip-checks.txt",results);
        Debug.Log("OVEN FOOD PASS "+results.Count+" checks; "+plugin.GetName().Version);
        Object.DestroyImmediate(host);
    }
    // Native packed sprites need their atlas rectangle reconstructed in editor previews.
    static Sprite PreviewIcon(Sprite original)
    {
        foreach(var b in bundles.Values.Where(x=>x))foreach(var n in b.GetAllAssetNames().Where(n=>n.EndsWith(".spriteatlas"))) {
            var atlas=b.LoadAsset<UnityEngine.U2D.SpriteAtlas>(n);var sprite=atlas.GetSprite(original.name);if(!sprite)continue;
            var entries=(Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(UnityEditor.EditorJsonUtility.ToJson(atlas))["SpriteAtlas"]["m_RenderDataMap"];
            var key=Newtonsoft.Json.Linq.JObject.Parse(UnityEditor.EditorJsonUtility.ToJson(sprite))["Sprite"]["m_RenderDataKey"]["first"].ToString();
            int index=Enumerable.Range(0,entries.Count).First(i=>entries[i]["first"]["first"].ToString()==key);var r=entries[index]["second"]["textureRect"];
            var texture=(Texture2D)new UnityEditor.SerializedObject(atlas).FindProperty("m_RenderDataMap").GetArrayElementAtIndex(index).FindPropertyRelative("second.texture").objectReferenceValue;
            return Sprite.Create(texture,new Rect((float)r["x"],(float)r["y"],(float)r["width"],(float)r["height"]),new Vector2(.5f,.5f),100);
        }
        return original;
    }
    static void Render(GameObject box)
    {
        var root=new GameObject("Canvas",typeof(Canvas));var cameraGo=new GameObject("Camera",typeof(Camera));var camera=cameraGo.GetComponent<Camera>();
        camera.orthographic=true;camera.orthographicSize=400;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.11f,.095f);camera.cullingMask=1<<30;
        var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
        var target=new RenderTexture(600,800,24);camera.targetTexture=target;box.transform.SetParent(root.transform,false);
        var rect=(RectTransform)box.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,1);rect.anchoredPosition=new Vector2(0,-25);rect.sizeDelta=new Vector2(500,1000);
        foreach(var t in box.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;box.SetActive(true);
        Canvas.ForceUpdateCanvases();UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rect);Canvas.ForceUpdateCanvases();camera.Render();
        RenderTexture.active=target;var image=new Texture2D(600,800,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,600,800),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/oven-food-tooltip.png",image.EncodeToPNG());RenderTexture.active=null;
        Object.DestroyImmediate(root);Object.DestroyImmediate(cameraGo);Object.DestroyImmediate(target);Object.DestroyImmediate(image);
    }
}

