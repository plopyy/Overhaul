using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerFishingCastGame
    {
        internal const string Cast="fish.cast", Return="fish.return";
        internal static readonly int OwnerUser="overhaul_fishing_owner_user".GetStableHashCode(), OwnerId="overhaul_fishing_owner_id".GetStableHashCode();
        internal static readonly int BaitData="overhaul_fishing_bait".GetStableHashCode(), Used="overhaul_fishing_used".GetStableHashCode(), Ready="overhaul_fishing_ready".GetStableHashCode(), Length="overhaul_fishing_length".GetStableHashCode();
        private static readonly int Velocity="overhaul_fishing_velocity".GetStableHashCode();
        private static readonly int Lifetime="overhaul_fishing_lifetime".GetStableHashCode();
        private sealed class Landing {internal PlayerActionPlan Plan;internal Task<bool> Saved;internal bool Failed;}
        private static readonly List<Landing> landings=new List<Landing>();
        internal static FishingFloat FloatPrefab(GameObject prefab)
        {if(!prefab)return null;var direct=prefab.GetComponent<FishingFloat>();if(direct)return direct;var flight=prefab.GetComponent<Projectile>();return flight && flight.m_spawnOnHit?flight.m_spawnOnHit.GetComponent<FishingFloat>():null;}
        internal static void Tick()
        {
            for(int i=landings.Count-1;i>=0;i--)
            {
                var landing=landings[i];if(landing.Failed || !landing.Saved.IsCompleted)continue;
                if(landing.Saved.IsFaulted || landing.Saved.IsCanceled || !landing.Saved.Result)
                {landing.Failed=true;ZLog.LogError("[Overhaul fishing] Transition failed; reservation retained: "+landing.Saved.Exception);continue;}
                landing.Plan.Publish();landings.RemoveAt(i);
            }
        }
        private sealed class Draw {internal int X,Y;internal float Started;}
        private static readonly Dictionary<ZDOID,Draw> draws=new Dictionary<ZDOID,Draw>();
        private static readonly HashSet<ZDOID> owned=new HashSet<ZDOID>();
        private static PlayerActionCommand queued;
        private static int queuedX,queuedY;
        internal static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true;
        internal static void Clear(){draws.Clear();queued=null;}
        internal static void Close(){Clear();owned.Clear();landings.Clear();ValidateNibble.Clear();}
        internal static bool HasServerLines=>Enabled && owned.Count!=0;
        internal static void TrackRestored(ZDO data){if(data.GetLong(OwnerUser,0)!=0)owned.Add(data.m_uid);}
        internal static void RemoveLine(ZDOID id){owned.Remove(id);ValidateNibble.Remove(id);}
        internal static void FilterCapture(ZDO data,ObjectRecord record)
        {
            if(Enabled && data.GetLong(OwnerUser,0)!=0 && !owned.Contains(data.m_uid))
                record.Properties.RemoveAll(p=>p.Key==OwnerUser || p.Key==OwnerId || p.Key==BaitData || p.Key==Used || p.Key==Ready || p.Key==Length || p.Key==Velocity || p.Key==Lifetime);
        }
        internal static void Forget(ZDOID actor)=>draws.Remove(actor);
        internal static void Begin(ZDO actor,int x,int y)
        {if(actor!=null && x>=0 && x<256 && y>=0 && y<256)draws[actor.m_uid]=new Draw{X=x,Y=y,Started=Time.time};}
        internal static bool Managed(FishingFloat line)=>line && line.m_nview && line.m_nview.IsValid() && line.m_nview.GetZDO().GetLong(OwnerUser,0)!=0 &&
            (!ZNet.instance || !ZNet.instance.IsServer() || owned.Contains(line.m_nview.GetZDO().m_uid));
        internal static ZDOID Owner(FishingFloat line)
        {var data=line.m_nview.GetZDO();return new ZDOID(data.GetLong(OwnerUser,0),unchecked((uint)data.GetInt(OwnerId,0)));}
        internal static bool ServerOwned(ZDOID id)
        {var data=ZDOMan.instance?.GetZDO(id);return Enabled && data!=null && data.GetLong(OwnerUser,0)!=0 && (!ZNet.instance.IsServer() || owned.Contains(id));}
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            if(request.Gameplay.Definition==Return)return ReturnBait(actor,request,snapshot,inventory);
            int slot=request.Action.FromY*256+request.Action.FromX;var rod=PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);var attack=rod.m_shared.m_attack;
            var projectile=attack.m_attackProjectile;
            if(request.Action.Amount!=1 || !rod.m_equipped || rod.m_shared.m_useDurability && rod.m_durability<=0 || !FloatPrefab(projectile) ||
                attack.m_projectiles!=1 || attack.m_projectileBursts!=1 || actor.GetBool(ZDOVars.s_dead,false) ||
                !draws.TryGetValue(actor.m_uid,out var draw) || draw.X!=request.Action.FromX || draw.Y!=request.Action.FromY)
                throw new InvalidOperationException("Fishing cast was not started with the equipped rod");
            var aim=new Vector3(request.Gameplay.Position[0],request.Gameplay.Position[1],request.Gameplay.Position[2]);
            if(aim.sqrMagnitude<.9f || aim.sqrMagnitude>1.1f)throw new InvalidOperationException("Invalid fishing aim");aim.Normalize();
            if(attack.m_useCharacterFacing)
            {var forward=actor.GetRotation()*Vector3.forward;if(attack.m_useCharacterFacingYAim){forward.y=aim.y;forward.Normalize();}aim=forward;}
            var ammo=inventory.Keys.Select(k=>new{Key=k,Item=PlayerInventoryView.ReadItem(inventory.Item(k),null,true)}).Where(p=>p.Item.m_shared.m_ammoType==rod.m_shared.m_ammoType &&
                (p.Item.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Ammo || p.Item.m_shared.m_itemType==ItemDrop.ItemData.ItemType.AmmoNonEquipable) && p.Item.m_worldLevel>=Game.m_worldLevel).OrderByDescending(p=>p.Item.m_equipped).ThenBy(p=>p.Key).FirstOrDefault();
            if(ammo==null || string.IsNullOrEmpty(rod.m_shared.m_ammoType))throw new InvalidOperationException("Fishing bait is unavailable");
            var bait=ammo.Item;bait.m_customData=inventory.Data(ammo.Key);bait.m_stack=1;bait.m_equipped=false;
            float skill=PlayerCraftProgressGame.Factor(snapshot,rod.m_shared.m_skillType);float duration=Mathf.Lerp(attack.m_drawDurationMin,attack.m_drawDurationMin*.2f,skill);
            float fraction=duration<=0?1:Mathf.Clamp01((Time.time-draw.Started)/duration);
            float speed=attack.m_bowDraw?Mathf.Lerp(attack.m_projectileVelMin,attack.m_projectileVel,attack.m_drawVelocityCurve.Evaluate(fraction)):attack.m_projectileVel;
            float accuracy=attack.m_bowDraw?Mathf.Lerp(attack.m_projectileAccuracyMin,attack.m_projectileAccuracy,Mathf.Sqrt(fraction)):attack.m_projectileAccuracy;
            if(float.IsNaN(speed) || float.IsInfinity(speed) || speed<0 || speed>1000)throw new InvalidOperationException("Invalid fishing velocity");
            var axis=Vector3.Cross(Vector3.up,aim);if(attack.m_launchAngle!=0)aim=Quaternion.AngleAxis(attack.m_launchAngle,axis)*aim;
            aim=Quaternion.AngleAxis(UnityEngine.Random.Range(-accuracy,accuracy),Vector3.up)*Quaternion.AngleAxis(UnityEngine.Random.Range(-accuracy,accuracy),Vector3.Cross(aim,Vector3.up))*aim;
            var actorObject=ZNetScene.instance.FindInstance(actor.m_uid);var character=actorObject?actorObject.GetComponent<Humanoid>():null;
            if(!character)throw new InvalidOperationException("Fishing actor is unavailable");
            var cast=attack.Clone();cast.m_character=character;cast.GetProjectileSpawnPoint(out var position,out _);
            var output=GamePersistence.AllocateActionObject(projectile,position,Quaternion.LookRotation(aim),true);
            void Add(int key,string type,object value)=>output.Properties.Add(new PropertyRecord{Key=key,Type=type,Value=value});
            Add(OwnerUser,"long",actor.m_uid.UserID);Add(OwnerId,"int",unchecked((int)actor.m_uid.ID));Add(ZDOVars.s_rodOwner,"long",actor.m_uid.UserID);Add(ZDOVars.s_bait,"string",bait.m_dropPrefab.name);
            var payload=new ZPackage();payload.Write((byte)109);bait.Save(payload);Add(BaitData,"bytes",payload.GetArray());Add(Used,"int",0);Add(Ready,"int",0);Add(Length,"float",Vector3.Distance(position,actor.GetPosition()+Vector3.up));Add(Velocity,"vector3",GameSnapshot.Components(aim*speed));Add(ZDOVars.s_bodyVelHash,"vector3",GameSnapshot.Components(aim*speed));
            var flight=projectile.GetComponent<Projectile>();if(flight)Add(Lifetime,"float",flight.m_ttl);
            inventory.Remove(ammo.Key,1);draws.Remove(actor.m_uid);
            var old=owned.Select(id=>ZDOMan.instance.GetZDO(id)).Where(d=>d!=null && d.GetLong(OwnerUser,0)==actor.m_uid.UserID && unchecked((uint)d.GetInt(OwnerId,0))==actor.m_uid.ID).ToArray();
            var batch=inventory.Delta(request.Action.Operation,snapshot.Revision);
            // A new cast replaces an old line as in native Setup; old ammunition is not refunded.
            if(old.Length>1)throw new InvalidOperationException("Multiple server fishing lines for one player");
            if(old.Length==1)
            {var prior=ZNetScene.instance.FindInstance(old[0].m_uid);if(!prior)throw new InvalidOperationException("Previous fishing line is outside the active scene");return PlayerActionGame.RemoveWorldObject(prior.GetComponent<ZNetView>(),batch,new[]{output});}
            return new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>{{output.Id,output}}),()=>GamePersistence.PublishActionObject(output));
        }
        private static PlayerActionPlan ReturnBait(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var target=ZNetScene.instance.FindInstance(new ZDOID(request.Gameplay.TargetUser,request.Gameplay.TargetId));var line=target?target.GetComponent<FishingFloat>():null;
            if(!Managed(line) || Owner(line)!=actor.m_uid || GamePersistence.ActionReserved(line.m_nview.GetZDO().m_uid) || request.Action.Amount!=1)
                throw new InvalidOperationException("Fishing line is unavailable");
            var data=line.m_nview.GetZDO();if(data.GetInt(Ready,0)!=2 || data.GetBool(Used,false) || line.m_baitConsumed)throw new InvalidOperationException("Bait cannot be returned");
            var prefab=ObjectDB.instance.GetItemPrefab(data.GetString(ZDOVars.s_bait,""));var drop=prefab?prefab.GetComponent<ItemDrop>():null;if(!drop)throw new InvalidOperationException("Saved bait definition is unavailable");
            var bait=drop.m_itemData.Clone();bait.m_dropPrefab=prefab;var package=new ZPackage(data.GetByteArray(BaitData));var version=(global::Version.Item)package.ReadByte();
            if(ItemDrop.ItemData.Load(package,bait,version)!=prefab.name.GetStableHashCode() || bait.m_stack!=1)throw new InvalidOperationException("Invalid saved bait");bait.m_equipped=false;
            var outputs=new List<ObjectRecord>();try{inventory.Add(PlayerActionGame.Row(bait).Values,bait.m_customData);}catch(PlayerInventoryFullException){outputs.Add(PlayerDropGame.Ground(bait,actor.GetPosition()+Vector3.up,Quaternion.identity));}
            return PlayerActionGame.RemoveWorldObject(line.m_nview,inventory.Delta(request.Action.Operation,snapshot.Revision),outputs);
        }
        internal static void ClientTick()
        {
            var controller=InventoryMoveGame.Client?.Controller;
            if(!PlayerSessionGame.Managed || controller==null || controller.Closed){queued=null;return;}
            if(queued!=null && !controller.Busy && controller.Act(queued,queuedX,queuedY,1))queued=null;
        }
        private static bool ManagedFlight(Projectile flight)=>flight && flight.m_nview && flight.m_nview.IsValid() && ServerOwned(flight.m_nview.GetZDO().m_uid) && FloatPrefab(flight.gameObject);
        internal static void Land(Projectile flight,Vector3 normal,bool water,bool expired=false)
        {
            if(!ManagedFlight(flight) || !ZNet.instance.IsServer() || GamePersistence.ActionReserved(flight.m_nview.GetZDO().m_uid))return;
            var data=flight.m_nview.GetZDO();var outputs=new List<ObjectRecord>();
            if(!expired)
            {
                var ownerObject=ZNetScene.instance.FindInstance(new ZDOID(data.GetLong(OwnerUser,0),unchecked((uint)data.GetInt(OwnerId,0))));
                var owner=ownerObject?ownerObject.GetComponent<Character>():null;var prefab=FloatPrefab(flight.gameObject);
                var top=owner?prefab.GetRodTop(owner):null;
                // Unequipping the rod or disconnecting during flight ends the cast.
                // Persist removal instead of throwing on every physics tick.
                if(!top){Land(flight,normal,water,true);return;}
                var point=flight.transform.position+flight.transform.TransformDirection(flight.m_spawnOffset)+normal*.25f;
                var rotation=flight.m_copyProjectileRotation?flight.transform.rotation:Quaternion.identity;
                var output=GamePersistence.AllocateActionObject(prefab.gameObject,point,rotation,true);
                void Add(int key,string type,object value)=>output.Properties.Add(new PropertyRecord{Key=key,Type=type,Value=value});
                Add(OwnerUser,"long",data.GetLong(OwnerUser,0));Add(OwnerId,"int",data.GetInt(OwnerId,0));Add(ZDOVars.s_rodOwner,"long",data.GetLong(OwnerUser,0));
                Add(ZDOVars.s_bait,"string",data.GetString(ZDOVars.s_bait,""));Add(BaitData,"bytes",data.GetByteArray(BaitData));Add(Used,"int",0);Add(Ready,"int",0);
                Add(Length,"float",Vector3.Distance(top.position,point));Add(Velocity,"vector3",GameSnapshot.Components(Vector3.zero));outputs.Add(output);
            }
            var position=flight.transform.position;
            var plan=PlayerActionGame.RemoveWorldObject(flight.m_nview,new PlayerBatch(Guid.NewGuid().ToString("N"),0,Array.Empty<PlayerChange>()),outputs,()=>
            {if(!expired){(water?flight.m_hitWaterEffects:flight.m_hitEffects)?.Create(position,Quaternion.identity);flight.m_spawnOnHitEffects?.Create(position,Quaternion.identity);}});
            // Stop the remainder of this native physics tick while the worker commits the replacement.
            flight.m_didHit=true;flight.m_ttl=0;
            landings.Add(new Landing{Plan=plan,Saved=GamePersistence.CommitWorldObjects(plan.Change)});
        }
        [HarmonyPatch(typeof(Projectile),"Awake")]
        private static class RestoreFlight
        {
            private static void Postfix(Projectile __instance)
            {
                if(!ManagedFlight(__instance))return;var data=__instance.m_nview.GetZDO();
                __instance.m_vel=data.GetVec3(Velocity,Vector3.zero);__instance.m_ttl=data.GetFloat(Lifetime,__instance.m_ttl);
                var owner=ZNetScene.instance.FindInstance(new ZDOID(data.GetLong(OwnerUser,0),unchecked((uint)data.GetInt(OwnerId,0))));
                __instance.m_owner=owner?owner.GetComponent<Character>():null;
            }
        }
        [HarmonyPatch(typeof(Projectile),"FixedUpdate")]
        private static class FlightUpdate
        {
            private static bool Prefix(Projectile __instance)
            {
                if(!ManagedFlight(__instance))return true;var data=__instance.m_nview.GetZDO();
                if(GamePersistence.ActionReserved(data.m_uid))return false;
                if(data.IsOwner() && __instance.m_ttl>0 && __instance.m_ttl<=Time.fixedDeltaTime){Land(__instance,Vector3.zero,false,true);return false;}
                return true;
            }
            private static void Postfix(Projectile __instance)
            {
                if(!ManagedFlight(__instance))return;var data=__instance.m_nview.GetZDO();if(!data.IsOwner() || GamePersistence.ActionReserved(data.m_uid))return;
                data.Set(Velocity,__instance.m_vel);data.Set(Lifetime,__instance.m_ttl);
            }
        }
        [HarmonyPatch(typeof(Projectile),nameof(Projectile.OnHit))]
        private static class FlightImpact
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Projectile __instance,Collider collider,bool water,Vector3 normal)
            {
                if(!ManagedFlight(__instance))return true;
                if(!__instance.m_nview.IsOwner() || GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return false;
                var target=collider?Projectile.FindHitObject(collider):null;var destructible=target?target.GetComponent<IDestructible>():null;
                if(destructible!=null && !__instance.IsValidTarget(destructible))return false;
                Land(__instance,normal,water);return false;
            }
        }
        [HarmonyPatch(typeof(Attack),nameof(Attack.StartDraw))]
        private static class DrawIntent
        {
            private static void Postfix(Humanoid character,ItemDrop.ItemData weapon,bool __result)
            {if(__result && character==Player.m_localPlayer && PlayerSessionGame.Managed && FloatPrefab(weapon.m_shared.m_attack.m_attackProjectile))InventoryMoveGame.Client?.BeginFishingDraw(weapon.m_gridPos.x,weapon.m_gridPos.y);}
        }
        [HarmonyPatch(typeof(Attack),nameof(Attack.OnAttackTrigger))]
        private static class CastIntent
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Attack __instance)
            {
                if(__instance.m_character!=Player.m_localPlayer || !PlayerSessionGame.Managed || !FloatPrefab(__instance.m_attackProjectile))return true;
                __instance.GetProjectileSpawnPoint(out _,out var aim);queued=new PlayerActionCommand{Kind=PlayerActionKind.Attack,Definition=Cast,Position=new[]{aim.x,aim.y,aim.z}};queuedX=__instance.m_weapon.m_gridPos.x;queuedY=__instance.m_weapon.m_gridPos.y;ClientTick();return false;
            }
        }
        [HarmonyPatch(typeof(FishingFloat),"GetOwner")]
        private static class ResolveOwner
        {
            private static bool Prefix(FishingFloat __instance,ref Character __result)
            {if(!Managed(__instance))return true;var target=ZNetScene.instance.FindInstance(Owner(__instance));__result=target?target.GetComponent<Character>():null;return false;}
        }
        [HarmonyPatch(typeof(FishingFloat),"Awake")]
        private static class Restore
        {
            private static void Postfix(FishingFloat __instance)
            {
                if(!Managed(__instance))return;var data=__instance.m_nview.GetZDO();__instance.m_lineLength=data.GetFloat(Length,1);__instance.m_baitConsumed=data.GetBool(Used,false);
                if(__instance.m_rodLine)__instance.m_rodLine.SetPeer(Owner(__instance));
                if(__instance.m_body && data.IsOwner())__instance.m_body.linearVelocity=data.GetVec3(Velocity,Vector3.zero);
            }
        }
        [HarmonyPatch(typeof(FishingFloat),"SetCatch")]
        private static class BaitConsumed
        {private static void Postfix(FishingFloat __instance,Fish fish){if(fish && Managed(__instance) && __instance.m_nview.IsOwner())__instance.m_nview.GetZDO().Set(Used,true);}}
        [HarmonyPatch(typeof(Fish),"TestBate")]
        private static class DeferBaitRoll
        {
            private static bool Prefix(FishingFloat ff,ref bool __result)
            {if(!Enabled || !Managed(ff))return true;__result=true;return false;}
        }
        [HarmonyPatch(typeof(FishingFloat),nameof(FishingFloat.RPC_Nibble))]
        private static class ValidateNibble
        {
            private static readonly Dictionary<ZDOID,float> attempted=new Dictionary<ZDOID,float>();
            internal static void Clear()=>attempted.Clear();
            internal static void Remove(ZDOID id)=>attempted.Remove(id);
            private static bool Prefix(FishingFloat __instance,long sender,ZDOID fishID,ref bool correctBait)
            {
                if(!Enabled || !Managed(__instance))return true;
                if(!ZNet.instance.IsServer() || !__instance.m_nview.IsOwner())return false;
                var data=__instance.m_nview.GetZDO();
                if(GamePersistence.ActionReserved(data.m_uid) || data.GetInt(Ready,0)!=0 || __instance.GetCatch())return false;
                var target=ZNetScene.instance.FindInstance(fishID);var fish=target?target.GetComponent<Fish>():null;
                if(!fish || !fish.m_nview || !fish.m_nview.IsValid() || fish.m_nview.GetZDO().GetOwner()!=sender || GamePersistence.ActionReserved(fishID) ||
                    Vector3.Distance(fish.transform.position,__instance.transform.position)>3 || fish.IsOutOfWater())return false;
                if(attempted.TryGetValue(data.m_uid,out var last) && Time.time-last<1)return false;
                attempted[data.m_uid]=Time.time;
                string bait=__instance.GetBait();correctBait=false;
                foreach(var rule in fish.m_baits)
                    if(rule.m_bait && rule.m_bait.name==bait && UnityEngine.Random.value<rule.m_chance){correctBait=true;break;}
                return true;
            }
        }
        [HarmonyPatch(typeof(FishingFloat),"ReturnBait")]
        private static class ReturnIntent
        {
            private static bool Prefix(FishingFloat __instance)
            {if(!Managed(__instance))return !Enabled;if(__instance.m_nview.IsOwner() && !__instance.m_baitConsumed && !__instance.m_nview.GetZDO().GetBool(Used,false))__instance.m_nview.GetZDO().Set(Ready,2);return false;}
        }
        [HarmonyPatch(typeof(ZNetView),nameof(ZNetView.Destroy))]
        private static class KeepReadyLine
        {private static bool Prefix(ZNetView __instance)=>!__instance.IsValid() || __instance.GetZDO().GetInt(Ready,0)==0 || __instance.GetZDO().GetLong(OwnerUser,0)==0;}
        [HarmonyPatch(typeof(FishingFloat),"FixedUpdate")]
        private static class ReadyLine
        {
            private static bool Prefix(FishingFloat __instance)
            {
                if(!Managed(__instance))return true;var data=__instance.m_nview.GetZDO();int ready=data.GetInt(Ready,0);
                if(GamePersistence.ActionReserved(data.m_uid))return false;
                if(ready==0)return true;
                if(ZNet.instance.IsServer() && !__instance.GetOwner())
                {data.Set(Ready,0);__instance.m_nview.Destroy();return false;}
                if(PlayerSessionGame.Managed && Player.m_localPlayer && Owner(__instance)==Player.m_localPlayer.GetZDOID())
                {
                    if(ready==1){var fish=__instance.GetCatch();if(fish)PlayerFishingGame.RequestCatch(__instance,fish);}
                    else if(ready==2){var id=data.m_uid;InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Return,TargetUser=id.UserID,TargetId=id.ID},0,0,1);}
                }
                return false;
            }
            private static void Postfix(FishingFloat __instance)
            {if(Managed(__instance) && __instance.m_nview.IsOwner() && !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))__instance.m_nview.GetZDO().Set(Length,__instance.m_lineLength);}
        }
    }
}
