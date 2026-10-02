using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
public static class AdminCommandAccessChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type access,commands;static readonly List<string> report=new List<string>();static readonly List<string> messages=new List<string>();
    static object Call(Type t,string name,params object[] args)=>t.GetMethod(name,F).Invoke(null,args);
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);report.Add("PASS "+label);}
    static bool Skip()=>false;
    static bool Cheats(ref bool __result){__result=true;return false;}
    static bool Output(string text){messages.Add(text);return false;}
    sealed class Socket:ISocket
    {
        internal ZPackage Sent;public bool IsConnected()=>true;public void Send(ZPackage p){Sent=new ZPackage(p.GetArray());}public ZPackage Recv()=>null;
        public int GetSendQueueSize()=>0;public int GetCurrentSendRate()=>0;public bool IsHost()=>false;public void Dispose(){}public bool GotNewData()=>false;public void Close(){}
        public string GetEndPointString()=>"fixture";public void GetAndResetStats(out int a,out int b){a=b=0;}public void GetConnectionQuality(out float a,out float b,out int c,out float d,out float e){a=b=d=e=0;c=0;}
        public ISocket Accept()=>null;public int GetHostPort()=>0;public bool Flush()=>true;public string GetHostName()=>"Steam_76561198000000001";public void VersionMatch(){}
    }
    public static void Run()
    {
        try{Test();File.WriteAllLines("../../../Tools/AugaWork/admin-command-access-checks.txt",report);}catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/admin-command-access-checks.txt",report);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));access=mod.GetType("Overhaul.Commands.AdminCommandAccess");commands=mod.GetType("Overhaul.Commands.AdminCommands");
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.admin.access.check"});var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        void Hook(MethodBase method,string name)=>patch.Invoke(harmony,new object[]{method,Activator.CreateInstance(hm,new object[]{typeof(AdminCommandAccessChecks).GetMethod(name,F)}),null,null,null});
        Hook(typeof(SyncedList).GetMethod("CheckLoad",F),"Skip");Hook(typeof(Achievements).GetMethod("IsCheatedAtAll",F),"Cheats");Hook(typeof(Terminal).GetMethod("AddString",new[]{typeof(string)}),"Output");
        Hook(typeof(Player).GetMethod("Message",F),"Skip");
        foreach(var type in mod.GetTypes().Where(t=>t.DeclaringType==access&&t.GetCustomAttributes(false).Any(a=>a.GetType().Name=="HarmonyPatch"))){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type});processor.GetType().GetMethod("Patch").Invoke(processor,null);}
        var host=new GameObject("inactive access fixtures");host.SetActive(false);ZNet.m_instance=host.AddComponent<ZNet>();ZNet.m_isServer=true;ZNet.m_connectionStatus=ZNet.ConnectionStatus.Connected;new ZRoutedRpc(true);var manager=new ZDOMan(512);ZNetScene.s_instance=host.AddComponent<ZNetScene>();
        ZNet.instance.m_adminList=(SyncedList)FormatterServices.GetUninitializedObject(typeof(SyncedList));ZNet.instance.m_adminList.m_list=new List<string>();
        var terminal=host.AddComponent<Console>();Terminal.InitTerminal();
        Console.m_consoleEnabledPermanent=false;Console.SetConsoleEnabled(false);Terminal.m_cheat=false;
        Call(commands,"Initialize");
        Check(terminal.IsConsoleEnabled(),"console enabled without launch option or saved preference");
        Check(!Terminal.m_cheat,"opening console does not enable devcommands");
        Console.SetConsoleEnabled(false);
        Check(terminal.IsConsoleEnabled(),"native Awake/settings reload cannot disable console during session");
        Call(access,"Clear");
        Check(terminal.IsConsoleEnabled()&&!Terminal.m_cheat,"connection reset keeps console available without admin grant");
        var serverSocket=new Socket();var clientSocket=new Socket();var peer=new ZNetPeer(serverSocket,false){m_uid=123};ZNet.instance.m_peers.Add(peer);Call(commands,"Register",peer);
        var serverPeer=new ZNetPeer(clientSocket,true){m_uid=456};Call(commands,"Register",serverPeer);
        void Side(bool server){ZNet.m_isServer=server;ZNet.instance.m_peers.Clear();ZNet.instance.m_peers.Add(server?peer:serverPeer);}
        void Deliver(){Side(true);peer.m_rpc.HandlePackage(new ZPackage(clientSocket.Sent.GetArray()));Side(false);serverPeer.m_rpc.HandlePackage(new ZPackage(serverSocket.Sent.GetArray()));}
        Check(!(bool)Call(commands,"Authorized",peer.m_rpc),"non-admin direct RPC denied");
        Check(!(bool)Call(access,"SetEnabled",peer.m_rpc,true),"non-admin cannot enable devcommands");
        ZNet.instance.m_adminList.m_list.Add(serverSocket.GetHostName());Check(!(bool)Call(commands,"Authorized",peer.m_rpc),"admin without devcommands denied");
        Side(false);Terminal.m_cheat=true;Check(!(bool)access.GetProperty("LocalEnabled",F).GetValue(null),"local cheat flag alone grants no rights");Terminal.m_cheat=false;
        terminal.TryRunCommand("devcommands");Check(!Terminal.m_cheat,"devcommands waits for server confirmation");Deliver();Check(Terminal.m_cheat&&(bool)access.GetProperty("LocalEnabled",F).GetValue(null),"native command enables only after authenticated server reply");
        Check(!terminal.IsCheatsEnabled(),"other native cheats remain disabled on client");
        foreach(var command in Terminal.commands.Values.Where(c=>c.Command.StartsWith("o_")).ToArray())Check(command.IsValid(terminal),"authorized client can use "+command.Command);
        Check(Terminal.commands["spawn"].IsValid(terminal)&&Terminal.commands["confirmcheats"].IsValid(terminal),"native spawn and its cheat confirmation allowed for approved admin");
        Call(access,"ReceiveState",new ZRpc(new Socket()),false,"forged");Check(Terminal.m_cheat,"non-server state packet ignored");
        var player=host.AddComponent<Player>();Player.m_localPlayer=player;player.transform.position=new Vector3(10,2,20);
        var prefab=new GameObject("Overhaul_AccessSpawnFixture");prefab.AddComponent<ZNetView>();ZNetScene.instance.m_namedPrefabs[prefab.name.GetStableHashCode()]=prefab;ZNetScene.instance.m_prefabs.Add(prefab);
        int before=manager.m_objectsByID.Count;terminal.TryRunCommand("spawn Overhaul_AccessSpawnFixture 3 1 2");Check(manager.m_objectsByID.Count==before,"spawn waits for fresh server approval");
        var request=clientSocket.Sent;Deliver();
        // Edit-mode Instantiate does not run ordinary MonoBehaviour Awake; run the
        // real native initializer as Unity does when these objects spawn in a game.
        typeof(Game).GetProperty("instance",F).SetValue(null,host.AddComponent<Game>());
        foreach(var view in Resources.FindObjectsOfTypeAll<ZNetView>().Where(v=>v.gameObject.name==prefab.name+"(Clone)"&&!v.IsValid()))view.Awake();
        typeof(Game).GetProperty("instance",F).SetValue(null,null);
        Check(manager.m_objectsByID.Count==before+3,"original native spawn creates requested three networked objects on client: before="+before+" after="+manager.m_objectsByID.Count);
        var created=ZNetScene.instance.m_instances.Values.Where(v=>v&&v.gameObject.name==prefab.name+"(Clone)").ToArray();Check(created.Length==3&&created.All(v=>Vector3.Distance(v.transform.position,player.transform.position+Vector3.forward*2+Vector3.up)<=2.01f),"native spawn keeps count radius and requesting player position");
        int after=manager.m_objectsByID.Count;serverPeer.m_rpc.HandlePackage(new ZPackage(serverSocket.Sent.GetArray()));Check(manager.m_objectsByID.Count==after,"replayed spawn approval cannot execute twice");
        terminal.TryRunCommand("spawn Overhaul_AccessSpawnFixture");ZNet.instance.m_adminList.m_list.Clear();Deliver();Check(manager.m_objectsByID.Count==after,"admin revoked before approval cannot spawn");
        Side(true);Check(!(bool)Call(commands,"Authorized",peer.m_rpc),"server rechecks current admin list for every o_ RPC");Call(access,"Tick");Side(false);serverPeer.m_rpc.HandlePackage(new ZPackage(serverSocket.Sent.GetArray()));Check(!Terminal.m_cheat,"revocation disables devcommands on client");
        int calls=0;var test=new Terminal.ConsoleCommand("o_permission_fixture","",(Terminal.ConsoleEvent)(a=>calls++));test.RunAction(new Terminal.ConsoleEventArgs("o_permission_fixture",terminal,test));Check(calls==0,"direct RunAction cannot bypass permissions");Check(!test.IsValid(terminal,true),"skipAllowedCheck cannot bypass admin/devcommands");
        Side(true);ZNet.instance.m_adminList.m_list.Add(serverSocket.GetHostName());Call(access,"SetEnabled",peer.m_rpc,true);Terminal.m_cheat=true;
        ZNet.instance.RPC_RemoteCommand(peer.m_rpc,"o_permission_fixture");Check(calls==0,"legacy remote console cannot borrow server devcommands flag");
        Terminal.m_cheat=false;ZNet.instance.RPC_RemoteCommand(peer.m_rpc,"devcommands");Check(!Terminal.m_cheat&&!(bool)Call(access,"Enabled",peer.m_rpc),"remote devcommands never changes server operator flag");
        Terminal.m_cheat=true;test.RunAction(new Terminal.ConsoleEventArgs("o_permission_fixture",terminal,test));Check(calls==1,"local server operator allowed with devcommands");
        Call(access,"Clear");Check(!Terminal.m_cheat&&!(bool)Call(access,"Enabled",peer.m_rpc),"new session clears all grants and cheat flag");
        peer.m_rpc.Invoke("Overhaul_AdminMudTestApproved",true);Side(false);serverPeer.m_rpc.HandlePackage(new ZPackage(serverSocket.Sent.GetArray()));
        Check(!(bool)mod.GetType("Overhaul.Commands.MudCollisionTest").GetProperty("Enabled",F).GetValue(null),"late mud approval cannot enable command after devcommands disabled");
        report.Add("PASS total="+report.Count+" package="+mod.GetName().Version);
        ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
    }
}
