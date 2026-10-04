using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Each flight belongs to an admitted connection. Neither its landing point
    // nor the first-spawn flag comes from a client transform/profile.
    internal static class GameArrivalRuntime
    {
        private sealed class Flight
        {
            internal Vector3 Landing;
            internal Player Player;
            internal Valkyrie Bird;
            internal bool Ready,Skip,Pending,Finished,Kinematic;
            internal double Began;
        }
        private static readonly Dictionary<ZRpc,Flight> flights=new Dictionary<ZRpc,Flight>();
        private static readonly Dictionary<Valkyrie,Flight> birds=new Dictionary<Valkyrie,Flight>();
        private static readonly Dictionary<ZDOID,Flight> actors=new Dictionary<ZDOID,Flight>();
        [ThreadStatic] private static Flight creating;
        private static double nextControl;
        private static bool clientSkip,clientStarted;
        internal static IEnumerable<Vector3> Areas=>flights.Values.Where(f=>!f.Finished).Select(f=>f.Landing);
        internal static void ClearClient(){nextControl=0;clientSkip=false;clientStarted=false;}
        internal static void Clear(){foreach(var rpc in flights.Keys.ToArray())Forget(rpc);ClearClient();}
        internal static bool First(PlayerSnapshot state)=>state!=null&&!GameDeathProgress.IsDead(state)&&state.Rows.Any(r=>r.Table=="state"&&(string)r.Values[0]=="first_spawn"&&Convert.ToBoolean(r.Values[1]));
        internal static void Grant(ZRpc rpc,Vector3 point)
        {if(!flights.ContainsKey(rpc))flights.Add(rpc,new Flight{Landing=point,Began=Time.timeAsDouble});}
        internal static bool Active(ZDOID actor)=>actors.TryGetValue(actor,out var flight)&&!flight.Finished;
        internal static void Control(ZRpc rpc,ZDOID actor,bool skip)
        {
            if(!flights.TryGetValue(rpc,out var flight)||flight.Finished||!flight.Player||flight.Player.GetZDOID()!=actor)return;
            flight.Ready=true;flight.Skip|=skip;
        }
        internal static void Forget(ZRpc rpc)
        {if(flights.TryGetValue(rpc,out var flight)){Remove(flight);flights.Remove(rpc);}}
        internal static void ForgetActor(ZDOID actor)
        {foreach(var pair in flights.Where(p=>p.Value.Player&&p.Value.Player.GetZDOID()==actor).ToArray())Forget(pair.Key);}
        private static void Remove(Flight flight)
        {
            if(flight.Player){actors.Remove(flight.Player.GetZDOID());Intro(flight.Player,false);if(flight.Player.m_body)flight.Player.m_body.isKinematic=flight.Kinematic;}
            if(flight.Bird){birds.Remove(flight.Bird);if(flight.Bird.m_nview&&flight.Bird.m_nview.IsValid())flight.Bird.m_nview.Destroy();else UnityEngine.Object.Destroy(flight.Bird.gameObject);}
        }
        internal static void Tick(ZRpc rpc,ZDO actor)
        {
            if(actor==null||!flights.TryGetValue(rpc,out var flight))return;
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;if(!player)return;
            if(!flight.Player)
            {
                flight.Player=player;flight.Kinematic=player.m_body&&player.m_body.isKinematic;actors[actor.m_uid]=flight;
                Intro(player,true);if(player.m_body)player.m_body.isKinematic=true;
            }
            if(flight.Finished)return;
            if(flight.Skip||Time.timeAsDouble-flight.Began>300){Finish(flight,true);return;}
            if(!flight.Ready||flight.Bird)return;
            var previous=creating;creating=flight;
            try
            {
                player.m_valkyrie.Load();
                try{var bird=UnityEngine.Object.Instantiate(player.m_valkyrie.Asset,flight.Landing,Quaternion.identity);bird.GetComponent<ZNetView>().HoldReferenceTo(player.m_valkyrie);}
                finally{player.m_valkyrie.Release();}
            }
            finally{creating=previous;}
        }
        private static void Finish(Flight flight,bool skip)
        {
            if(flight.Pending||flight.Finished||!flight.Player)return;
            var player=flight.Player;
            if(!ZNetScene.instance.IsAreaReady(flight.Landing))return;
            var point=skip?flight.Landing:player.transform.position;
            flight.Pending=true;
            if(!InventoryMoveGame.TimedAction(player,state=>
            {
                var rows=new List<PlayerChange>{new PlayerChange("state",false,"first_spawn",false,null,null,null),
                    new PlayerChange("spawn",false,"home",(double)flight.Landing.x,(double)flight.Landing.y,(double)flight.Landing.z),
                    new PlayerChange("spawn",false,"logout",(double)point.x,(double)point.y,(double)point.z),
                    PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)PlayerStatType.PlayerSpawn).ToString(System.Globalization.CultureInfo.InvariantCulture),1)};
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>
                {
                    flight.Pending=false;flight.Finished=true;
                    if(!player)return;
                    player.transform.position=point;Intro(player,false);
                    if(player.m_body){player.m_body.position=point;player.m_body.isKinematic=flight.Kinematic;player.m_body.linearVelocity=Vector3.zero;}
                    player.m_maxAirAltitude=point.y;GameMovementRuntime.Record(player);
                    if(flight.Bird){flight.Bird.m_droppedPlayer=true;if(flight.Bird.m_animator)flight.Bird.m_animator.SetBool("dropped",true);if(skip)flight.Bird.m_nview.Destroy();}
                });
            }))flight.Pending=false;
        }
        private static Player Passenger(Valkyrie bird)=>birds.TryGetValue(bird,out var flight)?flight.Player:Player.m_localPlayer;
        private static void Intro(Player player,bool active)
        {player.SetIntro(active);if(player.m_nview&&player.m_nview.IsValid())player.m_nview.GetZDO().Set(unchecked(438569+ZSyncAnimation.GetHash("intro")),active?1:0);}
        private static bool Waiting(Valkyrie bird)=>birds.TryGetValue(bird,out var flight)?!flight.Ready||flight.Pending:TextViewer.IsShowingIntro();
        private static void Multiplayer(ZNet net){if(!GameCreatureAuthority.Enabled)net.SetMultiplayerUsageStart();}
        internal static void ClientTick()
        {
            var player=Player.m_localPlayer;
            if(!PlayerSessionGame.Managed||!player)return;
            if(!player.InIntro())
            {if(!clientStarted&&ZNet.instance){clientStarted=true;ZNet.instance.SetMultiplayerUsageStart();}return;}
            if(TextViewer.IsShowingIntro()||Time.timeAsDouble<nextControl)return;
            nextControl=Time.timeAsDouble+.25;InventoryMoveGame.Client?.ArrivalControl(player.GetZDOID(),clientSkip);
        }
        [HarmonyPatch(typeof(Player),"SpawnValkyrie")]
        private static class ClientBird
        {private static bool Prefix()=>!PlayerSessionGame.Managed;}
        [HarmonyPatch(typeof(Game),nameof(Game.SkipIntro))]
        private static class Skip
        {
            private static bool Prefix(Game __instance)
            {
                if(!PlayerSessionGame.Managed)return true;
                clientSkip=true;
                if(!Player.m_localPlayer||!Player.m_localPlayer.InIntro())return true;
                __instance.m_queuedIntro=false;__instance.m_inIntro=false;if(TextViewer.instance)TextViewer.instance.HideIntro();
                InventoryMoveGame.Client?.ArrivalControl(Player.m_localPlayer.GetZDOID(),true);return false;
            }
        }
        [HarmonyPatch(typeof(Valkyrie),"Awake")]
        private static class Awake
        {
            private static void Prefix(Valkyrie __instance,out Valkyrie __state)
            {__state=Valkyrie.m_instance;if(creating!=null){creating.Bird=__instance;birds.Add(__instance,creating);}}
            private static void Postfix(Valkyrie __instance,Valkyrie __state)
            {if(birds.ContainsKey(__instance))Valkyrie.m_instance=__state;}
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach(var code in instructions)
                {
                    if(code.opcode==OpCodes.Ldsfld&&Equals(code.operand,AccessTools.Field(typeof(Player),nameof(Player.m_localPlayer))))
                    {var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);load.blocks.AddRange(code.blocks);yield return load;yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameArrivalRuntime),nameof(Passenger)));}
                    else yield return code;
                }
            }
        }
        [HarmonyPatch(typeof(Valkyrie),"UpdateValkyrie")]
        private static class Update
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach(var code in instructions)
                {
                    if(code.Calls(AccessTools.Method(typeof(TextViewer),nameof(TextViewer.IsShowingIntro))))
                    {var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code.labels);load.blocks.AddRange(code.blocks);yield return load;yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameArrivalRuntime),nameof(Waiting)));}
                    else if(code.Calls(AccessTools.Method(typeof(ZNet),nameof(ZNet.SetMultiplayerUsageStart))))yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameArrivalRuntime),nameof(Multiplayer))){labels=code.labels,blocks=code.blocks};
                    else yield return code;
                }
            }
        }
        [HarmonyPatch(typeof(Valkyrie),"SyncPlayer")]
        private static class Sync
        {
            private static bool Prefix(Valkyrie __instance,bool doNetworkSync)
            {
                if(!birds.TryGetValue(__instance,out var flight))return true;
                var player=flight.Player;if(!player||flight.Finished)return false;
                player.transform.rotation=__instance.m_attachPoint.rotation;
                player.transform.position=__instance.m_attachPoint.position-player.transform.TransformVector(__instance.m_attachOffset);
                if(player.m_body){player.m_body.position=player.transform.position;player.m_body.rotation=player.transform.rotation;}
                if(doNetworkSync)GameMovementRuntime.Record(player);return false;
            }
        }
        [HarmonyPatch(typeof(Valkyrie),nameof(Valkyrie.DropPlayer))]
        private static class Drop
        {private static bool Prefix(Valkyrie __instance,bool destroy){if(!birds.TryGetValue(__instance,out var flight))return true;Finish(flight,destroy);return false;}}
        [HarmonyPatch(typeof(Valkyrie),"OnDestroy")]
        private static class Destroyed
        {private static void Prefix(Valkyrie __instance){if(birds.TryGetValue(__instance,out var flight)){birds.Remove(__instance);flight.Bird=null;if(!flight.Finished)flight.Skip=true;}}}
    }
}
