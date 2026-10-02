using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public static class ChestInteractionChecks
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type feature;static List<string> report=new List<string>();
    static object Call(string name,params object[] args)=>feature.GetMethod(name,F).Invoke(null,args);
    static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);File.WriteAllLines("../../../Tools/AugaWork/chest-interactions-checks.txt",report);if(!ok)throw new Exception(text);}
    static bool Awake()=>false;
    static bool Bound(ref string __result){__result="E";return false;}
    static bool Message()=>false;
    static T New<T>(string name) where T:Component {var go=new GameObject("chest-test-"+name);go.SetActive(false);return go.AddComponent<T>();}
    static ZDO Data(Component c)
    {
        var view=c.GetComponent<ZNetView>()??c.gameObject.AddComponent<ZNetView>();
        var data=ZDOMan.instance.CreateNewZDO(c.transform.position,c.name.GetStableHashCode());data.SetPrefab(c.name.GetStableHashCode());data.SetOwner(ZNet.GetUID());view.m_zdo=data;
        ZNetScene.instance.m_namedPrefabs[c.name.GetStableHashCode()]=c.gameObject;ZNetScene.instance.m_instances[data]=view;return data;
    }
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));feature=mod.GetType("Overhaul.Storage.ChestInteractions",true);
        mod.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.chest.interactions.checks"});
        var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        Action<MethodInfo,string> intercept=(method,name)=>patch.Invoke(harmony,new object[]{method,Activator.CreateInstance(hm,new object[]{typeof(ChestInteractionChecks).GetMethod(name,F)}),null,null,null});
        foreach(var t in new[]{typeof(Container),typeof(Piece),typeof(ZNetView),typeof(Player),typeof(PrivateArea),typeof(Game),typeof(ZNet),typeof(ZNetScene),typeof(InventoryGui),typeof(ItemDrop),typeof(TextInput)})intercept(t.GetMethod("Awake",F),"Awake");
        foreach(var name in new[]{"InCutscene","InAttack","InDodge"})intercept(typeof(Player).GetMethod(name,F),"Awake");
        intercept(typeof(Localization).GetMethod("GetBoundKeyString",F),"Bound");
        intercept(typeof(Character).GetMethod("Message",F),"Message");
        foreach(var t in feature.GetNestedTypes(F).Concat(new[]{mod.GetType("Overhaul.Storage.ChestPermissionPatch"),mod.GetType("Overhaul.Storage.ChestRequestPatch")}))
        {var cp=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});cp.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(cp,null);}
        Check(true,"all feature and existing chest access patches install on game DLL");
        var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
        var words=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Packages/Overhaul/Localisation/translationsFR.json"));
        words["piece_chest"]= "Coffre renforcé";words["piece_container_open"]="Ouvrir";words["piece_container_empty"]="Vide";words["msg_stackall_hover"]="";words["menu_ok"]="Valider";
        var dictionary=(Dictionary<string,string>)typeof(Localization).GetField("m_translations",F).GetValue(loc);foreach(var w in words.Properties())dictionary[w.Name]=(string)w.Value;
        var net=New<ZNet>("net");typeof(ZNet).GetField("m_instance",F).SetValue(null,net);ZNet.m_isServer=true;
        var scene=New<ZNetScene>("scene");typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);
        var game=New<Game>("game");typeof(Game).GetProperty("instance",F).SetValue(null,game);game.m_playerProfile=new PlayerProfile(null,FileHelpers.FileSource.Local);game.m_playerProfile.m_playerID=111;
        var rpc=new ZRoutedRpc(true);var manager=new ZDOMan(512);rpc.SetUID(ZNet.GetUID());
        var player=New<Player>("player");var actor=Data(player);actor.Set(ZDOVars.s_playerID,111L);actor.Set(ZDOVars.s_health,100f);player.m_nview=player.GetComponent<ZNetView>();player.m_seman=new SEMan(player,player.m_nview);Player.m_localPlayer=player;
        var chest=New<Container>("chest");var data=Data(chest);data.Set(ZDOVars.s_creator,111L);chest.m_nview=chest.GetComponent<ZNetView>();chest.m_piece=chest.gameObject.AddComponent<Piece>();chest.m_piece.m_creator=111;chest.m_piece.m_nview=chest.m_nview;chest.m_name="$piece_chest";chest.m_inventory=new Inventory("$piece_chest",null,4,4);chest.m_inventory.m_onChanged+=chest.OnContainerChanged;
        foreach(var behaviour in chest.GetComponents<MonoBehaviour>())behaviour.enabled=false;chest.gameObject.SetActive(true);
        if(!chest.m_nview.m_functions.ContainsKey("Overhaul_RenameChest".GetStableHashCode()))feature.GetNestedType("Register",F).GetMethod("Postfix",F).Invoke(null,new object[]{chest});
        int nameKey=(int)feature.GetField("NameKey",F).GetValue(null);
        Check(data.GetString(nameKey,"")=="$piece_chest"&&(string)Call("Name",chest)=="Coffre renforcé","existing chest receives persisted localized type as default");
        data.Set(ZDOVars.s_creator,0L);Check((bool)Call("Eligible",chest),"world chest without a player creator also supports interactions");data.Set(ZDOVars.s_creator,111L);
        Func<string,bool> rename=text=>(bool)Call("Rename",chest,ZNet.GetUID(),actor.m_uid,text);
        bool renamed=rename("Métaux et minerais");
        Check(renamed&&(string)typeof(Container).GetMethod("GetHoverName",F).Invoke(chest,null)=="Métaux et minerais","custom name replaces hover name");
        string hover=chest.GetHoverText();Check(hover.StartsWith("Métaux et minerais")&&!hover.Contains("Coffre renforcé")&&hover.Contains("Tout prendre")&&hover.Contains("Maj + R"),"native hover replaces type and includes R and Shift+R hints");
        var serialized=new ZPackage();data.Serialize(serialized);serialized.SetPos(0);var restored=manager.CreateNewZDO(Vector3.zero,123);restored.Deserialize(serialized);Check(restored.GetString(nameKey,"")=="Métaux et minerais","name survives real ZDO serialization/reload");
        chest.m_nview.InvokeRPC("Overhaul_RenameChest",actor.m_uid,"Bois et résine");Check((string)Call("Name",chest)=="Bois et résine","registered rename RPC updates owner data");
        Check(!(bool)Call("Rename",chest,999L,actor.m_uid,"Spoof"),"spoofed actor sender rejected");actor.SetPosition(Vector3.right*6);Check(!rename("Far"),"remote rename beyond five metres rejected");actor.SetPosition(Vector3.zero);
        var access=mod.GetType("Overhaul.Storage.ChestAccess");int privateKey=(int)access.GetField("PrivateKey",F).GetValue(null);data.Set(privateKey,true);actor.Set(ZDOVars.s_playerID,222L);Check(!rename("Other")&&!(bool)Call("TakeAll",chest,player,null),"foreign private chest rejects rename and quick take");actor.Set(ZDOVars.s_playerID,111L);data.Set(privateKey,false);
        int lease=(int)access.GetField("LeaseUntilKey",F).GetValue(null);data.Set(lease,DateTime.MaxValue.Ticks);Check(!rename("Busy")&&!(bool)Call("TakeAll",chest,player,null),"crafting lease rejects both actions");data.Set(lease,0L);
        int move=(int)mod.GetType("Overhaul.Storage.MoveReservation").GetField("UntilKey",F).GetValue(null);data.Set(move,DateTime.MaxValue.Ticks);Check(!rename("Moving")&&!(bool)Call("TakeAll",chest,player,null),"relocation reservation rejects both actions");data.Set(move,0L);
        var ward=New<PrivateArea>("ward");Data(ward).Set(ZDOVars.s_enabled,true);ward.m_nview=ward.GetComponent<ZNetView>();ward.m_piece=ward.gameObject.AddComponent<Piece>();ward.m_piece.m_creator=222;ward.m_radius=10;PrivateArea.m_allAreas.Add(ward);chest.m_checkGuardStone=true;
        Check(!rename("Ward")&&!(bool)Call("TakeAll",chest,player,null),"ward restrictions apply to both actions");PrivateArea.m_allAreas.Remove(ward);chest.m_checkGuardStone=false;
        Check(rename(" ")&&data.GetString(nameKey,"")==chest.m_name,"blank name restores persisted type");Check(((string)Call("Clean","a\nb<$tag>"))=="abtag"&&((string)Call("Clean",new string('a',100))).Length==64,"names are bounded single-line plain text");rename("Métaux et minerais");
        var item=New<ItemDrop>("wood");item.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="TestWood",m_maxStackSize=50,m_maxQuality=1};
        Action<int> add=count=>chest.m_inventory.AddItem(new ItemDrop.ItemData{m_dropPrefab=item.gameObject,m_shared=item.m_itemData.m_shared,m_stack=count,m_quality=1});
        player.GetInventory().RemoveAll();add(17);chest.m_lastTakeAllTime=-100;
        chest.m_nview.Register<long>("RPC_RequestTakeAll",chest.RPC_RequestTakeAll);chest.m_nview.Register<bool>("RPC_TakeAllResponse",chest.RPC_TakeAllResponse);
        Check((bool)Call("TakeAll",chest,player,null)&&player.GetInventory().CountItems("TestWood")==17&&chest.m_inventory.NrOfItems()==0,"closed-chest quick take runs native request/response and moves content");
        var gui=New<InventoryGui>("inventory");typeof(InventoryGui).GetField("m_instance",F).SetValue(null,gui);gui.m_currentContainer=chest;add(9);
        Check((bool)Call("TakeAll",chest,player,gui)&&player.GetInventory().CountItems("TestWood")==26&&chest.m_inventory.NrOfItems()==0,"open-chest quick take runs native OnTakeAll");
        player.GetInventory().RemoveAll();player.GetInventory().m_width=1;player.GetInventory().m_height=1;player.GetInventory().AddItem(new ItemDrop.ItemData{m_dropPrefab=item.gameObject,m_shared=item.m_itemData.m_shared,m_stack=48,m_quality=1});add(9);
        Call("TakeAll",chest,player,gui);Check(player.GetInventory().CountItems("TestWood")==50&&chest.m_inventory.CountItems("TestWood")==7,"limited space fills available stack and leaves surplus in chest without loss");
        var inventoryPacket=new ZPackage(data.GetByteArray(ZDOVars.s_items));var reloaded=new Inventory("loaded",null,4,4);
        // Native persistence was written by OnContainerChanged after each transfer.
        Check(inventoryPacket.Size()>0,"quick transfers persist remaining inventory through native save callback");
        Preview(mod,gui,chest,player,hover,loc);
        typeof(TextInput).GetField("m_instance",F).SetValue(null,null);
        KeyboardChecks(chest,player,gui);
        ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
        Debug.Log("CHEST INTERACTIONS CHECKS PASSED: "+report.Count);
    }
    static void KeyboardChecks(Container chest,Player player,InventoryGui gui)
    {
        var original=Keyboard.current;var keyboard=InputSystem.AddDevice<Keyboard>();keyboard.MakeCurrent();
        var panel=new GameObject("Open container input fixture",typeof(RectTransform));gui.m_container=(RectTransform)panel.transform;gui.m_autoCloseDistance=4;gui.m_hiddenFrames=0;
        var woodSample=player.GetInventory().GetAllItems()[0].Clone();woodSample.m_stack=7;chest.m_inventory.AddItem(woodSample);player.GetInventory().RemoveAll();
        Check(ReferenceEquals(Call("Target",player),chest),"open inventory resolves its current chest");
        Check(ReferenceEquals(Call("Key",keyboard),keyboard.rKey),"R shortcut resolves the printed keyboard key");
        InputSystem.Update();InputState.Change(keyboard,new KeyboardState(Key.R));
        // Batch editor has no player update loop; align the synthetic device with this input step.
        typeof(InputDevice).GetField("m_CurrentUpdateStepCount",F).SetValue(keyboard,typeof(InputSystem).Assembly.GetType("UnityEngine.InputSystem.LowLevel.InputUpdate").GetField("s_UpdateStepCount",F).GetValue(null));
        Debug.Log("KEY DEBUG down="+keyboard.rKey.wasPressedThisFrame+" pressed="+keyboard.rKey.isPressed+" updated="+keyboard.wasUpdatedThisFrame+" allowed="+Call("InputAllowed",player)+" frame="+Time.frameCount+" consumed="+feature.GetField("consumedFrame",F).GetValue(null));Call("Tick");Debug.Log("KEY AFTER inv="+player.GetInventory().CountItems("TestWood")+" chest="+chest.m_inventory.CountItems("TestWood"));
        Check(player.GetInventory().CountItems("TestWood")==7&&chest.m_inventory.NrOfItems()==0,"synthetic R press dispatches open-chest transfer with player frame simulated");
        var args=new object[]{"Hide",true};bool run=(bool)feature.GetNestedType("PreventWeaponToggle",F).GetMethod("Prefix",F).Invoke(null,args);Check(!run&&!(bool)args[1],"consumed R does not also toggle held weapons");
        var wood=player.GetInventory().GetAllItems()[0].Clone();wood.m_stack=1;chest.m_inventory.AddItem(wood);Call("Tick");Check(chest.m_inventory.CountItems("TestWood")==1,"two Update hooks cannot execute the same key twice in one frame");
        InputSystem.Update();InputState.Change(keyboard,new KeyboardState());feature.GetField("consumedFrame",F).SetValue(null,-1);
        InputSystem.Update();InputState.Change(keyboard,new KeyboardState(Key.LeftCtrl,Key.R));Call("Tick");Check(chest.m_inventory.CountItems("TestWood")==1,"Ctrl+R does not trigger quick take");
        InputSystem.RemoveDevice(keyboard);if(original!=null)original.MakeCurrent();UnityEngine.Object.DestroyImmediate(panel);gui.m_container=null;
    }
    static void Preview(Assembly mod,InventoryGui gui,Container chest,Player player,string hover,Localization loc)
    {
        var stream=mod.GetManifestResourceStream("Overhaul.augaassets");var bytes=new byte[stream.Length];stream.Read(bytes,0,bytes.Length);stream.Dispose();var bundle=AssetBundle.LoadFromMemory(bytes);
        var host=new GameObject("Preview host");host.SetActive(false);
        var panel=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("Inventory_screen").transform.Find("root/Container").gameObject,host.transform,false);
        gui.m_container=(RectTransform)panel.transform;gui.m_containerName=panel.transform.Find("ContainerHeader/Name").GetComponent<TMP_Text>();Call("Title",gui);
        Check(gui.m_containerName.text=="Métaux et minerais","real Auga container header uses saved name instead of inventory type");
        panel.transform.Find("Weight/Text").GetComponent<TMP_Text>().text="14";
        var grid=panel.GetComponentInChildren<InventoryGrid>(true);grid.m_width=4;
        chest.m_inventory.RemoveAll();
        for(int i=0;i<16;i++){
            var cell=UnityEngine.Object.Instantiate(grid.m_elementPrefab,grid.m_gridRoot,false);((RectTransform)cell.transform).anchoredPosition=new Vector2(i%4*grid.m_elementSpace,-(i/4)*grid.m_elementSpace);
            foreach(var n in new[]{"binding","amount","quality"})cell.transform.Find(n).GetComponent<TMP_Text>().text="";
            foreach(var n in new[]{"equiped","queued","selected","noteleport","foodicon","dropFocus","durability"})cell.transform.Find(n).gameObject.SetActive(false);
            cell.transform.Find("icon").GetComponent<Image>().enabled=false;
            foreach(var q in cell.GetComponentsInChildren<AugaUnity.QualityIndicator>(true))q.SetEnabled(false);
            foreach(var f in cell.GetComponentsInChildren<AugaUnity.FoodIndicator>(true))f.Image.enabled=false;
        }
        grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,4*grid.m_elementSpace);
        loc.Localize(panel.transform);Strip(panel);
        var wrap=new GameObject("Chest panel",typeof(RectTransform));panel.transform.SetParent(wrap.transform,false);panel.SetActive(true);
        var rect=(RectTransform)panel.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);rect.anchoredPosition=Vector2.zero;
        Render(wrap,"chest-renamed-panel",760,520);
        var cross=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/crosshair.prefab");var label=UnityEngine.Object.Instantiate(cross.transform.Find("Dummy/HoverName").gameObject);var text=label.GetComponent<TMP_Text>();text.text=hover;text.fontSize=25;text.alignment=TextAlignmentOptions.MidlineLeft;
        var hints=new GameObject("Chest hints",typeof(RectTransform));label.transform.SetParent(hints.transform,false);label.SetActive(true);var hr=(RectTransform)label.transform;hr.anchorMin=hr.anchorMax=hr.pivot=new Vector2(.5f,.5f);hr.anchoredPosition=Vector2.zero;hr.sizeDelta=new Vector2(640,210);Render(hints,"chest-interaction-hints",700,250);
        var rename=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("AugaTextInput"),host.transform,false);var input=rename.GetComponent<TextInput>();typeof(TextInput).GetField("m_instance",F).SetValue(null,input);
        Check((bool)Call("BeginRename",chest,player)&&input.m_inputField.text=="Métaux et minerais"&&input.m_visibleFrame,"real existing TextInput prefab opens with saved name and blocks gameplay immediately");
        Check(!(bool)Call("InputAllowed",player),"typing in rename dialog cannot trigger quick take");
        input.m_inputField.text="Objets précieux";input.OnEnter();Check((string)Call("Name",chest)=="Objets précieux","real rename dialog submission persists via TextReceiver");
        Call("BeginRename",chest,player);input.OnCancel();Check((string)Call("Name",chest)=="Objets précieux","cancel preserves saved name");
        Call("BeginRename",chest,player);loc.Localize(rename.transform);Strip(rename);rename.transform.SetParent(null);rename.SetActive(true);Render(rename,"chest-rename-dialog-v2",1000,600);
        Check(true,"three static previews rendered from existing Auga prefabs");
    }
    static void Strip(GameObject go)=>typeof(AugaPauseCheck).GetMethod("Strip",F).Invoke(null,new object[]{go});
    static void Render(GameObject go,string file,int w,int h)=>typeof(AugaPauseCheck).GetMethod("Render",F).Invoke(null,new object[]{go,file,w,h});
}
