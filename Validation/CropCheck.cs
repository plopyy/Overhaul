using System; using System.Reflection;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class CropCheck {
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
var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));cropType=plugin.GetType("Overhaul.CropFarming");
foreach(var p in paths.Where(p=>p.Key.EndsWith("/Cultivator.prefab",StringComparison.OrdinalIgnoreCase))){Load(p.Value);var native=bundles[p.Value].LoadAsset<GameObject>(p.Key).GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces;Call("Register",native);
foreach(var prefab in native.m_pieces){var plant=prefab.GetComponent<Plant>();if(!plant)continue;var rule=Call("Find",prefab);bool field=plant.m_grownPrefabs.All(g=>g.GetComponent<Pickable>()&&!g.GetComponent<Vine>());Check((rule!=null)==field,"Native scope "+prefab.name);if(rule!=null){Check(((ItemDrop)rule.GetType().GetField("Ingredient",F).GetValue(rule)).name==prefab.GetComponent<Piece>().m_resources[0].m_resItem.name,"Exact native ingredient "+prefab.name);Check((bool)rule.GetType().GetField("Replant",F).GetValue(rule)==!prefab.name.StartsWith("sapling_seed"),"Replant classification "+prefab.name);foreach(var mature in plant.m_grownPrefabs)Check((Call("Find",mature)!=null)==(!prefab.name.Contains("jotunpuffs")&&!prefab.name.Contains("magecap")),"Existing mature scope "+prefab.name);}}}
var host=new GameObject("Crop fixtures");host.SetActive(false);var net=host.AddComponent<ZNet>();typeof(ZNet).GetField("m_instance",F).SetValue(null,net);net.m_netTime=10000;var game=host.AddComponent<Game>();typeof(Game).GetProperty("instance",F).SetValue(null,game);new ZRoutedRpc(true);manager=new ZDOMan(512);var scene=host.AddComponent<ZNetScene>();typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);var player=host.AddComponent<Player>();player.m_nview=host.AddComponent<ZNetView>();InitView(player.m_nview);Player.m_localPlayer=player;
var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));ht=ha.GetType("HarmonyLib.Harmony");hm=ha.GetType("HarmonyLib.HarmonyMethod");harmony=Activator.CreateInstance(ht,new object[]{"overhaul.crop.check"});
foreach(var t in cropType.GetNestedTypes(F).Where(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);}
Patch(typeof(ZNetView).GetMethod("Awake",F),"InitView");Patch(typeof(ZNetView).GetMethod("IsValid",F),"EnsureView");Patch(typeof(ZNetView).GetMethod("InvokeRPC",new[]{typeof(long),typeof(string),typeof(object[])}),"Rpc");Patch(typeof(ZNetScene).GetMethod("Destroy",new[]{typeof(GameObject)}),"Destroyed");Patch(typeof(ItemDrop).GetMethod("Awake",F),"ItemAwake");Patch(typeof(Pickable).GetMethod("Drop",F),"HarvestDrop");Patch(typeof(Destructible).GetMethod("CreateDestructionEffects",F),"Skip");Patch(typeof(Game).GetMethod("ScaleDrops",new[]{typeof(GameObject),typeof(int)}),"Scale");
var table=host.AddComponent<PieceTable>();
GameObject Obj(string name){var o=new GameObject(name);o.transform.SetParent(host.transform);var v=o.AddComponent<ZNetView>();InitView(v);return o;}
GameObject Item(string name){var o=Obj(name);o.AddComponent<ItemDrop>();return o;}
var seed=Item("CarrotSeeds");var food=Item("Carrot");var grain=Item("Barley");
GameObject Define(string name,GameObject input,GameObject output,int amount){var young=Obj(name);var p=young.AddComponent<Piece>();p.m_resources=new[]{new Piece.Requirement{m_resItem=input.GetComponent<ItemDrop>(),m_amount=1,m_recover=false}};var grown=Obj("grown_"+name);var pick=grown.AddComponent<Pickable>();pick.m_nview=grown.GetComponent<ZNetView>();pick.m_itemPrefab=output;pick.m_amount=amount;pick.m_pickEffector=new EffectList();pick.m_extraDrops=new DropTable();var d=grown.AddComponent<Destructible>();d.m_nview=pick.m_nview;d.m_destroyNoise=0;d.m_autoCreateFragments=false;var plant=young.AddComponent<Plant>();plant.m_nview=young.GetComponent<ZNetView>();plant.m_grownPrefabs=new[]{grown};plant.m_status=Plant.Status.Healthy;plant.m_growEffect=new EffectList();table.m_pieces.Add(young);return young;}
var carrot=Define("sapling_carrot",seed,food,1);var seedCarrot=Define("sapling_seedcarrot",food,seed,3);var barley=Define("sapling_barley",grain,grain,2);var mushroom=Define("sapling_magecap",grain,grain,1);mushroom.GetComponent<Plant>().m_grownPrefabs[0].GetComponent<Pickable>().m_respawnTimeMinutes=30;Call("Register",table);
GameObject Mature(GameObject sapling){return sapling.GetComponent<Plant>().m_grownPrefabs[0];}
void Harvest(GameObject mature){typeof(Pickable).GetMethod("RPC_Pick",F).Invoke(mature.GetComponent<Pickable>(),new object[]{ZNet.GetUID(),2});}
int PlantCount(){return created.Count(v=>v&&v.GetComponent<Plant>());}
int ItemCount(){foreach(var i in Resources.FindObjectsOfTypeAll<ItemDrop>().Where(i=>i.gameObject.scene.IsValid()&&i.name.EndsWith("(Clone)"))){var v=i.GetComponent<ZNetView>();if(v.m_zdo==null)InitView(v);}return created.Count(v=>v&&v.GetComponent<ItemDrop>());}
// Native growth and owner-only harvest, with world yield deliberately multiplied by five.
var grown=carrot.GetComponent<Plant>().Grow();Check(grown&&grown.GetComponent<ZNetView>().GetZDO().GetString("overhaul_crop_source_v1","")=="sapling_carrot","Growth persists planting identity");var gv=grown.GetComponent<ZNetView>();gv.GetZDO().Set(ZDOVars.s_creator,42L);gv.GetZDO().Set(ZDOVars.s_creatorIndex,7);int n=PlantCount();Harvest(grown);Check(PlantCount()==n+1,"Harvest creates one fresh seedling");Check(harvestAmount==7,"Native harvest yield plus farming bonus retained");var replanted=created.Last(v=>v.GetComponent<Plant>());Check(replanted.GetZDO().GetLong(ZDOVars.s_plantTime,0)==net.GetTime().Ticks,"Fresh native growth timer");Check(replanted.GetComponent<Piece>().GetCreator()==42&&replanted.GetZDO().GetInt(ZDOVars.s_creatorIndex,-1)==7,"Creator survives cycle");Harvest(grown);Check(PlantCount()==n+1,"Repeated harvest does not duplicate");
n=PlantCount();Harvest(Mature(seedCarrot));Check(PlantCount()==n,"Seed-producing plant not replanted");
var bv=Mature(barley).GetComponent<ZNetView>();bv.GetZDO().SetOwner(ZNet.GetUID()+1);Harvest(Mature(barley));Check(PlantCount()==n,"Non-owner cannot replant");bv.GetZDO().SetOwner(ZNet.GetUID());Harvest(Mature(barley));Check(PlantCount()==n+1,"Barley replants once");
int drops=ItemCount();replanted.GetComponent<Piece>().DropResources();Check(ItemCount()==drops+1,"Hammer returns one seed despite m_recover false");var item=created.Last(v=>v.GetComponent<ItemDrop>()).GetComponent<ItemDrop>();Check(item.name.StartsWith("CarrotSeeds")&&item.m_itemData.m_stack==1,"Exact seed refund without world multiplier");replanted.GetComponent<Piece>().DropResources();Check(ItemCount()==drops+1,"Repeated refund suppressed");
var matureSeed=UnityEngine.Object.Instantiate(Mature(seedCarrot));drops=ItemCount();EnsureView(matureSeed.GetComponent<ZNetView>());matureSeed.GetComponent<Destructible>().Destroy();Check(ItemCount()==drops+1&&created.Last(v=>v.GetComponent<ItemDrop>()).name.StartsWith("Carrot("),"Destroy mature seed crop returns vegetable");
var matureGrain=UnityEngine.Object.Instantiate(Mature(barley));EnsureView(matureGrain.GetComponent<ZNetView>());var dd=matureGrain.AddComponent<DropOnDestroyed>();typeof(DropOnDestroyed).GetMethod("Awake",F).Invoke(dd,null);dd.m_dropWhenDestroyed.m_drops.Add(new DropTable.DropData{m_item=grain,m_stackMin=10,m_stackMax=10,m_weight=1});drops=ItemCount();matureGrain.GetComponent<Destructible>().Destroy();Check(ItemCount()==drops+1&&created.Last(v=>v.GetComponent<ItemDrop>()).GetComponent<ItemDrop>().m_itemData.m_stack==1,"Break barley bypasses native destruction drop table");
Check(Call("Find",Mature(mushroom))==null,"Wild respawning mushroom excluded");n=PlantCount();Harvest(Mature(mushroom));Check(PlantCount()==n,"Wild mushroom does not create a plantation");Mature(mushroom).GetComponent<Pickable>().m_picked=false;var cultivated=mushroom.GetComponent<Plant>().Grow();Harvest(cultivated);Check(PlantCount()==n+1&&cultivated.GetComponent<Pickable>().m_respawnTimeMinutes==0,"Cultivated mushroom replants and cancels old natural respawn");
var replay=UnityEngine.Object.Instantiate(Mature(carrot));EnsureView(replay.GetComponent<ZNetView>());n=PlantCount();replay.GetComponent<Pickable>().SetPicked(true);Check(PlantCount()==n,"Restoring saved picked state does not replant");
File.WriteAllText("../../../Tools/AugaWork/crop-check.txt","PASS "+checks+" checks. Native prefabs + patched growth/harvest/destruction with simulated networking. "+plugin.GetName().Version);ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
}
const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;static Type cropType,ht,hm;static object harmony;static int checks,harvestAmount;static ZDOMan manager;static List<ZNetView> created=new List<ZNetView>();
static object Call(string name,params object[] args)=>cropType.GetMethod(name,F).Invoke(null,args);
static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
static void Patch(MethodInfo method,string prefix){if(method==null)throw new Exception("Missing method for "+prefix);var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters()[0].ParameterType==typeof(MethodBase));var args=new object[patch.GetParameters().Length];args[0]=method;args[1]=Activator.CreateInstance(hm,new object[]{typeof(CropCheck).GetMethod(prefix,F)});patch.Invoke(harmony,args);}
static void EnsureView(ZNetView __instance){if(__instance.m_zdo!=null)return;InitView(__instance);var plant=__instance.GetComponent<Plant>();if(plant)plant.Awake();var piece=__instance.GetComponent<Piece>();if(piece){piece.m_nview=__instance;}var pick=__instance.GetComponent<Pickable>();if(pick)pick.m_nview=__instance;var d=__instance.GetComponent<Destructible>();if(d)d.m_nview=__instance;}
static bool InitView(ZNetView __instance){__instance.m_zdo=manager.CreateNewZDO(__instance.transform.position,__instance.name.Replace("(Clone)","").GetStableHashCode());__instance.m_zdo.SetOwner(ZNet.GetUID());created.Add(__instance);return false;}
static bool Rpc(ZNetView __instance,long targetID,string method,object[] parameters){if(method=="RPC_SetPicked")__instance.GetComponent<Pickable>().SetPicked((bool)parameters[0]);return false;}
static bool Destroyed(GameObject go){return false;}
static bool ItemAwake(ItemDrop __instance){__instance.m_nview=__instance.GetComponent<ZNetView>();return false;}
static bool HarvestDrop(GameObject prefab,int offset,int stack){harvestAmount+=stack;return false;}
static bool Skip()=>false;
static bool Scale(int amount,ref int __result){__result=amount*5;return false;}
}





