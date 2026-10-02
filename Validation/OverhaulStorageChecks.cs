using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class OverhaulStorageChecks
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type access, storage; static int checks; static ItemDrop material;
    static readonly List<GameObject> roots = new List<GameObject>();
    static object Call(Type t,string name,params object[] args)=>t.GetMethod(name,All).Invoke(null,args);
    static void Field(Type t,string name,object value)=>t.GetField(name,All).SetValue(null,value);
    static void Check(bool ok,string label){if(!ok)throw new Exception("STORAGE: "+label);checks++;Debug.Log("STORAGE PASS "+label);}
    static void Logged(object sender,object args){var value=args.GetType().GetProperty("Data").GetValue(args);if(value!=null&&value.ToString().StartsWith("Craft from chests:"))Debug.LogError(value);}
    static bool Awake(Component __instance)=>!__instance.name.StartsWith("storage-test-");
    static bool Language(string name,string defaultValue,ref string __result){__result=name=="language"?"French":defaultValue;return false;}
    static int lootCalls, lootAmount;
    static bool CaptureLoot(List<KeyValuePair<GameObject,int>> drops) { lootCalls++;lootAmount+=drops.Sum(d=>d.Value);return false; }
    static bool testShift;
    static float dummyShownDamage;
    static int dummyRefunds;
    static HitData dummyRemoval;
    static readonly Dictionary<ZDOID,float> meterAmounts=new Dictionary<ZDOID,float>();
    static bool MeterSend(ZDOID playerId,float damage)
    { if(playerId!=ZDOID.None){meterAmounts.TryGetValue(playerId,out var old);meterAmounts[playerId]=old+damage;}return false; }
    static bool DummyText(float __2) { dummyShownDamage=__2;return false; }
    static bool DummyRefund(bool __runOriginal) { if(__runOriginal)dummyRefunds++;return false; }
    static bool DummyRemovalHit(HitData hit) { dummyRemoval=hit;return false; }
    static void PrepareRefundStack(ItemDrop __instance)
    {
        if(!__instance.name.StartsWith("storage-test-"))return;
        Data(__instance);__instance.m_nview=__instance.GetComponent<ZNetView>();
    }
    static bool captureRecipes, recipeCraftable;
    static bool CaptureRecipe(bool canCraft)
    {
        if (!captureRecipes) return true;
        recipeCraftable = canCraft;
        return false;
    }
    static bool Shift(ref bool __result){__result=testShift;return false;}
    static T New<T>(string name) where T:Component {var go=new GameObject("storage-test-"+name);go.SetActive(false);roots.Add(go);return go.GetComponent<T>() ?? go.AddComponent<T>();}
    static ZDO Data(Component c,long owner=0)
    {
        var v=c.GetComponent<ZNetView>();if(!v)v=c.gameObject.AddComponent<ZNetView>();
        var d=ZDOMan.instance.CreateNewZDO(c.transform.position,c.name.GetStableHashCode());d.SetPrefab(c.name.GetStableHashCode());d.SetOwner(owner==0?ZNet.GetUID():owner);v.m_zdo=d;
        ZNetScene.instance.m_namedPrefabs[c.name.GetStableHashCode()]=c.gameObject;return d;
    }
    static Container Chest(string name,long creator,Vector3 position,int count)
    {
        var c=New<Container>(name);c.transform.position=position;var d=Data(c);d.Set(ZDOVars.s_creator,creator);
        c.gameObject.AddComponent<Piece>();c.m_nview=c.GetComponent<ZNetView>();c.m_checkGuardStone=false;
        c.m_inventory=new Inventory(name,null,4,4);c.m_inventory.m_onChanged+=c.OnContainerChanged; Add(c.GetInventory(),count);c.Save();
        foreach(var b in c.GetComponents<MonoBehaviour>())b.enabled=false;
        c.gameObject.SetActive(true);
        return c;
    }
    static void Add(Inventory inv,int count,int quality=1,int world=0)
    {
        if(count<=0)return;
        var item=new ItemDrop.ItemData {m_dropPrefab=material.gameObject,m_shared=material.m_itemData.m_shared,m_stack=count,m_quality=quality,m_worldLevel=world};
        item.m_shared.m_name="StorageWood";item.m_shared.m_maxStackSize=100;item.m_shared.m_maxQuality=2;
        inv.AddItem(item);
    }
    static void ChestPlacementChecks(InventoryGui gui)
    {
        var chest=New<Container>("placement-chest");
        chest.m_inventory=new Inventory("placement",null,2,3);
        var source=new Inventory("placement-source",null,4,4);
        var previous=gui.m_currentContainer;gui.m_currentContainer=chest;
        var target=chest.GetInventory();
        Func<string,int,ItemDrop.ItemData> item=(name,count)=>new ItemDrop.ItemData {
            m_shared=new ItemDrop.ItemData.SharedData { m_name=name,m_itemType=ItemDrop.ItemData.ItemType.Material,m_maxStackSize=100 },
            m_stack=count,m_quality=1
        };
        try
        {
            for(int i=0;i<5;i++)
            {
                var entry=item("placement-"+i,1);source.AddItem(entry);target.MoveItemToThis(source,entry);
                Check(entry.m_gridPos.x==i%2&&entry.m_gridPos.y==i/2,"chest quick transfer fills left to right then top to bottom: "+i);
            }
            var first=target.GetItemAt(0,0);target.RemoveItem(first);
            var fill=item("placement-hole",1);source.AddItem(fill);target.MoveItemToThis(source,fill);
            Check(fill.m_gridPos==new Vector2i(0,0),"chest quick transfer fills an upper hole first");
            target.GetItemAt(1,0).m_stack=98;
            var stack=item("placement-1",5);source.AddItem(stack);target.MoveItemToThis(source,stack);
            Check(target.GetItemAt(1,0).m_stack==100&&stack.m_stack==3&&stack.m_gridPos==new Vector2i(1,2),"quick transfer completes existing stack before placing remainder");
            var blocked=item("placement-full",2);source.AddItem(blocked);target.MoveItemToThis(source,blocked);
            Check(source.GetAllItems().Contains(blocked)&&blocked.m_stack==2&&target.GetAllItems().Count==6,"full chest keeps transferred items in source");
            target.RemoveAll();var manual=item("placement-manual",1);source.AddItem(manual);
            Check(target.MoveItemToThis(source,manual,1,1,2)&&target.GetItemAt(1,2)!=null,"manual drop keeps requested bottom slot");
            var ordinary=new Inventory("ordinary",null,2,3);var other=item("placement-other",1);ordinary.AddItem(other);
            Check(other.m_gridPos.y==2,"other inventories keep native material placement");
        }
        finally { gui.m_currentContainer=previous; }
    }
    public static void Run(Assembly plugin)
    {
        access=plugin.GetType("Overhaul.Storage.ChestAccess",true);storage=plugin.GetType("Overhaul.Storage.StationStorage",true);
        var ha=Assembly.LoadFrom(System.IO.Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");
        var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.storage.fixtures"});
        var prefix=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod("Awake",All)});
        var patchMethod=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        foreach(var t in new[]{typeof(Container),typeof(Piece),typeof(ZNetView)})patchMethod.Invoke(harmony,new object[]{t.GetMethod("Awake",All),prefix,null,null,null});
        var language=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod("Language",All)});
        patchMethod.Invoke(harmony,new object[]{typeof(PlatformPrefs).GetMethod("GetString",new[]{typeof(string),typeof(string)}),language,null,null,null});
        var original=Player.m_localPlayer;int world=Game.m_worldLevel;var oldDb=ObjectDB.instance;
        var oldGui=InventoryGui.instance;var oldProfile=Game.instance.m_playerProfile;
        var oldZone=ZoneSystem.instance;
        bool oldModded=Game.isModded;
        var log=plugin.GetType("Overhaul.Utility.Log").GetField("_logSource",All).GetValue(null);var logEvent=log.GetType().GetEvent("LogEvent");
        var sink=Delegate.CreateDelegate(logEvent.EventHandlerType,typeof(OverhaulStorageChecks).GetMethod("Logged",All));logEvent.AddEventHandler(log,sink);
        try
        {
            Game.m_worldLevel=0;
            Game.isModded=true;Achievements.m_cheatCheckFrame=-1;
            if(!ZoneSystem.instance)typeof(ZoneSystem).GetField("s_instance",All).SetValue(null,New<ZoneSystem>("zone"));
            var db=New<ObjectDB>("database");typeof(ObjectDB).GetField("m_instance",All).SetValue(null,db);
            material=New<ItemDrop>("wood");material.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="StorageWood",m_maxStackSize=100,m_maxQuality=2};
            db.m_itemByHash[material.name.GetStableHashCode()]=material.gameObject;material.gameObject.SetActive(true);
            var player=New<Player>("player");var actor=Data(player);actor.Set(ZDOVars.s_playerID,111L);player.m_nview=player.GetComponent<ZNetView>();
            player.GetInventory().RemoveAll();Player.m_localPlayer=player;
            var station=New<CraftingStation>("bench");station.m_showBasicRecipies=true;station.m_useDistance=4;var stationData=Data(station);
            player.m_currentStation=station;
            var remote=New<Player>("remote");var remoteActor=Data(remote,778);remoteActor.Set(ZDOVars.s_playerID,222L);
            var own=Chest("own",111,Vector3.right*2,4);var shared=Chest("shared",222,Vector3.right*30,5);
            var foreign=Chest("foreign",222,Vector3.right*3,50);var outside=Chest("outside",111,Vector3.right*30.01f,50);
            int privateKey=(int)access.GetField("PrivateKey",All).GetValue(null);
            Check(!(bool)Call(access,"Private",own)&&own.CheckAccess(222),"crafted chests public by default including another player");
            Call(access,"SetPrivacy",own,778L,remoteActor.m_uid,true);
            Check(!(bool)Call(access,"Private",own),"non-creator cannot make chest private");
            Call(access,"SetPrivacy",own,778L,actor.m_uid,true);
            Check(!(bool)Call(access,"Private",own),"spoofed character ID rejected");
            Call(access,"SetPrivacy",own,ZNet.GetUID(),actor.m_uid,true);
            Check(own.CheckAccess(111)&&!own.CheckAccess(222),"creator can make private and only creator passes native access");
            foreign.m_nview.GetZDO().Set(privateKey,true);
            var build=plugin.GetType("Overhaul.Storage.BuildStorage",true);
            var table=New<PieceTable>("hammer");table.m_skill=Skills.SkillType.Crafting;
            player.m_buildPieces=table;player.m_currentStation=null;
            var piece=New<Piece>("building");piece.m_resources=new[]{new Piece.Requirement{m_resItem=material,m_amount=9}};
            Check(player.HaveRequirements(piece,Player.RequirementMode.CanBuild),"hammer combines public and own private chests at 30m without station");
            piece.m_resources[0].m_amount=10;
            Check(!player.HaveRequirements(piece,Player.RequirementMode.CanBuild),"hammer excludes foreign private chest and chest beyond 30m");
            Check(player.HaveRequirements(piece,Player.RequirementMode.CanAlmostBuild),"hammer available-material filter includes chest resources");
            Check(player.GetInventory().CountItems("StorageWood")==0,"hammer resource reads do not alter normal inventory counts");
            Call(access,"RequestLease",foreign,ZNet.GetUID(),actor.m_uid,ZDOID.None,"build-private");
            Check(!(bool)Call(access,"Leased",foreign),"hammer authority refuses another owner's private chest");
            Call(access,"RequestLease",outside,ZNet.GetUID(),actor.m_uid,ZDOID.None,"build-far");
            Check(!(bool)Call(access,"Leased",outside),"hammer authority refuses resources beyond 30m");
            Call(access,"RequestLease",own,ZNet.GetUID(),actor.m_uid,ZDOID.None,"build-own");
            Check((bool)Call(access,"OwnLease",own,"build-own"),"hammer authority permits owner private chest without station");
            Call(access,"Release",own,"build-own");
                        table.m_selectedCategory=Piece.PieceCategory.Misc;
            table.m_availablePiecesByCategory=new List<List<Piece>>{new List<Piece>{piece}};
            player.m_placementGhost=New<Piece>("ghost").gameObject;
            piece.m_resources[0].m_amount=9;player.m_lastToolUseTime=-9999;
            Check(!(bool)Call(build,"Begin",player,piece),"hammer queues missing resources instead of placing early");
            Check(own.GetInventory().CountItems("StorageWood")==4,"queue does not consume resources");
            for(int i=0;i<10;i++)Call(build,"Tick");
            Check((bool)Call(build,"Resume",player),"hammer reserves both chests then resumes native placement input");
            player.ConsumeResources(piece.m_resources,0,-1,1);Call(build,"Finish");
            Check(own.GetInventory().CountItems("StorageWood")==0&&shared.GetInventory().CountItems("StorageWood")==0,"hammer native consumption debits exact amount across two chests");
            Check(!(bool)Call(access,"Leased",own)&&!(bool)Call(access,"Leased",shared),"hammer releases reservations after placement");
            Add(own.GetInventory(),4);own.Save();Add(shared.GetInventory(),5);shared.Save();
            Call(build,"Begin",player,piece);player.m_placementGhost.transform.position=Vector3.right;
            Call(build,"Tick");
            Check(!(bool)build.GetProperty("Busy",All).GetValue(null)&&own.GetInventory().CountItems("StorageWood")==4,"changed target cancels pending placement without consuming resources");
            player.m_placementGhost=null;
            player.m_buildPieces=null;player.m_currentStation=station;
            Func<List<Container>> nearby=()=> (List<Container>)Call(storage,"Nearby",player);
            Check(nearby().Contains(own)&&nearby().Contains(shared)&&!nearby().Contains(foreign)&&!nearby().Contains(outside),"30m station radius includes public and own private, excludes foreign private and outside");
            player.transform.position=Vector3.left*2;
            Check(nearby().Contains(shared),"range measured from station rather than crafting player");
            shared.m_inUse=true;Check(!nearby().Contains(shared),"open chest excluded from crafting");shared.m_inUse=false;
            Add(player.GetInventory(),2);
            Check(player.GetInventory().CountItems("StorageWood")==2,"ordinary inventory count unchanged");
            var resource=material;
            var recipe=ScriptableObject.CreateInstance<Recipe>();recipe.m_resources=new[]{new Piece.Requirement{m_resItem=resource,m_amount=11}};
            Func<bool> have=()=> (bool)typeof(Player).GetMethod("HaveRequirementItems",All).Invoke(player,new object[]{recipe,false,1,1});
            Check(have(),"native requirements combine carried and both authorized chests");
            Check(player.GetInventory().CountItems("StorageWood")==2,"resource query restores isolated count scope");
            Field(storage,"SuppressReading",true);Check(!have(),"local-only preflight excludes virtual resources");Field(storage,"SuppressReading",false);
            player.m_currentStation=null;Check(!have(),"no chest crafting away from station");player.m_currentStation=station;
            recipe.m_resources[0].m_amount=12;Check(!have(),"insufficient total resources remain unavailable");recipe.m_resources[0].m_amount=11;
            shared.m_nview.GetZDO().Set(privateKey,true);Check(!have(),"privacy change immediately excludes remote resources");shared.m_nview.GetZDO().Set(privateKey,false);
            Call(access,"RequestLease",own,778L,remoteActor.m_uid,stationData.m_uid,"foreign");Check(!(bool)Call(access,"Leased",own),"private resources cannot be reserved by another player");
            Call(access,"RequestLease",own,ZNet.GetUID(),actor.m_uid,stationData.m_uid,"first");
            Check((bool)Call(access,"OwnLease",own,"first"),"authenticated owner obtains exclusive crafting lease");
            Call(access,"SetPrivacy",own,ZNet.GetUID(),actor.m_uid,false);Check((bool)Call(access,"Private",own),"privacy stable during transaction");
            Call(access,"RequestLease",own,ZNet.GetUID(),actor.m_uid,stationData.m_uid,"second");Check((bool)Call(access,"OwnLease",own,"first"),"second transaction cannot replace live lease");
            Check(own.GetInventory().CountItems("StorageWood")==4,"reservation never consumes materials");
            Call(access,"Release",own,"first");Check(!(bool)Call(access,"Leased",own)&&own.GetInventory().CountItems("StorageWood")==4,"cancel releases unchanged inventory");
            Call(access,"RequestLease",shared,778L,actor.m_uid,stationData.m_uid,"spoof");Check(!(bool)Call(access,"Leased",shared),"public crafting lease still authenticates sender");
            Call(access,"RequestLease",outside,ZNet.GetUID(),actor.m_uid,stationData.m_uid,"far");Check(!(bool)Call(access,"Leased",outside),"authority rejects out-of-range resource request");
            Call(access,"RequestLease",shared,778L,remoteActor.m_uid,stationData.m_uid,"remote");
            Check(shared.m_nview.GetZDO().GetOwner()==778&&(bool)Call(access,"Leased",shared),"native ownership transferred to authorized remote crafter");
            shared.m_nview.GetZDO().SetOwner(ZNet.GetUID());int until=(int)access.GetField("LeaseUntilKey",All).GetValue(null);
            shared.m_nview.GetZDO().Set(until,0L);Check(!(bool)Call(access,"Leased",shared),"expired/disconnected lease no longer blocks access");
            Field(storage,"Transaction",new List<Container>{own,shared});Field(storage,"Executing",true);
            player.ConsumeResources(recipe.m_resources,1,-1,1);
            Field(storage,"Executing",false);Field(storage,"Transaction",null);
            Check(player.GetInventory().CountItems("StorageWood")==0&&own.GetInventory().CountItems("StorageWood")==0&&shared.GetInventory().CountItems("StorageWood")==0,"native consumption removes exact total once across carried and two chests");
            Check(foreign.GetInventory().CountItems("StorageWood")==50&&outside.GetInventory().CountItems("StorageWood")==50,"foreign private and distant chest inventories untouched");
            Add(player.GetInventory(),4);Add(own.GetInventory(),4);own.Save();
            Field(storage,"Transaction",new List<Container>{own});Field(storage,"Executing",true);
            player.GetInventory().RemoveItem("StorageWood",3,-1,true);Field(storage,"Executing",false);Field(storage,"Transaction",null);
            Check(player.GetInventory().CountItems("StorageWood")==1&&own.GetInventory().CountItems("StorageWood")==4,"carried materials consumed before chest resources");
            player.GetInventory().RemoveAll();recipe.m_requireOnlyOneIngredient=true;recipe.m_resources[0].m_amount=4;
            int amount,extra;var chosen=player.GetFirstRequiredItem(player.GetInventory(),recipe,1,out amount,out extra);
            Check(chosen!=null&&amount==4&&chosen.m_shared.m_name=="StorageWood","native one-ingredient recipe resolves item data from chest");
            Game.m_worldLevel=1;Check(!have(),"old world-level resources excluded");Game.m_worldLevel=0;
            own.GetInventory().RemoveAll();Add(own.GetInventory(),2,1);Add(own.GetInventory(),2,2);own.Save();
            Check(!have(),"different item qualities not improperly combined");
            own.GetInventory().RemoveAll();Add(own.GetInventory(),8);own.Save();player.GetInventory().RemoveAll();
            var output=New<ItemDrop>("product");output.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="StorageProduct",m_maxStackSize=1,m_maxQuality=1};
            db.m_itemByHash[output.name.GetStableHashCode()]=output.gameObject;output.gameObject.SetActive(true);
            recipe.m_item=output;recipe.m_requireOnlyOneIngredient=false;recipe.m_resources[0].m_amount=4;
            var gui=New<InventoryGui>("inventory");typeof(InventoryGui).GetField("m_instance",All).SetValue(null,gui);
            gui.m_hiddenFrames=0;gui.m_craftRecipe=recipe;
            ChestPlacementChecks(gui);
            var captureRecipe = Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod("CaptureRecipe",All)});
            patchMethod.Invoke(harmony,new object[]{typeof(InventoryGui).GetMethod("AddRecipeToList",All),captureRecipe,null,null,null});
            gui.m_tabCraft=New<UnityEngine.UI.Button>("craft-tab");gui.m_tabCraft.interactable=false;gui.m_tabCraft.gameObject.SetActive(true);
            var recipeListRoot=new GameObject("storage-test-recipe-list",typeof(RectTransform));roots.Add(recipeListRoot);
            gui.m_recipeListRoot=recipeListRoot.GetComponent<RectTransform>();
            captureRecipes=true;
            Action refreshList=()=> { recipeCraftable=false;gui.UpdateRecipeList(new List<Recipe>{recipe}); };
            Field(storage,"SuppressReading",true);
            refreshList();
            Check(recipeCraftable,"recipe list includes chest resources during carried-only craft scope");
            Check((bool)storage.GetField("SuppressReading",All).GetValue(null),"recipe list restores carried-only craft protection");
            Field(storage,"SuppressReading",false);
            Add(shared.GetInventory(),4);shared.Save();recipe.m_resources[0].m_amount=12;
            var reserved=new List<Container>{own};Field(storage,"Transaction",reserved);Field(storage,"Executing",true);
            refreshList();
            Check(recipeCraftable,"recipe list includes authorized chests outside current craft transaction");
            Check(ReferenceEquals(storage.GetField("Transaction",All).GetValue(null),reserved)&&!have(),"recipe list restores restricted transaction and consumption requirement checks");
            shared.m_inUse=true;refreshList();Check(!recipeCraftable,"recipe list still excludes occupied chests");shared.m_inUse=false;
            recipe.m_resources[0].m_amount=13;refreshList();Check(!recipeCraftable,"recipe list stays grey when total materials are insufficient");
            Field(storage,"Executing",false);Field(storage,"Transaction",null);captureRecipes=false;
            shared.GetInventory().RemoveAll();shared.Save();recipe.m_resources[0].m_amount=4;
            Game.instance.m_playerProfile=new PlayerProfile(null,FileHelpers.FileSource.Local);
            patchMethod.Invoke(harmony,new object[]{typeof(InventoryGui).GetMethod("UpdateCraftingPanel",All),prefix,null,null,null});
            gui.DoCrafting(player);
            Check((bool)storage.GetProperty("Busy",All).GetValue(null)&&own.GetInventory().CountItems("StorageWood")==8&&player.GetInventory().CountItems("StorageProduct")==0,"craft waits for reservation without early output or consumption");
            for(int i=0;i<15;i++)Call(storage,"Tick");
            Check(!(bool)storage.GetProperty("Busy",All).GetValue(null)&&own.GetInventory().CountItems("StorageWood")==4&&player.GetInventory().CountItems("StorageProduct")==1,"complete native crafting through pending ownership flow creates one item and consumes exact chest resources");
            Check(Game.instance.m_playerProfile.m_playerStats[0].m_stats.TryGetValue(PlayerStatType.Crafts,out float crafted)&&crafted==1,"native craft completes through final player statistics");
            Check(!(bool)Call(access,"Leased",own),"successful craft releases chest immediately");
            own.m_lastRevision=0;own.Load();Check(own.GetInventory().CountItems("StorageWood")==4,"consumed chest quantity persists through native ZDO reload: "+own.GetInventory().CountItems("StorageWood"));
            gui.DoCrafting(player);gui.m_hiddenFrames=2;Call(storage,"Tick");gui.m_hiddenFrames=0;
            Check(!(bool)storage.GetProperty("Busy",All).GetValue(null)&&own.GetInventory().CountItems("StorageWood")==4&&player.GetInventory().CountItems("StorageProduct")==1,"closing crafting UI cancels pending operation without loss or duplicate output");
            while(player.GetInventory().HaveEmptySlot())player.GetInventory().AddItem(output.m_itemData.Clone());
            int products=player.GetInventory().CountItems("StorageProduct");gui.DoCrafting(player);for(int i=0;i<15;i++)Call(storage,"Tick");
            Check(player.GetInventory().CountItems("StorageProduct")==products&&own.GetInventory().CountItems("StorageWood")==4,"full player inventory produces nothing and consumes no chest resources");
            var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");
            try
            {
                var host=New<Transform>("ui-host");
                var panel=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("Inventory_screen").transform.Find("root/Container").gameObject,host,false);
                gui.m_container=(RectTransform)panel.transform;gui.m_currentContainer=own;
                var ui=plugin.GetType("Overhaul.Storage.ChestPrivacyUi");Call(ui,"Postfix",gui);
                var node=panel.transform.Find("Privacy");var button=node.GetComponent<UnityEngine.UI.Button>();
                Check(node.gameObject.activeSelf&&((RectTransform)node).anchoredPosition==new Vector2(-18,-234)&&((RectTransform)node).sizeDelta==new Vector2(40,40),"real prefab shows owner button below existing actions with proportional size");
                button.onClick.Invoke();Call(ui,"Postfix",gui);
                Check(!(bool)Call(access,"Private",own)&&node.Find("Public").gameObject.activeSelf&&!node.Find("Locked").gameObject.activeSelf,"real UI callback makes chest public and updates lock icon");
                button.onClick.Invoke();Call(ui,"Postfix",gui);
                Check((bool)Call(access,"Private",own)&&node.Find("Locked").gameObject.activeSelf,"real UI callback makes chest private");
                gui.m_currentContainer=foreign;Call(ui,"Postfix",gui);
                Check(!node.gameObject.activeSelf&&panel.transform.Find("Bkg").GetComponent<AugaUnity.AugaPanelNotches>().CentersFromTopLeft.Length==3,"non-creator has no privacy button or leftover panel notch");
                UnityEngine.Object.DestroyImmediate(panel);gui.m_container=null;
            }
            finally{bundle.Unload(true);}
            own.m_nview.GetZDO().Set(ZDOVars.s_creator,0L);Check(!(bool)Call(access,"Eligible",own),"world chest without crafted owner excluded");
            var dismantle=plugin.GetType("Overhaul.Storage.ShipDismantle",true);
            var buoyancy=plugin.GetType("Overhaul.Storage.ItemBuoyancy",true);
            var sinking=New<ItemDrop>("sinking-item");Data(sinking);
            var body=sinking.gameObject.AddComponent<Rigidbody>();sinking.gameObject.AddComponent<BoxCollider>();
            Call(buoyancy,"Prefix",sinking);
            var floating=sinking.GetComponent<Floating>();
            Check(floating&&floating.enabled,"dropped item without buoyancy receives native Floating");
            floating.m_force=.75f;Call(buoyancy,"Prefix",sinking);
            Check(sinking.GetComponents<Floating>().Length==1&&floating.m_force==.75f,"existing floating behavior retained without duplicate component");
            floating.enabled=false;Call(buoyancy,"Prefix",sinking);
            Check(floating.enabled,"disabled item flotation enabled");
            // Initialize the same references as native Awake on the inactive fixture.
            floating.m_body=body;floating.m_nview=sinking.GetComponent<ZNetView>();floating.m_collider=sinking.GetComponent<Collider>();
            floating.CustomFixedUpdate(.02f);
            Check(body.linearVelocity==Vector3.zero,"floating does not push dry land drops");
            floating.m_waterLevel=sinking.transform.position.y+2f;
            // Isolate the buoyancy physics from the unrelated drop registry lifecycle.
            UnityEngine.Object.DestroyImmediate(sinking);
            floating.gameObject.SetActive(true);floating.m_waterLevel=floating.transform.position.y+2f;
            var simulation=Physics.simulationMode;
            try { Physics.simulationMode=SimulationMode.Script;floating.CustomFixedUpdate(.02f);Physics.Simulate(.02f); }
            finally { Physics.simulationMode=simulation; }
            Check(body.linearVelocity.y>0,"submerged drop receives upward force from native Floating");
            floating.gameObject.SetActive(false);
            var refundInventory=new Inventory("boat-test",null,1,1);Add(refundInventory,98);
            var refund=material.m_itemData.Clone();refund.m_stack=5;refund.m_dropPrefab=material.gameObject;
            int remainder=(int)Call(dismantle,"Store",refundInventory,refund);
            Check(remainder==3&&refundInventory.CountItems("StorageWood")==100,"boat refund fills partial stack and leaves exact surplus for ground drop");
            Check((int)Call(dismantle,"Store",refundInventory,refund)==5,"full inventory retains entire boat refund for ground drop");
            refundInventory.RemoveAll();
            Check((int)Call(dismantle,"Store",refundInventory,refund)==0&&refundInventory.CountItems("StorageWood")==5,"boat refund fully enters available inventory");
            var boatPiece=New<Piece>("boat-refund");var boatZdo=Data(boatPiece);boatPiece.m_nview=boatPiece.GetComponent<ZNetView>();
            boatZdo.Set(ZDOVars.s_creator,111L);boatPiece.m_creator=111L;
            boatPiece.m_resources=new[]{new Piece.Requirement{m_resItem=material,m_amount=205,m_recover=true},new Piece.Requirement{m_resItem=material,m_amount=7,m_recover=false}};
            var refunds=(List<ItemDrop.ItemData>)Call(dismantle,"Resources",boatPiece);
            Check(refunds.Count==3&&refunds.Sum(i=>i.m_stack)==205&&refunds.All(i=>i.m_stack<=100),"boat refund preserves full recoverable recipe and native stack sizes");
                        var boat=boatPiece.gameObject.AddComponent<Ship>();boat.m_nview=boatPiece.m_nview;
            boatPiece.gameObject.AddComponent<WearNTear>();boatPiece.gameObject.AddComponent<BoxCollider>();
            int boatToken="overhaul_ship_remove_token".GetStableHashCode();
            Call(dismantle,"Request",boat,ZNet.GetUID(),actor.m_uid,"ocean");
            Check(boatZdo.GetString(boatToken,"")=="","boat dismantle rejected without workbench even for authenticated owner");
            var workbench=New<CraftingStation>("boat-workbench");workbench.m_name="$piece_workbench";workbench.m_rangeBuild=20;workbench.m_buildRange=20;
            workbench.transform.position=actor.GetPosition()+Vector3.right*30;CraftingStation.m_allStations.Add(workbench);
            Call(dismantle,"Request",boat,ZNet.GetUID(),actor.m_uid,"far-bench");
            Check(boatZdo.GetString(boatToken,"")=="","boat dismantle rejected outside workbench build radius");
            workbench.transform.position=actor.GetPosition();
            Check((bool)Call(dismantle,"HasWorkbench",actor.GetPosition()),"nearby workbench permits boat dismantling");
            Call(dismantle,"Request",boat,778L,actor.m_uid,"spoof");
            Check(boatZdo.GetString(boatToken,"")=="","boat dismantle rejects spoofed player ownership");
            Call(dismantle,"Request",boat,ZNet.GetUID(),actor.m_uid,"first");
            Check(boatZdo.GetString(boatToken,"")=="first","boat owner authorizes authenticated nearby player");
            Call(dismantle,"Request",boat,ZNet.GetUID(),actor.m_uid,"second");
            Check(boatZdo.GetString(boatToken,"")=="first","second dismantle request cannot replace active reservation");
            boatZdo.Set("overhaul_ship_remove_until".GetStableHashCode(),0L);boatZdo.Set(ZDOVars.s_health,0f);
            Call(dismantle,"Request",boat,ZNet.GetUID(),actor.m_uid,"dead");
            Check(boatZdo.GetString(boatToken,"")=="first","destroyed boat cannot be authorized again");
            CraftingStation.m_allStations.Remove(workbench);
            Check(!(bool)Call(dismantle,"HasWorkbench",actor.GetPosition()),"removed workbench invalidates final dismantle guard");
                        var instant=plugin.GetType("Overhaul.Storage.InstantLoot",true);
            var mob=New<Character>("loot-mob");
            var corpse=New<Ragdoll>("loot-corpse");
            mob.m_deathAnimation=true;Call(instant,"ShortenDeath",mob);
            Check(!mob.m_deathAnimation,"creature death no longer waits for a long animation before creating its corpse");
            corpse.m_ttl=10;Call(instant,"ShortenCorpse",mob,corpse);
            Check(corpse.m_ttl==.5f&&corpse.IsInvoking("DestroyNow"),"corpse lifetime capped at half a second and native destruction rescheduled");
            corpse.CancelInvoke();corpse.m_ttl=.2f;Call(instant,"ShortenCorpse",mob,corpse);
            Check(corpse.m_ttl==.5f,"corpse lifetime consistently set to half a second");
            corpse.CancelInvoke();corpse.m_ttl=10;player.m_deathAnimation=true;
            Call(instant,"ShortenDeath",player);Call(instant,"ShortenCorpse",player,corpse);
            Check(player.m_deathAnimation&&corpse.m_ttl==10&&!corpse.IsInvoking("DestroyNow"),"player death animation and corpse lifetime untouched");
                        var humanoid=New<Humanoid>("corpse-humanoid");corpse.m_ttl=10;
            typeof(Humanoid).GetMethod("OnRagdollCreated",All).Invoke(humanoid,new object[]{corpse});
            Check(corpse.m_ttl==.5f,"humanoid override also receives half-second corpse lifetime");corpse.CancelInvoke();
            if(Environment.GetCommandLineArgs().Contains("-resourceRangeOnly"))
            {
                UnityEngine.Object.DestroyImmediate(recipe);
                System.IO.File.WriteAllText("../../../Tools/OverhaulV2Work/storage-results.txt","PASS "+checks+" storage and resource range checks; package "+plugin.GetName().Version+"\n");
                return;
            }
            BossLootChecks(plugin);
            BurningFoodChecks(plugin,player);
            QuickFillChecks(plugin,player,harmony,ha,patchMethod,prefix);
            NutritionChecks(plugin,player);
            CircletFogChecks(plugin,player);
            ArmorStandHammerChecks(plugin);
            TrainingDummyChecks(plugin,harmony,ha,patchMethod);
            UnityEngine.Object.DestroyImmediate(recipe);
            System.IO.File.WriteAllText("../../../Tools/OverhaulV2Work/storage-results.txt","PASS "+checks+" storage checks\n");
        }
        finally
        {
            Field(storage,"Reading",0);Field(storage,"Executing",false);Field(storage,"SuppressReading",false);Field(storage,"Transaction",null);
            Player.m_localPlayer=original;Game.m_worldLevel=world;ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
            typeof(ObjectDB).GetField("m_instance",All).SetValue(null,oldDb);
            typeof(InventoryGui).GetField("m_instance",All).SetValue(null,oldGui);Game.instance.m_playerProfile=oldProfile;
            typeof(ZoneSystem).GetField("s_instance",All).SetValue(null,oldZone);
            Game.isModded=oldModded;Achievements.m_cheatCheckFrame=-1;logEvent.RemoveEventHandler(log,sink);
            foreach(var go in roots)if(go)UnityEngine.Object.DestroyImmediate(go);roots.Clear();
        }
    }
    static void CircletFogChecks(Assembly plugin,Player player)
    {
        var fog=plugin.GetType("Overhaul.Storage.CircletFog");var oldHelmet=player.m_helmetItem;var oldUtility=player.m_utilityItem;float oldDensity=RenderSettings.fogDensity;
        var fogObject=new GameObject("storage-test-circlet-fog");fogObject.SetActive(false);roots.Add(fogObject);
        var ps=fogObject.AddComponent<ParticleSystem>();var renderer=ps.GetComponent<ParticleSystemRenderer>();var material=new Material(Shader.Find("UI/Default")){name="heavymist_mistlands"};renderer.sharedMaterial=material;
        var block=new MaterialPropertyBlock();int color=Shader.PropertyToID("_Color");var original=new Color(.6f,.7f,.8f,.8f);block.SetColor(color,original);block.SetFloat("test_preserved",7);renderer.SetPropertyBlock(block);
        try {
            player.m_helmetItem=null;player.m_utilityItem=null;Check(!(bool)Call(fog,"Equipped"),"circlet bonus requires actual equipped item, not carried or cosmetic item");
            player.m_utilityItem=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_helmet_dverger",m_itemType=ItemDrop.ItemData.ItemType.Utility}};
            Check((bool)Call(fog,"Equipped"),"circlet equipped as utility enables clarity with helmet slot empty");
            var multi=plugin.GetType("EquipmentAndQuickSlots.src.MultiUtility.MultiUtility");
            var ownerField=multi.GetField("owner",All);var oldOwner=ownerField.GetValue(null);
            var extras=(ItemDrop.ItemData[])multi.GetField("extras",All).GetValue(null);var oldExtras=extras.ToArray();
            var countField=plugin.GetType("EquipmentAndQuickSlots.ValConfig").GetField("UtilitySlotCount",All);var oldCount=countField.GetValue(null);
            var bep=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="BepInEx");var cfType=bep.GetType("BepInEx.Configuration.ConfigFile");
            var cf=Activator.CreateInstance(cfType,new object[]{System.IO.Path.GetFullPath("../../../Tools/AugaWork/circlet-slot-test.cfg"),false,null});
            cfType.GetProperty("SaveOnConfigSet").SetValue(cf,false);
            var bind=cfType.GetMethods().First(m=>m.Name=="Bind"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==4&&m.GetParameters()[0].ParameterType==typeof(string)&&m.GetParameters()[3].ParameterType==typeof(string));
            countField.SetValue(null,bind.MakeGenericMethod(typeof(int)).Invoke(cf,new object[]{"Test","Slots",3,""}));
            var circlet=player.m_utilityItem;
            try {
                player.m_utilityItem=null;ownerField.SetValue(null,player);
                for(int slot=0;slot<2;slot++) {Array.Clear(extras,0,extras.Length);extras[slot]=circlet;Check((bool)Call(fog,"Equipped"),"circlet activates from extra accessory slot "+slot);}
                Array.Clear(extras,0,extras.Length);Check(!(bool)Call(fog,"Equipped"),"removing extra accessory disables clarity");
            } finally {ownerField.SetValue(null,oldOwner);Array.Copy(oldExtras,extras,extras.Length);countField.SetValue(null,oldCount);player.m_utilityItem=circlet;}
            RenderSettings.fogDensity=.02f;Call(fog,"ApplyFog",true);Check(Mathf.Approximately(RenderSettings.fogDensity,.01f),"circlet reduces atmospheric fog density by 50 percent");
            for(int i=0;i<10;i++)Call(fog,"ApplyFog",false);
            Check(Mathf.Approximately(RenderSettings.fogDensity,.01f),"fog reduction never compounds over frames");
            RenderSettings.fogDensity=.04f;Call(fog,"ApplyFog",true);Check(Mathf.Approximately(RenderSettings.fogDensity,.02f),"changing weather uses new native fog baseline");
            Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);
            Check(Mathf.Approximately(block.GetColor(color).a,.4f)&&block.GetColor(color).r==original.r&&block.GetFloat("test_preserved")==7,"mist opacity reduced by 50 percent while preserving tint and other renderer properties");
            Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);Check(Mathf.Approximately(block.GetColor(color).a,.4f),"mist reduction never compounds on rescans");
            Check(material.GetColor(color).a==1,"shared native material remains unchanged");
            var spirit=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$overhaul_spirit_circlet",m_itemType=ItemDrop.ItemData.ItemType.Utility}};
            player.m_utilityItem=spirit;Call(fog,"ApplyFog",false);Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);
            Check(RenderSettings.fogDensity==0&&block.GetColor(color).a==0,"spirit circlet removes all targeted fog and particle opacity");
            player.m_helmetItem=circlet;Check((float)Call(fog,"GetOpacity")==0,"spirit takes priority when both circlets are equipped");player.m_helmetItem=null;
            player.m_utilityItem=circlet;Call(fog,"ApplyFog",false);Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);
            Check(Mathf.Approximately(RenderSettings.fogDensity,.02f)&&Mathf.Approximately(block.GetColor(color).a,.4f),"switching spirit to dverger restores 50 percent without accumulating reductions");
            player.m_helmetItem=null;player.m_utilityItem=null;Call(fog,"Tick");renderer.GetPropertyBlock(block);
            Check(block.GetColor(color)==original&&Mathf.Approximately(RenderSettings.fogDensity,.04f),"unequipping restores particles and current weather immediately");
            player.m_utilityItem=circlet;renderer.SetPropertyBlock(null);Call(fog,"ApplyRenderer",renderer);Call(fog,"Clear");renderer.GetPropertyBlock(block);Check(block.isEmpty,"renderer without original overrides returns to empty property block");
            foreach(var name in new[]{"fog","distant_fog","forest_groundmist","swamp_mist","rain_fogclouds","heavymist_mistlands_small_lux"}) {
                material.name=name;Check((bool)Call(fog,"IsFog",material),"native atmospheric material covered: "+name);
            }
            material.name="demister";Check(!(bool)Call(fog,"IsFog",material),"wisplight effect is not modified");
            material.name="build_fog_lowres";Check((bool)Call(fog,"IsFog",material),"construction dust is reduced");
            foreach(var name in new[]{"smoke","slowwispysmoke","dust_footstep","dust_particle","winddust"}) {
                material.name=name;Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);
                Check(Mathf.Approximately(block.GetColor(color).a,.5f),"smoke/dust opacity reduced by 50 percent: "+name);Call(fog,"Clear");
            }
            material.name="heavymist_mistlands";Call(fog,"ApplyRenderer",renderer);material.name="sparks";var smoke=new Material(material){name="sparks"};renderer.sharedMaterial=smoke;Call(fog,"ApplyRenderer",renderer);renderer.GetPropertyBlock(block);
            Check(block.isEmpty,"renderer changing away from fog loses obsolete alpha override");UnityEngine.Object.DestroyImmediate(smoke);
            player.m_utilityItem=null;player.m_helmetItem=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_helmet_iron"}};Check(!(bool)Call(fog,"Equipped"),"other helmets grant no clarity");
        } finally {Call(fog,"Clear");player.m_helmetItem=oldHelmet;player.m_utilityItem=oldUtility;RenderSettings.fogDensity=oldDensity;UnityEngine.Object.DestroyImmediate(material);}
    }
    static void NutritionChecks(Assembly plugin,Player player)
    {
        var nutrition=plugin.GetType("Overhaul.Leveling.NutritionDuration");
        var stateType=plugin.GetType("Overhaul.Leveling.OverhaulCharacter");var state=Call(stateType,"Get",player);
        var data=stateType.GetField("Data").GetValue(state);var ranks=(Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data);
        var oldRanks=new Dictionary<string,int>(ranks);var oldReady=stateType.GetField("Ready").GetValue(state);
        var rules=plugin.GetType("Overhaul.Leveling.LevelingConfig").GetField("Current").GetValue(null);
        var stats=(System.Collections.IDictionary)rules.GetType().GetField("Stats").GetValue(rules);var rule=stats["food"];var oldPerPoint=rule.GetType().GetField("PerPoint").GetValue(rule);
        rule.GetType().GetField("PerPoint").SetValue(rule,.03d);stateType.GetField("Ready").SetValue(state,true);ranks["food"]=10;
        var oldFoods=player.m_foods.ToArray();var oldProfile=Game.instance.m_playerProfile;var oldSe=player.m_seman;var oldRate=Game.m_foodRate;
        player.m_foods.Clear();player.m_seman=new SEMan(player,player.m_nview);Game.m_foodRate=1;
        Game.instance.m_playerProfile=new PlayerProfile(null,FileHelpers.FileSource.Local);
        var prefab=New<ItemDrop>("nutrition-food");prefab.m_itemData.m_shared=new ItemDrop.ItemData.SharedData {m_name="$nutrition_test_food",m_description="Durée de base : 20 min. Nutrition : +30 %.",m_itemType=ItemDrop.ItemData.ItemType.Consumable,m_food=20,m_foodStamina=30,m_foodBurnTime=1200,m_foodRegen=1,m_icons=new Sprite[1],m_attack=new Attack(),m_secondaryAttack=new Attack()};
        prefab.m_itemData.m_dropPrefab=prefab.gameObject;var item=prefab.m_itemData;
        void Sync()=>Call(nutrition,"Sync",player,1f);
        void Store()=>stateType.GetMethod("Store",All).Invoke(state,null);
        string key="Overhaul.NutritionDuration";player.m_customData.TryGetValue(key,out var oldSave);player.m_customData.Remove(key);
        try {
            Check(Mathf.Approximately((float)Call(nutrition,"Preview",item),1560),"nutrition preview adds 30 percent duration");
            Check(player.EatFood(item),"native EatFood accepts nutrition meal");
            var meal=player.m_foods.Single();
            Check(Mathf.Approximately(meal.m_item.m_shared.m_foodBurnTime,1560)&&meal.m_time>1558&&meal.m_time<=1560,"native meal receives actual extended seconds");
            Check(item.m_shared.m_foodBurnTime==1200&&!ReferenceEquals(item.m_shared,meal.m_item.m_shared),"nutrition does not mutate shared inventory or prefab data");
            float before=meal.m_time;player.m_foodUpdateTimer=0;player.UpdateFood(1,false);
            Check(Mathf.Approximately(meal.m_time,before-1),"food countdown loses exactly one second per second at normal world rate");
            meal.m_time=800;Check(!meal.CanEatAgain(),"refill threshold uses half of extended meal duration");
            meal.m_time=779;Check(meal.CanEatAgain(),"meal becomes refillable below extended half duration");
            Check(player.EatFood(item)&&meal.m_time>1558&&meal.m_time<=1560,"native refill restores extended duration without stacking bonus");
            meal.m_time=700;ranks["food"]=20;Store();
            Check(Mathf.Approximately(meal.m_time,1060)&&Mathf.Approximately(meal.m_item.m_shared.m_foodBurnTime,1920),"allocating nutrition adds full duration difference to consumed meal immediately");
            ranks["food"]=0;Store();
            Check(Mathf.Approximately(meal.m_time,340)&&Mathf.Approximately(meal.m_item.m_shared.m_foodBurnTime,1200),"stat reset removes full bonus duration while retaining elapsed time");
            Check(meal.m_health==20&&meal.m_stamina==30,"food benefits remain complete after nutrition stat reset");
            ranks["food"]=10;Store();meal.m_time=100;ranks["food"]=0;Store();
            Check(player.m_foods.Count==0,"negative remaining duration ends meal immediately on reset");
            ranks["food"]=10;Check(player.EatFood(item),"can eat again after nutrition reset expires meal");meal=player.m_foods.Single();meal.m_time=360;ranks["food"]=0;Store();
            Check(player.m_foods.Count==0,"exactly zero remaining duration also ends meal immediately");
            ranks["food"]=10;player.m_foods.Add(new Player.Food{m_name=prefab.name,m_item=item,m_time=600});Call(nutrition,"Load",player);meal=player.m_foods.Single();
            Check(Mathf.Approximately(meal.m_time,780),"legacy slow-timer save migrates once preserving real remaining duration");
            Call(nutrition,"Save",player);float savedTime=meal.m_time;string savedScale=player.m_customData[key];
            var packet=new ZPackage();packet.Write(meal.m_name);packet.Write(meal.m_time);packet.Write(savedScale);packet.SetPos(0);
            player.m_foods.Clear();player.m_foods.Add(new Player.Food{m_name=packet.ReadString(),m_time=packet.ReadSingle(),m_item=item});player.m_customData[key]=packet.ReadString();
            Call(nutrition,"Load",player);Call(nutrition,"Load",player);meal=player.m_foods.Single();
            Check(Mathf.Approximately(meal.m_time,savedTime)&&Mathf.Approximately(meal.m_item.m_shared.m_foodBurnTime,1560),"serialized actual seconds reload without bonus duplication");
            stateType.GetField("Ready").SetValue(state,false);Sync();
            Check(Mathf.Approximately(meal.m_time,savedTime),"waiting for server progression does not remove saved nutrition duration");
            stateType.GetField("Ready").SetValue(state,true);ranks["food"]=0;Store();
            Check(Mathf.Approximately(meal.m_time,savedTime-360),"server-confirmed reset adjusts previously loaded meal exactly once");
            Check(AugaUnity.PlayerPanelFoodController.FormatFoodTime(4800)=="1:20:00","long food durations display hours rather than wrapping minutes");
            ranks["food"]=10;Store();NutritionTooltipCheck(plugin,player,item);
            player.m_foods.Clear();ranks["food"]=0;stateType.GetField("Ready").SetValue(state,false);
            player.m_shownTutorials.Add("eitr");
            var full=new Player.Food {m_item=new ItemDrop.ItemData {m_shared=new ItemDrop.ItemData.SharedData {m_food=100,m_foodStamina=50,m_foodEitr=25,m_foodBurnTime=1200}},m_time=1200};
            player.m_foods.Add(full);
            foreach(float remaining in new[]{1200f,600f,1.5f}) {
                full.m_time=remaining;full.m_health=1;full.m_stamina=1;full.m_eitr=1;player.m_foodUpdateTimer=0;player.UpdateFood(1,false);
                Check(full.m_health==100&&full.m_stamina==50&&full.m_eitr==25,"native food update retains full benefits without ready leveling at remaining "+remaining);
                player.GetTotalFoodValue(out var hp,out var stamina,out var eitr);
                Check(hp==player.m_baseHP+100&&stamina==player.m_baseStamina+50&&eitr==25,"native totals include full food benefits at remaining "+remaining);
            }
            player.m_foodUpdateTimer=0;player.UpdateFood(1,false);player.GetTotalFoodValue(out var endHp,out var endStamina,out var endEitr);
            Check(player.m_foods.Count==0&&endHp==player.m_baseHP&&endStamina==player.m_baseStamina&&endEitr==0,"all food benefits end on native meal expiration");
            full.m_time=100;full.m_health=1;full.m_stamina=1;full.m_eitr=1;player.m_foods.Add(full);
            Call(plugin.GetType("Overhaul.Storage.FullFoodLoadPatch"),"Postfix",player);
            Check(full.m_health==100&&full.m_stamina==50&&full.m_eitr==25,"loaded active meal regains full benefits immediately");
        } finally {
            ranks.Clear();foreach(var pair in oldRanks)ranks[pair.Key]=pair.Value;stateType.GetField("Ready").SetValue(state,oldReady);rule.GetType().GetField("PerPoint").SetValue(rule,oldPerPoint);
            player.m_foods.Clear();player.m_foods.AddRange(oldFoods);player.m_seman=oldSe;Game.instance.m_playerProfile=oldProfile;Game.m_foodRate=oldRate;
            if(oldSave==null)player.m_customData.Remove(key);else player.m_customData[key]=oldSave;
        }
    }
    static void NutritionTooltipCheck(Assembly plugin,Player player,ItemDrop.ItemData item)
    {
        foreach(var field in typeof(Player).GetFields(All).Where(f=>f.FieldType==typeof(float[])&&f.Name.Contains("ModifierValues")))
            if(field.GetValue(player)==null)field.SetValue(player,new float[0]);
        var preview=plugin.GetType("Overhaul.Leveling.NutritionDuration").GetMethod("Preview",All);
        AugaUnity.ComplexTooltip.FoodDuration=(Func<ItemDrop.ItemData,float>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData,float>),preview);
        plugin.GetType("Auga.Auga").GetMethod("LoadTranslations",All).Invoke(null,new object[]{Localization.instance,"French"});
        Localization.instance.AddWord("nutrition_test_food","Repas de test");Localization.instance.AddWord("item_food_duration","Durée");
        Localization.instance.AddWord("item_food_health","Santé");Localization.instance.AddWord("item_food_stamina","Endurance");Localization.instance.AddWord("item_food_regen","Régénération");
        Localization.instance.AddWord("item_weight","Poids");
        AssetBundle bundle;using(var stream=plugin.GetManifestResourceStream("Overhaul.augaassets"))using(var bytes=new System.IO.MemoryStream()){stream.CopyTo(bytes);bundle=AssetBundle.LoadFromMemory(bytes.ToArray());}
        var go=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"));var tip=go.GetComponent<AugaUnity.ComplexTooltip>();
        try {
            go.SetActive(true);tip.SetItem(item);string Text()=>string.Join("\n",go.GetComponentsInChildren<TMPro.TMP_Text>(true).Where(t=>t.gameObject.activeInHierarchy).Select(t=>t.text));
            Check(Text().Contains("26:00"),"actual Auga food tooltip displays nutrition-adjusted total");
            var state=Call(plugin.GetType("Overhaul.Leveling.OverhaulCharacter"),"Get",player);var data=state.GetType().GetField("Data").GetValue(state);var ranks=(Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data);
            ranks["food"]=0;var oldTooltip=UITooltip.m_tooltip;UITooltip.m_tooltip=go;
            state.GetType().GetMethod("Store",All).Invoke(state,null);UITooltip.m_tooltip=oldTooltip;
            Check(Text().Contains("20:00")&&!Text().Contains("26:00"),"already visible Auga tooltip refreshes immediately after resetting stats");
            ranks["food"]=10;Call(plugin.GetType("Overhaul.Leveling.NutritionDuration"),"Sync",player,1f);tip.RefreshFoodDuration();
            var screenshot="../../../Tools/AugaWork/oven-food-tooltip.png";
            var previous=System.IO.File.Exists(screenshot)?System.IO.File.ReadAllBytes(screenshot):null;
            typeof(OvenFoodTooltipCheck).GetMethod("Render",All).Invoke(null,new object[]{go});go=null;
            System.IO.File.Copy(screenshot,"../../../Tools/AugaWork/nutrition-duration-tooltip.png",true);
            if(previous!=null)System.IO.File.WriteAllBytes(screenshot,previous);
        } finally {if(go)UnityEngine.Object.DestroyImmediate(go);bundle.Unload(true);}
    }
    static void ArmorStandHammerChecks(Assembly plugin)
    {
        var stand=New<ArmorStand>("hammer-stand");
        var button=New<Switch>("hammer-stand-switch");
        button.m_onUse=(caller,user,item)=>stand.UseItem(caller,user,item);
        var hammer=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_hammer"}};
        Check(!button.UseItem(null,hammer)&&stand.m_queuedItem==null,"native stand switch returns unhandled for hammer, allowing equipment fallback");
        var prefix=plugin.GetType("Overhaul.Storage.ArmorStandHammer").GetMethod("Prefix",All);
        var weapon=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_name="$item_sledge_iron"}};
        Check((bool)prefix.Invoke(null,new object[]{weapon,false}),"weapon hammers retain native stand attachment");
        Check((bool)prefix.Invoke(null,new object[]{null,false}),"empty interaction retains native stand retrieval");
    }
    static void TrainingMeterChecks(Assembly plugin,Character dummy,object harmony,Assembly ha,MethodInfo patchMethod)
    {
        var type=plugin.GetType("Overhaul.Storage.TrainingDamageSession");var session=Activator.CreateInstance(type,true);
        object Run(string method,params object[] args)=>type.GetMethod(method,All).Invoke(session,args);
        double Number(string name)=>Convert.ToDouble(type.GetField(name,All)!=null?type.GetField(name,All).GetValue(session):type.GetProperty(name,All).GetValue(session));
        Run("Add",100d,10d);Check(Number("Count")==1&&Number("Duration")==0,"meter first impact has no fabricated duration");
        Run("Add",50d,12d);Check(Number("Dps")==75&&Number("Peak")==100,"meter DPS uses total damage divided by first-to-last impact interval");
        Run("Tick",20d);Check(Number("Dps")==75,"meter does not dilute DPS during idle time");
        Run("Tick",21.999d);Check(Number("Count")==2,"meter retains data before ten idle seconds");
        Run("Tick",22d);Check(Number("Count")==0,"meter clears at exactly ten idle seconds");
        Run("Add",900d,12d);Check(Number("Count")==0,"meter ignores delayed pre-reset packets");
        Run("Add",30d,23d);Run("Add",40d,25d);Run("Reset",26d);Run("Add",500d,25d);Run("Add",20d,27d);Run("Add",10d,28d);
        Check(Number("Total")==30&&Number("Peak")==20&&Number("Dps")==30,"manual reset excludes all previous damage");
        Run("Add",10d,27.5d);Check(Number("Total")==40&&Number("Duration")==1,"out-of-order delivery preserves first and last hit timestamps");
        Run("Add",float.NaN,29d);Run("Add",double.PositiveInfinity,29d);Check(Number("Count")==3,"invalid damage values are rejected");
        var hud=plugin.GetType("Overhaul.Storage.TrainingDamageHud");
        Check((bool)Call(hud,"InRange",Vector3.zero,new Vector3(20,0,0))&&!(bool)Call(hud,"InRange",Vector3.zero,new Vector3(20.01f,0,0)),"meter range includes exactly 20 metres and excludes beyond");

        var meter=plugin.GetType("Overhaul.Storage.TrainingDamageMeter");
        var send=meter.GetMethod("Send",All);
        var hook=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod("MeterSend",All)});
        patchMethod.Invoke(harmony,new object[]{send,hook,null,null,null});
        var local=Player.m_localPlayer;
        ZNetScene.instance.m_instances[local.m_nview.GetZDO()]=local.m_nview;
        var other=New<Player>("meter-other-player");Data(other);other.m_nview=other.GetComponent<ZNetView>();
        ZNetScene.instance.m_instances[other.m_nview.GetZDO()]=other.m_nview;
        var a=local.GetZDOID();var b=other.GetZDOID();
        float Amount(ZDOID id)=>meterAmounts.TryGetValue(id,out var value)?value:0;
        var fire=ScriptableObject.CreateInstance<SE_Burning>();fire.m_character=dummy;fire.m_ttl=10;fire.m_damageInterval=1;
        var poison=ScriptableObject.CreateInstance<SE_Poison>();poison.m_character=dummy;
        try {
            meterAmounts.Clear();var direct=new HitData(100);direct.SetAttacker(local);dummy.ApplyDamage(direct,false,false);
            Check(Mathf.Approximately(Amount(a),direct.GetTotalDamage())&&Amount(b)==0,"native applied direct damage reports only its player author");
            var arrow=new HitData();arrow.m_damage.m_pierce=80;arrow.SetAttacker(other);dummy.ApplyDamage(arrow,false,false);
            Check(Mathf.Approximately(Amount(b),arrow.GetTotalDamage()),"projectile attacker identity remains separate from local player");
            float totalBefore=Amount(a)+Amount(b);dummy.ApplyDamage(new HitData(90),false,false);
            Check(Mathf.Approximately(Amount(a)+Amount(b),totalBefore),"unattributed environmental damage is excluded");
            var rejected=new HitData(0);rejected.SetAttacker(local);dummy.ApplyDamage(rejected,false,false);
            Check(Mathf.Approximately(Amount(a)+Amount(b),totalBefore),"native rejected zero damage is excluded");
            var scaleMethod=typeof(Game).GetMethod("GetDifficultyDamageScaleEnemy");var scaleHook=typeof(OverhaulStorageChecks).GetMethod("MeterDifficultyScale",All);
            patchMethod.Invoke(harmony,new object[]{scaleMethod,Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{scaleHook}),null,null,null});
            try {
                meterAmounts.Clear();dummyShownDamage=0;var scaled=new HitData();scaled.m_damage.m_blunt=70;scaled.SetAttacker(local);dummy.ApplyDamage(scaled,true,false);
                Check(Mathf.Approximately(scaled.GetTotalDamage(),54)&&dummyShownDamage==70&&Amount(a)==70,"meter records displayed70 even when multiplayer scaling applies54: actual="+scaled.GetTotalDamage()+" shown="+dummyShownDamage+" meter="+Amount(a));
                meterAmounts.Clear();var marked=new HitData();marked.m_damage.m_blunt=70;marked.SetAttacker(local);Call(plugin.GetType("Overhaul.Leveling.CriticalHits"),"Mark",marked);dummy.ApplyDamage(marked,true,false);
                Check(dummyShownDamage==70&&Amount(a)==70,"critical floating text and training meter use identical damage");
            }finally{harmony.GetType().GetMethod("Unpatch",new[]{typeof(MethodBase),typeof(MethodInfo)}).Invoke(harmony,new object[]{scaleMethod,scaleHook});}
            Field(meter,"SourceTarget",dummy);Field(meter,"SourcePlayer",a);fire.AddFireDamage(100);
            Field(meter,"SourcePlayer",b);fire.AddFireDamage(300);
            Field(meter,"SourcePlayer",ZDOID.None);fire.AddFireDamage(100);
            Field(meter,"SourceTarget",null);meterAmounts.Clear();fire.UpdateStatusEffect(1);
            Check(Amount(a)>0&&Mathf.Approximately(Amount(b),Amount(a)*3),"mixed native fire tick splits damage by contributor, excluding monster share");
            Check(Mathf.Approximately(fire.m_fireDamageLeft,450),"meter preserves native fire pool and tick damage");
            Field(meter,"SourceTarget",dummy);Field(meter,"SourcePlayer",a);fire.AddFireDamage(50);
            Field(meter,"SourceTarget",null);meterAmounts.Clear();fire.UpdateStatusEffect(1);
            Check(Amount(a)>0&&Mathf.Abs(Amount(b)/Amount(a)-270f/140f)<.001f,"stacked fire attribution uses remaining damage after earlier ticks");
            Field(meter,"SourceTarget",dummy);Field(meter,"SourcePlayer",a);poison.AddDamage(100);
            Field(meter,"SourcePlayer",b);poison.AddDamage(50);
            Field(meter,"SourceTarget",null);meterAmounts.Clear();poison.UpdateStatusEffect(1);
            Check(Amount(a)>0&&Amount(b)==0,"weaker poison cannot steal attribution of an existing stronger poison");
            Field(meter,"SourceTarget",dummy);Field(meter,"SourcePlayer",b);poison.AddDamage(200);
            Field(meter,"SourceTarget",null);meterAmounts.Clear();poison.UpdateStatusEffect(1);
            Check(Amount(b)>0&&Amount(a)==0,"stronger poison replacement belongs only to its author");
            Check(meter.GetField("Active",All).GetValue(null)==null&&meter.GetField("TickEffect",All).GetValue(null)==null,"damage and periodic attribution scopes restore after native processing");
        } finally {
            Field(meter,"SourceTarget",null);Field(meter,"SourcePlayer",ZDOID.None);
            harmony.GetType().GetMethod("Unpatch",new[]{typeof(MethodBase),typeof(MethodInfo)}).Invoke(harmony,new object[]{send,typeof(OverhaulStorageChecks).GetMethod("MeterSend",All)});
            UnityEngine.Object.DestroyImmediate(fire);UnityEngine.Object.DestroyImmediate(poison);
        }
        var active=meter.GetField("Session",All).GetValue(null);
        type.GetMethod("Reset",All).Invoke(active,new object[]{double.NegativeInfinity});type.GetField("Cutoff",All).SetValue(active,double.NegativeInfinity);
        ZPackage Packet(){var packet=new ZPackage();packet.Write(dummy.GetZDOID());packet.Write(123f);packet.Write(ZNet.instance.GetTimeSeconds());packet.SetPos(0);return packet;}
        Call(meter,"Receive",other,ZNet.GetUID(),Packet());
        Check((int)type.GetField("Count",All).GetValue(active)==0,"remote player's event cannot update local meter");
        Call(meter,"Receive",local,ZNet.GetUID()+1,Packet());
        Check((int)type.GetField("Count",All).GetValue(active)==0,"meter accepts reports only from dummy network authority");
        Call(meter,"Receive",local,ZNet.GetUID(),Packet());
        Check((int)type.GetField("Count",All).GetValue(active)==1&&(double)type.GetField("Total",All).GetValue(active)==123,"serialized authoritative network event reaches local player's session");
        Call(plugin.GetType("Overhaul.Storage.TrainingDamageReceiver"),"Postfix",local);
        Call(meter,"Send",dummy,a,77f);
        Check((int)type.GetField("Count",All).GetValue(active)==2&&(double)type.GetField("Total",All).GetValue(active)==200,"native routed RPC delivers authoritative dummy damage to player owner");
        TrainingMeterUiChecks(plugin,dummy,local);
        type.GetMethod("Reset",All).Invoke(active,new object[]{double.NegativeInfinity});type.GetField("Cutoff",All).SetValue(active,double.NegativeInfinity);
        ZNetScene.instance.m_instances.Remove(other.m_nview.GetZDO());ZNetScene.instance.m_instances.Remove(local.m_nview.GetZDO());
        meterAmounts.Clear();
    }
    static void TrainingMeterUiChecks(Assembly plugin,Character dummy,Player local)
    {
        AssetBundle bundle;
        using(var stream=plugin.GetManifestResourceStream("Overhaul.augaassets"))using(var bytes=new System.IO.MemoryStream()) {stream.CopyTo(bytes);bundle=AssetBundle.LoadFromMemory(bytes.ToArray());}
        var prefab=bundle.LoadAsset<GameObject>("AugaTrainingMeter");Check(prefab,"training meter prefab is embedded in shipped DLL");
        var assets=plugin.GetType("Auga.Auga").GetField("Assets",All).GetValue(null);assets.GetType().GetField("TrainingMeter").SetValue(assets,prefab);
        plugin.GetType("Auga.Auga").GetMethod("LoadTranslations",All).Invoke(null,new object[]{Localization.instance,"French"});
        var host=new GameObject("meter-ui-test",typeof(RectTransform),typeof(Canvas));
        var root=new GameObject("hudroot",typeof(RectTransform));root.transform.SetParent(host.transform,false);
        var rr=(RectTransform)root.transform;rr.anchorMin=Vector2.zero;rr.anchorMax=Vector2.one;rr.offsetMin=rr.offsetMax=Vector2.zero;
        var cameraGo=new GameObject("meter-camera",typeof(Camera));var camera=cameraGo.GetComponent<Camera>();camera.cullingMask=1<<30;
        var canvas=host.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;
        var events=new GameObject("meter-events",typeof(UnityEngine.EventSystems.EventSystem));
        var controllerType=plugin.GetType("Overhaul.Storage.TrainingDamageHud");var controller=host.AddComponent(controllerType);
        var meter=plugin.GetType("Overhaul.Storage.TrainingDamageMeter");var session=meter.GetField("Session",All).GetValue(null);var st=session.GetType();
        var oldDummy=dummy.transform.position;var oldPlayer=local.transform.position;
        bool added=!Character.GetAllCharacters().Contains(dummy);if(added)Character.GetAllCharacters().Add(dummy);
        try {
            local.transform.position=Vector3.zero;dummy.transform.position=new Vector3(20,0,0);
            controllerType.GetMethod("Update",All).Invoke(controller,null);
            var panel=(GameObject)controllerType.GetField("panel",All).GetValue(controller);
            Check(panel&&panel.activeSelf,"runtime HUD creates visible panel at 20 metres");
            var pr=(RectTransform)panel.transform;
            Check(pr.anchorMin==new Vector2(0,.5f)&&pr.anchorMax==pr.anchorMin&&pr.pivot==pr.anchorMin,"meter anchors at middle of left edge");
            Check(panel.transform.Find("Frame").GetComponent<CanvasGroup>().alpha<1,"frame background remains translucent");
            var dl=panel.transform.Find("DpsLabel").GetComponent<TMPro.TMP_Text>();var ml=panel.transform.Find("PeakLabel").GetComponent<TMPro.TMP_Text>();
            Check(dl.fontSize==ml.fontSize&&dl.color==Color.white&&ml.color==Color.white,"DPS and max damage labels have equal white type");
            Check(ml.text=="Dégât Max","runtime uses requested French max damage label");
            var button=panel.transform.Find("Reset").GetComponent<UnityEngine.UI.Button>();
            Check(button.transform.localScale.x==button.transform.localScale.y,"Auga reset button uses proportional scale");
            foreach(var size in new[]{new Vector2Int(1920,1080),new Vector2Int(1280,720),new Vector2Int(3440,1440)}) {
                var target=new RenderTexture(size.x,size.y,24);camera.targetTexture=target;canvas.scaleFactor=size.y/1080f;
                foreach(var t in host.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
                Canvas.ForceUpdateCanvases();camera.Render();
                st.GetField("Cutoff",All).SetValue(session,double.NegativeInfinity);
                st.GetMethod("Add",All).Invoke(session,new object[]{100d,100d});st.GetMethod("Add",All).Invoke(session,new object[]{50d,102d});
                controllerType.GetMethod("Refresh",All).Invoke(controller,null);
                Check(panel.transform.Find("DpsValue").GetComponent<TMPro.TMP_Text>().text==75d.ToString("0.0"),"runtime DPS text updates at "+size);
                var pointer=new UnityEngine.EventSystems.PointerEventData(events.GetComponent<UnityEngine.EventSystems.EventSystem>()) {position=RectTransformUtility.WorldToScreenPoint(camera,button.transform.TransformPoint(((RectTransform)button.transform).rect.center)),button=UnityEngine.EventSystems.PointerEventData.InputButton.Left};
                var hits=new List<UnityEngine.EventSystems.RaycastResult>();host.GetComponent<UnityEngine.UI.GraphicRaycaster>().Raycast(pointer,hits);
                Check((bool)Call(controllerType,"BlocksPointer",pointer.position,false)&&!(bool)Call(controllerType,"BlocksPointer",pointer.position,true),"reset pointer blocks gameplay only with cursor released at "+size);
                Check(hits.Count>0&&UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[0].gameObject)==button.gameObject,"real raycast reaches reset button at "+size);
                UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                Check((int)st.GetField("Count",All).GetValue(session)==0&&panel.transform.Find("DpsValue").GetComponent<TMPro.TMP_Text>().text=="—","real button click clears session and display at "+size);
                camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(target);
            }
            dummy.transform.position=new Vector3(20.1f,0,0);controllerType.GetField("nextScan",All).SetValue(controller,0f);controllerType.GetMethod("Update",All).Invoke(controller,null);
            Check(!panel.activeSelf,"runtime HUD hides beyond 20 metres");
            dummy.transform.position=new Vector3(20,0,0);controllerType.GetField("nextScan",All).SetValue(controller,0f);controllerType.GetMethod("Update",All).Invoke(controller,null);
            double now=ZNet.instance.GetTimeSeconds();st.GetField("Cutoff",All).SetValue(session,double.NegativeInfinity);
            st.GetMethod("Add",All).Invoke(session,new object[]{70d,now-1});st.GetMethod("Add",All).Invoke(session,new object[]{50d,now});
            controllerType.GetMethod("Refresh",All).Invoke(controller,null);
            Check(panel.transform.Find("PeakValue").GetComponent<TMPro.TMP_Text>().text==70d.ToString("0.0"),"peak uses displayed damage before leaving range");
            dummy.transform.position=new Vector3(20.1f,0,0);controllerType.GetField("nextScan",All).SetValue(controller,0f);controllerType.GetMethod("Update",All).Invoke(controller,null);
            Check(!panel.activeSelf&&(int)st.GetField("Count",All).GetValue(session)==0&&(double)st.GetField("Total",All).GetValue(session)==0&&(double)st.GetField("Peak",All).GetValue(session)==0,"leaving range clears total peak count and DPS session");
            st.GetMethod("Add",All).Invoke(session,new object[]{999d,now});Check((int)st.GetField("Count",All).GetValue(session)==0,"delayed pre-exit impacts cannot restore old results");
            var oldPosition=dummy.m_nview.GetZDO().GetPosition();dummy.m_nview.GetZDO().SetPosition(dummy.transform.position);
            var outside=new ZPackage();outside.Write(dummy.GetZDOID());outside.Write(999f);outside.Write(now+.1);outside.SetPos(0);Call(meter,"Receive",local,dummy.m_nview.GetZDO().GetOwner(),outside);
            Check((int)st.GetField("Count",All).GetValue(session)==0,"new projectile or periodic reports received out of range are ignored");dummy.m_nview.GetZDO().SetPosition(oldPosition);
            dummy.transform.position=new Vector3(20,0,0);controllerType.GetField("nextScan",All).SetValue(controller,0f);controllerType.GetMethod("Update",All).Invoke(controller,null);
            Check(panel.activeSelf&&(int)st.GetField("Count",All).GetValue(session)==0,"returning to dummy starts with an empty session");
        } finally {
            dummy.transform.position=oldDummy;local.transform.position=oldPlayer;if(added)Character.GetAllCharacters().Remove(dummy);
            UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(events);
            assets.GetType().GetField("TrainingMeter").SetValue(assets,null);bundle.Unload(true);
        }
    }
    static bool MeterDifficultyScale(ref float __result){__result=54f/70f;return false;}
    static void TrainingDummyChecks(Assembly plugin,object harmony,Assembly ha,MethodInfo patchMethod)
    {
        CinematicsManager.s_instance=New<CinematicsManager>("dummy-cinematics");
        CinematicsManager.s_instance.m_videoPlayer=CinematicsManager.s_instance.gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
        object Hook(string name)=>Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod(name,All)});
        patchMethod.Invoke(harmony,new object[]{typeof(DamageText).GetMethod("ShowText",new[]{typeof(HitData.DamageModifier),typeof(Vector3),typeof(float),typeof(bool)}),Hook("DummyText"),null,null,null});
        var refund=Hook("DummyRefund");refund.GetType().GetField("priority").SetValue(refund,0);
        patchMethod.Invoke(harmony,new object[]{typeof(Piece).GetMethod("DropResources",All),refund,null,null,null});
        var damageText=New<DamageText>("dummy-damage-text");typeof(DamageText).GetField("m_instance",All).SetValue(null,damageText);
        var dummy=New<Character>("training-dummy");var data=Data(dummy);data.SetPrefab("piece_TrainingDummy".GetStableHashCode());dummy.m_nview=dummy.GetComponent<ZNetView>();dummy.gameObject.AddComponent<Piece>();dummy.m_health=2500;dummy.m_staggerDamageFactor=0;dummy.SetMaxHealth(3750);dummy.SetHealth(100);
        float received=0;dummy.m_onDamaged+=(amount,attacker)=>received=amount;
        foreach(var field in typeof(HitData.DamageTypes).GetFields().Where(f=>f.FieldType==typeof(float))) {
            object damages=new HitData.DamageTypes();field.SetValue(damages,100000f);
            for(int n=0;n<20;n++) {
                var elemental=new HitData{m_damage=(HitData.DamageTypes)damages};
                dummy.ApplyDamage(elemental,true,false);dummy.CheckDeath();
            }
            Check(dummy.GetHealth()==3750&&!dummy.IsDead()&&dummyRefunds==0,"dummy survives repeated lethal damage channel: "+field.Name);
        }
        dummy.m_collider=dummy.gameObject.AddComponent<CapsuleCollider>();
        dummy.m_seman=new SEMan(dummy,dummy.m_nview);
        TrainingMeterChecks(plugin,dummy,harmony,ha,patchMethod);
        var burningTick=ScriptableObject.CreateInstance<SE_Burning>();burningTick.m_character=dummy;burningTick.m_fireDamagePerHit=100000;burningTick.m_spiritDamagePerHit=100000;
        var poisonTick=ScriptableObject.CreateInstance<SE_Poison>();poisonTick.m_character=dummy;poisonTick.m_damagePerHit=100000;
        for(int tick=0;tick<20;tick++) {
            burningTick.UpdateStatusEffect(2);poisonTick.UpdateStatusEffect(2);dummy.CheckDeath();
        }
        Check(dummy.GetHealth()==3750&&!dummy.IsDead()&&dummyRefunds==0,"native burning, spirit and poison ticks remain nonlethal in repeated bursts");
        UnityEngine.Object.DestroyImmediate(burningTick);UnityEngine.Object.DestroyImmediate(poisonTick);
        foreach(float damage in new[]{50f,3750f,10000000000f}) {
            dummyRefunds=0;dummyShownDamage=0;var hit=new HitData(damage);dummy.ApplyDamage(hit,true,false);
            Check(dummyShownDamage==damage&&received>0&&hit.GetTotalDamage()>=damage,"dummy retains full damage text and damage callback: "+damage);
            Check(dummy.GetHealth()==3750&&!dummy.IsDead()&&dummyRefunds==0,"dummy instantly restores full HP without material drops: "+damage+" health="+dummy.GetHealth()+" refunds="+dummyRefunds+" scope="+plugin.GetType("Overhaul.Storage.TrainingDummyProtection").GetField("ReceivingAttack",All).GetValue(null));
            dummy.CheckDeath();Check(!dummy.IsDead(),"dummy survives native death check: "+damage);
        }
        var ordinary=New<Character>("ordinary-damage");Data(ordinary);ordinary.m_nview=ordinary.GetComponent<ZNetView>();ordinary.m_health=100;ordinary.m_staggerDamageFactor=0;ordinary.SetMaxHealth(100);ordinary.SetHealth(100);ordinary.ApplyDamage(new HitData(20),false,false);
        Check(ordinary.GetHealth()<100,"other creatures still lose health");
        patchMethod.Invoke(harmony,new object[]{typeof(Character).GetMethod("Damage",All),Hook("DummyRemovalHit"),null,null,null});
        Call(plugin.GetType("Overhaul.Storage.TrainingDummyProtection"),"RemoveWithHammer",dummy,new HitData(10000000000f));
        Check(dummyRemoval.m_statusEffectHash==(int)plugin.GetType("Overhaul.Storage.TrainingDummyProtection").GetField("HammerRemoval",All).GetValue(null),"hammer branch marks removal hit for native network transport");
        dummy.ApplyDamage(dummyRemoval,false,false);
        Check(dummy.GetHealth()<=0&&dummyRefunds==1,"hammer removal remains lethal and refunds materials exactly once");
        Check(plugin.GetType("Overhaul.Storage.TrainingDummyProtection").GetField("ReceivingAttack",All).GetValue(null)==null,"dummy damage scope restored after attacks and removal");
        harmony.GetType().GetMethod("Unpatch",new[]{typeof(MethodBase),typeof(MethodInfo)}).Invoke(harmony,new object[]{typeof(Piece).GetMethod("DropResources",All),typeof(OverhaulStorageChecks).GetMethod("DummyRefund",All)});
        var piece=dummy.GetComponent<Piece>();piece.m_nview=dummy.m_nview;piece.m_creator=111;data.Set(ZDOVars.s_creator,111L);
        piece.m_resources=new[]{new Piece.Requirement{m_resItem=material,m_amount=205,m_recover=true}};
        patchMethod.Invoke(harmony,new object[]{typeof(ItemDrop).GetMethod("SetStack",All),Hook("PrepareRefundStack"),null,null,null});
        var beforeDrops=new HashSet<ItemDrop>(UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None));
        piece.DropResources(dummyRemoval);
        var returned=UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Where(i=>!beforeDrops.Contains(i)&&i.m_itemData.m_shared.m_name=="StorageWood").ToArray();
        Check(returned.Sum(i=>i.m_itemData.m_stack)==205,"native hammer refund returns full construction quantity across stacks");
        foreach(var item in returned)UnityEngine.Object.DestroyImmediate(item.gameObject);
    }

    static void BurningFoodChecks(Assembly plugin,Player player)
    {
        var mob=New<Character>("burning-loot");Data(mob);mob.m_nview=mob.GetComponent<ZNetView>();mob.m_seman=new SEMan(mob,mob.m_nview);
        var drop=mob.gameObject.AddComponent<CharacterDrop>();drop.m_character=mob;
        var raw=New<ItemDrop>("arbitrary-flesh");raw.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="Arbitrary flesh"};
        var cooked=New<ItemDrop>("cooked-result");cooked.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="Cooked result",m_food=55};
        var coal=New<ItemDrop>("coal-result");coal.m_itemData.m_shared=new ItemDrop.ItemData.SharedData{m_name="Coal",m_food=0};
        var station=New<CookingStation>("cooking-recipes");
        station.m_conversion=new List<CookingStation.ItemConversion>{new CookingStation.ItemConversion{m_from=raw,m_to=cooked},new CookingStation.ItemConversion{m_from=cooked,m_to=coal},new CookingStation.ItemConversion{m_from=material,m_to=coal}};
        ZNetScene.instance.m_namedPrefabs[station.name.GetStableHashCode()]=station.gameObject;
        foreach(var item in new[]{raw,cooked,coal})ZNetScene.instance.m_namedPrefabs[item.name.GetStableHashCode()]=item.gameObject;
        var type=plugin.GetType("Overhaul.Storage.BurningFoodDrops");Call(type,"Rebuild",ZNetScene.instance);
        drop.m_drops=new List<CharacterDrop.Drop>{new CharacterDrop.Drop{m_prefab=raw.gameObject,m_amountMin=3,m_amountMax=3,m_chance=1,m_dontScale=true,m_levelMultiplier=false},new CharacterDrop.Drop{m_prefab=material.gameObject,m_amountMin=2,m_amountMax=2,m_chance=1,m_dontScale=true,m_levelMultiplier=false}};
        Check(drop.GenerateDropList()[0].Key==raw.gameObject,"unburnt creature keeps raw food");
        var spirit=ScriptableObject.CreateInstance<SE_Burning>();spirit.name="Spirit";mob.m_seman.GetStatusEffects().Add(spirit);mob.m_seman.m_statusEffectsHashSet.Add(SEMan.s_statusEffectSpirit);
        Check(drop.GenerateDropList()[0].Key==raw.gameObject,"spirit damage alone does not cook food");mob.m_seman.GetStatusEffects().Clear();
        var burning=ScriptableObject.CreateInstance<SE_Burning>();burning.name="Burning";mob.m_seman.GetStatusEffects().Add(burning);mob.m_seman.m_statusEffectsHashSet.Clear();mob.m_seman.m_statusEffectsHashSet.Add(SEMan.s_statusEffectBurning);
        var drops=drop.GenerateDropList();
        Check(drops[0].Key==cooked.gameObject&&drops[0].Value==3,"burning creature drops cooked recipe output with exact quantity");
        Check(drops[1].Key==material.gameObject&&drops[1].Value==2,"non-food drops and coal transformations remain unchanged");
        Check(drop.m_drops[0].m_prefab==raw.gameObject,"shared creature loot table remains raw");
        Call(type,"Convert",mob,drops);Check(drops[0].Key==cooked.gameObject&&drops[0].Value==3,"cooked result is not converted again to coal");
        var corpse=New<Ragdoll>("cooked-loot-corpse");var data=Data(corpse);corpse.m_nview=corpse.GetComponent<ZNetView>();
        corpse.SaveLootList(drop);
        Check(data.GetInt("drop_hash0")==cooked.name.GetStableHashCode()&&data.GetInt("drop_amount0")==3,"native ragdoll persists cooked prefab and quantity");
        mob.m_seman.GetStatusEffects().Clear();mob.m_seman.m_statusEffectsHashSet.Clear();
        Check(drop.GenerateDropList()[0].Key==raw.gameObject,"extinguished creature keeps raw food");
        Check(data.GetInt("drop_hash0")==cooked.name.GetStableHashCode(),"delayed corpse loot remains cooked after creature fire is gone");
        var playerDrops=new List<KeyValuePair<GameObject,int>>{new KeyValuePair<GameObject,int>(raw.gameObject,4)};
        Call(type,"Convert",player,playerDrops);Check(playerDrops[0].Key==raw.gameObject,"player inventory drops excluded");
        UnityEngine.Object.DestroyImmediate(burning);UnityEngine.Object.DestroyImmediate(spirit);
    }

    static void BossLootChecks(Assembly plugin)
    {
        var data=plugin.GetType("Overhaul.Dungeons.BossLootData");var encounter=plugin.GetType("Overhaul.Dungeons.BossEncounter");
        var field=data.GetField("tables",All);var previous=field.GetValue(null);var random=UnityEngine.Random.state;int before=checks;
        string name=material.name;
        string config="[Forest]\n"+name+" = 950, 950, 100\n[Swamp]\n"+name+" = 3, 3\n[Mountain]\n"+name+" = 9, 9, 0\n";
        try
        {
            var defaults=(System.Collections.IDictionary)Call(data,"Read",System.IO.File.ReadAllText("../../../Packages/Overhaul/DungeonBossLoot.cfg"));
            Check(defaults.Count==3&&((Array)defaults["Mountain"]).Length>=4,"boss loot packaged defaults contain three families and original four mountain rewards");
            foreach(var invalid in new[]{config.Replace("950, 950","951, 950"),config.Replace("100\n","NaN\n"),config.Replace("100\n","101\n"),config.Replace("3, 3","-1, 3"),config.Replace("950, 950","1, 10001"),config.Replace("[Forest]","[Unknown]"),"[Forest]\n[Swamp]\n",config+"[Forest]\n",config.Replace("[Swamp]",name+" = 1, 1\n[Swamp]")})
            {
                bool refused=false;try{Call(data,"Read",invalid);}catch(TargetInvocationException e){refused=e.InnerException is System.IO.InvalidDataException;}
                Check(refused,"boss loot invalid section, duplicate, quantity or chance rejected");
            }
            field.SetValue(null,Call(data,"Read",config));
            var chest=Chest("boss-loot",0,Vector3.zero,0);chest.m_width=4;chest.m_height=2;
            var zdo=chest.m_nview.GetZDO();
            zdo.SetPrefab(((string)encounter.GetField("ChestName",All).GetValue(null)).GetStableHashCode());Call(encounter,"FillChest",chest);
            Check(chest.GetInventory().CountItems("StorageWood")==950&&chest.GetInventory().GetAllItems().Count==10&&chest.GetInventory().GetAllItems().All(i=>i.m_stack<=100),"custom boss quantity splits across native stack limits and expands chest rows");
            chest.m_lastRevision=0;chest.Load();Check(chest.GetInventory().CountItems("StorageWood")==950&&chest.GetInventory().GetHeight()>=3,"expanded boss loot persists through native inventory reload");
            zdo.SetPrefab(((string)encounter.GetField("SwampChestName",All).GetValue(null)).GetStableHashCode());Call(encounter,"FillChest",chest);
            Check(chest.GetInventory().CountItems("StorageWood")==3,"swamp chest selects its configured table and omitted chance defaults to guaranteed");
            zdo.SetPrefab(((string)encounter.GetField("MountainChestName",All).GetValue(null)).GetStableHashCode());Call(encounter,"FillChest",chest);
            Check(chest.GetInventory().NrOfItems()==0,"zero chance disables reward");
            field.SetValue(null,Call(data,"Read",config.Replace("9, 9, 0","2, 7, 50")));
            bool empty=false,awarded=false;
            for(int seed=0;seed<24;seed++){UnityEngine.Random.InitState(seed);Call(encounter,"FillChest",chest);int count=chest.GetInventory().CountItems("StorageWood");Check(count==0||(count>=2&&count<=7),"chance-based boss reward respects inclusive quantity bounds");empty|=count==0;awarded|=count>0;}
            Check(empty&&awarded,"deterministic samples exercise both probability outcomes");
            field.SetValue(null,Call(data,"Read","[Forest]\n[Swamp]\n[Mountain]\n"));Call(encounter,"FillChest",chest);Check(chest.GetInventory().NrOfItems()==0,"intentionally empty section yields empty chest");
            Add(chest.GetInventory(),5);field.SetValue(null,Call(data,"Read",config.Replace(name,"MissingLootPrefab").Replace("9, 9, 0","9, 9, 100")));
            bool missing=false;try{Call(encounter,"FillChest",chest);}catch(TargetInvocationException e){missing=e.InnerException is System.IO.InvalidDataException;}
            Check(missing&&chest.GetInventory().CountItems("StorageWood")==5,"unknown prefab rejected before touching existing inventory");
            System.IO.File.WriteAllText("../../../Tools/OverhaulV2Work/boss-loot-results.txt","PASS "+(checks-before)+" boss loot checks\n");
        }
        finally{field.SetValue(null,previous);UnityEngine.Random.state=random;}
    }
    static void QuickFillChecks(Assembly plugin,Player player,object harmony,Assembly ha,MethodInfo patchMethod,object skip)
    {
        int before=checks;var type=plugin.GetType("Overhaul.Storage.SmelterQuickFill");
        var shift=Activator.CreateInstance(ha.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(OverhaulStorageChecks).GetMethod("Shift",All)});
        patchMethod.Invoke(harmony,new object[]{type.GetMethod("ShiftHeld",All),shift,null,null,null});
        patchMethod.Invoke(harmony,new object[]{typeof(Character).GetMethod("Message",All),skip,null,null,null});
        var smelter=New<Smelter>("smelter");Data(smelter);smelter.m_nview=smelter.GetComponent<ZNetView>();
        ZNetScene.instance.m_instances[smelter.m_nview.GetZDO()]=smelter.m_nview;
        smelter.m_nview.Register<string,bool>("RPC_AddOre",smelter.RPC_AddOre);smelter.m_nview.Register("RPC_AddFuel",smelter.RPC_AddFuel);
        smelter.m_conversion=new List<Smelter.ItemConversion>{new Smelter.ItemConversion{m_from=material,m_to=material}};
        smelter.m_maxOre=25;smelter.m_maxFuel=20;smelter.m_fuelItem=material;smelter.m_addOreAnimationDuration=0;
        var inv=player.GetInventory();inv.RemoveAll();Add(inv,60);testShift=false;
        smelter.OnAddOre(null,player,null);
        Check(smelter.GetQueueSize()==1&&inv.CountItems("StorageWood")==59,"normal smelter interaction still adds exactly one item");
        testShift=true;smelter.OnAddOre(null,player,null);
        Check(smelter.GetQueueSize()==25&&inv.CountItems("StorageWood")==35,"shift ore/wood interaction fills remaining queue exactly");
        smelter.OnAddOre(null,player,null);
        Check(smelter.GetQueueSize()==25&&inv.CountItems("StorageWood")==35,"full queue consumes nothing");
        smelter.m_nview.GetZDO().Set(ZDOVars.s_queued,0);inv.RemoveAll();Add(inv,3);smelter.OnAddOre(null,player,null);
        Check(smelter.GetQueueSize()==3&&inv.CountItems("StorageWood")==0,"insufficient ore/wood adds all available and stops");
        inv.RemoveAll();Add(inv,30);smelter.SetFuel(15.6f);smelter.OnAddFuel(null,player,null);
        Check(Mathf.Abs(smelter.GetFuel()-19.6f)<.001f&&inv.CountItems("StorageWood")==26,"shift fuel respects fractional remaining capacity without overfill");
        smelter.OnAddFuel(null,player,null);Check(inv.CountItems("StorageWood")==26,"less than one free fuel unit consumes nothing");
        smelter.SetFuel(0);inv.RemoveAll();Add(inv,2);smelter.OnAddFuel(null,player,null);
        Check(smelter.GetFuel()==2&&inv.CountItems("StorageWood")==0,"shift fuel stops after available stock");
        testShift=false;Add(inv,4);smelter.OnAddFuel(null,player,null);
        Check(smelter.GetFuel()==3&&inv.CountItems("StorageWood")==3,"normal fuel interaction remains single unit");
        testShift=true;smelter.SetFuel(0);var wrong=material.m_itemData.Clone();wrong.m_shared=new ItemDrop.ItemData.SharedData{m_name="Wrong fuel"};smelter.OnAddFuel(null,player,wrong);
        Check(smelter.GetFuel()==0&&inv.CountItems("StorageWood")==3,"wrong selected fuel keeps native refusal without bulk consumption");
        smelter.m_nview.GetZDO().Set(ZDOVars.s_queued,20);smelter.m_nview.GetZDO().SetOwner(778);inv.RemoveAll();Add(inv,50);
        smelter.OnAddOre(null,player,null);
        Check(inv.CountItems("StorageWood")==45,"delayed remote replication cannot turn one bulk click into an unbounded loop");
        FuelDeviceChecks(plugin,player,harmony,ha,patchMethod,skip);
        testShift=false;System.IO.File.WriteAllText("../../../Tools/OverhaulV2Work/quick-fill-results.txt","PASS "+(checks-before)+" quick-fill checks\n");
    }

    static void FuelDeviceChecks(Assembly plugin,Player player,object harmony,Assembly ha,MethodInfo patchMethod,object skip)
    {
        var loc=(Localization)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var field in typeof(Localization).GetFields(All).Where(f=>!f.IsStatic)) {
            if(field.FieldType==typeof(char[]))field.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(field.FieldType==typeof(System.Text.StringBuilder))field.SetValue(loc,new System.Text.StringBuilder());
            else if(field.FieldType.IsGenericType&&(field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||field.FieldType.GetGenericTypeDefinition()==typeof(List<>)))field.SetValue(loc,Activator.CreateInstance(field.FieldType));
            else if(field.Name=="m_cache")field.SetValue(loc,Activator.CreateInstance(field.FieldType,new object[]{100}));
        }
        typeof(Localization).GetField("m_instance",All).SetValue(null,loc);
        var inv=player.GetInventory();
        patchMethod.Invoke(harmony,new object[]{typeof(Fireplace).GetMethod("UpdateState",All),skip,null,null,null});
        var fire=New<Fireplace>("refillable-light");var fireData=Data(fire);fire.m_nview=fire.GetComponent<ZNetView>();
        ZNetScene.instance.m_instances[fireData]=fire.m_nview;fire.m_nview.Register("RPC_AddFuel",fire.RPC_AddFuel);fire.m_nview.Register("RPC_ToggleOn",fire.RPC_ToggleOn);
        fire.m_fuelItem=material;fire.m_maxFuel=6;fire.m_canRefill=true;fire.m_canTurnOff=false;
        inv.RemoveAll();Add(inv,20);testShift=false;fire.Interact(player,false,false);
        Check(fireData.GetFloat(ZDOVars.s_fuel)==1&&inv.CountItems("StorageWood")==19,"ordinary fire click adds one");
        testShift=true;fire.Interact(player,false,false);
        Check(fireData.GetFloat(ZDOVars.s_fuel)==6&&inv.CountItems("StorageWood")==14,"shift fills fire or torch to native capacity");
        fire.Interact(player,false,false);Check(inv.CountItems("StorageWood")==14,"full fire consumes nothing");
        fireData.Set(ZDOVars.s_fuel,2.4f);fire.Interact(player,false,false);
        Check(Mathf.Abs(fireData.GetFloat(ZDOVars.s_fuel)-5.4f)<.001f&&inv.CountItems("StorageWood")==11,"fractional fire level does not overfill");
        fireData.Set(ZDOVars.s_fuel,0f);inv.RemoveAll();Add(inv,2);fire.Interact(player,false,false);
        Check(fireData.GetFloat(ZDOVars.s_fuel)==2&&inv.CountItems("StorageWood")==0,"fire fills only available stock");
        Add(inv,10);fire.m_canTurnOff=true;fireData.Set(ZDOVars.s_state,1);fire.Interact(player,false,false);
        Check(fireData.GetFloat(ZDOVars.s_fuel)==6&&fireData.GetInt(ZDOVars.s_state)==1,"shift fills switchable light without turning it off");
        testShift=false;fire.Interact(player,false,false);Check(fireData.GetInt(ZDOVars.s_state)==2,"ordinary click still toggles switchable light");
        fire.m_canTurnOff=false;testShift=true;fire.m_infiniteFuel=true;int count=inv.CountItems("StorageWood");fire.Interact(player,false,false);
        Check(inv.CountItems("StorageWood")==count,"infinite light consumes no fuel");fire.m_infiniteFuel=false;
        fireData.Set(ZDOVars.s_fuel,1f);fireData.SetOwner(778);inv.RemoveAll();Add(inv,20);fire.Interact(player,false,false);
        Check(inv.CountItems("StorageWood")==15,"remote fire refill is bounded before replication");

        var oven=New<CookingStation>("oven");var ovenData=Data(oven);oven.m_nview=oven.GetComponent<ZNetView>();
        ZNetScene.instance.m_instances[ovenData]=oven.m_nview;oven.m_nview.Register("RPC_AddFuel",oven.RPC_AddFuel);
        oven.m_fuelItem=material;oven.m_useFuel=true;oven.m_maxFuel=10;
        Call(plugin.GetType("Overhaul.Storage.CookingFuelCapacityPatch"),"Prefix",oven);
        Check(oven.m_maxFuel==100,"cooking fuel capacity raised to 100");
        inv.RemoveAll();Add(inv,120);oven.OnAddFuelSwitch(null,player,null);
        Check(oven.GetFuel()==100&&inv.CountItems("StorageWood")==20,"oven shift fills 100 fuel");
        oven.m_maxFuel=150;Call(plugin.GetType("Overhaul.Storage.CookingFuelCapacityPatch"),"Prefix",oven);Check(oven.m_maxFuel==150,"larger cooking fuel capacity preserved");
        ovenData.Set(ZDOVars.s_fuel,0f);inv.RemoveAll();Add(inv,2);oven.OnAddFuelSwitch(null,player,null);
        Check(oven.GetFuel()==2&&inv.CountItems("StorageWood")==0,"oven stops on empty inventory");
        oven.m_slots=new[]{oven.transform,oven.transform,oven.transform,oven.transform};oven.m_requireFire=false;oven.m_skill=Skills.SkillType.None;
        var cooked=New<ItemDrop>("cooked");oven.m_overCookedItem=New<ItemDrop>("burnt");
        oven.m_conversion=new List<CookingStation.ItemConversion>{new CookingStation.ItemConversion{m_from=material,m_to=cooked}};
        oven.m_nview.Register<string,bool>("RPC_AddItem",oven.RPC_AddItem);
        inv.RemoveAll();Add(inv,10);oven.OnInteract(player);
        Check(oven.GetFreeSlot()==-1&&inv.CountItems("StorageWood")==6&&oven.m_slots.Length==4,"shift fills four physical cooking slots without adding slots");
        oven.OnInteract(player);Check(inv.CountItems("StorageWood")==6,"full cooking rack consumes nothing");

        var shield=New<ShieldGenerator>("shield");var shieldData=Data(shield);shield.m_nview=shield.GetComponent<ZNetView>();
        ZNetScene.instance.m_instances[shieldData]=shield.m_nview;shield.m_nview.Register("RPC_AddFuel",shield.RPC_AddFuel);shield.m_nview.Register<float>("RPC_SetFuel",shield.RPC_SetFuel);
        shield.m_fuelItems=new List<ItemDrop>{material};shield.m_maxFuel=10;
        inv.RemoveAll();Add(inv,20);shield.OnAddFuel(null,player,null);
        Check(shield.GetFuel()==10&&inv.CountItems("StorageWood")==10,"shield generator shift fills native fuel capacity");
        shieldData.Set(ZDOVars.s_fuel,0f);inv.RemoveAll();Add(inv,2);shield.OnAddFuel(null,player,inv.GetAllItems()[0]);
        Check(shield.GetFuel()==2&&inv.CountItems("StorageWood")==0,"selected shield fuel never creates free fuel after stack exhausted");

        var machine=New<Smelter>("future-biome-machine");machine.m_maxOre=20;machine.m_maxFuel=20;
        var capacity=plugin.GetType("Overhaul.Storage.SmelterCapacityPatch");Call(capacity,"Prefix",machine);
        Check(machine.m_maxOre==100&&machine.m_maxFuel==100,"production capacity applies independently of prefab name or biome");
        machine.m_maxOre=150;machine.m_maxFuel=250;Call(capacity,"Prefix",machine);
        Check(machine.m_maxOre==150&&machine.m_maxFuel==250,"existing capacities over 100 preserved");
        machine.m_maxOre=0;machine.m_maxFuel=0;Call(capacity,"Prefix",machine);
        Check(machine.m_maxOre==0&&machine.m_maxFuel==0,"absent inputs remain disabled");
    }
}





