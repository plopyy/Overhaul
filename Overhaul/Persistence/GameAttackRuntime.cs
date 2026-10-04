using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameAttackRuntime
    {
        internal const string Start="combat.start",Unarmed="combat.unarmed";
        private sealed class Running
        {
            internal Player Player;
            internal Attack Attack;
            internal ItemDrop.ItemData Weapon,Ammo;
            internal string Identity;
            internal int WeaponSlot;
            internal bool Secondary,Pending,Triggered,BurstPending;
            internal float Began;
            internal AnimatorCullingMode Culling;
        }
        private static readonly Dictionary<ZDOID,Running> running=new Dictionary<ZDOID,Running>();
        private sealed class ClientIntent
        {
            internal PlayerActionCommand Command;
            internal ItemDrop.ItemData Weapon;
            internal int X,Y,Retries;
            internal double Deadline;
            internal string Operation;
        }
        private static ClientIntent clientIntent;
        [ThreadStatic] private static bool executing,starting,firing;
        private static bool Fishing(ItemDrop.ItemData item)=>item!=null && PlayerFishingCastGame.FloatPrefab(item.m_shared.m_attack?.m_attackProjectile);
        private static bool Managed(Player player)=>player && player.m_nview && player.m_nview.IsValid() && InventoryMoveGame.State(player.GetZDOID())!=null;
        private static bool Active(Attack attack)=>executing && GameCombatContext.Matches(attack.m_character);
        internal static bool Active(ZDOID actor)=>running.ContainsKey(actor);
        internal static void Forget(ZDOID actor)
        {
            if(!running.TryGetValue(actor,out var current))return;
            running.Remove(actor);current.Attack.Stop();
            if(current.Player && current.Player.m_currentAttack==current.Attack)
            {current.Player.m_previousAttack=current.Attack;current.Player.m_currentAttack=null;}
            if(current.Player && current.Player.m_animator)current.Player.m_animator.cullingMode=current.Culling;
        }
        internal static void Clear(){foreach(var key in running.Keys.ToArray())Forget(key);ClearClient();}
        internal static void ClearClient()=>clientIntent=null;
        internal static void ClientTick()
        {
            var intent=clientIntent;if(intent==null)return;
            var player=Player.m_localPlayer;var controller=InventoryMoveGame.Client?.Controller;
            if(!player || !PlayerSessionGame.Managed || controller==null || controller.Closed || Time.timeAsDouble>intent.Deadline || player.IsDead())
            {clientIntent=null;return;}
            if(intent.Operation!=null || controller.Busy)return;
            var weapon=intent.Command.Definition==Unarmed?player.GetCurrentWeapon():player.GetInventory().GetItemAt(intent.X,intent.Y);
            if(weapon==null || !SameWeapon(intent.Weapon,weapon)){clientIntent=null;return;}
            if(controller.Act(intent.Command,intent.X,intent.Y))intent.Operation=controller.Pending?.Action.Operation;
        }
        internal static void ClientReply(InventoryMoveReply reply,InventoryMoveRequest request)
        {
            var intent=clientIntent;if(intent==null || request==null || intent.Operation!=request.Action.Operation || reply.Notification)return;
            // Only retry an explicitly rejected, stale revision. The selected
            // weapon is checked again after the authoritative snapshot is applied.
            // Never replay a successful shot or a gameplay validation rejection.
            if(!reply.Accepted && reply.Snapshot && reply.Player.ExpectedRevision>request.Action.PlayerRevision && intent.Retries++<2 && Time.timeAsDouble<intent.Deadline)
                intent.Operation=null;
            else clientIntent=null;
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            if(request.Gameplay.Definition!=Start && request.Gameplay.Definition!=Unarmed || request.Action.Amount!=1)throw new InvalidOperationException("Invalid combat intent");
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!player || !player.m_animator || !player.m_zanim || !player.m_animEvent || !player.m_body || player.IsDead() || player.IsTeleporting() || player.InIntro() || player.InDodge() || player.IsStaggering() || player.InMinorAction())
                throw new InvalidOperationException("Character cannot start this attack");
            if(running.TryGetValue(actor.m_uid,out var previous) && (!previous.Attack.IsDone() || previous.Pending || previous.BurstPending))
                throw new InvalidOperationException("Previous server attack is still active");
            int slot=request.Gameplay.Definition==Unarmed?-1:request.Action.FromY*256+request.Action.FromX;
            var preview=GameAttackInventory.Trigger(snapshot,slot,request.Gameplay.Alternate,request.Action.Operation);var attack=preview.Definition;
            if(attack.m_attackType!=Attack.AttackType.Horizontal && attack.m_attackType!=Attack.AttackType.Vertical && attack.m_attackType!=Attack.AttackType.Area && attack.m_attackType!=Attack.AttackType.Projectile ||
                attack.m_loopingAttack || attack.m_attackUseAdrenaline!=0 || attack.m_selfDamage!=0 || attack.m_attackKillsSelf)
                throw new InvalidOperationException("This attack requires its additional server combat phase");
            if(!preview.Weapon.m_customData.TryGetValue(GameEquipmentWear.Identity,out var identity) && preview.Weapon.m_shared.m_useDurability && preview.Weapon.m_shared.m_maxStackSize==1)
                throw new InvalidOperationException("Weapon identity has not been confirmed");
            var q=request.Gameplay.Rotation;var rotation=new Quaternion(q[0],q[1],q[2],q[3]);
            if(Mathf.Abs(Quaternion.Dot(rotation,rotation)-1)>.01f)throw new InvalidOperationException("Invalid attack aim");
            var draw=attack.m_bowDraw?GameBowDraw.Prepare(actor.m_uid,snapshot,preview.Weapon,slot):null;
            var reload=attack.m_requiresReload?GameWeaponReload.Prepare(actor.m_uid,preview.Weapon,slot):null;
            var resourceState=draw==null?snapshot:PlayerProgressService.Overlay(snapshot,draw.Changes);
            GameAttackResources.ValidateStart(resourceState,preview.Weapon,attack);
            var changes=(draw?.Changes??Array.Empty<PlayerChange>()).ToList();
            foreach(var change in GameAttackResources.Spend(resourceState,preview.Weapon,attack,false))
            {changes.RemoveAll(row=>PlayerProgressService.SameKey(row,change));changes.Add(change);}
            var batch=new PlayerBatch(request.Action.Operation,snapshot.Revision,changes);
            return new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>()),()=>
            {
                draw?.Confirm();
                reload?.Invoke();
                if(!player || !Managed(player))return;
                Forget(actor.m_uid);
                var current=new Running{Player=player,Attack=attack,Weapon=preview.Weapon,Ammo=preview.Ammo,Identity=identity,
                    WeaponSlot=slot,Secondary=request.Gameplay.Alternate,Began=Time.time,Culling=player.m_animator.cullingMode};
                running[actor.m_uid]=current;player.m_animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                player.SetLookDir(rotation*Vector3.forward);
                InContext(current,snapshot,()=>
                {
                    starting=true;
                    try
                    {
                        if(!attack.Start(player,player.m_body,player.m_zanim,player.m_animEvent,player.m_visEquipment,current.Weapon,player.m_previousAttack,player.m_timeSinceLastAttack,draw?.Fraction??0))
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
            if(current.Pending || current.Triggered || !current.Player || !current.Player.InAttack())return;
            current.Pending=true;var actor=current.Player.GetZDOID();
            if(!InventoryMoveGame.ServerAction(actor,state=>
            {
                try
                {
                    if(!current.Player || current.Player.IsStaggering() || current.Player.IsDead() || !running.TryGetValue(actor,out var active) || active!=current)return null;
                    int slot=current.WeaponSlot;
                    if(current.Identity!=null)
                    {
                        var identity=state.Rows.FirstOrDefault(r=>r.Table=="item_data" && (string)r.Values[3]==GameEquipmentWear.Identity && (string)r.Values[4]==current.Identity);
                        if(identity==null)return null;
                        var values=identity.Values;slot=Convert.ToInt32(values[2])*256+Convert.ToInt32(values[1]);
                    }
                    var result=GameAttackInventory.Trigger(state,slot,current.Secondary,Guid.NewGuid().ToString("N"));
                    if(!SameWeapon(current.Weapon,result.Weapon))return null;
                    return new PlayerActionPlan(result.Change,()=>
                    {
                        if(!current.Player || current.Player.IsDead() || !running.TryGetValue(actor,out var active) || active!=current)return;
                        current.Triggered=true;current.Weapon=result.Weapon;current.Ammo=result.Ammo;
                        current.Attack.m_weapon=result.Weapon;
                        InContext(current,state,()=>current.Attack.OnAttackTrigger());
                    });
                }
                catch(InvalidOperationException){return null;}
            },()=>{current.Pending=false;if(!current.Triggered)Forget(actor);}))current.Pending=false;
        }
        private static bool SameWeapon(ItemDrop.ItemData before,ItemDrop.ItemData after)=>before.m_dropPrefab==after.m_dropPrefab && before.m_quality==after.m_quality &&
            before.m_variant==after.m_variant && before.m_crafterID==after.m_crafterID && before.m_crafterName==after.m_crafterName && before.m_worldLevel==after.m_worldLevel &&
            before.m_customData.Count==after.m_customData.Count && before.m_customData.All(p=>after.m_customData.TryGetValue(p.Key,out var value) && value==p.Value);
        private static void Burst(Running current)
        {
            if(current.BurstPending)return;
            current.BurstPending=true;var actor=current.Player.GetZDOID();bool fired=false;
            if(!InventoryMoveGame.ServerAction(actor,state=>
            {
                try
                {
                    if(!current.Player || current.Player.IsDead() || current.Player.IsStaggering() || current.Attack.IsDone() || !running.TryGetValue(actor,out var active) || active!=current)return null;
                    var costs=GameAttackResources.Spend(state,current.Weapon,current.Attack,true);
                    return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,costs),new Dictionary<long,ObjectRecord>()),()=>
                    {
                        if(!current.Player || current.Player.IsDead() || !running.TryGetValue(actor,out var active) || active!=current)return;
                        bool previous=firing;firing=true;
                        try{InContext(current,state,()=>current.Attack.FireProjectileBurst());fired=true;}
                        finally{firing=previous;}
                    });
                }
                catch(InvalidOperationException){return null;}
            },()=>{current.BurstPending=false;if(!fired)Forget(actor);}))current.BurstPending=false;
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
                if(clientIntent!=null)return false;
                var rotation=Quaternion.LookRotation(__instance.GetLookDir());
                clientIntent=new ClientIntent{Weapon=weapon.Clone(),X=weapon.m_gridPos.x,Y=weapon.m_gridPos.y,Deadline=Time.timeAsDouble+1,
                    Command=new PlayerActionCommand{Kind=PlayerActionKind.Attack,Definition=__instance.GetInventory().ContainsItem(weapon)?Start:Unarmed,Alternate=secondaryAttack,
                    Rotation=new[]{rotation.x,rotation.y,rotation.z,rotation.w}}};
                ClientTick();__result=clientIntent!=null;
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
                if(current.Pending || current.BurstPending)return;
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
        [HarmonyPatch(typeof(Attack),"FireProjectileBurst")]
        private static class ProjectileBurst
        {
            private static bool Prefix(Attack __instance)
            {
                if(!Active(__instance) || !__instance.m_perBurstResourceUsage || firing)return true;
                if(running.TryGetValue(__instance.m_character.GetZDOID(),out var current))Burst(current);
                return false;
            }
        }
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
        {private static bool Prefix(Player __instance,ref bool __result){if(!executing || !GameCombatContext.Matches(__instance))return true;__result=true;return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.IsWeaponLoaded))]
        private static class LoadedWeapon
        {private static bool Prefix(Player __instance,ref bool __result){if(!starting || !GameCombatContext.Matches(__instance))return true;__result=true;return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.HaveEitr))]
        private static class BurstEitr
        {private static bool Prefix(Player __instance,ref bool __result){if(!executing || !GameCombatContext.Matches(__instance))return true;__result=true;return false;}}
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
