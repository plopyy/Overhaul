using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class BowPoseChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> checks=new List<string>();
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add("PASS "+label);}
    public static void Run()
    {
        try{BowAnimationInspect.AfterPose=Validate;BowAnimationInspect.Run();File.WriteAllLines("../../../Tools/AugaWork/bow-pose-checks.txt",checks);}
        catch(Exception e){checks.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/bow-pose-checks.txt",checks);throw;}
        finally{BowAnimationInspect.AfterPose=null;}
    }
    static void Validate(Animator animator,List<string> report)
    {
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));var cf=bep.GetType("BepInEx.Configuration.ConfigFile");
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/AugaWork/BowTest.exe"),null,null,new string[0]});
        var config=Activator.CreateInstance(cf,new object[]{Path.GetFullPath("../../../Tools/AugaWork/bow-pose-test.cfg"),false});
        var bind=cf.GetMethods().Single(m=>m.Name=="Bind"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==4&&m.GetParameters()[3].ParameterType==typeof(string));
        var entry=bind.MakeGenericMethod(typeof(bool)).Invoke(config,new object[]{"Combat","CrouchedBowAiming",true,"test"});
        entry.GetType().GetProperty("Value").SetValue(entry,true);
        mod.GetType("Overhaul.Utility.OverhaulConfig").GetProperty("CrouchedBowAiming").SetValue(null,entry);
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=harmonyAssembly.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.bow.pose.check"});
        var feature=mod.GetType("Overhaul.CrouchedBow");
        foreach(var type in feature.GetNestedTypes(F))
        {var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);}
        checks.Add("PASS native UpdateCrouch transpiler installs");
        var poseType=mod.GetType("Overhaul.AttackLocomotionPose");object pose=null;
        try
        {
            var hips=animator.GetBoneTransform(HumanBodyBones.Hips);var spine=animator.GetBoneTransform(HumanBodyBones.Spine);
            var hand=animator.GetBoneTransform(HumanBodyBones.LeftHand);var arm=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var standing=hips.position;var armRotation=arm.rotation;var spineLocal=spine.localPosition;var controller=animator.runtimeAnimatorController;
            Capture(animator,"bow-standing.png");
            pose=Activator.CreateInstance(poseType,F,null,new object[]{animator,true},null);
            var apply=poseType.GetMethod("Apply",F);apply.Invoke(pose,new object[]{0f,0f,.1f,1f});
            Check(hips.position.y<standing.y-.2f,"crouched pelvis is lower than standing bow pose");
            Check(Quaternion.Angle(arm.rotation,armRotation)<.1f,"upper-body bow aim keeps native arm orientation");
            Check(Vector3.Distance(spine.localPosition,spineLocal)<.001f,"torso stays attached to pelvis without stretching spine");
            Check(animator.runtimeAnimatorController==controller,"original animator controller is untouched");
            Check(Vector3.Distance(hand.position,hips.position)<2,"aiming hand stays near crouched body");
            report.Add("MIXED hips="+hips.position+" standing="+standing+" hand="+hand.position);
            var sample=(Animator)poseType.GetField("sample",F).GetValue(pose);checks.Add("INFO sampled clips="+string.Join(",",sample.GetCurrentAnimatorClipInfo(0).Select(c=>c.clip.name))+" hips="+sample.GetBoneTransform(HumanBodyBones.Hips).position+" target="+hips.position+" feet delta="+Vector3.Distance(sample.GetBoneTransform(HumanBodyBones.LeftFoot).position,animator.GetBoneTransform(HumanBodyBones.LeftFoot).position));
            Capture(animator,"bow-crouched.png");
            var enter=mod.GetType("Overhaul.CrouchedBowVisual").GetMethod("EnterAim",F);
            animator.SetBool("bow_aim",false);animator.SetBool("crouching",true);
            animator.Play("Base Layer.Crouch",0,0);animator.Update(.1f);
            animator.SetBool("bow_aim",true);
            for(int i=0;i<90;i++){enter.Invoke(null,new object[]{animator});animator.Update(1f/60);apply.Invoke(pose,new object[]{0f,0f,1f/60,1f});}
            Check(animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip.name.StartsWith("Bow Aim")),"crouch first then draw enters native bow aim");
            Check(hips.position.y<standing.y-.2f,"crouch-first aiming keeps pelvis low");
            animator.SetBool("bow_aim",false);animator.SetTrigger("bow_fire");bool recoil=false;
            for(int i=0;i<60;i++){
                animator.Update(1f/60);apply.Invoke(pose,new object[]{0f,0f,1f/60,1f});
                recoil|=animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip.name=="Bow Aim Recoil");
                Check(hips.position.y<standing.y-.2f,"release frame "+i+" stays crouched");
            }
            Check(recoil,"native bow release animation still plays");
            animator.SetBool("crouching",false);animator.SetBool("bow_aim",true);animator.Play("Base Layer.Movement",0,0);
            for(int i=0;i<90;i++)animator.Update(1f/60);
            animator.SetBool("crouching",true);animator.Update(.1f);apply.Invoke(pose,new object[]{0f,0f,.1f,1f});
            Check(animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip.name.StartsWith("Bow Aim")),"draw first then crouch still aims");
            var firstFoot=animator.GetBoneTransform(HumanBodyBones.LeftFoot).position;
            for(int frame=0;frame<20;frame++){animator.Update(1f/60);apply.Invoke(pose,new object[]{2f,0f,1f/60,1f});}
            Check(Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position,firstFoot)>.02f,"crouched locomotion animates feet while aiming");
            poseType.GetMethod("Dispose").Invoke(pose,null);pose=null;animator.Update(.1f);
            Check(Mathf.Abs(hips.position.y-standing.y)<.08f,"disposing overlay restores native standing bow pose on next evaluation");
            var go=new GameObject("inactive bow player");go.SetActive(false);var player=go.AddComponent<Player>();player.m_animator=animator;
            player.m_rightItem=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData{m_itemType=ItemDrop.ItemData.ItemType.Bow,m_skillType=Skills.SkillType.Bows,m_attack=new Attack{m_bowDraw=true}}};player.m_attackDrawTime=1;
            player.m_cachedFrame=MonoUpdaters.UpdateCount;player.m_cachedAttack=true;
            Check(!(bool)feature.GetMethod("StandForAttack",F).Invoke(null,new object[]{player}),"bow release no longer suspends crouch");
            Check(!(bool)feature.GetMethod("StandForDraw",F).Invoke(null,new object[]{player}),"enabled bow draw no longer cancels crouch");
            entry.GetType().GetProperty("Value").SetValue(entry,false);
            Check((bool)feature.GetMethod("StandForAttack",F).Invoke(null,new object[]{player}),"disabled option restores native attack crouch suspension");
            Check((bool)feature.GetMethod("StandForDraw",F).Invoke(null,new object[]{player}),"false option immediately restores vanilla bow crouch cancellation");
            entry.GetType().GetProperty("Value").SetValue(entry,true);player.m_rightItem.m_shared.m_skillType=Skills.SkillType.Crossbows;
            Check((bool)feature.GetMethod("StandForDraw",F).Invoke(null,new object[]{player}),"crossbow behavior remains native");
            UnityEngine.Object.DestroyImmediate(go);checks.Add("PASS package "+mod.GetName().Version);
        }
        finally{if(pose!=null)poseType.GetMethod("Dispose").Invoke(pose,null);ht.GetMethod("UnpatchSelf").Invoke(harmony,null);}
    }
    static void Capture(Animator animator,string name)
    {
        foreach(var renderer in animator.GetComponentsInChildren<Renderer>(true))
        {
            if(!renderer.enabled)continue;
            renderer.materials=renderer.sharedMaterials.Select(m=>{var n=new Material(Shader.Find("Standard"));if(m&&m.HasProperty("_MainTex"))n.mainTexture=m.GetTexture("_MainTex");n.color=new Color(.75f,.65f,.53f);return n;}).ToArray();
        }
        var baked=new List<GameObject>();var hidden=new List<SkinnedMeshRenderer>();
        foreach(var skin in animator.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r=>r.enabled))
        {
            var mesh=new Mesh();skin.BakeMesh(mesh);mesh.RecalculateBounds();checks.Add("INFO baked "+skin.name+" vertices="+mesh.vertexCount+" bounds="+mesh.bounds+" scale="+skin.transform.lossyScale);var go=new GameObject("baked pose");go.transform.SetPositionAndRotation(skin.transform.position,skin.transform.rotation);go.transform.localScale=Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials;baked.Add(go);hidden.Add(skin);skin.enabled=false;
        }
        RenderSettings.ambientLight=new Color(.65f,.65f,.65f);
        var light=new GameObject("bow preview light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.1f;light.transform.rotation=Quaternion.Euler(35,-30,0);
        var camera=new GameObject("bow preview camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.16f,.19f,.23f);camera.fieldOfView=35;
        camera.transform.position=animator.transform.position+new Vector3(3,1.8f,3.2f);camera.transform.LookAt(animator.transform.position+Vector3.up*.95f);
        var rt=new RenderTexture(900,900,24);camera.targetTexture=rt;camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;
        var image=new Texture2D(900,900,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,900,900),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/"+name,image.EncodeToPNG());RenderTexture.active=previous;
        UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(light.gameObject);
        foreach(var go in baked){UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshFilter>().sharedMesh);UnityEngine.Object.DestroyImmediate(go);}foreach(var skin in hidden)skin.enabled=true;
    }
}
