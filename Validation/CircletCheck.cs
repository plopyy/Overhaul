using System.Reflection;using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class CircletCheck {
static void Art(GameObject source,GameObject spirit,GameObject attached,Assembly plugin,Action<bool,string> check){
var originals=source.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m).ToArray();
foreach(var obj in new[]{spirit,attached}){
var mats=obj.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m).ToArray();
var gold=mats.Single(m=>m.name=="Overhaul_SpiritCirclet_dvergerhat");var gem=mats.Single(m=>m.name=="Overhaul_SpiritCirclet_dvergerhat_stone");
check(!originals.Contains(gold)&&!originals.Contains(gem),obj.name+" independent materials");
check(gold.GetTexture("_MainTex").name=="Overhaul_SpiritCirclet_spirit_texture.png",obj.name+" gold texture");
check(gold.GetTexture("_BumpMap")==originals.Single(m=>m.name=="dvergerhat").GetTexture("_BumpMap"),obj.name+" native normal map");
check(gem.GetColor("_Color")==new Color(1,1,1,originals.Single(m=>m.name=="dvergerhat_stone").GetColor("_Color").a)&&gem.GetColor("_EmissionColor")==Color.white,obj.name+" white gem");
}
var originalGem=originals.Single(m=>m.name=="dvergerhat_stone");
check(originalGem.GetColor("_Color").r==0&&originalGem.GetColor("_Color").g==1&&originalGem.GetColor("_EmissionColor").r==0,"Dverger gem unchanged");
check(originals.Single(m=>m.name=="dvergerhat").GetTexture("_MainTex").name=="dvergerhat_d","Dverger texture unchanged");
var icon=spirit.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons[0];
check(icon.name=="Overhaul_SpiritCirclet_icon"&&source.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons[0]!=icon,"Distinct spirit icon");
var before=spirit.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).ToArray();
plugin.GetType("Overhaul.DvergerCirclet").GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{spirit});
check(before.SequenceEqual(spirit.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials))&&icon==spirit.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons[0],"Repeated configuration reuses art");
foreach(var file in new[]{"spirit_icon.png","spirit_texture.png"})using(var stream=plugin.GetManifestResourceStream("Overhaul.Assets."+file))using(var memory=new MemoryStream()){stream.CopyTo(memory);check(memory.ToArray().SequenceEqual(File.ReadAllBytes("../../../Overhaul/Overhaul/Assets/"+file)),"Embedded approved bytes "+file);}
}
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
var rows=new List<string>();var items=new List<ItemDrop.ItemData>();
foreach(var p in paths.Where(p=>p.Key.EndsWith("/HelmetDverger.prefab",StringComparison.OrdinalIgnoreCase))){
Load(p.Value);var go=bundles[p.Value].LoadAsset<GameObject>(p.Key);if(!go)continue;var item=go.GetComponent<ItemDrop>();if(item){item.m_itemData.m_dropPrefab=go;items.Add(item.m_itemData);}if(item)rows.Add(item.m_itemData.m_shared.m_maxStackSize+"\t"+go.name+"\t"+item.m_itemData.m_shared.m_name);
}
File.WriteAllLines("../../../Tools/AugaWork/item-stack-survey.tsv",rows.OrderByDescending(r=>int.Parse(r.Split('\t')[0])));
var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;var type=plugin.GetType("Overhaul.DvergerCirclet");var visual=plugin.GetType("Overhaul.DvergerCircletVisual");var prefab=items.Single().m_dropPrefab;int count=0;Action<bool,string> Check=(b,m)=>{if(!b)throw new Exception(m);count++;};var nativeColor=prefab.GetComponentInChildren<Light>(true).color;var configure=type.GetMethod("Configure",F);configure.Invoke(null,new object[]{prefab});configure.Invoke(null,new object[]{prefab});Check(items[0].m_shared.m_itemType==ItemDrop.ItemData.ItemType.Utility,"Accessory type");Check(prefab.GetComponentsInChildren(visual,true).Length==1,"Idempotent visual setup");
var host=new GameObject("Inactive circlet fixtures");host.SetActive(false);var db=host.AddComponent<ObjectDB>();typeof(ObjectDB).GetField("m_instance",F).SetValue(null,db);db.m_itemByHash[prefab.name.GetStableHashCode()]=prefab;var mm=host.AddComponent<MaterialMan>();typeof(MaterialMan).GetField("s_instance",F).SetValue(null,mm);var vis=host.AddComponent<VisEquipment>();var head=new GameObject("Head").transform;head.SetParent(host.transform);vis.m_helmet=head;
var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.circlet.check"});var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type.GetNestedType("Attach",F)});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);
try{for(int slot=0;slot<2;slot++){var attached=vis.AttachArmor(prefab.name.GetStableHashCode());Check(attached.Count==1&&attached[0].transform.parent==head,"Native utility attachment on head "+slot);var instance=attached[0];var component=instance.GetComponent(visual);var lights=instance.GetComponentsInChildren<Light>(true);Check(lights.Length==1&&lights.All(l=>l.type==LightType.Point&&l.cookie==null),"Point light without beam");Check(lights.All(l=>l.color==nativeColor&&l.range==10f),"Native light color and original range");var meshes=instance.GetComponentsInChildren<Renderer>(true);Check(meshes.Length>0,"Actual circlet mesh");vis.m_currentHelmetItemHash=123;visual.GetMethod("LateUpdate",F).Invoke(component,null);Check(meshes.All(r=>r.forceRenderingOff),"Helmet hides circlet");Check(lights.All(l=>l.enabled),"Helmet keeps light enabled");vis.m_currentHelmetItemHash=0;visual.GetMethod("LateUpdate",F).Invoke(component,null);Check(meshes.All(r=>!r.forceRenderingOff),"Removing helmet restores circlet");UnityEngine.Object.DestroyImmediate(instance);}var spirit=UnityEngine.Object.Instantiate(prefab,host.transform);spirit.name="Overhaul_SpiritCirclet";configure.Invoke(null,new object[]{spirit});
var spiritDrop=spirit.GetComponent<ItemDrop>();spiritDrop.m_itemData.m_dropPrefab=spirit;
Check(spiritDrop.m_itemData.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Utility,"Spirit is accessory");
Check(spirit.GetComponentsInChildren<Light>(true).All(l=>l.color==new Color(.84f,.93f,1f,1f)&&l.type==LightType.Point&&l.range==10f),"Spirit keeps pale blue light and range");
Check(prefab.GetComponentInChildren<Light>(true).color==nativeColor,"Spirit never recolors standard circlet");
Check((bool)type.GetMethod("IsCirclet",F).Invoke(null,new object[]{spiritDrop.m_itemData}),"Spirit recognized by shared visual behavior");
db.m_itemByHash[spirit.name.GetStableHashCode()]=spirit;
var spiritAttached=vis.AttachArmor(spirit.name.GetStableHashCode());Check(spiritAttached.Count==1&&spiritAttached[0].transform.parent==head,"Spirit attaches at head from utility slot");
Art(prefab,spirit,spiritAttached[0],plugin,Check);
var config=type.GetMethod("RecipeConfig",F).Invoke(null,null);var ct=config.GetType();
Check((string)ct.GetProperty("Name").GetValue(config)=="$overhaul_spirit_circlet","Localized spirit name");
Check((int)ct.GetProperty("Amount").GetValue(config)==1&&!(bool)ct.GetProperty("RequireOnlyOneIngredient").GetValue(config),"Recipe makes one and requires both ingredients");
var requirements=(Array)ct.GetProperty("Requirements").GetValue(config);
Check(requirements.Length==2,"Exactly two ingredients");
var recipe=ScriptableObject.CreateInstance<Recipe>();recipe.m_item=spiritDrop;recipe.m_resources=new Piece.Requirement[2];
for(int i=0;i<2;i++) {var req=requirements.GetValue(i);string ingredient=(string)req.GetType().GetProperty("Item").GetValue(req);int amount=(int)req.GetType().GetProperty("Amount").GetValue(req);Check(ingredient==(i==0?"HelmetDverger":"Demister")&&amount==1,"Recipe ingredient "+i);var path=paths.First(p=>p.Key.EndsWith("/"+ingredient+".prefab",StringComparison.OrdinalIgnoreCase));Load(path.Value);recipe.m_resources[i]=new Piece.Requirement{m_resItem=bundles[path.Value].LoadAsset<GameObject>(path.Key).GetComponent<ItemDrop>(),m_amount=amount};}
var player=host.AddComponent<Player>();
foreach(int mask in new[]{0,1,2,3}) {player.m_knownMaterial.Clear();for(int i=0;i<2;i++)if((mask&(1<<i))!=0)player.m_knownMaterial.Add(recipe.m_resources[i].m_resItem.m_itemData.m_shared.m_name);Check(player.HaveRequirements(recipe,true,1)==(mask==3),"Native discovery requires both known materials: "+mask);}
File.WriteAllText("../../../Tools/AugaWork/circlet-check.txt","PASS "+count+" native prefab / patched attachment checks. "+plugin.GetName().Version);}finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
}}
