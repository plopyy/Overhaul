using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Overhaul.Storage;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerRecyclerGame
    {
        internal const string Interaction = "recycler.use";
        private static bool Enabled => PlayerPersistenceConfig.Enabled?.Value == true;
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var machine = target.GetComponent<Incinerator>(); var chest = machine ? machine.m_container : null; var data = ChestAccess.Data(chest);
            long playerId = actor.GetLong(ZDOVars.s_playerID,0);
            if (!machine || machine.isInUse || data == null || data != machine.m_nview.GetZDO() || request.Action.Amount != 1 ||
                !ChestAccess.Allows(chest,playerId) || !chest.CheckAccess(playerId) || !ChestAccess.WardAccess(chest,playerId) ||
                !ChestAccess.WardAccessAt(target.transform.position,playerId) || ChestAccess.Leased(chest) || MoveReservation.Busy(chest) ||
                !InventoryMoveGame.SharedView(chest) && chest.IsInUse()) throw new InvalidOperationException("Recycler is unavailable");
            float delay = UnityEngine.Random.Range(machine.m_effectDelayMin,machine.m_effectDelayMax);
            if (float.IsNaN(delay) || float.IsInfinity(delay) || delay < 0 || delay > 20) throw new InvalidOperationException("Invalid recycler delay");
            chest.Load(); var live = chest.GetInventory(); var package = new ZPackage(); live.Save(package);
            var before = PlayerNativeFormat.DecodeInventory(package.GetArray()).ToArray(); var layout = InventoryMoveGame.ContainerLayout(chest);
            var detached = new Inventory("Recycler action",null,live.GetWidth(),live.GetHeight());
            detached.m_inventory.AddRange(live.GetAllItems().Select(i => i.Clone()));
            var overflow = new List<ItemDrop.ItemData>();
            int count = EquipmentRecycler.Recycle(detached,i => overflow.Add(i.Clone()));
            if (count == 0) throw new InvalidOperationException("Recycler contains no recyclable items");
            if (overflow.Count > 128) throw new InvalidOperationException("Recycler output exceeds one action capacity");
            package = new ZPackage(); detached.Save(package);
            var after = new PlayerActionInventory(PlayerNativeFormat.DecodeInventory(package.GetArray()),layout);
            var delta = after.DeltaAgainst(request.Action.Operation,0,before); var cells = ContainerVersions.Slots(delta);
            data.SetOwner(ZNet.GetUID()); chest.Save(); long id = GamePersistence.SnapshotInventory(data);
            if (!GamePersistence.ReserveSlots(data.m_uid,cells)) throw new InvalidOperationException("Recycler slots are busy");
            bool released = false;
            void Release()
            {
                if (released) return; released = true;
                if (machine) machine.isInUse = false;
                GamePersistence.ReleaseSlots(data,cells);
            }
            void Lever(bool start)
            {
                try { if (machine) machine.m_nview.InvokeRPC(ZNetView.Everybody,start ? "RPC_AnimateLever" : "RPC_AnimateLeverReturn"); }
                catch (Exception e) { ZLog.LogWarning("[Overhaul recycler visual] "+e.Message); }
            }
            try
            {
                var objects = overflow.Select(i => PlayerDropGame.Ground(i,machine.transform.position+machine.transform.forward*2+Vector3.up,Quaternion.identity,0,Vector3.zero)).ToDictionary(o => o.Id);
                var action = new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,Array.Empty<PlayerChange>()),objects,
                    new Dictionary<long,PlayerContainerAction> { [id] = new PlayerContainerAction(before,delta) });
                float due = Time.time+delay; machine.isInUse = true; Lever(true);
                return new PlayerActionPlan(action,() =>
                {
                    foreach (var output in objects.Values) GamePersistence.PublishActionObject(output);
                    var confirmed = action.CommittedContainers[id]; var contents = InventoryMovePresentation.Prepare(live,confirmed,false);
                    live.m_inventory.Clear(); live.m_inventory.AddRange(contents); live.Changed(); chest.Save();
                    InventoryMoveGame.Broadcast(chest,confirmed,null); Release(); Lever(false);
                    try
                    {
                        if (machine)
                        {
                            machine.isInUse = true; machine.Invoke("StopAOE",4f);
                            if (machine.m_lightingAOEs) UnityEngine.Object.Instantiate(machine.m_lightingAOEs,machine.transform.position,machine.transform.rotation);
                        }
                    }
                    catch (Exception e) { ZLog.LogWarning("[Overhaul recycler visual] "+e.Message); }
                })
                {
                    Ready = () =>
                    {
                        if (!machine || !chest || !chest.m_nview.IsValid() || actor.GetBool(ZDOVars.s_dead,false) ||
                            Vector3.Distance(actor.GetPosition(),machine.transform.position) > 5 || !ChestAccess.Allows(chest,playerId) ||
                            !chest.CheckAccess(playerId) || !ChestAccess.WardAccess(chest,playerId)) throw new InvalidOperationException("Recycling interrupted");
                        return Time.time >= due;
                    },
                    Cancel = () => { Release(); Lever(false); }
                };
            }
            catch { Release(); throw; }
        }
        [HarmonyPatch(typeof(Incinerator),"OnIncinerate")]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Incinerator __instance,Humanoid user,ref bool __result)
            {
                if (user != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
                __result = false;
                if (__instance.m_nview && __instance.m_nview.IsValid() && !user.IsTeleporting())
                {
                    var id = __instance.m_nview.GetZDO().m_uid;
                    InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.UseOn,Definition = Interaction,TargetUser = id.UserID,TargetId = id.ID },0,0,1);
                }
                return false;
            }
        }
        [HarmonyPatch(typeof(Incinerator),"RPC_RequestIncinerate")]
        private static class Legacy
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Incinerator __instance) => !Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() ||
                !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid) && !GamePersistence.InventoryReserved(__instance.m_nview.GetZDO().m_uid));
        }
    }
}

