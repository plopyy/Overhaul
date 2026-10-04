using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerCatapultGame
    {
        internal const string Load = "catapult.load", Legs = "catapult.legs";
        private static readonly int Ammo = "overhaul_catapult_ammo".GetStableHashCode(), Payload = "overhaul_catapult_payload".GetStableHashCode(), Due = "overhaul_catapult_due".GetStableHashCode();
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        private sealed class Shot { internal Task<bool> Saved; internal PlayerActionPlan Plan; internal bool Failed; }
        private static readonly List<Shot> pending = new List<Shot>();
        internal static void Clear() => pending.Clear();
        internal static void Tick()
        {
            for (int i=pending.Count-1;i>=0;i--)
            {
                var shot=pending[i]; if (!shot.Saved.IsCompleted || shot.Failed) continue;
                if (shot.Saved.IsFaulted || shot.Saved.IsCanceled)
                { shot.Failed=true; ZLog.LogError("[Overhaul catapult] Shot was not committed; reservation retained: "+shot.Saved.Exception); continue; }
                shot.Plan.Publish(); pending.RemoveAt(i);
            }
        }
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var machine=target.GetComponent<Catapult>();
            if (!machine || !machine.m_nview || !machine.m_nview.IsValid() || request.Action.Amount!=1 ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0))) throw new InvalidOperationException("Catapult is unavailable");
            var data=machine.m_nview.GetZDO();
            if (request.Gameplay.Definition==Legs)
            {
                if (machine.m_movingLegs || machine.m_armAnimTime!=0 || data.GetInt(Ammo,0)!=0) throw new InvalidOperationException("Catapult is moving or loaded");
                bool locked=!data.GetBool(ZDOVars.s_locked,false);
                using(var world=new PlayerActionObjectGame(data))
                {world.Set(ZDOVars.s_locked,locked?1:0);return world.Finish(inventory.Delta(request.Action.Operation,snapshot.Revision),()=>machine.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_OnLegUse",locked));}
            }
            if (request.Gameplay.Definition!=Load || !request.Gameplay.Alternate || !machine.m_loadPoint || machine.m_loadedItem!=null || machine.m_armAnimTime!=0 || data.GetInt(Ammo,0)!=0)
                throw new InvalidOperationException("Catapult is already loaded or firing");
            int slot=request.Action.FromY*256+request.Action.FromX;
            var item=PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);item.m_customData=inventory.Data(slot);
            if (item.m_worldLevel<Game.m_worldLevel || !machine.CanItemBeLoaded(item) || machine.m_maxLoadStack<1 || machine.m_maxLoadStack>128 ||
                float.IsNaN(machine.m_shootAfterLoadDelay) || float.IsInfinity(machine.m_shootAfterLoadDelay) || machine.m_shootAfterLoadDelay<0 || machine.m_shootAfterLoadDelay>120)
                throw new InvalidOperationException("Invalid catapult ammunition");
            int count=Math.Min(item.m_stack,machine.m_maxLoadStack);inventory.Remove(slot,count);
            if(inventory.Keys.Contains(slot))inventory.Equip(slot,false);
            item.m_stack=count;item.m_equipped=false;item.m_customData.Remove("eaqs_parked");
            var bytes=new ZPackage();bytes.Write((byte)109);item.Save(bytes);
            using(var world=new PlayerActionObjectGame(data))
            {
                world.Set(Ammo,item.m_dropPrefab.name.GetStableHashCode());world.Set(Payload,bytes.GetArray());world.Set(Due,ZNet.instance.GetTime().AddSeconds(machine.m_shootAfterLoadDelay).Ticks);
                return world.Finish(inventory.Delta(request.Action.Operation,snapshot.Revision),()=>
                {Restore(machine);machine.m_loadItemEffect.Create(machine.m_loadPoint.transform.position,machine.m_loadPoint.transform.rotation,null,1,-1,default(ZDOID));});
            }
        }
        private static ItemDrop.ItemData Read(ZDO data)
        {
            var prefab=ObjectDB.instance.GetItemPrefab(data.GetInt(Ammo,0));var drop=prefab?prefab.GetComponent<ItemDrop>():null;
            if(!drop)throw new InvalidOperationException("Saved catapult ammunition is unavailable");
            var item=drop.m_itemData.Clone();item.m_dropPrefab=prefab;var bytes=data.GetByteArray(Payload);
            if(bytes==null || bytes.Length==0)throw new InvalidOperationException("Saved catapult ammunition is incomplete");
            var package=new ZPackage(bytes);int version=package.ReadByte();item.Load(package,(global::Version.Item)version);
            if(item.m_stack<1 || item.m_stack>128 || item.m_stack>item.m_shared.m_maxStackSize)throw new InvalidOperationException("Invalid saved catapult stack");
            item.m_equipped=false;return item;
        }
        private static void Restore(Catapult machine)
        {
            var data=machine.m_nview.GetZDO();if(data.GetInt(Ammo,0)==0 || machine.m_loadedItem!=null)return;
            var item=Read(data);
            machine.RPC_SetLoadedVisual(ZNet.GetUID(),item.m_dropPrefab.name);
            machine.m_loadStack=item.m_stack;item.m_stack=1;machine.m_loadedItem=item;
        }
        internal static void Advance(Catapult machine)
        {
            if(!machine.m_nview || !machine.m_nview.IsValid())return;
            var data=machine.m_nview.GetZDO();if(GamePersistence.ActionReserved(data.m_uid))return;
            if(data.GetInt(Ammo,0)==0)return;
            Restore(machine);
            if(!ZNet.instance.IsServer() || !GamePersistence.Active || ZNet.instance.GetTime().Ticks<data.GetLong(Due,long.MaxValue) || machine.m_armAnimTime!=0)return;
            using(var world=new PlayerActionObjectGame(data))
            {
                world.Set(Ammo,0);world.Set(Payload,Array.Empty<byte>());world.Set(Due,0L);
                // Firing consumes the persisted load before publishing the transient projectile simulation.
                // A crash after this commit can lose an in-flight shot, but cannot replay the ammunition.
                var plan=world.Finish(new PlayerBatch(Guid.NewGuid().ToString("N"),0,Array.Empty<PlayerChange>()),()=>
                {if(machine)machine.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_Shoot");});
                pending.Add(new Shot{Plan=plan,Saved=GamePersistence.CommitWorldObjects(plan.Change)});
            }
        }
        [HarmonyPatch(typeof(Catapult),"FixedUpdate")]
        private static class Update
        {
            private static bool Prefix(Catapult __instance)
            {
                if(!Enabled)return true;
                if(__instance.m_nview && __instance.m_nview.IsValid() && GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return false;
                Advance(__instance);return true;
            }
        }
        [HarmonyPatch]
        private static class Intent
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Catapult),"OnLoadPointUse");yield return AccessTools.Method(typeof(Catapult),"OnLegUse");}
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Catapult __instance,Humanoid user,ItemDrop.ItemData item,MethodBase __originalMethod,ref bool __result)
            {
                if(user!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;__result=false;
                if(!__instance.m_nview || !__instance.m_nview.IsValid() || user.IsTeleporting())return false;
                bool load=__originalMethod.Name=="OnLoadPointUse";if(load && (item==null || !user.GetInventory().ContainsItem(item)))return false;
                var id=__instance.m_nview.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=load?Load:Legs,TargetUser=id.UserID,TargetId=id.ID,Alternate=load},item?.m_gridPos.x??0,item?.m_gridPos.y??0,1);return false;
            }
        }
        [HarmonyPatch]
        private static class Rpc
        {
            private static IEnumerable<MethodBase> TargetMethods(){foreach(string n in new[]{"RPC_OnLegUse","RPC_Shoot","RPC_SetLoadedVisual"})yield return AccessTools.Method(typeof(Catapult),n);}
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Catapult __instance,long sender)
            {
                if(__instance.m_nview && __instance.m_nview.IsValid() && GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return false;
                return !Enabled || ZNet.instance && sender==(ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid);
            }
        }
        [HarmonyPatch(typeof(Catapult),"Shoot")]
        private static class LegacyShoot {private static bool Prefix()=>!Enabled;}
    }
}

