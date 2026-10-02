using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class RelocationChecks
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type feature; static readonly List<string> report=new List<string>();
    static object Call(string method,params object[] args)=>feature.GetMethod(method,F).Invoke(null,args);
    static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);if(!ok)throw new Exception(text);}
    static bool Awake(Component __instance)=>!__instance.name.StartsWith("move-test-");
    static T New<T>(string name) where T:Component {var go=new GameObject("move-test-"+name);go.SetActive(false);return go.AddComponent<T>();}
    static ZDO Data(Component c,long owner=0)
    {
        var view=c.GetComponent<ZNetView>()??c.gameObject.AddComponent<ZNetView>();
        var data=ZDOMan.instance.CreateNewZDO(c.transform.position,c.name.GetStableHashCode());data.SetPrefab(c.name.GetStableHashCode());
        data.SetOwner(owner==0?ZNet.GetUID():owner);view.m_zdo=data;
        ZNetScene.instance.m_namedPrefabs[c.name.GetStableHashCode()]=c.gameObject;return data;
    }
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var path=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        try
        {
            var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));feature=mod.GetType("Overhaul.Storage.PieceRelocation",true);
            var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var h=Activator.CreateInstance(ht,new object[]{"overhaul.relocation.check"});
            var hm=ha.GetType("HarmonyLib.HarmonyMethod");var prefix=Activator.CreateInstance(hm,new object[]{typeof(RelocationChecks).GetMethod("Awake",F)});
            var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
            foreach(var t in new[]{typeof(Piece),typeof(Container),typeof(ZNetView),typeof(Player),typeof(PrivateArea),typeof(Smelter)})
                patch.Invoke(h,new object[]{t.GetMethod("Awake",F),prefix,null,null,null});
            foreach(var t in new[]{feature,mod.GetType("Overhaul.Storage.RelocationClient"),mod.GetType("Overhaul.Storage.MoveReservation")}.SelectMany(t=>t.GetNestedTypes(F)))
            {var cp=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(h,new object[]{t});cp.GetType().GetMethod("Patch").Invoke(cp,null);Check(true,"Harmony installs "+t.Name);}
            var net=New<ZNet>("network");typeof(ZNet).GetField("m_instance",F).SetValue(null,net);ZNet.m_isServer=true;
            var scene=New<ZNetScene>("scene");typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);
            var game=New<Game>("game");typeof(Game).GetProperty("instance",F).SetValue(null,game);
            var rpc=new ZRoutedRpc(true);var manager=new ZDOMan(512);rpc.SetUID(ZNet.GetUID());
            var actor=New<Player>("actor");var actorData=Data(actor);actorData.Set(ZDOVars.s_playerID,111L);actorData.Set(ZDOVars.s_health,100f);
            var piece=New<Piece>("chest");var data=Data(piece);piece.m_nview=piece.GetComponent<ZNetView>();piece.m_creator=111;data.Set(ZDOVars.s_creator,111L);
            var chest=piece.gameObject.AddComponent<Container>();chest.m_nview=piece.m_nview;chest.m_piece=piece;chest.m_checkGuardStone=false;
            chest.m_inventory=new Inventory("Move inventory",null,4,4);chest.m_inventory.m_onChanged+=chest.OnContainerChanged;
            var item=New<ItemDrop>("wood");item.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="TestWood",m_maxStackSize=100,m_maxQuality=4};
            var stack=new ItemDrop.ItemData{m_dropPrefab=item.gameObject,m_shared=item.m_itemData.m_shared,m_stack=17,m_quality=3,m_durability=42,m_crafterName="Tester"};stack.m_customData["test"]="retained";chest.m_inventory.AddItem(stack);chest.Save();
            var box=piece.gameObject.AddComponent<BoxCollider>();box.center=Vector3.up*.5f;
            foreach(var b in piece.GetComponents<MonoBehaviour>())b.enabled=false;piece.gameObject.SetActive(true);
            if(!piece.GetComponent(mod.GetType("Overhaul.Storage.RelocatedPieceSync")))Call("Register",piece);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="move-test-floor";floor.transform.position=Vector3.down*.5f;floor.transform.localScale=new Vector3(40,1,40);
            Physics.SyncTransforms();var target=Vector3.right*4;var turn=Quaternion.Euler(0,90,0);
            InputAndSourceChecks(mod,actor,piece,box);
            const string token="move-fixture-reservation";
            Func<Vector3,int,Vector3,bool> move=(origin,revision,destination)=>(bool)Call("Commit",piece,ZNet.GetUID(),actorData.m_uid,origin,revision,destination,turn,token);
            var reservation=mod.GetType("Overhaul.Storage.MoveReservation");var reserve=reservation.GetMethod("Change",F);
            Func<long,string,bool,bool> change=(peer,key,release)=>(bool)reserve.Invoke(null,new object[]{piece,peer,actorData.m_uid,key,release});
            Check(!move(Vector3.zero,0,target),"move without reservation rejected");
            chest.m_inUse=true;Check(!change(ZNet.GetUID(),token,false),"already open chest cannot be reserved");chest.m_inUse=false;
            Check(change(ZNet.GetUID(),token,false),"owner grants move reservation before preview");
            Check(!change(ZNet.GetUID(),"competitor",false),"second move reservation rejected");
            Check(!change(999L,token,true),"another peer cannot release reservation");
            Check(!change(ZNet.GetUID(),"competitor",true),"wrong token cannot release reservation");
            Check(chest.Interact(actor,false,false),"opening reserved chest handled by in-use response");
            Check(change(ZNet.GetUID(),token,false),"same mover renews reservation");
            Check((bool)Call("Eligible",piece),"placed container eligible");
            var originalItems=data.GetString(ZDOVars.s_items,"");var originalId=data.m_uid;
            Check(!move(Vector3.zero,0,Vector3.up*4),"unsupported floating destination rejected");
            Check(!move(Vector3.zero,0,Vector3.down),"terrain penetration rejected");
            Check(!move(Vector3.zero,0,Vector3.right*11),"destination beyond reach rejected");
            Check(!(bool)Call("Commit",piece,999L,actorData.m_uid,Vector3.zero,0,target,turn,token),"spoofed actor rejected");
            Check(!move(Vector3.zero,0,new Vector3(float.NaN,0,0)),"non-finite pose rejected");
            chest.m_inUse=true;Check(!move(Vector3.zero,0,target),"open chest rejected");chest.m_inUse=false;
            var access=mod.GetType("Overhaul.Storage.ChestAccess");int privacy=(int)access.GetField("PrivateKey",F).GetValue(null);data.Set(privacy,true);actorData.Set(ZDOVars.s_playerID,222L);
            Check(!move(Vector3.zero,0,target),"other player's private chest rejected");actorData.Set(ZDOVars.s_playerID,111L);
            int lease=(int)access.GetField("LeaseUntilKey",F).GetValue(null);data.Set(lease,DateTime.MaxValue.Ticks);Check(!move(Vector3.zero,0,target),"crafting lease rejected");data.Set(lease,0L);
            access.GetMethod("RequestLease",F).Invoke(null,new object[]{chest,ZNet.GetUID(),actorData.m_uid,ZDOID.None,"craft-fixture"});
            Check(data.GetLong(lease,0)==0,"craft from nearby chests cannot reserve moving chest");
            var ward=New<PrivateArea>("ward");ward.transform.position=target;Data(ward).Set(ZDOVars.s_enabled,true);ward.m_nview=ward.GetComponent<ZNetView>();ward.m_piece=ward.gameObject.AddComponent<Piece>();ward.m_piece.m_creator=222;ward.m_radius=1;
            PrivateArea.m_allAreas.Add(ward);Check(!move(Vector3.zero,0,target),"ward at destination rejected");ward.transform.position=Vector3.zero;Check(!move(Vector3.zero,0,target),"ward at origin rejected");PrivateArea.m_allAreas.Remove(ward);
            Check(move(Vector3.zero,0,target),"valid relocation commits");
            Check(data.m_uid==originalId&&piece.transform.position==target&&data.GetPosition()==target&&Quaternion.Angle(data.GetRotation(),turn)<.01f,"same entity stores new position and rotation");
            Check(data.GetString(ZDOVars.s_items,"")==originalItems&&chest.m_inventory.GetAllItems()[0]==stack&&stack.m_stack==17&&stack.m_customData["test"]=="retained","inventory object and serialized items unchanged");
            Check(data.GetBool(privacy,false)&&data.GetLong(ZDOVars.s_creator,0)==111,"privacy and creator unchanged");
            Check(!move(Vector3.zero,0,Vector3.right*6),"stale concurrent request rejected");
            Check(!move(target,0,Vector3.right*6),"stale revision rejected even with current position");
            int revisionKey=(int)feature.GetField("RevisionKey",F).GetValue(null);
            var serialized=new ZPackage();data.Serialize(serialized);serialized.SetPos(0);var restored=manager.CreateNewZDO(target,123);restored.Deserialize(serialized);
            Check(restored.GetInt(revisionKey,0)==1&&restored.GetString(ZDOVars.s_items,"")==originalItems,"network serialization retains revision and chest contents");
            piece.transform.position=Vector3.zero;var sync=piece.GetComponent(mod.GetType("Overhaul.Storage.RelocatedPieceSync"));sync.GetType().GetMethod("LateUpdate",F).Invoke(sync,null);
            Check(piece.transform.position==target,"remote/reloaded static piece applies stored transform");
            data.SetOwner(777);Check(!move(target,1,Vector3.right*6),"non-owner cannot commit");data.SetOwner(ZNet.GetUID());
            var smelter=piece.gameObject.AddComponent<Smelter>();smelter.enabled=false;
            data.Set("fuel",7f);data.Set("queued",3);data.Set("item0","CopperOre");data.Set("bakeTimer",2.5f);
            Check(move(target,1,Vector3.right*6)&&data.GetFloat("fuel",0)==7&&data.GetInt("queued",0)==3&&data.GetString("item0","")=="CopperOre"&&data.GetFloat("bakeTimer",0)==2.5f,"production queue fuel and progress unchanged");
            data.Set("item","SwordIron");data.Set("quality",4);Check(move(Vector3.right*6,2,target)&&data.GetString("item","")=="SwordIron"&&data.GetInt("quality",0)==4,"display item data unchanged");
            Check(change(ZNet.GetUID(),token,true),"mover releases reservation after placement");
            Check(change(ZNet.GetUID(),"next",false),"released object can be reserved again");
            int until=(int)reservation.GetField("UntilKey",F).GetValue(null);data.Set(until,1L);
            Check(!move(target,3,Vector3.right*6),"expired disconnected mover cannot commit");
            Check(change(ZNet.GetUID(),token,false),"expired reservation recovers without reconnecting old mover");
            UnityEngine.Object.DestroyImmediate(floor);piece.gameObject.SetActive(false);NativeAssets(mod);
        }
        catch(Exception e){report.Add("FAIL "+e);throw;}
        finally{File.WriteAllLines("../../../Tools/AugaWork/relocation-checks.txt",report);}
    }
    static void InputAndSourceChecks(Assembly mod,Player actor,Piece piece,Collider box)
    {
        var client=mod.GetType("Overhaul.Storage.RelocationClient");
        Action<string,object> set=(n,v)=>client.GetField(n,F).SetValue(null,v);
        var oldLocal=Player.m_localPlayer;Player.m_localPlayer=actor;set("player",actor);set("Target",piece);
        var disabled=piece.gameObject.AddComponent<SphereCollider>();disabled.enabled=false;
        var native=client.GetMethod("WithNative",F);
        try{native.Invoke(null,new object[]{(Action)(()=>{
            Check(!box.enabled&&!disabled.enabled,"source colliders excluded during native placement");
            Check(Physics.Raycast(piece.transform.position+Vector3.up*3,Vector3.down,out var hit,5)&&hit.collider.name=="move-test-floor","placement ray reaches floor through source object");
            throw new InvalidOperationException("fixture");
        })});}catch(TargetInvocationException e){if(!(e.InnerException is InvalidOperationException))throw;}
        Check(box.enabled&&!disabled.enabled,"source collider states restored even after placement exception");
        var menu=client.GetNestedType("MenuInput",F).GetMethod("Prefix",F);
        Check(!(bool)menu.Invoke(null,null),"Escape menu blocked while relocating");
        client.GetMethod("End",F).Invoke(null,null);
        Check(!(bool)menu.Invoke(null,null),"Escape menu blocked on cancellation frame");
        var controls=client.GetNestedType("Controls",F).GetMethod("Prefix",F);
        object[] args={actor,true,true,false,false,false,false};controls.Invoke(null,args);
        Check(!(bool)args[1]&&!(bool)args[2],"confirm click cannot attack on completion frame");
        set("closedFrame",-1);args=new object[]{actor,false,true,false,false,false,false};controls.Invoke(null,args);
        Check(!(bool)args[2],"held confirm click stays suppressed after completion");
        args=new object[]{actor,false,false,false,false,false,false};controls.Invoke(null,args);
        args=new object[]{actor,true,true,false,false,false,false};controls.Invoke(null,args);
        Check((bool)args[1]&&(bool)args[2]&&(bool)menu.Invoke(null,null),"fresh attack and menu restored after release");
        Player.m_localPlayer=oldLocal;UnityEngine.Object.DestroyImmediate(disabled);
    }
    const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static readonly Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static readonly Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
    static void NativeAssets(Assembly mod)
    {
        var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
        foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest")))
        {
            if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
            else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
            else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
        }
        var inventory=new List<string>();GameObject chest=null;
        foreach(var entry in paths.Where(p=>p.Key.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase)&&p.Key.IndexOf("/Pieces/",StringComparison.OrdinalIgnoreCase)>=0))
        {
            Load(entry.Value);var prefab=bundles[entry.Value].LoadAsset<GameObject>(entry.Key);if(!prefab)continue;var piece=prefab.GetComponent<Piece>();if(!piece)continue;
            bool allowed=(bool)Call("Functional",piece);inventory.Add(prefab.name+"\t"+allowed+"\t"+string.Join(",",prefab.GetComponents<Component>().Select(c=>c?c.GetType().Name:"missing")));
            if(prefab.name=="piece_chest_wood")chest=prefab;
            if(new[]{"piece_workbench","forge","piece_chest_wood","smelter","charcoal_kiln","piece_walltorch","itemstand","itemstandh","piece_armorstand"}.Contains(prefab.name))Check(allowed,"native eligible "+prefab.name);
            if(prefab.GetComponent<Door>()||prefab.name=="wood_floor"||prefab.name=="wood_wall"||prefab.name=="piece_chest_treasure")Check(!allowed,"native excluded "+prefab.name);
        }
        File.WriteAllLines("../../../Tools/AugaWork/relocation-native-pieces.tsv",inventory.OrderBy(x=>x));Check(chest,"native chest prefab found");
        GhostChecks(mod,chest);Preview(mod,chest);
    }
    static void GhostChecks(Assembly mod,GameObject prefab)
    {
        var client=mod.GetType("Overhaul.Storage.RelocationClient");
        var player=New<Player>("ghost-player");var actor=Data(player);actor.Set(ZDOVars.s_playerID,111L);actor.Set(ZDOVars.s_health,100f);player.m_nview=player.GetComponent<ZNetView>();player.m_cachedFrame=MonoUpdaters.UpdateCount;
        var piece=New<Piece>("ghost-source");var data=Data(piece);piece.m_nview=piece.GetComponent<ZNetView>();piece.m_creator=111;data.Set(ZDOVars.s_creator,111L);
        var container=piece.gameObject.AddComponent<Container>();container.m_nview=piece.m_nview;container.m_piece=piece;container.m_checkGuardStone=false;
        piece.gameObject.AddComponent<BoxCollider>();
        int hash=prefab.name.GetStableHashCode();ZNetScene.instance.m_namedPrefabs[hash]=prefab;data.SetPrefab(hash);
        var oldGhost=new GameObject("previous-building-ghost");player.m_placementGhost=oldGhost;player.m_placeRotation=7;player.m_manualSnapPoint=2;player.m_placeRayMask=~0;
        client.GetMethod("Begin",F).Invoke(null,new object[]{player,piece});
        Check(!(bool)client.GetProperty("Active",F).GetValue(null),"relocation refused without hammer");
var hammer=new GameObject("Hammer");hammer.SetActive(false);player.m_rightItem=new ItemDrop.ItemData{m_dropPrefab=hammer};
Check((bool)client.GetMethod("HasHammer",F).Invoke(null,new object[]{player}),"equipped hammer enables relocation");
player.m_hiddenRightItem=player.m_rightItem;player.m_rightItem=null;
Check(!(bool)client.GetMethod("HasHammer",F).Invoke(null,new object[]{player}),"stowed hammer does not enable relocation");
player.m_rightItem=player.m_hiddenRightItem;player.m_hiddenRightItem=null;
client.GetMethod("Begin",F).Invoke(null,new object[]{player,piece});
Check((bool)client.GetProperty("Active",F).GetValue(null),"enter relocation with equipped hammer and no workbench");
        var ghost=(GameObject)client.GetField("ghost",F).GetValue(null);
        Check(ghost&&ghost.GetComponent<Piece>()&&ghost.name==prefab.name,"native setup creates selected chest ghost");
        Check(!ghost.GetComponent<ZNetView>().IsValid(),"preview has no network entity");
        Check(player.m_buildPieces==null&&player.m_placementGhost==oldGhost&&!oldGhost.activeSelf,"temporary table restored and previous ghost hidden");
        client.GetMethod("End",F).Invoke(null,null);
        Check(!(bool)client.GetProperty("Active",F).GetValue(null)&&!ghost&&player.m_placeRotation==7&&player.m_manualSnapPoint==2&&player.m_placementGhost==oldGhost,"cancel destroys preview and restores native placement state");
        Check(data.GetInt((int)feature.GetField("RevisionKey",F).GetValue(null),0)==0&&piece.transform.position==Vector3.zero,"cancel leaves original object untouched");
        Check(data.GetLong((int)mod.GetType("Overhaul.Storage.MoveReservation").GetField("UntilKey",F).GetValue(null),0)==0,"cancel immediately releases chest reservation");
    }
    static void Preview(Assembly mod,GameObject prefab)
    {
        // Real native meshes/textures and the runtime guide, rendered in a static studio view.
        var inactive=new GameObject("inactive-preview");inactive.SetActive(false);
        foreach(var pos in new[]{new Vector3(-3,0,2),new Vector3(3,0,2)})
        {
            var go=UnityEngine.Object.Instantiate(prefab,inactive.transform);foreach(var b in go.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(b);
            foreach(var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var originals=r.sharedMaterials;var mats=new Material[originals.Length];
                for(int i=0;i<mats.Length;i++){mats[i]=new Material(Shader.Find("Standard"));if(originals[i]&&originals[i].HasProperty("_MainTex"))mats[i].mainTexture=originals[i].GetTexture("_MainTex");mats[i].SetFloat("_Glossiness",.1f);if(pos.x>0)mats[i].color=new Color(.5f,1,.65f);}
                r.sharedMaterials=mats;
            }
            go.transform.SetParent(null);go.transform.position=pos;go.transform.rotation=Quaternion.Euler(0,pos.x>0?30:0,0);go.SetActive(true);
        }
        var guide=Activator.CreateInstance(mod.GetType("Overhaul.Storage.RelocationGuide"),true);
        guide.GetType().GetMethod("Update",F).Invoke(guide,new object[]{new Vector3(-3,0,2),new Vector3(3,0,2),true});
        var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.transform.position=new Vector3(0,-.08f,2);ground.transform.localScale=new Vector3(18,.1f,13);ground.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Standard")){color=new Color(.15f,.19f,.18f)};
        RenderSettings.ambientLight=new Color(.6f,.6f,.6f);RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;
        var light=new GameObject("preview-light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(45,-30,0);
        var camera=new GameObject("preview-camera").AddComponent<Camera>();camera.transform.position=new Vector3(8,8,-12);camera.transform.LookAt(new Vector3(0,0,2));camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.1f,.12f);camera.fieldOfView=35;
        var rt=new RenderTexture(1400,850,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;var image=new Texture2D(1400,850,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1400,850),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/relocation-preview-dark-v2.png",image.EncodeToPNG());RenderTexture.active=null;
        Check(true,"static Unity preview rendered with native chest meshes and runtime rings/dashes");
    }
}

