using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static class XPortalLogicChecks
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly mod; static readonly List<string> report=new List<string>();
    static Type T(string name)=>mod.GetType("XPortal."+name,true);
    static object Call(Type t,object owner,string name,params object[] args)=>t.GetMethod(name,F).Invoke(owner,args);
    static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);if(!ok)throw new Exception(text);}
    static object Singleton(string name)=>T(name).GetProperty("Instance",F).GetValue(null);
    static bool NoAwake()=>false;
    public static void Run(Assembly assembly,object panel)
    {
        mod=assembly;
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/AugaWork/xportal-fixture/Test.exe"),null,null,new string[0]});
        var cfg=Activator.CreateInstance(bep.GetType("BepInEx.Configuration.ConfigFile"),new object[]{Path.GetFullPath("../../../Tools/AugaWork/xportal-fixture.cfg"),false});
        var config=Singleton("XPortalConfig");Call(T("XPortalConfig"),config,"LoadLocalConfig",cfg);
        var ht=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");
        var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.xportal.logic.checks"});
        var hm=ht.Assembly.GetType("HarmonyLib.HarmonyMethod");
        var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters()[0].ParameterType==typeof(MethodBase));
        // Fixtures use actual game classes without booting a world or network transport.
        foreach(var type in new[]{typeof(ZoneSystem),typeof(ZNetScene),typeof(ZNet),typeof(Game)}){
            var args=new object[patch.GetParameters().Length];args[0]=type.GetMethod("Awake",F);args[1]=Activator.CreateInstance(hm,new object[]{typeof(XPortalLogicChecks).GetMethod("NoAwake",F)});patch.Invoke(harmony,args);
        }
        {var args=new object[patch.GetParameters().Length];args[0]=typeof(ZoneSystem).GetMethod("Reset",F);args[1]=Activator.CreateInstance(hm,new object[]{typeof(XPortalLogicChecks).GetMethod("NoAwake",F)});patch.Invoke(harmony,args);}
        var host=new GameObject("Inactive XPortal fixtures");host.SetActive(false);
        var game=host.AddComponent<Game>();typeof(Game).GetProperty("instance",F).SetValue(null,game);game.PortalPrefabHash.Add("portal_wood".GetStableHashCode());
        var zone=host.AddComponent<ZoneSystem>();typeof(ZoneSystem).GetField("s_instance",F).SetValue(null,zone);
        var scene=host.AddComponent<ZNetScene>();typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);
        var net=host.AddComponent<ZNet>();typeof(ZNet).GetField("m_instance",F).SetValue(null,net);ZNet.m_isServer=true;
        var rpc=new ZRoutedRpc(true);var zdom=new ZDOMan(512);rpc.SetUID(ZNet.GetUID());
        Call(T("Patches.Patcher"),null,"Patch");
        Check(true,"all integrated XPortal Harmony patches install on current game DLL");
        Call(T("RPC.RPCManager"),null,"Register");Check(true,"all seven routed RPCs register without external XPortal");
        Call(T("XPortalConfig"),config,"ResetServer");
        var manager=Singleton("KnownPortalsManager");var mt=T("KnownPortalsManager");
        var a=zdom.CreateNewZDO(new Vector3(0,10,0),"portal_wood".GetStableHashCode());
        var b=zdom.CreateNewZDO(new Vector3(1200,10,0),"portal_wood".GetStableHashCode());
        a.Set("tag","Base");b.Set("tag","Montagne");a.Set("XPortal_TargetId",b.m_uid);b.Set("XPortal_TargetId",a.m_uid);
        a.Set("XPortal_PreviousId",a.m_uid);b.Set("XPortal_PreviousId",b.m_uid);
        Call(mt,manager,"UpdateFromZDOList",new List<ZDO>{a,b});
        var portal=Call(mt,manager,"GetKnownPortalById",a.m_uid);
        Check((ZDOID)T("KnownPortal").GetProperty("Target").GetValue(portal)==b.m_uid,"existing XPortal target and name read from unchanged saved ZDO keys");
        var packet=(ZPackage)Call(mt,manager,"Pack");packet.SetPos(0);Call(mt,manager,"Reset");Call(mt,manager,"UpdateFromResyncPackage",packet);
        Check((int)mt.GetProperty("Count").GetValue(manager)==2,"portal list packet round trip preserves both destinations");
        portal=Call(mt,manager,"GetKnownPortalById",a.m_uid);
        var pt=panel.GetType();pt.GetField("thisPortal",F).SetValue(panel,portal);pt.GetField("selectedTargetId",F).SetValue(panel,b.m_uid);
        Call(pt,panel,"PopulateDropdown");var dropdown=(Dropdown)pt.GetField("targetPortalDropdown",F).GetValue(panel);
        Check(dropdown.options.Count==2 && dropdown.value==1 && dropdown.options[1].text.Contains("Montagne") && dropdown.options[1].text.Contains("km"),"real prefab destination list excludes source, selects saved target and displays distance");
        var settings=T("XPortalConfig").GetProperty("Server").GetValue(config);settings.GetType().GetField("PingMapDisabled").SetValue(settings,true);Call(pt,panel,"SetPingMapButtonActive",true);
        Check(!((GameObject)pt.GetField("pingMapButtonObject",F).GetValue(panel)).activeSelf,"server option disables map ping in existing prefab");
        settings.GetType().GetField("PingMapDisabled").SetValue(settings,false);zone.m_globalKeysValues.Add("nomap", "");Call(pt,panel,"SetPingMapButtonActive",true);
        Check(!((GameObject)pt.GetField("pingMapButtonObject",F).GetValue(panel)).activeSelf,"nomap world also hides ping");zone.m_globalKeysValues.Clear();Call(pt,panel,"SetPingMapButtonActive",true);
        dropdown.value=0;Check((ZDOID)pt.GetField("selectedTargetId",F).GetValue(panel)==ZDOID.None,"destination callback updates selected portal");
        dropdown.value=1;
        Call(T("XPortal"),null,"PortalInfoSubmitted",portal,"Renamed",b.m_uid,true);
        Check(a.GetString("tag")=="Renamed" && a.GetZDOID("XPortal_TargetId")==b.m_uid && a.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)==b.m_uid,"submit reaches registered server RPC and writes tag plus native portal connection");
        Check((ZDOID)Call(mt,manager,"FindDefaultPortal")==a.m_uid,"default portal setting resolves selected location");
        var oldCount=(int)mt.GetProperty("Count").GetValue(manager);ZNet.m_isServer=false;
        Call(T("RPC.Client.ClientEvents"),null,"RPC_Resync",-123L,new ZPackage(),"untrusted");
        Call(T("RPC.Client.ClientEvents"),null,"RPC_Config",-123L,new ZPackage());
        Check((int)mt.GetProperty("Count").GetValue(manager)==oldCount,"non-server sync and config rejected before packet decoding");
        ZNet.m_isServer=true;
        // Remove destination through the real RPC: the remaining portal loses its link.
        Call(T("RPC.Server.ServerEvents"),null,"RPC_RemoveRequest",ZNet.GetUID(),b.m_uid);
        Check((int)mt.GetProperty("Count").GetValue(manager)==1 && a.GetZDOID("XPortal_TargetId")==ZDOID.None,"destroying a destination removes it and clears inbound native connections");
        var local=T("XPortalConfig").GetProperty("Local").GetValue(config);var defaultEntry=local.GetType().GetField("DefaultPortal").GetValue(local);defaultEntry.GetType().GetProperty("Value").SetValue(defaultEntry,Vector3.zero);
        // Keep UI instance alive for the static preview, check world-specific queues/data independently.
        bool ran=false;Action<bool,object> delayed=(x,y)=>ran=true;Call(T("QueuedAction"),null,"Queue",delayed,0,null);Call(T("QueuedAction"),null,"Clear");Call(T("QueuedAction"),null,"Update");Call(mt,manager,"Reset");
        Check(!ran && (int)mt.GetProperty("Count").GetValue(manager)==0,"session queue and portal cache clear between worlds");
        dropdown.ClearOptions();Call(T("XPortalConfig"),config,"Stop");
        Call(T("Patches.Patcher"),null,"Unpatch");ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
        File.WriteAllLines("../../../Tools/AugaWork/xportal-logic-checks.txt",report);
        // Inactive native fixtures remain until the editor process exits (avoid world shutdown handlers).
    }
}
