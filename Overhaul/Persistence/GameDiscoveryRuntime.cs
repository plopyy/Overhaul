using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Location searches remain native. Only their origin and the durable read
    // progress are resolved from objects actually loaded on the server.
    internal static class GameDiscoveryRuntime
    {
        internal const string Read="discovery.read";
        private static readonly HashSet<Component> stones=new HashSet<Component>();
        internal static void Clear()=>stones.Clear();
        internal static void Register(GameObject root)
        {
            if(!root||!GameCreatureAuthority.Enabled)return;
            foreach(var stone in root.GetComponentsInChildren<Component>(true))
            {
                if(!(stone is RuneStone)&&!(stone is Vegvisir)||!stones.Add(stone))continue;
                stone.gameObject.AddComponent<StoneLifetime>().Stone=stone;
            }
        }
        public sealed class StoneLifetime:MonoBehaviour
        {internal Component Stone;private void OnDestroy(){stones.Remove(Stone);}}
        private static bool Nearby(Component stone,Vector3 point,Vector3 actor)=>stone&&stone.gameObject.activeInHierarchy&&
            (stone.transform.position-point).sqrMagnitude<.0625f&&(stone.transform.position-actor).sqrMagnitude<=25;
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            if(request.Action.Amount!=1||request.Gameplay.TargetId!=0||request.Gameplay.Position==null||request.Gameplay.Position.Length!=3)
                throw new InvalidOperationException("Invalid stone interaction");
            var p=request.Gameplay.Position;var point=new Vector3(p[0],p[1],p[2]);
            var stone=stones.FirstOrDefault(candidate=>Nearby(candidate,point,actor.GetPosition())&&
                (request.Gameplay.Variant==0?candidate is RuneStone:request.Gameplay.Variant==1&&candidate is Vegvisir));
            if(!stone)throw new InvalidOperationException("Stone is unavailable");
            var changes=new List<PlayerChange>();string global=null;
            if(stone is RuneStone rune)
            {
                var text=rune.GetRandomText();string label=text?.m_label??rune.m_label,value=text?.m_text??rune.m_text;
                if(!string.IsNullOrEmpty(label))changes.Add(new PlayerChange("knowledge",false,"texts",label,value??""));
            }
            else if(stone is Vegvisir vegvisir)
            {
                if(!string.IsNullOrEmpty(vegvisir.m_setsPlayerKey))changes.Add(new PlayerChange("knowledge",false,"uniques",vegvisir.m_setsPlayerKey,""));
                global=vegvisir.m_setsGlobalKey;
            }
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>
            {
                // No inventory is consumed: world progress uses the normal progressive
                // world writer, while character knowledge has already committed.
                if(!string.IsNullOrEmpty(global)&&ZoneSystem.instance)ZoneSystem.instance.SetGlobalKey(global);
            });
        }
        internal static bool LocationAllowed(Vector3 actor,string name,Vector3 point,string pinName,int pinType,bool showMap,bool discoverAll)
        {
            foreach(var stone in stones)
            {
                if(!Nearby(stone,point,actor))continue;
                if(stone is RuneStone rune&&name==rune.m_locationName&&pinName==rune.m_pinName&&pinType==(int)rune.m_pinType&&showMap==rune.m_showMap&&!discoverAll)return true;
                if(stone is Vegvisir veg&&veg.m_locations.Any(location=>name==location.m_locationName&&pinName==location.m_pinName&&pinType==(int)location.m_pinType&&showMap==location.m_showMap&&discoverAll==location.m_discoverAll))return true;
            }
            return false;
        }
        [HarmonyPatch(typeof(Game),"RPC_DiscoverClosestLocation")]
        private static class LocationRequest
        {
            private static bool Prefix(long sender,string name,Vector3 point,string pinName,int pinType,bool showMap,bool discoverAll)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                var peer=sender==ZNet.GetUID()?null:ZNet.instance.GetPeer(sender);
                var actor=sender==ZNet.GetUID()?Player.m_localPlayer?.m_nview?.GetZDO():peer==null?null:PlayerSessionGame.Actor(peer.m_rpc);
                return actor!=null&&LocationAllowed(actor.GetPosition(),name,point,pinName,pinType,showMap,discoverAll);
            }
        }
        [HarmonyPatch(typeof(RuneStone),nameof(RuneStone.Interact))]
        private static class RuneIntent
        {private static void Prefix(RuneStone __instance,Humanoid character,bool hold){if(!hold)Send(__instance,character,0);}}
        [HarmonyPatch(typeof(Vegvisir),nameof(Vegvisir.Interact))]
        private static class VegvisirIntent
        {private static void Prefix(Vegvisir __instance,Humanoid character,bool hold){if(!hold)Send(__instance,character,1);}}
        private static void Send(Component stone,Humanoid character,int kind)
        {
            if(!PlayerSessionGame.Managed||character!=Player.m_localPlayer)return;
            var point=stone.transform.position;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Read,Variant=kind,Position=new[]{point.x,point.y,point.z}});
        }
    }
}
