using System;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameMovementView
    {
        private static long sequence;
        private static Vector3 correction;
        internal static void Clear(){sequence=0;correction=Vector3.zero;}
        internal static ZPackage Encode(ZDOID actor,long order,long input,Vector3 position,Quaternion rotation,Vector3 velocity,bool teleporting,bool distant)
        {var packet=new ZPackage();packet.Write(actor);packet.Write(order);packet.Write(input);packet.Write(position);packet.Write(rotation);packet.Write(velocity);packet.Write(teleporting);packet.Write(distant);return packet;}
        private static bool Finite(float number)=>!float.IsNaN(number)&&!float.IsInfinity(number);
        private static bool Finite(Vector3 vector)=>Finite(vector.x)&&Finite(vector.y)&&Finite(vector.z);
        internal static bool Receive(ZPackage packet)
        {
            var player=Player.m_localPlayer;if(!player||packet==null)return false;
            int size=packet.Size()-packet.GetPos();if(size!=70&&size!=71)return false;
            if(packet.ReadZDOID()!=player.GetZDOID())return false;
            long order=packet.ReadLong(),input=packet.ReadLong();var point=packet.ReadVector3();var rotation=packet.ReadQuaternion();var velocity=packet.ReadVector3();
            bool teleporting=packet.ReadBool(),distant=packet.ReadBool();
            bool intro=size==71&&packet.ReadBool();
            float length=Quaternion.Dot(rotation,rotation);
            if(order<=sequence||input<0||!Finite(point)||!Finite(velocity)||!Finite(length)||Mathf.Abs(length-1)>.01f)return false;
            sequence=order;
            // A host already renders the authoritative body. Remote clients keep
            // local prediction and reconcile against authenticated server poses.
            if(GameCreatureAuthority.Enabled||player.IsDead()){correction=Vector3.zero;return true;}
            bool landed=player.InIntro()&&!intro;player.SetIntro(intro);
            bool completed=player.m_teleporting&&!teleporting;
            player.m_teleporting=teleporting;player.m_distantTeleport=distant;
            if(!player.m_body)return true;
            if(teleporting||completed||intro||landed){player.transform.SetPositionAndRotation(point,rotation);player.m_body.position=point;player.m_body.rotation=rotation;player.m_body.linearVelocity=velocity;player.m_maxAirAltitude=point.y;correction=Vector3.zero;if(completed||landed){player.InvalidateCachedLiquidDepth();player.ResetCloth();}return true;}
            double age=GameMovementControl.Age(input);
            var target=point+velocity*(float)Math.Min(.15,age*.5);
            var delta=target-player.m_body.position;
            if(delta.sqrMagnitude>25)
            {player.m_body.position=target;player.m_body.rotation=rotation;player.m_body.linearVelocity=velocity;correction=Vector3.zero;}
            else correction=delta.sqrMagnitude>.0025f?delta:Vector3.zero;
            return true;
        }
        internal static void Tick()
        {
            var player=Player.m_localPlayer;if(!player||!player.m_body||!PlayerSessionGame.Managed||GameCreatureAuthority.Enabled)return;
            if(player.IsDead()||player.IsTeleporting()||player.InIntro()){correction=Vector3.zero;return;}
            if(correction.sqrMagnitude<.000001f)return;
            float blend=1-Mathf.Exp(-30*Time.deltaTime);var step=correction*blend;
            player.m_body.position+=step;correction-=step;
        }
    }
}

