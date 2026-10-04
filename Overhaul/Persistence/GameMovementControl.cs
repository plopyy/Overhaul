using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Movement input is transient. Only server-simulated positions will be
    // persisted; receiving a control packet never writes a client position.
    internal static class GameMovementControl
    {
        internal sealed class Input
        {
            internal long Sequence;
            internal Vector3 Move,Look;
            internal bool Run,Walk,Crouch;
            internal double Seen;
        }
        private static readonly Dictionary<ZDOID,Input> inputs=new Dictionary<ZDOID,Input>();
        private static long sequence;
        private static double next;
        private static Input last;
        private static readonly long[] sentSequence=new long[64];
        private static readonly double[] sentTime=new double[64];
        internal static double Age(long number)
        {int slot=(int)(number&63);return number>0&&sentSequence[slot]==number?Math.Max(0,Time.timeAsDouble-sentTime[slot]):0;}
        internal static void Forget(ZDOID actor){inputs.Remove(actor);GameMovementRuntime.Forget(actor);}
        internal static void ClearClient(){sequence=0;next=0;last=null;Array.Clear(sentSequence,0,64);GameMovementView.Clear();}
        internal static void Clear(){inputs.Clear();ClearClient();GameMovementRuntime.Clear();}
        internal static Input Read(ZDOID actor)
        {
            if(!inputs.TryGetValue(actor,out var value))return null;
            bool stale=Time.timeAsDouble-value.Seen>.5;
            return new Input{Sequence=value.Sequence,Move=stale?Vector3.zero:value.Move,Look=value.Look,Run=!stale&&value.Run,Walk=value.Walk,Crouch=value.Crouch,Seen=value.Seen};
        }
        internal static bool Receive(ZDO actor,ZPackage packet)
        {
            if(actor==null||packet==null||packet.Size()-packet.GetPos()!=48)return false;
            if(packet.ReadZDOID()!=actor.m_uid)return false;
            var state=InventoryMoveGame.State(actor.m_uid);if(state==null||GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0)return false;
            long number=packet.ReadLong();Vector3 move=packet.ReadVector3(),look=packet.ReadVector3();int flags=packet.ReadInt();
            if(number<=0||(flags&~7)!=0||float.IsNaN(move.sqrMagnitude)||float.IsInfinity(move.sqrMagnitude)||move.sqrMagnitude>1.0001f||Mathf.Abs(move.y)>.001f||
                float.IsNaN(look.sqrMagnitude)||float.IsInfinity(look.sqrMagnitude)||Mathf.Abs(look.sqrMagnitude-1)>.01f)return false;
            if(inputs.TryGetValue(actor.m_uid,out var previous)&&number<=previous.Sequence)return false;
            inputs[actor.m_uid]=new Input{Sequence=number,Move=move,Look=look.normalized,Run=(flags&1)!=0,Walk=(flags&2)!=0,Crouch=(flags&4)!=0,Seen=Time.timeAsDouble};return true;
        }
        internal static ZPackage Encode(ZDOID actor,long number,Vector3 move,Vector3 look,bool run,bool walk,bool crouch)
        {
            var package=new ZPackage();package.Write(actor);package.Write(number);package.Write(move);package.Write(look);package.Write((run?1:0)|(walk?2:0)|(crouch?4:0));return package;
        }
        internal static void ClientTick()
        {
            var player=Player.m_localPlayer;if(!player||!PlayerSessionGame.Managed||InventoryMoveGame.Client==null)return;
            var move=player.IsDead()?Vector3.zero:player.m_moveDir;move.y=0;move=Vector3.ClampMagnitude(move,1);
            var look=player.GetLookDir().normalized;if(look.sqrMagnitude<.99f)look=player.transform.forward;
            bool run=player.m_run&&!player.IsDead(),walk=player.m_walk,crouch=player.m_crouchToggled;
            // Direction changes are sampled at 20 Hz too: analogue input must
            // not turn this heartbeat into one packet per rendered frame.
            if(last!=null&&Time.timeAsDouble<next)return;
            last=new Input{Move=move,Look=look,Run=run,Walk=walk,Crouch=crouch};
            ++sequence;int slot=(int)(sequence&63);sentSequence[slot]=sequence;sentTime[slot]=Time.timeAsDouble;
            InventoryMoveGame.Client.MovementControl(Encode(player.GetZDOID(),sequence,move,look,run,walk,crouch));next=Time.timeAsDouble+.05;
        }
        [HarmonyPatch(typeof(Player),nameof(Player.SetControls))]
        private static class Capture
        {private static void Postfix(Player __instance){if(__instance==Player.m_localPlayer)ClientTick();}}
    }
}
