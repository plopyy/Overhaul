using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Reuse the native grapple movement; its camera, input and local-player
    // singleton assumptions are redirected only during server simulation.
    internal static class GameGrapplingRuntime
    {
        private const string Owner="overhaul_grapple_player";
        private static readonly Dictionary<ZDOID,GrapplingPoint> points=new Dictionary<ZDOID,GrapplingPoint>();
        private static GrapplingPoint visual;
        private static bool visualSecondary;
        [ThreadStatic] private static GrapplingPoint current;
        internal static StatusEffect[] Effects(Player player,StatusEffect[] effects)
        {
            if(!player||!player.m_nview||!player.m_nview.IsValid()||!points.TryGetValue(player.GetZDOID(),out var point)||!point||!point.m_se)return effects;
            return effects.Where(e=>e.NameHash()!=point.m_se.NameHash()).Concat(new[]{point.m_se}).ToArray();
        }
        internal static void Close(ZDOID actor)
        {if(points.TryGetValue(actor,out var point)&&point)point.Break(true);}
        internal static void Forget(ZDOID actor)
        {
            if(!points.TryGetValue(actor,out var point))return;
            points.Remove(actor);GameMovementRuntime.InvalidateEffects(actor);
            if(point&&point.m_character is Player player)player.m_grappling=0;
            if(point&&point.m_nview&&point.m_nview.IsValid()&&point.m_nview.IsOwner()&&ZNetScene.instance)ZNetScene.instance.Destroy(point.gameObject);
        }
        private static void Run(GrapplingPoint point,Player player,PlayerSnapshot state,Action action)
        {
            var previous=current;current=point;
            try{GameCombatContext.Run(player,state,null,null,action);}
            finally{current=previous;}
        }
        private static Player Local()=>GameCreatureAuthority.Enabled?null:Player.m_localPlayer;
        private static bool Button(string name)=>current&&current.m_character is Player player
            ?GameBlockControl.Held(player.GetZDOID()):ZInput.GetButton(name);
        private static float FallSpeed()=>current&&current.m_grappledSE?current.m_grappledSE.m_maxMaxFallSpeed:GrapplingPoint.m_seFallSpeed;
        private static void Fov(GameCamera camera,float target,float inertia){if(!GameCreatureAuthority.Enabled&&camera)camera.SetTempFOV(target,inertia);}
        private static void ResetFov(GameCamera camera){if(!GameCreatureAuthority.Enabled&&camera)camera.ResetTempFOV();}
        private static void Visual(GrapplingPoint point)
        {
            if(!Player.m_localPlayer||point.m_character!=Player.m_localPlayer)return;
            Player.m_localPlayer.m_grappling=.2f;
            if(!point.GetComponent<ViewCleanup>())point.gameObject.AddComponent<ViewCleanup>().Point=point;
            if(visual==point&&visualSecondary==point.m_secondary)return;
            visual=point;visualSecondary=point.m_secondary;
            if(GameCamera.instance){if(point.m_secondary)GameCamera.instance.ResetTempFOV();else if(point.m_FOVTarget!=0)GameCamera.instance.SetTempFOV(point.m_FOVTarget,point.m_FOVInertia);}
        }
        public sealed class ViewCleanup:MonoBehaviour
        {
            internal GrapplingPoint Point;
            private void OnDestroy()
            {
                if(visual!=Point)return;
                visual=null;if(Player.m_localPlayer)Player.m_localPlayer.m_grappling=0;
                if(GrapplingPoint.m_localGrappler==Point)GrapplingPoint.m_localGrappler=null;
                if(GameCamera.instance)GameCamera.instance.ResetTempFOV();
            }
        }
        private static StatusEffect Add(SEMan manager,StatusEffect definition,bool reset,int level,float skill,short variant)
        {
            if(!current)return manager.AddStatusEffect(definition,reset,level,skill,variant);
            var effect=definition.Clone();effect.m_startEffectInstances=null;effect.m_character=manager.m_character;
            effect.m_time=0;effect.SetLevel(level,skill);return effect;
        }
        [HarmonyPatch]
        private static class NativeCalls
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {foreach(string name in new[]{"Activate","Update","Break","Deactivate"})yield return AccessTools.Method(typeof(GrapplingPoint),name);}
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                foreach(var code in codes)
                {
                    string replacement=null;
                    if(code.opcode==OpCodes.Ldsfld&&code.operand is FieldInfo field)
                    {
                        if(field==AccessTools.Field(typeof(Player),nameof(Player.m_localPlayer)))replacement=nameof(Local);
                        else if(field==AccessTools.Field(typeof(GrapplingPoint),"m_seFallSpeed"))replacement=nameof(FallSpeed);
                    }
                    else if(code.operand is MethodInfo method)
                    {
                        if(method==AccessTools.Method(typeof(ZInput),nameof(ZInput.GetButton),new[]{typeof(string)}))replacement=nameof(Button);
                        else if(method==AccessTools.Method(typeof(GameCamera),nameof(GameCamera.SetTempFOV)))replacement=nameof(Fov);
                        else if(method==AccessTools.Method(typeof(GameCamera),nameof(GameCamera.ResetTempFOV)))replacement=nameof(ResetFov);
                        else if(method==AccessTools.Method(typeof(SEMan),nameof(SEMan.AddStatusEffect),new[]{typeof(StatusEffect),typeof(bool),typeof(int),typeof(float),typeof(short)}))replacement=nameof(Add);
                    }
                    if(replacement!=null)yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameGrapplingRuntime),replacement)){labels=code.labels,blocks=code.blocks};
                    else yield return code;
                }
            }
        }
        [HarmonyPatch(typeof(GrapplingPoint),nameof(GrapplingPoint.Activate))]
        private static class Activate
        {
            private static bool Prefix(GrapplingPoint __instance,Character character)
            {
                if(!GameCreatureAuthority.Enabled||current==__instance)return true;
                if(!(character is Player player)||!__instance.m_grappledSE||!__instance.m_nview||!__instance.m_nview.IsValid())return false;
                var actor=player.GetZDOID();var state=InventoryMoveGame.State(actor);if(state==null||GameDeathProgress.IsDead(state))return false;
                if(points.TryGetValue(actor,out var old)&&old&&old!=__instance)old.Break(true);
                __instance.m_nview.GetZDO().Set(Owner,actor);
                Run(__instance,player,state,()=>__instance.Activate(player));
                points[actor]=__instance;player.m_grappling=.2f;GameMovementRuntime.InvalidateEffects(actor);GameMovementRuntime.Record(player);
                return false;
            }
        }
        [HarmonyPatch(typeof(GrapplingPoint),"FindCharacter")]
        private static class Find
        {
            private static bool Prefix(GrapplingPoint __instance)
            {
                if(!GameCreatureAuthority.Enabled&&!PlayerSessionGame.Managed)return true;
                if(!__instance.m_nview||!__instance.m_nview.IsValid()||!ZNetScene.instance)return false;
                var id=__instance.m_nview.GetZDO().GetZDOID(Owner);if(id.IsNone())return false;
                var go=ZNetScene.instance.FindInstance(id);var player=go?go.GetComponent<Player>():null;
                if(player)__instance.Activate(player);return false;
            }
        }
        [HarmonyPatch(typeof(GrapplingPoint),"Update")]
        private static class Update
        {
            private static bool Prefix(GrapplingPoint __instance)
            {
                if(current==__instance)return true;
                if(!GameCreatureAuthority.Enabled)
                {
                    if(PlayerSessionGame.Managed)
                    {
                        if(!__instance.m_character)__instance.FindCharacter();
                        if(__instance.m_nview&&__instance.m_nview.IsValid())__instance.m_secondary=__instance.m_nview.GetZDO().GetBool("overhaul_grapple_secondary",__instance.m_secondary);
                        Visual(__instance);
                    }
                    return true;
                }
                if(!(__instance.m_character is Player player))
                {
                    __instance.FindCharacter();
                    if(!__instance.m_character&&__instance.m_nview&&__instance.m_nview.IsValid()&&__instance.m_nview.IsOwner())ZNetScene.instance.Destroy(__instance.gameObject);
                    return false;
                }
                var state=InventoryMoveGame.State(player.GetZDOID());
                if(state==null||player.IsDead()||player.IsTeleporting()){__instance.Break(true);return false;}
                Run(__instance,player,state,()=>__instance.Update());
                if(points.TryGetValue(player.GetZDOID(),out var active)&&active==__instance)
                {
                    player.m_grappling=.2f;
                    Visual(__instance);
                    if(player.m_grapplingStaminaDrain>0&&!player.IsOnGround())InventoryMoveGame.SpendStamina(player.GetZDOID(),Time.deltaTime*player.m_grapplingStaminaDrain,player.m_staminaRegenDelay);
                }
                GameMovementRuntime.Record(player);return false;
            }
        }
        [HarmonyPatch(typeof(GrapplingPoint),"Deactivate")]
        private static class Deactivate
        {
            private static void Postfix(GrapplingPoint __instance)
            {if(GameCreatureAuthority.Enabled&&__instance.m_nview&&__instance.m_nview.IsValid())__instance.m_nview.GetZDO().Set("overhaul_grapple_secondary",true);}
        }
        [HarmonyPatch(typeof(GrapplingPoint),nameof(GrapplingPoint.Break))]
        private static class Break
        {
            private static bool Prefix(GrapplingPoint __instance,bool early)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                if(!(__instance.m_character is Player player))return true;
                if(current==__instance)
                {
                    var actor=player.GetZDOID();
                    if(points.TryGetValue(actor,out var active)&&active==__instance){points.Remove(actor);player.m_grappling=0;GameMovementRuntime.InvalidateEffects(actor);}
                    return true;
                }
                var state=InventoryMoveGame.State(player.GetZDOID());if(state==null){Forget(player.GetZDOID());return false;}
                Run(__instance,player,state,()=>__instance.Break(early));return false;
            }
            private static void Postfix(GrapplingPoint __instance)
            {
                if(!GameCreatureAuthority.Enabled||!(__instance.m_character is Player player))return;
                var actor=player.GetZDOID();
                if(points.TryGetValue(actor,out var active)&&active==__instance){points.Remove(actor);player.m_grappling=0;GameMovementRuntime.InvalidateEffects(actor);}
                if(current!=__instance||!__instance.m_se)return;
                var effect=__instance.m_se.Clone();
                InventoryMoveGame.TimedAction(player,state=>new PlayerActionPlan(new PlayerWorldAction(
                    new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,GameStatusCodec.Delta(state,effect,ZDOID.None)),
                    new Dictionary<long,ObjectRecord>()),()=>{}));
            }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.IsItemTypeEquiped))]
        private static class Equipped
        {
            private static bool Prefix(Humanoid __instance,ItemDrop.ItemData item,ref bool __result)
            {if(!GameCombatContext.Matches(__instance))return true;__result=GameCombatContext.Current.Equipment.Any(i=>i.m_shared.m_name==item.m_shared.m_name);return false;}
        }
    }
}
