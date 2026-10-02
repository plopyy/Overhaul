using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
public static class KnifeLeapChecks
{
 const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 public static void Run(){try{BowAnimationInspect.AfterPose=Check;BowAnimationInspect.Run();}finally{BowAnimationInspect.AfterPose=null;}}
 static void Check(Animator animator,List<string> report)
 {
  var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var type=mod.GetType("Overhaul.DynamicCombat");
  var host=new GameObject("Inactive knife leap player");host.SetActive(false);var player=host.AddComponent<Player>();player.m_animator=animator;
  var previous=Player.m_localPlayer;Player.m_localPlayer=player;var lines=new List<string>();
  try{
   foreach(var trigger in new[]{"knife_secondary","dual_knives_secondary"}){
    animator.Rebind();animator.Play("Base Layer.Movement",0,0);animator.SetBool("onGround",true);animator.SetTrigger(trigger);
    for(int i=0;i<20;i++)animator.Update(1f/60);
    var attack=new Attack{m_attackAnimation=trigger};player.m_currentAttack=attack;
    type.GetField("lungingPlayer",F).SetValue(null,player);type.GetField("lungingAttack",F).SetValue(null,attack);type.GetField("lungeAnimationStarted",F).SetValue(null,true);type.GetField("lungePoseWeight",F).SetValue(null,1f);
    var hips=animator.GetBoneTransform(HumanBodyBones.Hips);var leg=animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);var position=hips.localPosition;var rotation=leg.localRotation;
    type.GetMethod("UpdateAttackLocomotionPose",F).Invoke(null,null);
    if(hips.localPosition!=position||Quaternion.Angle(rotation,leg.localRotation)>.001f||(float)type.GetField("lungePoseWeight",F).GetValue(null)!=0)throw new Exception("Native leap pose overwritten: "+trigger);
    lines.Add("PASS native hips/legs preserved and walking overlay cleared: "+trigger);
   }
   if((bool)type.GetMethod("PreservesFullBodyAttackPose",F).Invoke(null,new object[]{new Attack{m_attackAnimation="knife_stab"}}))throw new Exception("Normal dagger attack excluded");
   lines.Add("PASS ordinary dagger attack retains walking overlay eligibility");File.WriteAllLines("../../../Tools/AugaWork/knife-leap-checks.txt",lines);
  }finally{Player.m_localPlayer=previous;type.GetField("lungingPlayer",F).SetValue(null,null);type.GetField("lungingAttack",F).SetValue(null,null);UnityEngine.Object.DestroyImmediate(host);}
 }
}
