using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    internal static class CrouchedBow
    {
        internal static bool Enabled=>Utility.OverhaulConfig.CrouchedBowAiming?.Value==true;
        internal static bool Bow(Player player)=>player&&player.GetCurrentWeapon()?.m_shared.m_skillType==Skills.SkillType.Bows;
        internal static bool StandForDraw(Player player)=>player.IsDrawingBow()&&!(Enabled&&Bow(player));
        internal static bool StandForAttack(Player player)=>player.InAttack()&&!(Enabled&&Bow(player));
        internal static bool Visual(Player player)=>Enabled&&player&&player.m_animator&&
            (Bow(player)||player.m_animator.GetInteger("statei")== (int)ItemDrop.ItemData.AnimationState.Bow)&&
            player.m_animator.GetBool("crouching")&&!player.IsDead()&&
            !player.IsSwimming()&&!player.IsFlying()&&!player.IsAttached()&&!player.InDodge()&&!player.IsStaggering();

        [HarmonyPatch(typeof(Player),"UpdateCrouch")]
        private static class Crouch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int replaced=0, attacks=0;
                foreach(var code in instructions)
                {
                    if(code.Calls(AccessTools.Method(typeof(Character),nameof(Character.IsDrawingBow))))
                    {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(CrouchedBow),nameof(StandForDraw));replaced++;}
                    if(code.Calls(AccessTools.Method(typeof(Character),nameof(Character.InAttack))))
                    {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(CrouchedBow),nameof(StandForAttack));attacks++;}
                    yield return code;
                }
                if(replaced!=1||attacks!=1)throw new InvalidOperationException("Player.UpdateCrouch bow/attack check changed");
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.IsCrouching))]
        private static class Sneaking
        { private static void Postfix(Player __instance,ref bool __result){if(Visual(__instance))__result=true;} }

        [HarmonyPatch(typeof(Player),"Awake")]
        private static class Attach
        { private static void Postfix(Player __instance){if(!__instance.GetComponent<CrouchedBowVisual>())__instance.gameObject.AddComponent<CrouchedBowVisual>();} }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class CrouchedBowVisual:MonoBehaviour
    {
        private static readonly int CrouchState=Animator.StringToHash("Base Layer.Crouch");
        private static readonly int MovementState=Animator.StringToHash("Base Layer.Movement");
        // Native Crouch has no transition to bow aiming. Movement has that transition.
        // Leave once, then let the native controller handle aiming and the shot events.
        internal static void EnterAim(Animator animator)
        {
            if(animator.GetBool("bow_aim")&&!animator.IsInTransition(0)&&
                animator.GetCurrentAnimatorStateInfo(0).fullPathHash==CrouchState)
                animator.CrossFadeInFixedTime(MovementState,0.08f,0);
        }
        private Player player;
        private AttackLocomotionPose pose;
        private float weight;
        private bool failed;
        private void Awake()=>player=GetComponent<Player>();
        private void LateUpdate()
        {
            if(!CrouchedBow.Enabled){Release();failed=false;return;}
            if(!CrouchedBow.Visual(player)){weight=0;return;}
            if(failed)return;
            try
            {
                EnterAim(player.m_animator);
                if(pose!=null&&pose.Target!=player.m_animator)Release();
                if(pose==null)pose=new AttackLocomotionPose(player.m_animator,true);
                weight=Mathf.MoveTowards(weight,1,Time.deltaTime/.12f);
                pose.Apply(player.m_animator.GetFloat("forward_speed"),player.m_animator.GetFloat("sideway_speed"),Time.deltaTime,weight);
            }
            catch(Exception error){Release();failed=true;Debug.LogWarning("[Overhaul] Crouched bow pose unavailable: "+error.Message);}
        }
        private void Release(){pose?.Dispose();pose=null;weight=0;}
        private void OnDisable()=>Release();
        private void OnDestroy()=>Release();
    }
}
