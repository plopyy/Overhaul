using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class PieceRelocation
    {
        internal const string RequestRpc="Overhaul_MovePiece", ReplyRpc="Overhaul_MovePieceReply";
        internal static readonly int RevisionKey="overhaul_piece_move_revision".GetStableHashCode();
        internal const float Reach=10f;
        static readonly Vector3[] ContactOffsets={Vector3.down,Vector3.up,Vector3.left,Vector3.right,Vector3.forward,Vector3.back};
        static readonly System.Reflection.EventInfo TerrainChanged=typeof(Heightmap).GetEvent("m_clearConnectedWearNTearCache");

        internal static bool Functional(Piece p)
        {
            if(!p || p.GetComponent<Door>() || p.GetComponent<Ship>() || p.GetComponent<Vagon>() || p.GetComponent<TerrainModifier>() || p.GetComponent<TerrainOp>())return false;
            return p.GetComponent<CraftingStation>() || p.GetComponent<StationExtension>() || p.GetComponent<Container>() ||
                p.GetComponent<Smelter>() || p.GetComponent<Fermenter>() || p.GetComponent<CookingStation>() ||
                p.GetComponent<Fireplace>() || p.GetComponent<ItemStand>() || p.GetComponent<ArmorStand>() ||
                p.GetComponent<Beehive>() || p.GetComponent<SapCollector>() || p.GetComponent<WispSpawner>() ||
                p.GetComponent<Incinerator>() || p.GetComponent<Turret>() || p.GetComponent<Trap>() ||
                p.GetComponent<ShieldGenerator>() || p.GetComponentInChildren<Light>(true);
        }
        internal static bool Eligible(Piece p)=>Functional(p)&&p.m_nview&&p.m_nview.IsValid()&&p.IsPlacedByPlayer();
        internal static bool Ward(Vector3 point,long player)
        {
            bool inside=false;
            foreach(var area in PrivateArea.m_allAreas)
            {
                if(!area||!area.IsEnabled()||!area.IsInside(point,0))continue;
                inside=true;
                if(area.m_piece.GetCreator()==player||area.IsPermitted(player))return true;
            }
            return !inside;
        }
        internal static bool Access(Piece p,long player,long peer=0,string token=null)
        {
            if(!Eligible(p)||p.m_nview.GetZDO().GetFloat(ZDOVars.s_health,1)<=0||
                Location.IsInsideNoBuildLocation(p.transform.position)||!Ward(p.transform.position,player)||
                (MoveReservation.Busy(p)&&!MoveReservation.Own(p,peer,token)))return false;
            foreach(var c in p.GetComponentsInChildren<Container>(true))
                if(!ChestAccess.Allows(c,player)||!c.CheckAccess(player)||c.m_inUse||ChestAccess.Leased(c)||
                    (ChestAccess.Data(c)?.GetInt(ZDOVars.s_inUse,0)??0)!=0)return false;
            var station=p.GetComponent<CraftingStation>();
            if(station&&station.m_useTimer<1f)return false;
            return !Player.GetAllPlayers().Any(x=>x && x.IsAttached() && Vector3.Distance(x.transform.position,p.transform.position)<3f);
        }
        internal static bool Finite(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsNaN(v.y)&&!float.IsNaN(v.z)&&
            !float.IsInfinity(v.x)&&!float.IsInfinity(v.y)&&!float.IsInfinity(v.z);
        internal static bool Near(Piece p,Vector3 point)
        {
            return p.GetComponentsInChildren<Collider>().Any(c=>c.enabled&&!c.isTrigger&&
                Vector3.Distance(point,c.bounds.ClosestPoint(point))<=Reach);
        }
        internal static bool Destination(Piece p,Vector3 point,Quaternion rotation,long player)
        {
            if(!Finite(point)||!Finite(new Vector3(rotation.x,rotation.y,rotation.z))||float.IsNaN(rotation.w)||float.IsInfinity(rotation.w))return false;
            float magnitude=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            if(Mathf.Abs(magnitude-1f)>.02f||Vector3.Dot(rotation*Vector3.up,Vector3.up)<.99f)return false;
            if(!Ward(point,player)||Location.IsInsideNoBuildLocation(point))return false;
            if(p.m_onlyInBiome!=Heightmap.Biome.None&&(Heightmap.FindBiome(point)&p.m_onlyInBiome)==0)return false;
            var extension=p.GetComponent<StationExtension>();
            if(extension&&!extension.FindClosestStationInRange(point))return false;
            int mask=LayerMask.GetMask("Default","static_solid","piece","terrain","character","character_net","vehicle");
            Quaternion delta=rotation*Quaternion.Inverse(p.transform.rotation);
            bool supported=false;
            foreach(var shape in p.GetComponentsInChildren<Collider>())
            {
                if(!shape.enabled||shape.isTrigger)continue;
                // Compute each child collider at the proposed pose, without moving the live object.
                Vector3 at=point+delta*(shape.transform.position-p.transform.position);
                Quaternion facing=delta*shape.transform.rotation;
                var bounds=shape.bounds;
                Vector3 center=point+delta*(bounds.center-p.transform.position);
                float radius=bounds.extents.magnitude+.15f;
                foreach(var other in Physics.OverlapSphere(center,radius,mask,QueryTriggerInteraction.Ignore))
                {
                    if(other.transform.IsChildOf(p.transform))continue;
                    if(Physics.ComputePenetration(shape,at,facing,other,other.transform.position,other.transform.rotation,out var direction,out float depth)&&depth>.15f)return false;
                    // Surface contact in any direction also permits wall and ceiling fixtures.
                    if(!other.GetComponentInParent<Character>()&&!other.attachedRigidbody)
                        foreach(var offset in ContactOffsets)
                            if(Physics.ComputePenetration(shape,at+offset*.12f,facing,other,other.transform.position,other.transform.rotation,out _,out _))
                            {supported=true;break;}
                }
            }
            return supported;
        }
        internal static bool Commit(Piece p,long sender,ZDOID actorId,Vector3 origin,int revision,Vector3 point,Quaternion rotation,string token)
        {
            if(!Eligible(p)||!p.m_nview.IsOwner()||!MoveReservation.Own(p,sender,token))return false;
            var actor=ChestAccess.Actor(sender,actorId);
            if(actor==null||actor.GetFloat(ZDOVars.s_health,0)<=0)return false;
            var data=p.m_nview.GetZDO();long user=actor.GetLong(ZDOVars.s_playerID,0);
            if(data.GetInt(RevisionKey,0)!=revision||Vector3.Distance(p.transform.position,origin)>.05f||
                !Near(p,actor.GetPosition())||Vector3.Distance(actor.GetPosition(),point)>Reach||
                Vector3.Distance(origin,point)>Reach*2||!Access(p,user,sender,token)||!Destination(p,point,rotation,user))return false;
            // Only transform fields change. The same ZDO and components retain inventories,
            // fuel, queues, progress, attached display items, health and ownership settings.
            data.SetPosition(point);data.SetRotation(rotation);data.Set(RevisionKey,revision+1);
            Apply(p,point,rotation);
            return true;
        }
        internal static void Apply(Piece p,Vector3 point,Quaternion rotation)
        {
            p.transform.SetPositionAndRotation(point,rotation);
            Physics.SyncTransforms();
            foreach(var w in p.GetComponentsInChildren<WearNTear>())
            {
                w.ClearCachedSupport();w.SetupColliders();w.m_biome=Heightmap.Biome.None;w.m_heightmap=null;
                if(w.m_connectedHeightMap)TerrainChanged.RemoveEventHandler(w.m_connectedHeightMap,new Action(w.ClearCachedSupport));
                w.m_connectedHeightMap=Heightmap.FindHeightmap(point);
                if(w.m_connectedHeightMap)TerrainChanged.AddEventHandler(w.m_connectedHeightMap,new Action(w.ClearCachedSupport));
            }
            foreach(var station in CraftingStation.m_allStations)if(station)station.m_updateExtensionTimer=2f;
            if(p.GetComponent<PrivateArea>())foreach(var area in PrivateArea.m_allAreas)if(area)area.m_connectionUpdateTime=float.NegativeInfinity;
            var extension=p.GetComponent<StationExtension>();if(extension)extension.StopConnectionEffect();
        }
        internal static void Register(Piece p)
        {
            if(!p.m_nview||!p.m_nview.IsValid()||!Functional(p))return;
            MoveReservation.Register(p);
            if(!p.GetComponent<RelocatedPieceSync>())p.gameObject.AddComponent<RelocatedPieceSync>().Piece=p;
            p.m_nview.Register<ZPackage>(RequestRpc,(sender,packet)=>
            {
                if(!p.m_nview.IsOwner())return;
                int request=0;bool ok=false;
                try
                {
                    var actor=packet.ReadZDOID();request=packet.ReadInt();var origin=packet.ReadVector3();int revision=packet.ReadInt();
                    var point=packet.ReadVector3();var rotation=packet.ReadQuaternion();string token=packet.ReadString();
                    ok=Commit(p,sender,actor,origin,revision,point,rotation,token);
                }
                catch(Exception error){Debug.LogWarning("[Overhaul] Move request rejected: "+error.Message);}
                p.m_nview.InvokeRPC(sender,ReplyRpc,request,ok);
            });
            p.m_nview.Register<int,bool>(ReplyRpc,(sender,request,ok)=>
            {if(sender==p.m_nview.GetZDO().GetOwner())RelocationClient.Reply(p,request,ok);});
        }
        [HarmonyPatch(typeof(Piece),"Awake")]
        private static class Setup {private static void Postfix(Piece __instance)=>Register(__instance);}
    }
    internal sealed class RelocatedPieceSync:MonoBehaviour
    {
        internal Piece Piece;
        int revision=-1;
        void LateUpdate()
        {
            if(!Piece||!Piece.m_nview||!Piece.m_nview.IsValid())return;
            var data=Piece.m_nview.GetZDO();int current=data.GetInt(PieceRelocation.RevisionKey,0);
            if(current==revision)return;
            revision=current;
            if(current>0)PieceRelocation.Apply(Piece,data.GetPosition(),data.GetRotation());
        }
    }
}
