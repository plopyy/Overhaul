using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using TMPro;
public static class RelocationHintPreview
{
 public static void Run()
 {
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/crosshair.prefab");
  var source=prefab.transform.Find("Dummy/HoverName");
  var wrapper=new GameObject("Relocation hints",typeof(RectTransform));
  var label=Object.Instantiate(source.gameObject,wrapper.transform,false);label.SetActive(true);
  var rect=(RectTransform)label.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);rect.anchoredPosition=Vector2.zero;rect.sizeDelta=new Vector2(650,140);
  var text=label.GetComponent<TMP_Text>();text.text=((string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Packages/Overhaul/Localisation/translationsFR.json"))["overhaul_move_controls"]).Replace("{0}","H");text.richText=true;text.fontSize=25;text.alignment=TextAlignmentOptions.MidlineLeft;
  typeof(AugaPauseCheck).GetMethod("Render",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{wrapper,"relocation-action-hints-h",710,180});Object.DestroyImmediate(wrapper);
 }
}
