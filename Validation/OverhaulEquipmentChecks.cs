using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using AugaUnity;
using Object = UnityEngine.Object;

public static class OverhaulEquipmentChecks
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly mod;
    static List<string> results = new List<string>();
    static Type T(string name) => mod.GetType("EquipmentAndQuickSlots." + name, true);
    static object Call(Type type, string name, params object[] args) => type.GetMethods(F).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); results.Add("PASS " + message); }
    static void Set(object target, string name, object value) { for (var t=target.GetType();t!=null;t=t.BaseType) { var field=t.GetField(name,F); if(field!=null){field.SetValue(target,value);return;} } throw new Exception(name); }
    static object Config(string name) => T("ValConfig").GetField(name,F).GetValue(null);
    static void ConfigValue(string name, object value) { var c=Config(name); c.GetType().GetProperty("Value").SetValue(c,value); }
    static object Prop(object target,string name) => target.GetType().GetProperty(name,F).GetValue(target);
    static void Prefix(string type, Player player) => Call(T(type),"Prefix",player);

    public static void Run()
    {
        try { Test(); File.WriteAllLines("../../../Tools/AugaWork/equipment-integration-checks.txt", results); OverhaulEquipmentSilhouettePreview.Equipment(); }
        catch(Exception e) { results.Add("FAIL " + e); File.WriteAllLines("../../../Tools/AugaWork/equipment-integration-checks.txt", results); throw; }
    }

    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,a) => { var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null; };
        mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        Call(mod.GetType("Overhaul.IntegratedUi"),"LoadDependencies");
        Check(mod.GetTypes().Count(t=>t.GetCustomAttributes(false).Any(a=>a.GetType().FullName=="BepInEx.BepInPlugin"))==1,"one BepInEx plugin only");
        Check(!mod.GetReferencedAssemblies().Any(a=>a.Name=="EquipmentAndQuickSlots"),"no external EQS DLL dependency");
        var loc=(Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var field in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic)) {
            if(field.FieldType==typeof(char[]))field.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(field.FieldType==typeof(System.Text.StringBuilder))field.SetValue(loc,new System.Text.StringBuilder());
            else if(field.FieldType.IsGenericType&&(field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||field.FieldType.GetGenericTypeDefinition()==typeof(List<>)))field.SetValue(loc,Activator.CreateInstance(field.FieldType));
            else if(field.Name=="m_cache")field.SetValue(loc,Activator.CreateInstance(field.FieldType,new object[]{100}));
        }
        typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
        var temp=Path.GetFullPath("../../../Tools/AugaWork/eqs-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp,"randyknapp.mods.equipmentandquickslots.cfg"),"[Hotkeys]\nQuick slot hotkey 1 = C\n[Quick Slots]\nQuick Slot Count = 3\n[Equipment Slots]\nUtility Slot Count = 1\n");
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(temp,"TestGame.exe"),null,null,new string[0]});
        var cfg=Call(T("EquipmentAndQuickSlots"),"OpenConfig",temp,temp);
        var logger=Activator.CreateInstance(Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll")).GetType("BepInEx.Logging.ManualLogSource"),new object[]{"Overhaul EQS validation"});
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony");
        var harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.eqs.validation.ui"});
        var enabled=(IDictionary)mod.GetType("Auga.UiModules").GetField("EnabledModules",F).GetValue(null);
        enabled[Enum.Parse(mod.GetType("Auga.UiModule"),"QuickInventory")]=true;
        Call(mod.GetType("Auga.EquipmentQuickSlotsCompatibility"),"Install",harmony);
        Call(T("EquipmentAndQuickSlots"),"Initialize",logger,cfg);
        Check((bool)T("EquipmentAndQuickSlots").GetProperty("IsInitialized").GetValue(null),"all EQS Harmony patches install against current game");
        Check(AugaModsSettings.IsEqsActive(),"settings enabled without external Chainloader EQS plugin");
        Check(AugaModsSettings.ReadShortcut(0)=="C","legacy shortcut imported");
        AugaModsSettings.WriteShortcut(0,"G");
        Check(AugaModsSettings.ReadShortcut(0)=="G","Auga shortcut change uses integrated config");
        Check(File.ReadAllText(Path.Combine(temp,"randyknapp.mods.equipmentandquickslots.cfg")).Contains("= C"),"legacy config preserved");
        Check(File.ReadAllText(Path.Combine(temp,"plopyy.valheim.Overhaul.EquipmentSlots.cfg")).Contains("= G"),"new shortcut saved");
        var again=Call(T("EquipmentAndQuickSlots"),"OpenConfig",temp,temp);
        Check(File.ReadAllText(Path.Combine(temp,"plopyy.valheim.Overhaul.EquipmentSlots.cfg")).Contains("= G"),"migration never overwrites existing integrated config");

        var host=new GameObject("Inactive EQS test");host.SetActive(false);
        var player=host.AddComponent<Player>();Set(player,"m_inventory",new Inventory("Player",null,8,8));Player.m_localPlayer=player;
        var db=host.AddComponent<ObjectDB>();ObjectDB.m_instance=db;
        var helmet=MakeItem(host,db,"EqsTestHelmet",ItemDrop.ItemData.ItemType.Helmet,new Vector2i(0,5));helmet.m_equipped=true;
        var potion=MakeItem(host,db,"EqsTestPotion",ItemDrop.ItemData.ItemType.Consumable,new Vector2i(0,4));potion.m_stack=7;
        player.GetInventory().m_inventory.AddRange(new[]{helmet,potion});
        var slots=(Array)T("Slots").GetField("slots",F).GetValue(null);
        Check((int)Prop(Config("UtilitySlotCount"),"Value")==2,"old one-accessory config migrates to two");
        Check((bool)Prop(slots.GetValue(14),"IsActive"),"second utility uses existing reserved cell");
        Check((Vector2i)Prop(slots.GetValue(13),"GridPosition")==new Vector2i(5,5),"old trinket save cell unchanged");
        Check(slots.Length==32,"cosmetic row appended without reusing legacy slot indices");
        Check((Vector2i)Prop(slots.GetValue(8),"GridPosition")==new Vector2i(0,5),"legacy helmet cell preserved");
        Check((Vector2i)Prop(slots.GetValue(0),"GridPosition")==new Vector2i(0,4),"legacy quick cell preserved");
        Check((bool)slots.GetValue(8).GetType().GetMethod("ItemFits").Invoke(slots.GetValue(8),new object[]{helmet}),"helmet accepted in equipment cell");
        Check(!(bool)slots.GetValue(8).GetType().GetMethod("ItemFits").Invoke(slots.GetValue(8),new object[]{potion}),"potion rejected in helmet cell");
        var package=new ZPackage();player.GetInventory().Save(package);var saved=package.GetArray();
        player.GetInventory().m_inventory.Clear();player.GetInventory().Load(new ZPackage(saved));Call(T("Slots"),"ClearCachedItems");
        Check(player.GetInventory().NrOfItems()==2,"inventory roundtrip preserves both items");
        Check(player.GetInventory().GetItemAt(0,4)?.m_stack==7 && player.GetInventory().GetItemAt(0,5)?.m_quality==2,"slot positions, stacks and quality survive reload");
        Check(player.GetInventory().GetItemAt(0,5).m_customData["test"]=="kept","custom item data survives reload");
        // ItemDrop.Awake does not run on EditMode load clones; restore its prefab link in the fixture.
        foreach(var item in player.GetInventory().GetAllItems())item.m_dropPrefab=db.GetItemPrefab(item.m_shared.m_name);
        var firstUtility=MakeItem(host,db,"EqsUtilityOne",ItemDrop.ItemData.ItemType.Utility,new Vector2i(4,5));
        var secondUtility=MakeItem(host,db,"EqsUtilityTwo",ItemDrop.ItemData.ItemType.Utility,new Vector2i(6,5));
        var trinket=MakeItem(host,db,"EqsTrinket",ItemDrop.ItemData.ItemType.Trinket,new Vector2i(5,5));
        player.GetInventory().m_inventory.AddRange(new[]{firstUtility,secondUtility,trinket});
        Call(T("Slots"),"ClearCachedItems");
        Check((bool)slots.GetValue(14).GetType().GetMethod("ItemFits").Invoke(slots.GetValue(14),new object[]{secondUtility}),"second cell accepts accessories");
        Check(!(bool)slots.GetValue(14).GetType().GetMethod("ItemFits").Invoke(slots.GetValue(14),new object[]{trinket}),"second accessory cell rejects trinkets");
        var multi=T("src.MultiUtility.MultiUtility");Call(multi,"EnsureOwner",player);Set(player,"m_utilityItem",firstUtility);
        Check((int)Call(multi,"GetTargetExtraIndex",player,secondUtility)==0,"different second accessory routes to extra equipment");
        Call(multi,"SetExtra",0,secondUtility);secondUtility.m_equipped=true;
        Check(player.IsItemEquiped(firstUtility) && player.IsItemEquiped(secondUtility),"both accessories recognized as equipped simultaneously");
        Check((int)Call(multi,"GetTargetExtraIndex",player,firstUtility.Clone())==-1,"duplicate accessory does not occupy second slot");
        Set(player,"m_collider",host.AddComponent<CapsuleCollider>());
        var seman=(SEMan)FormatterServices.GetUninitializedObject(typeof(SEMan));
        foreach(var f in typeof(SEMan).GetFields(F).Where(f=>!f.IsStatic&&f.FieldType.IsGenericType&&(f.FieldType.GetGenericTypeDefinition()==typeof(List<>)||f.FieldType.GetGenericTypeDefinition()==typeof(HashSet<>))))f.SetValue(seman,Activator.CreateInstance(f.FieldType));
        Set(seman,"m_character",player);Set(player,"m_seman",seman);
        var effect1=ScriptableObject.CreateInstance<OverhaulEquipmentTestEffect>();effect1.name="EqsEffect1";effect1.m_addMaxCarryWeight=25;
        var effect2=ScriptableObject.CreateInstance<OverhaulEquipmentTestEffect>();effect2.name="EqsEffect2";effect2.m_addMaxCarryWeight=75;
        firstUtility.m_shared.m_equipStatusEffect=effect1;secondUtility.m_shared.m_equipStatusEffect=effect2;
        typeof(Humanoid).GetMethod("UpdateEquipmentStatusEffects",F).Invoke(player,null);
        float carry=300;seman.ModifyMaxCarryWeight(300,ref carry);
        Check(carry==400,"both equipped accessory effects modify actual carry capacity");
        Call(multi,"SetExtra",0,null);typeof(Humanoid).GetMethod("UpdateEquipmentStatusEffects",F).Invoke(player,null);
        carry=300;seman.ModifyMaxCarryWeight(300,ref carry);Check(carry==325,"removing extra accessory removes only its effect");
        Call(multi,"SetExtra",0,secondUtility);typeof(Humanoid).GetMethod("UpdateEquipmentStatusEffects",F).Invoke(player,null);
        carry=300;seman.ModifyMaxCarryWeight(300,ref carry);Check(carry==400,"reequipping restores second effect without duplication");
        var extraSave=new ZPackage();player.GetInventory().Save(extraSave);Call(multi,"Reset");Set(player,"m_utilityItem",null);
        player.GetInventory().m_inventory.Clear();player.m_isLoading=true;player.GetInventory().Load(new ZPackage(extraSave.GetArray()));player.m_isLoading=false;Call(T("Slots"),"ClearCachedItems");
        Check(player.GetInventory().NrOfItems()==5 && player.GetInventory().GetItemAt(6,5)?.m_shared.m_name=="EqsUtilityTwo" && player.GetInventory().GetItemAt(5,5)?.m_shared.m_name=="EqsTrinket","new accessory and old trinket survive save reload in separate cells: "+string.Join(";",player.GetInventory().GetAllItems().Select(i=>i.m_shared.m_name+"@"+i.m_gridPos)));
        foreach(var item in player.GetInventory().GetAllItems())item.m_dropPrefab=db.GetItemPrefab(item.m_shared.m_name);
        var fullGrave=new Inventory("Grave",null,8,7);fullGrave.MoveAll(player.GetInventory());
        Check(fullGrave.NrOfItems()==5 && fullGrave.GetItemAt(6,5)!=null,"grave carries second accessory too");player.GetInventory().MoveAll(fullGrave);
        player.GetInventory().m_inventory.RemoveAll(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Utility||i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Trinket);Call(T("Slots"),"ClearCachedItems");
        Prefix("InventoryBackup+Player_Save_WriteBackup",player);
        Check(player.m_customData.ContainsKey("eaqs_backup"),"character slot backup written");
        var grave=new Inventory("Grave",null,8,7);grave.MoveAll(player.GetInventory());
        Check(grave.NrOfItems()==2 && player.GetInventory().NrOfItems()==0,"grave transfer includes hidden equipment and quick slots");
        player.GetInventory().MoveAll(grave);Call(T("Slots"),"ClearCachedItems");
        Check(player.GetInventory().NrOfItems()==2 && grave.NrOfItems()==0,"grave recovery preserves exact item count");

        AssetBundle bundle;using(var stream=mod.GetManifestResourceStream("Overhaul.augaassets"))using(var buffer=new MemoryStream()){stream.CopyTo(buffer);bundle=AssetBundle.LoadFromMemory(buffer.ToArray());}
        var gui=host.AddComponent<InventoryGui>();InventoryGui.m_instance=gui;
        var panel=Object.Instantiate(bundle.LoadAsset<GameObject>("Inventory_screen").transform.Find("root/Player").gameObject,host.transform,false);
        gui.m_player=(RectTransform)panel.transform;gui.m_playerGrid=panel.GetComponentInChildren<InventoryGrid>(true);var grid=gui.m_playerGrid;Set(grid,"m_inventory",player.GetInventory());
        var elements=new List<InventoryElement>();for(int i=0;i<64;i++){var go=Object.Instantiate(grid.m_elementPrefab,grid.transform,false);var element=go.GetComponent<InventoryElement>();Set(element,"<Position>k__BackingField",new Vector2i(i%8,i/8));elements.Add(element);}Set(grid,"m_elements",elements);
        Call(mod.GetType("Auga.EquipmentPanelBridge"),"Layout",grid);
        var equipment=panel.transform.Find("AugaEquipment");
        var a=(RectTransform)equipment.Find("Equipment4");var b=(RectTransform)equipment.Find("Equipment6");var c=(RectTransform)equipment.Find("Equipment5");
        Check(a.anchoredPosition.x==b.anchoredPosition.x && b.anchoredPosition.x==c.anchoredPosition.x && a.anchoredPosition.y>b.anchoredPosition.y && b.anchoredPosition.y>c.anchoredPosition.y,"second accessory visually between first and trinket");
        var hint=elements[46].transform.Find("EquipmentHint").GetComponent<AugaUnity.EquipmentSlotHint>();
        Check(hint.Slot==6 && !hint.raycastTarget && hint.color.a<.35f,"empty second accessory has discreet noninteractive monochrome glyph");
        Call(mod.GetType("Auga.EquipmentPanelBridge"),"UpdateSlotHint",elements[46],6,true);
        Check(!hint.gameObject.activeSelf,"glyph hidden when equipment slot is occupied");
        Call(mod.GetType("Auga.EquipmentPanelBridge"),"UpdateSlotHint",elements[46],6,false);
        Check(hint.gameObject.activeSelf,"glyph returns when equipment slot is emptied");
        Check(equipment.gameObject.activeSelf,"existing Auga equipment panel visible");
        Check(equipment.Find("Paperdoll").GetSiblingIndex()>equipment.Find("Background").GetSiblingIndex() && equipment.Find("Paperdoll").GetSiblingIndex()<equipment.Find("Equipment0").GetSiblingIndex(),"silhouette drawn above opaque background and behind equipment cells");
        Check(elements[40].transform.IsChildOf(equipment) && elements[32].transform.IsChildOf(equipment),"equipment and quick slots use authored Auga anchors");
        Check(elements[32].transform.Find("binding").GetComponent<TMPro.TMP_Text>().enabled,"quick slot key label remains visible");
        var equipmentRect=(RectTransform)equipment;var originalSize=gui.m_player.sizeDelta;
        float equipmentHeight=equipmentRect.rect.height;var originalOffset=equipmentRect.anchoredPosition;
        foreach(int extraRows in new[]{1,2,5}) {
            gui.m_player.sizeDelta=originalSize+new Vector2(0,extraRows*70);Call(mod.GetType("Auga.EquipmentPanelBridge"),"Layout",grid);
            Check(Mathf.Approximately(equipmentRect.rect.height,392)&&equipmentRect.rect.height==equipmentHeight&&equipmentRect.anchoredPosition==originalOffset,"equipment frame stays fixed when inventory gains rows: "+extraRows);
        }
        gui.m_player.sizeDelta=originalSize;
        // Sorting must leave even unequipped quick-slot consumables in their reserved cell.
        var savedPotion=player.GetInventory().GetAllItems().First(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Consumable);
        var savedHelmet=player.GetInventory().GetAllItems().First(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Helmet);
        savedPotion.m_gridPos=new Vector2i(0,4);savedHelmet.m_gridPos=new Vector2i(0,5);savedHelmet.m_equipped=true;
        var ordinary=MakeItem(host,db,"EqsOrdinary",ItemDrop.ItemData.ItemType.Material,new Vector2i(5,2));player.GetInventory().m_inventory.Add(ordinary);
        Set(gui,"m_hiddenFrames",0);var sorter=host.AddComponent<AugaInventorySorter>();sorter.SortClicked();
        Check(savedPotion.m_gridPos==new Vector2i(0,4) && savedPotion.m_stack==7 && savedHelmet.m_gridPos==new Vector2i(0,5),"Auga sort protects integrated quick and equipment cells");
        Check(ordinary.m_gridPos==new Vector2i(0,1),"Auga sort still arranges ordinary inventory");
        var game=host.AddComponent<Game>();typeof(Game).GetProperty("instance",F).SetValue(null,game);Set(game,"m_playerProfile",FormatterServices.GetUninitializedObject(typeof(PlayerProfile)));
        Call(T("Slots"),"ClearCachedItems");
        Call(T("DeathPatches"),"OnDeathPrefix",player);
        Check(player.GetInventory().NrOfItems()==3,"default death policy leaves all items for native grave transfer");
        ConfigValue("DontDropQuickslotsOnDeath",true);ConfigValue("DontDropEquipmentOnDeath",true);
        Call(T("DeathPatches"),"OnDeathPrefix",player);
        Check(player.GetInventory().NrOfItems()==1 && player.GetInventory().ContainsItem(ordinary),"keep-on-death isolates only equipment and quick items");
        Call(T("DeathPatches"),"OnDeathPostfix",player);
        Check(player.GetInventory().NrOfItems()==3 && savedHelmet.m_equipped,"kept equipment restored once with equipped state");
        Call(T("DeathPatches"),"OnDeathPostfix",player);
        Check(player.GetInventory().NrOfItems()==3,"repeated death finalizer never duplicates items");
        Cosmetics(host, db, player, gui, slots, elements);
        typeof(Game).GetProperty("instance",F).SetValue(null,null);
        var art=(AssetBundle)Call(T("EquipmentAndQuickSlots"),"LoadAssetBundle","eaqs");Check(art.LoadAsset<Sprite>("PaperdollMale") && art.LoadAsset<GameObject>("Paperdolls"),"fallback artwork embedded in single DLL");art.Unload(true);
        Call(T("EquipmentAndQuickSlots"),"Stop");harmonyType.GetMethod("UnpatchSelf").Invoke(harmony,null);
        Player.m_localPlayer=null;InventoryGui.m_instance=null;ObjectDB.m_instance=null;Object.DestroyImmediate(host);bundle.Unload(true);
    }

    static void ClickTab(InventoryGui gui,string name)
    {
        var canvasObject=new GameObject("Tab pointer check",typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.GraphicRaycaster));
        var events=new GameObject("Tab events",typeof(UnityEngine.EventSystems.EventSystem));
        var cameraObject=new GameObject("Tab camera",typeof(Camera));var camera=cameraObject.GetComponent<Camera>();var target=new RenderTexture(640,480,24);camera.targetTexture=target;
        try {
            var canvas=canvasObject.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;
            var source=gui.m_player.Find("AugaEquipment/Tabs/"+name).GetComponent<UnityEngine.UI.Button>();
            var clone=Object.Instantiate(source.gameObject,canvasObject.transform,false);
            var rect=(RectTransform)clone.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);rect.anchoredPosition=Vector2.zero;
            var button=clone.GetComponent<UnityEngine.UI.Button>();button.onClick=new UnityEngine.UI.Button.ButtonClickedEvent();button.onClick.AddListener(()=>source.onClick.Invoke());
            Canvas.ForceUpdateCanvases();camera.Render();
            var pointer=new UnityEngine.EventSystems.PointerEventData(events.GetComponent<UnityEngine.EventSystems.EventSystem>()) { position=RectTransformUtility.WorldToScreenPoint(camera,rect.position),button=UnityEngine.EventSystems.PointerEventData.InputButton.Left };
            var hits=new List<UnityEngine.EventSystems.RaycastResult>();canvasObject.GetComponent<UnityEngine.UI.GraphicRaycaster>().Raycast(pointer,hits);
            Debug.Log("TAB RAYCAST "+name+" hits="+hits.Count+" position="+pointer.position+" screen="+Screen.width+"x"+Screen.height+" graphics="+string.Join(";",clone.GetComponentsInChildren<UnityEngine.UI.Graphic>().Select(g=>g.name+" active="+g.isActiveAndEnabled+" ray="+g.raycastTarget+" depth="+g.depth+" culled="+g.canvasRenderer.cull+" rect="+g.rectTransform.rect)));
            Check(hits.Count>0&&UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[0].gameObject)==clone,"mouse raycast reaches "+name+" tab button");
            UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        } finally { Object.DestroyImmediate(canvasObject);Object.DestroyImmediate(events);Object.DestroyImmediate(cameraObject);Object.DestroyImmediate(target); }
    }

    static void Cosmetics(GameObject host,ObjectDB db,Player player,InventoryGui gui,Array slots,List<InventoryElement> elements)
    {
        var inv=player.GetInventory();var originalItems=inv.GetAllItems().ToArray();
        var normal=originalItems.First(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Helmet);
        player.m_helmetItem=normal;normal.m_shared.m_armor=10;normal.m_shared.m_movementModifier=-.05f;
        float armor=player.GetBodyArmor(), movement=player.GetEquipmentMovementModifier();
        var effect=ScriptableObject.CreateInstance<OverhaulEquipmentTestEffect>();effect.name="CosmeticForbiddenEffect";effect.m_addMaxCarryWeight=1000;
        var types=new[]{ItemDrop.ItemData.ItemType.Helmet,ItemDrop.ItemData.ItemType.Chest,ItemDrop.ItemData.ItemType.Legs,ItemDrop.ItemData.ItemType.Shoulder};
        var cosmetics=new List<ItemDrop.ItemData>();
        for(int i=0;i<4;i++) {
            var slot=slots.GetValue(24+i);var pos=(Vector2i)Prop(slot,"GridPosition");
            Check(pos==new Vector2i(i,7)&&!(bool)Prop(slot,"IsEquipmentSlot")&&(bool)Prop(slot,"IsCosmeticSlot"),"cosmetic slot has distinct saved cell "+i);
            var item=MakeItem(host,db,"Cosmetic"+types[i],types[i],pos);item.m_shared.m_maxStackSize=1;item.m_shared.m_armor=1000;item.m_shared.m_movementModifier=-.9f;item.m_shared.m_equipStatusEffect=effect;item.m_variant=i;
            cosmetics.Add(item);inv.m_inventory.Add(item);
            Check((bool)slot.GetType().GetMethod("ItemFits").Invoke(slot,new object[]{item}),"matching cosmetic type accepted "+i);
            Check(!(bool)slot.GetType().GetMethod("ItemFits").Invoke(slot,new object[]{originalItems.First(x=>x.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Material)}),"non-armor rejected from cosmetic slot "+i);
            Check(!player.EquipItem(item,false)&&!player.IsItemEquiped(item)&&!item.m_equipped,"cosmetic cannot enter gameplay equipment "+i);
            int queued=player.m_actionQueue.Count;player.QueueEquipAction(item);Check(player.m_actionQueue.Count==queued,"cosmetic right-click does not enqueue equip "+i);
        }
        Call(T("Slots"),"ClearCachedItems");
        typeof(Humanoid).GetMethod("UpdateEquipmentStatusEffects",F).Invoke(player,null);
        Check(player.GetBodyArmor()==armor&&player.GetEquipmentMovementModifier()==movement,"cosmetic armor and movement stats ignored");
        float carry=300;player.m_seman.ModifyMaxCarryWeight(300,ref carry);Check(carry<1000,"cosmetic equipment status effects ignored");
        var view=host.GetComponent<ZNetView>()??host.AddComponent<ZNetView>();
        var visual=host.AddComponent<VisEquipment>();visual.m_nview=view;
        player.SetupVisEquipment(visual,false);
        Check(visual.m_helmetItem==cosmetics[0].m_dropPrefab.name.GetStableHashCode()&&visual.m_chestItem==cosmetics[1].m_dropPrefab.name.GetStableHashCode()&&visual.m_legItem==cosmetics[2].m_dropPrefab.name.GetStableHashCode()&&visual.m_shoulderItem==cosmetics[3].m_dropPrefab.name.GetStableHashCode(),"native visual setup prioritizes all four cosmetics");
        Check(visual.m_shoulderItemVariant==3,"cosmetic cape variant preserved");
        cosmetics[0].m_gridPos=new Vector2i(3,1);player.SetupVisEquipment(visual,false);
        Check(visual.m_helmetItem==normal.m_dropPrefab.name.GetStableHashCode(),"empty cosmetic slot falls back to normal worn helmet");
        cosmetics[0].m_gridPos=new Vector2i(0,7);player.SetupVisEquipment(visual,false);
        var previousMenu=FejdStartup.instance;var previousLoaded=T("Slots").GetField("loadedPlayer",F).GetValue(null);var previousVisual=player.m_visEquipment;
        var menu=host.AddComponent<FejdStartup>();
        try {
            typeof(FejdStartup).GetField("m_instance",F).SetValue(null,menu);menu.m_playerInstance=player.gameObject;
            Player.m_localPlayer=null;T("Slots").GetField("loadedPlayer",F).SetValue(null,null);player.m_visEquipment=visual;
            visual.m_helmetItem=visual.m_chestItem=visual.m_legItem=visual.m_shoulderItem=0;
            Call(T("CosmeticEquipment+RefreshMenuPreview"),"Postfix",menu);
            Check(visual.m_helmetItem==cosmetics[0].m_dropPrefab.name.GetStableHashCode()&&visual.m_chestItem==cosmetics[1].m_dropPrefab.name.GetStableHashCode()&&visual.m_legItem==cosmetics[2].m_dropPrefab.name.GetStableHashCode()&&visual.m_shoulderItem==cosmetics[3].m_dropPrefab.name.GetStableHashCode(),"menu preview applies all cosmetics without a local or loading player");
            Check(visual.m_shoulderItemVariant==3,"menu preserves cosmetic cape variant");
            var normalHelmet=player.m_helmetItem;player.m_helmetItem=null;Call(T("CosmeticEquipment+RefreshMenuPreview"),"Postfix",menu);
            Check(visual.m_helmetItem==cosmetics[0].m_dropPrefab.name.GetStableHashCode(),"menu character wearing only cosmetics is refreshed");player.m_helmetItem=normalHelmet;
            cosmetics[0].m_gridPos=new Vector2i(3,1);player.SetupVisEquipment(visual,false);
            Check(visual.m_helmetItem==normal.m_dropPrefab.name.GetStableHashCode(),"menu empty cosmetic falls back to equipped armor");cosmetics[0].m_gridPos=new Vector2i(0,7);
            menu.m_playerInstance=null;player.SetupVisEquipment(visual,false);
            Check(visual.m_helmetItem==normal.m_dropPrefab.name.GetStableHashCode(),"deselected menu character is not overridden");
            menu.m_playerInstance=player.gameObject;player.SetupVisEquipment(visual,false);
            Check(visual.m_helmetItem==cosmetics[0].m_dropPrefab.name.GetStableHashCode(),"reselecting character restores its cosmetics");
            Check(cosmetics.All(i=>!i.m_equipped)&&player.m_helmetItem==normalHelmet,"menu cosmetic preview does not equip items or change gameplay gear");
        } finally { Player.m_localPlayer=player;T("Slots").GetField("loadedPlayer",F).SetValue(null,previousLoaded);player.m_visEquipment=previousVisual;typeof(FejdStartup).GetField("m_instance",F).SetValue(null,previousMenu);Object.DestroyImmediate(menu); }
        var previousManager=ZDOMan.instance;var previousRpc=ZRoutedRpc.instance;var previousView=player.m_nview;var previousNet=ZNet.instance;
        try {
            typeof(ZNet).GetField("m_instance",F).SetValue(null,host.AddComponent<ZNet>());
            new ZRoutedRpc(true);var manager=new ZDOMan(512);
            var data=manager.CreateNewZDO(Vector3.zero,"CosmeticTestPlayer".GetStableHashCode());data.SetPrefab("CosmeticTestPlayer".GetStableHashCode());
            view.m_zdo=data;player.m_nview=view;
            visual.SetHelmetItem(0);visual.SetChestItem(0);visual.SetLegItem(0);visual.SetShoulderItem(0,0,0);
            player.SetupVisEquipment(visual,false);
            Check(data.GetInt(ZDOVars.s_helmetItem)==cosmetics[0].m_dropPrefab.name.GetStableHashCode()&&data.GetInt(ZDOVars.s_chestItem)==cosmetics[1].m_dropPrefab.name.GetStableHashCode()&&data.GetInt(ZDOVars.s_legItem)==cosmetics[2].m_dropPrefab.name.GetStableHashCode()&&data.GetInt(ZDOVars.s_shoulderItem)==cosmetics[3].m_dropPrefab.name.GetStableHashCode(),"cosmetic appearances published through native replicated armor fields");
            Check(data.GetInt(ZDOVars.s_shoulderItemVariant)==3,"cape variant replicated");
            cosmetics[0].m_gridPos=new Vector2i(3,1);player.SetupVisEquipment(visual,false);
            Check(data.GetInt(ZDOVars.s_helmetItem)==normal.m_dropPrefab.name.GetStableHashCode(),"removing cosmetic publishes normal appearance for peers");
            cosmetics[0].m_gridPos=new Vector2i(0,7);data.SetOwner(ZNet.GetUID()+1);
            int beforeRemote=visual.m_helmetItem;Call(T("CosmeticEquipment"),"Apply",player,visual);
            Check(visual.m_helmetItem==beforeRemote,"non-owner does not override replicated cosmetic visuals");
        } finally { view.m_zdo=null;player.m_nview=previousView;typeof(ZDOMan).GetField("s_instance",F).SetValue(null,previousManager);typeof(ZRoutedRpc).GetField("s_instance",F).SetValue(null,previousRpc);typeof(ZNet).GetField("m_instance",F).SetValue(null,previousNet); }
        ClickTab(gui,"Cosmetic");
        Check(elements.Skip(56).Take(4).All(e=>e.gameObject.activeSelf)&&!elements[40].gameObject.activeSelf&&!elements[32].gameObject.activeSelf,"cosmetic tab shows exactly its four slots and hides normal equipment and food");
        ClickTab(gui,"Equipment");
        Check(elements[40].gameObject.activeSelf&&elements[32].gameObject.activeSelf&&elements.Skip(56).Take(4).All(e=>!e.gameObject.activeSelf),"equipment tab restores equipment and food shortcuts");
        Prefix("InventoryBackup+Player_Save_WriteBackup",player);
        Check(player.m_customData.ContainsKey("eaqs_backup"),"cosmetics included in character slot backup");
        var save=new ZPackage();inv.Save(save);inv.m_inventory.Clear();player.m_isLoading=true;inv.Load(new ZPackage(save.GetArray()));player.m_isLoading=false;
        Call(T("Slots"),"ClearCachedItems");
        for(int i=0;i<4;i++){var item=inv.GetItemAt(i,7);Check(item!=null&&item.m_shared.m_itemType==types[i]&&item.m_customData["test"]=="kept"&&!item.m_equipped,"cosmetic persists with quality variant and custom data "+i);}
        var grave=new Inventory("Grave",null,8,8);int count=inv.NrOfItems();grave.MoveAll(inv);Check(grave.NrOfItems()==count&&grave.GetItemAt(3,7)!=null,"native grave includes cosmetic row");inv.MoveAll(grave);
        Check(inv.NrOfItems()==count&&grave.NrOfItems()==0&&inv.GetItemAt(3,7)!=null,"grave recovery restores cosmetics without duplication");
        Call(T("Slots"),"ClearCachedItems");Call(T("DeathPatches"),"OnDeathPrefix",player);
        Check(inv.GetAllItems().All(i=>i.m_gridPos.y!=7),"keep-equipment-on-death also protects cosmetics");Call(T("DeathPatches"),"OnDeathPostfix",player);
        Check(inv.NrOfItems()==count&&Enumerable.Range(0,4).All(i=>!inv.GetItemAt(i,7).m_equipped),"kept cosmetics return unequipped exactly once");
        inv.m_inventory.Clear();player.m_helmetItem=null;Call(T("Slots"),"ClearCachedItems");
        for(int i=0;i<4;i++) {
            var clickItem=MakeItem(host,db,"ClickCosmetic"+i,types[i],new Vector2i(i,0));inv.m_inventory.Add(clickItem);
            Check(!(bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,clickItem,false)&&clickItem.m_gridPos==new Vector2i(i,0),"normal equipment tab preserves native right click "+i);
            Check((bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,clickItem,true)&&clickItem.m_gridPos==new Vector2i(i,7)&&!clickItem.m_equipped,"cosmetic right click routes matching armor "+i);
            Check((bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,clickItem,true)&&clickItem.m_gridPos.y<4&&!clickItem.m_equipped,"cosmetic right click returns armor to bag "+i);
        }
        var replacement=inv.GetAllItems()[0];
        var oldCosmetic=MakeItem(host,db,"OldCosmetic",types[0],new Vector2i(0,7));inv.m_inventory.Add(oldCosmetic);
        var sourcePos=replacement.m_gridPos;
        Call(T("Slots"),"ClearCachedItems");
        Check((bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,replacement,true)&&oldCosmetic.m_gridPos==sourcePos&&replacement.m_gridPos==new Vector2i(0,7),"right click swaps resident into source bag cell");
        var material=MakeItem(host,db,"ClickMaterial",ItemDrop.ItemData.ItemType.Material,new Vector2i(7,0));inv.m_inventory.Add(material);
        Check(!(bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,material,true),"unsupported type preserves native item action");
        for(int y=0;y<4;y++)for(int x=0;x<8;x++)if(inv.GetItemAt(x,y)==null) {var filler=material.Clone();filler.m_gridPos=new Vector2i(x,y);inv.m_inventory.Add(filler);}
        int fullCount=inv.NrOfItems();Call(T("Slots"),"ClearCachedItems");
        Check((bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,replacement,true)&&replacement.m_gridPos==new Vector2i(0,7)&&inv.NrOfItems()==fullCount,"full bag refuses cosmetic removal without item loss");
        Check((bool)Call(T("CosmeticEquipment"),"HandleRightClick",player,inv,oldCosmetic,true)&&replacement.m_gridPos==sourcePos&&oldCosmetic.m_gridPos==new Vector2i(0,7)&&inv.NrOfItems()==fullCount,"full bag still allows cosmetic replacement");
        inv.m_inventory.Clear();inv.m_inventory.AddRange(originalItems);Call(T("Slots"),"ClearCachedItems");
        player.m_helmetItem=null;Object.DestroyImmediate(visual);
    }
    static ItemDrop.ItemData MakeItem(GameObject host,ObjectDB db,string name,ItemDrop.ItemData.ItemType type,Vector2i position)
    {
        var prefab=new GameObject(name);prefab.transform.SetParent(host.transform);var drop=prefab.AddComponent<ItemDrop>();
        drop.m_itemData=new ItemDrop.ItemData();drop.m_itemData.m_shared=new ItemDrop.ItemData.SharedData();drop.m_itemData.m_shared.m_name=name;drop.m_itemData.m_shared.m_itemType=type;drop.m_itemData.m_shared.m_maxStackSize=50;drop.m_itemData.m_dropPrefab=prefab;
        db.m_items.Add(prefab);db.m_itemByHash[name.GetStableHashCode()]=prefab;
        var item=drop.m_itemData.Clone();item.m_dropPrefab=prefab;item.m_gridPos=position;item.m_quality=2;item.m_customData["test"]="kept";return item;
    }
}

// Keep the real SE_Stats modifier; omit VFX/analytics-independent visual setup in this fixture.
public class OverhaulEquipmentTestEffect : SE_Stats { public override void Setup(Character character) { m_character=character; } }


