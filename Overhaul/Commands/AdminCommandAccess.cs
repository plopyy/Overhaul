using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Overhaul.Commands
{
    internal static class AdminCommandAccess
    {
        private const string Toggle="Overhaul_DevCommands",State="Overhaul_DevCommandsState",Spawn="Overhaul_SpawnRequest",Permit="Overhaul_SpawnPermit";
        private static readonly HashSet<ZRpc> enabled=new HashSet<ZRpc>();
        private static readonly Dictionary<int,Terminal.ConsoleEventArgs> pending=new Dictionary<int,Terminal.ConsoleEventArgs>();
        private static ZRpc grantedServer;
        private static int sequence;
        private static bool spawning;
        private static float nextAudit;
        internal static bool Enabled(ZRpc rpc)=>rpc!=null&&enabled.Contains(rpc);
        internal static bool LocalEnabled=>ZNet.instance&&Terminal.m_cheat&&(ZNet.instance.IsServer()||grantedServer!=null&&ZNet.instance.GetServerPeer()?.m_rpc==grantedServer);
        internal static bool Protected(string command)=>command!=null&&(command.StartsWith("o_",StringComparison.OrdinalIgnoreCase)||command.Equals("spawn",StringComparison.OrdinalIgnoreCase));
        internal static void Clear(){enabled.Clear();pending.Clear();grantedServer=null;spawning=false;Terminal.m_cheat=false;nextAudit=0;}
        internal static bool SetEnabled(ZRpc rpc,bool value)
        {
            enabled.Remove(rpc);
            if(value&&AdminCommands.IsAdministrator(rpc))enabled.Add(rpc);
            return Enabled(rpc);
        }
        private static void ReceiveToggle(ZRpc rpc,bool value)
        {
            if(!ZNet.instance||!ZNet.instance.IsServer())return;
            bool accepted=SetEnabled(rpc,value);
            rpc.Invoke(State,accepted,value&&!accepted?"Overhaul : acces refuse, administrateur serveur requis (adminlist.txt).":"Dev commands: "+accepted);
        }
        private static void ReceiveState(ZRpc rpc,bool value,string message)
        {
            if(!ZNet.instance||ZNet.instance.IsServer()||ZNet.instance.GetServerPeer()?.m_rpc!=rpc)return;
            grantedServer=value?rpc:null;Terminal.m_cheat=value;
            if(!value){pending.Clear();MudCollisionTest.Set(false);}
            if(Console.instance){Console.instance.AddString(message);Console.instance.updateCommandList();}
        }
        private static void RequestSpawn(ZRpc rpc,int id)
        {
            if(!ZNet.instance||!ZNet.instance.IsServer())return;
            // Authorize this request against the actual connection and current admin list.
            rpc.Invoke(Permit,id,AdminCommands.Authorized(rpc));
        }
        private static void ReceivePermit(ZRpc rpc,int id,bool allowed)
        {
            if(!ZNet.instance||ZNet.instance.IsServer()||ZNet.instance.GetServerPeer()?.m_rpc!=rpc||!pending.TryGetValue(id,out var args))return;
            pending.Remove(id);
            if(!allowed||!LocalEnabled){args.Context.AddString("Overhaul : spawn refuse, admin et devcommands actifs requis.");return;}
            // Execute the original spawn on the requesting player's client. Native ZNetView
            // creation replicates the objects; native syntax, pickup and level handling remain.
            try{spawning=true;Terminal.commands["spawn"].RunAction(args);}finally{spawning=false;}
        }
        internal static void Register(ZNetPeer peer)
        {
            peer.m_rpc.Register<bool>(Toggle,ReceiveToggle);peer.m_rpc.Register<bool,string>(State,ReceiveState);
            peer.m_rpc.Register<int>(Spawn,RequestSpawn);peer.m_rpc.Register<int,bool>(Permit,ReceivePermit);
        }
        internal static void Tick()
        {
            if(!ZNet.instance||!ZNet.instance.IsServer()||UnityEngine.Time.realtimeSinceStartup<nextAudit)return;
            nextAudit=UnityEngine.Time.realtimeSinceStartup+1;
            foreach(var rpc in enabled.ToArray())if(!AdminCommands.IsAdministrator(rpc))
            {enabled.Remove(rpc);if(ZNet.instance.GetPeer(rpc)!=null)rpc.Invoke(State,false,"Overhaul : autorisation admin retiree, devcommands desactive.");}
        }
        [HarmonyPatch(typeof(ZNet),"Awake")]
        private static class NewSession { private static void Prefix()=>Clear(); }
        [HarmonyPatch(typeof(ZNet),"OnDestroy")]
        private static class EndSession { private static void Postfix()=>Clear(); }
        [HarmonyPatch(typeof(Terminal),nameof(Terminal.TryRunCommand))]
        private static class ConsoleGate
        {
            private static bool Prefix(Terminal __instance,string text)
            {
                var name=(text??"").Split(' ')[0];
                if(!Protected(name)||LocalEnabled)return true;
                __instance.AddString("Overhaul : commande reservee aux admins ; activer devcommands avant de l'utiliser.");return false;
            }
        }
        [HarmonyPatch(typeof(Terminal.ConsoleCommand),nameof(Terminal.ConsoleCommand.IsValid))]
        private static class Valid
        {
            private static bool Prefix(Terminal.ConsoleCommand __instance,Terminal context,bool skipAllowedCheck,ref bool __result)
            {
                if(!Protected(__instance.Command)&&__instance.Command!="confirmcheats")return true;
                __result=LocalEnabled&&(skipAllowedCheck||context.isAllowedCommand(__instance));return false;
            }
        }
        [HarmonyPatch(typeof(Terminal.ConsoleCommand),nameof(Terminal.ConsoleCommand.RunAction))]
        private static class Execute
        {
            private static bool Prefix(Terminal.ConsoleCommand __instance,Terminal.ConsoleEventArgs args)
            {
                string name=__instance.Command;
                if(name=="devcommands"&&ZNet.instance&&!ZNet.instance.IsServer())
                {
                    var peer=ZNet.instance.GetServerPeer();
                    if(peer==null||!peer.IsReady()){args.Context.AddString("Overhaul : serveur non connecte.");return false;}
                    bool value=!Terminal.m_cheat;
                    if(!value){Terminal.m_cheat=false;grantedServer=null;pending.Clear();MudCollisionTest.Set(false);}
                    peer.m_rpc.Invoke(Toggle,value);return false;
                }
                if(!Protected(name)&&name!="confirmcheats")return true;
                if(!LocalEnabled){args.Context.AddString("Overhaul : administrateur et devcommands actifs requis.");return false;}
                if(name!="spawn")return true;
                if(!Player.m_localPlayer){args.Context.AddString("Overhaul : lancer spawn depuis la console d'un joueur admin connecte.");return false;}
                if(ZNet.instance.IsServer()||spawning)return true;
                if(pending.Count>=32){args.Context.AddString("Overhaul : attendre les demandes spawn en cours.");return false;}
                int id=++sequence;pending[id]=args;grantedServer.Invoke(Spawn,id);return false;
            }
        }
        [HarmonyPatch(typeof(ZNet),"RPC_RemoteCommand")]
        private static class Remote
        {
            private static bool Prefix(ZRpc rpc,string command)
            {
                var name=(command??"").TrimStart().Split(' ')[0].ToLowerInvariant();
                if(name=="devcommands"){ReceiveToggle(rpc,!Enabled(rpc));return false;}
                if(!Protected(name)&&name!="confirmcheats")return true;
                // Do not let the legacy remote console use the server operator's global flag.
                if(ZNet.instance&&ZNet.instance.IsServer())ZNet.instance.RemotePrint(rpc,"Overhaul : utiliser la console du joueur avec devcommands active.");
                return false;
            }
        }
    }
}
