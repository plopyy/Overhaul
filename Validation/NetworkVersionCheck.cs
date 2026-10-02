using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class NetworkVersionCheck
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static int checks, admitted;
    static bool Admission(bool __runOriginal) { if (__runOriginal) admitted++; return false; }
    static bool SendFixture(ZRpc rpc) { rpc.Invoke("PeerInfo", new ZPackage()); return false; }
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,a) => {
            var path=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var type=plugin.GetType("Overhaul.NetworkVersion");
        var version=plugin.GetName().Version;
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));
        var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");
        var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.test.full-version"});
        foreach(var name in new[]{"RegisterVersion","SendVersion","CheckVersion"}) {
            var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type.GetNestedType(name,F)});
            processor.GetType().GetMethod("Patch").Invoke(processor,null);
        }
        void Stub(string target,string method) {
            var hook=Activator.CreateInstance(hm,new object[]{typeof(NetworkVersionCheck).GetMethod(method,F)});
            hm.GetField("priority").SetValue(hook,0);
            ht.GetMethod("Patch",new[]{typeof(MethodBase),hm,hm,hm,hm}).Invoke(harmony,new object[]{typeof(ZNet).GetMethod(target,F),hook,null,null,null});
        }
        Stub("RPC_PeerInfo","Admission");Stub("SendPeerInfo","SendFixture");
        var go=new GameObject("network-version-test");go.SetActive(false);var net=go.AddComponent<ZNet>();
        bool previous=ZNet.m_isServer;ZNet.m_isServer=true;
        try {
            foreach(var remote in new[]{version.ToString(),$"{version.Major}.{version.Minor}.{version.Build}.{version.Revision-1}",$"{version.Major}.{version.Minor}.{version.Build}.{version.Revision+1}","2.1.0","2.0.0.1","3.1.0.1",null,"","bad-version"}) {
                var serverSocket=new Socket();var clientSocket=new Socket();var peer=new ZNetPeer(serverSocket,false);var client=new ZRpc(clientSocket);
                typeof(ZNet).GetMethod("OnNewConnection",F).Invoke(net,new object[]{peer});
                int error=0;client.Register<int>("Error",(rpc,value)=>error=value);
                admitted=0;
                if(remote==version.ToString()) typeof(ZNet).GetMethod("SendPeerInfo",F).Invoke(net,new object[]{client,""});
                else { if(remote!=null)client.Invoke("Overhaul_FullVersion",remote);client.Invoke("PeerInfo",new ZPackage()); }
                foreach(var packet in clientSocket.Packets)peer.m_rpc.HandlePackage(packet);
                foreach(var packet in serverSocket.Packets)client.HandlePackage(packet);
                bool allowed=remote==version.ToString();
                Check(admitted==(allowed?1:0),"Native admission gate: "+(remote??"absent"));
                Check(error==(allowed?0:(int)ZNet.ConnectionStatus.ErrorVersion),"Client rejection code: "+remote);
                Check(!peer.IsReady(),"Fixture never admits rejected peer");
                if(!allowed) {
                    clientSocket.Packets.Clear();client.Invoke("Overhaul_FullVersion",version.ToString());client.Invoke("PeerInfo",new ZPackage());
                    foreach(var packet in clientSocket.Packets)peer.m_rpc.HandlePackage(packet);
                    Check(admitted==0,"Rejected connection stays rejected");
                }
                net.m_peers.Remove(peer);
            }
            var socket=new Socket();var server=new ZNetPeer(socket,true);net.m_peers.Add(server);
            type.GetMethod("Register",F).Invoke(null,new object[]{server.m_rpc});
            ZNet.m_isServer=false;
            Check(!(bool)type.GetMethod("Validate",F).Invoke(null,new object[]{net,server.m_rpc}),"Client rejects server without full version");
            Check(ZNet.m_connectionStatus==ZNet.ConnectionStatus.ErrorVersion,"Client native mismatch status");
            File.WriteAllText("../../../Tools/AugaWork/network-version-checks.txt",$"PASS {checks} checks. Packaged {version}. Real Harmony hooks and serialized ZRpc packets; native admission body replaced by sentinel. No live Steam connection.");
        } finally { ZNet.m_isServer=previous;ht.GetMethod("UnpatchSelf").Invoke(harmony,null);UnityEngine.Object.DestroyImmediate(go); }
    }
    sealed class Socket : ISocket
    {
        public readonly List<ZPackage> Packets=new List<ZPackage>();
        public bool IsConnected()=>true;
        public void Send(ZPackage p)=>Packets.Add(new ZPackage(p.GetArray()));
        public ZPackage Recv()=>null;
        public int GetSendQueueSize()=>0;public int GetCurrentSendRate()=>0;
        public bool IsHost()=>false;public void Dispose(){}public bool GotNewData()=>false;public void Close(){}
        public string GetEndPointString()=>"fixture";
        public void GetAndResetStats(out int a,out int b){a=b=0;}
        public void GetConnectionQuality(out float a,out float b,out int c,out float d,out float e){a=b=d=e=0;c=0;}
        public ISocket Accept()=>null;public int GetHostPort()=>0;public bool Flush()=>true;
        public string GetHostName()=>"fixture";public void VersionMatch(){}
    }
}
