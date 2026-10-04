using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameAttackRuntime
    {
        internal const string Start="combat.start";
        private sealed class Running
        {
            internal Player Player;
            internal Attack Attack;
            internal ItemDrop.ItemData Weapon,Ammo;
            internal string Identity;
            internal bool Secondary,Pending,Triggered;
            internal float Began;
            internal AnimatorCullingMode Culling;
        }
        private static readonly Dictionary<ZDOID,Running> running=new Dictionary<ZDOID,Running>();
        [ThreadStatic] private static bool executing,starting;
        private static bool Fishing(ItemDrop.ItemData item)=>item!=null && PlayerFishingCastGame.FloatPrefab(item.m_shared.m_attack?.m_attackProjectile);
        private static bool Managed(Player player)=>player && player.m_nview && player.m_nview.IsValid() && InventoryMoveGame.State(player.GetZDOID())!=null;
        private static bool Active(Attack attack)=>executing && GameCombatContext.Matches(attack.m_character);
        internal static void Forget(ZDOID actor)
        {
            if(!running.TryGetValue(actor,out var current))return;
            running.Remove(actor);current.Attack.Abort();
            if(current.Player && current.Player.m_animator)current.Player.m_animator.cullingMode=current.Culling;
        }
        internal static void Clear(){foreach(var key in running.Keys.ToArray())Forget(key);}
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            if(request.Gameplay.Definition!=Start || request.Action.Amount!=1)throw new InvalidOperationException("Invalid combat intent");
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!player || player.IsDead() || player.IsTeleporting() || player.InIntro() || player.InDodge() || player.IsStaggering() || player.InMinorAction())
                throw new InvalidOperationException("Character cannot start this attack");
            if(running.TryGetValue(actor.m_uid,out var previous) && (!previous.Attack.IsDone() && player.InAttack() || previous.Pending))
                throw new InvalidOperationException("Previous server attack is still active");
            int slot=request.Action.FromY*256+request.Action.FromX;
            var preview=GameAttackInventory.Trigger(snapshot,slot,request.Gameplay.Alternate,request.Action.Operation);var attack=preview.Definition;
            if(attack.m_attackType!=Attack.AttackType.Horizontal && attack.m_attackType!=Attack.AttackType.Vertical && attack.m_attackType!=Attack.AttackType.Area ||
                attack.m_bowDraw || attack.m_requiresReload || attack.m_loopingAttack || attack.m_attackUseAdrenaline!=0 || attack.m_selfDamage!=0 || attack.m_attackKillsSelf)
                throw new InvalidOperationException("This attack requires its additional server combat phase");
            if(!preview.Weapon.m_customData.TryGetValue(GameEquipmentWear.Identity,out var identity))
                throw new InvalidOperationException("Weapon identity has not been confirmed");
            var q=request.Gameplay.Rotation;var rotation=new Quaternion(q[0],q[1],q[2],q[3]);
            if(Mathf.Abs(Quaternion.Dot(rotation,rotation)-1)>.01f)throw new InvalidOperationException("Invalid attack aim");
            GameAttackResources.ValidateStart(snapshot,preview.Weapon,attack);
            var batch=new PlayerBatch(request.Action.Operation,snapshot.Revision,GameAttackResources.Spend(snapshot,preview.Weapon,attack,false));
            return new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>()),()=>
            {
                if(!player || !Managed(player))return;
                Forget(actor.m_uid);
                var current=new Running{Player=player,Attack=attack,Weapon=preview.Weapon,Ammo=preview.Ammo,Identity=identity,
                    Secondary=request.Gameplay.Alternate,Began=Time.time,Culling=player.m_animator.cullingMode};
                running[actor.m_uid]=current;player.m_animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                player.m_lookDir=rotation*Vector3.forward;
                InContext(current,snapshot,()=>
                {
                    starting=true;
                    try
                    {
                        if(!attack.Start(player,player.m_body,player.m_zanim,player.m_animEvent,player.m_visEquipment,current.Weapon,player.m_previousAttack,player.m_timeSinceLastAttack,0))
                        {Forget(actor.m_uid);return;}
                        player.m_currentAttack=attack;player.m_lastCombatTimer=0;
                    }
                    finally{starting=false;}
                });
            });
        }
        private static void InContext(Running current,PlayerSnapshot state,Action action)
        {
            bool before=executing;executing=true;
            try{GameCombatContext.Run(current.Player,state,current.Weapon,current.Ammo,action);}finally{executing=before;}
        }
        private static void Trigger(Running current)
        {
            if(current.Pending || current.Triggered)return;
            current.Pending=true;var actor=current.Player.GetZDOID();
            if(!InventoryMoveGame.ServerAction(actor,state=>
            {
                try
                {
                    if(!current.Player || current.Player.IsStaggering() || current.Player.IsDead() || !running.TryGetValue(actor,out var active) || active!=current)return null;
                    var identity=state.Rows.FirstOrDefault(r=>r.Table=="item_data" && (string)r.Values[3]==GameEquipmentWear.Identity && (string)r.Values[4]==current.Identity);
                    if(identity==null)return null;
                    var values=identity.Values;int slot=Convert.ToInt32(values[2])*256+Convert.ToInt32(values[1]);
                    var result=GameAttackInventory.Trigger(state,slot,current.Secondary,Guid.NewGuid().ToString("N"));
                    return new PlayerActionPlan(result.Change,()=>
                    {
                        if(!current.Player)return;
                        current.Triggered=true;current.Weapon=result.Weapon;current.Ammo=result.Ammo;
                        current.Attack.m_weapon=result.Weapon;
                        InContext(current,state,()=>current.Attack.OnAttackTrigger());
                    });
                }
                catch(InvalidOperationException){return null;}
            },()=>{current.Pending=false;if(!current.Triggered)Forget(actor);}))current.Pending=false;
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.StartAttack))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+250)]
            private static bool Prefix(Humanoid __instance,bool secondaryAttack,ref bool __result)
            {
                if(__instance!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;
                var weapon=__instance.GetCurrentWeapon();if(Fishing(weapon))return true;
                __result=false;if(weapon==null)return false;
                var rotation=Quaternion.LookRotation(__instance.GetLookDir());
                __result=InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.Attack,Definition=Start,Alternate=secondaryAttack,
                    Rotation=new[]{rotation.x,rotation.y,rotation.z,rotation.w}},weapon.m_gridPos.x,weapon.m_gridPos.y)==true;
                return false;
            }
        }
        [HarmonyPatch(typeof(Humanoid),"UpdateAttack")]
        private static class NativeUpdate
        {
            private static bool Prefix(Humanoid __instance)=>executing || __instance!=Player.m_localPlayer || !PlayerSessionGame.Managed || Fishing(__instance.GetCurrentWeapon());
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.CustomFixedUpdate))]
        private static class Tick
        {
            private static void Postfix(Humanoid __instance,float fixedDeltaTime)
            {
                if(!(__instance is Player player) || !player.m_nview || !player.m_nview.IsValid() || !running.TryGetValue(player.GetZDOID(),out var current) || current.Player!=player)return;
                var state=InventoryMoveGame.State(player.GetZDOID());
                if(state==null || player.IsDead() || player.IsTeleporting() || Time.time-current.Began>15 || current.Attack.IsDone())
                {Forget(player.GetZDOID());return;}
                if(current.Pending)return;
                InContext(current,state,()=>player.UpdateAttack(fixedDeltaTime));
            }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.OnAttackTrigger))]
        private static class AnimationTrigger
        {
            [HarmonyPriority(Priority.First+250)]
            private static bool Prefix(Humanoid __instance)
            {
                if(!(__instance is Player player))return true;
                if(player.m_nview && player.m_nview.IsValid() && running.TryGetValue(player.GetZDOID(),out var current))
                {Trigger(current);return false;}
                return player!=Player.m_localPlayer || !PlayerSessionGame.Managed || Fishing(player.GetCurrentWeapon());
            }
        }
        [HarmonyPatch(typeof(Attack),"UseAmmo")]
        private static class AmmoDebit
        {
            private static bool Prefix(Attack __instance,ref ItemDrop.ItemData ammoItem,ref bool __result)
            {if(!Active(__instance))return true;ammoItem=GameCombatContext.Current.Ammo;__instance.m_ammoItem=ammoItem;__result=true;return false;}
        }
        [HarmonyPatch(typeof(Attack),"ConsumeItem")]
        private static class WeaponDebit {private static bool Prefix(Attack __instance)=>!Active(__instance);}
        [HarmonyPatch(typeof(Attack),"HaveAmmo")]
        private static class HaveAmmo
        {
            private static bool Prefix(Humanoid character,ItemDrop.ItemData weapon,ref bool __result)
            {if(!executing || !GameCombatContext.Matches(character))return true;__result=string.IsNullOrWhiteSpace(weapon.m_shared.m_ammoType) || GameCombatContext.Current.Ammo!=null;return false;}
        }
        [HarmonyPatch(typeof(Attack),"EquipAmmoItem")]
        private static class EquipAmmo
        {
            private static bool Prefix(Humanoid character,ref bool __result)
            {if(!executing || !GameCombatContext.Matches(character))return true;__result=true;return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.HaveStamina))]
        private static class HaveStamina
        {private static bool Prefix(Player __instance,ref bool __result){if(!starting || !GameCombatContext.Matches(__instance))return true;__result=true;return false;}}
        [HarmonyPatch(typeof(Character),nameof(Character.TryUseEitr))]
        private static class HaveEitr
        {private static bool Prefix(Character __instance,ref bool __result){if(!starting || !GameCombatContext.Matches(__instance))return true;__result=true;return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class StaminaDebit {private static bool Prefix(Player __instance)=>!executing || !GameCombatContext.Matches(__instance);}
        [HarmonyPatch(typeof(Player),nameof(Player.UseEitr))]
        private static class EitrDebit {private static bool Prefix(Player __instance)=>!executing || !GameCombatContext.Matches(__instance);}
        [HarmonyPatch(typeof(Character),nameof(Character.UseHealth))]
        private static class HealthDebit {private static bool Prefix(Character __instance)=>!executing || !GameCombatContext.Matches(__instance);}
    }
}
