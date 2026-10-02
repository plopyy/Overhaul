using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

using UnityEngine;
public static class SlopeChecks {
public static void Run(){
AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
var jump=mod.GetType("Overhaul.Patches.PlayerSlopeJumpPatch");var slide=mod.GetType("Overhaul.Patches.PlayerSlopeSlidePatch");
var ht=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.slope.checks"});var report=new List<string>();
var host=new GameObject("Slope test");host.SetActive(false);var player=host.AddComponent<Player>();player.m_body=host.AddComponent<Rigidbody>();
var mobHost=new GameObject("Creature test");mobHost.SetActive(false);var mob=mobHost.AddComponent<Character>();
try{
foreach(var t in new[]{jump,slide}){var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{t});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);}
report.Add("PASS Harmony patches installed on current native Jump and GetSlideAngle");
var normal=jump.GetMethod("JumpNormal",BindingFlags.Static|BindingFlags.NonPublic);
foreach(float angle in new[]{0f,26f,45f,49.9f,50.1f,60f}){
var n=Quaternion.Euler(angle,0,0)*Vector3.up;player.m_lastGroundNormal=n;mob.m_lastGroundNormal=n;
if((Vector3)normal.Invoke(null,new object[]{player})!=Vector3.up)throw new Exception("Player jump normal "+angle);
if((Vector3)normal.Invoke(null,new object[]{mob})!=n)throw new Exception("Creature normal "+angle);
if(player.m_lastGroundNormal!=n)throw new Exception("Ground mutated");
player.m_slippage=0;player.m_sliding=false;Vector3 velocity=Vector3.zero;
player.ApplySlide(1f,ref velocity,Vector3.zero,false);
if(player.m_sliding!=(angle>50f))throw new Exception("Sliding threshold "+angle);
report.Add("PASS slope "+angle+": vertical player impulse, creature normal preserved, native sliding="+player.m_sliding);
}
if(player.GetSlideAngle()!=50f||mob.GetSlideAngle()!=90f)throw new Exception("Angle scope");
report.Add("PASS player threshold 50 and creature threshold 90");
mobHost.name="Hen(Clone)";mob.m_body=mobHost.AddComponent<Rigidbody>();
if(mob.GetSlideAngle()!=35f)throw new Exception("Hen threshold");
foreach(float angle in new[]{34.9f,35.1f,45f}){mob.m_lastGroundNormal=Quaternion.Euler(angle,0,0)*Vector3.up;mob.m_slippage=0;mob.m_sliding=false;Vector3 v=Vector3.zero;mob.ApplySlide(1f,ref v,Vector3.zero,true);if(mob.m_sliding!=(angle>35f))throw new Exception("Hen sliding "+angle);report.Add("PASS adult hen native slide, running, angle="+angle+" sliding="+mob.m_sliding);}
mobHost.name="Chicken(Clone)";if(mob.GetSlideAngle()!=90f)throw new Exception("Chick changed");report.Add("PASS chicks retain native threshold");
File.WriteAllLines("../../../Tools/AugaWork/slope-checks.txt",report);
}finally{ht.GetMethod("UnpatchSelf").Invoke(harmony,null);UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(mobHost);}
}}