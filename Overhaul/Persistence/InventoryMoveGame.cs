using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Overhaul.Storage;
using EquipmentAndQuickSlots;

namespace Overhaul.Persistence
{
    internal static class InventoryMoveGame
    {
        internal static InventoryMoveRpc Client;
        internal static bool SharedView(Container container) => Access.HasViewers(container);
        internal static InventoryMoveLease Source(ZRpc rpc,InventoryMoveRequest request,out Container chest)
        {
            var command = request.Gameplay; var target = ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            chest = target ? target.GetComponent<Container>() : null; var access = Access.Viewer(rpc,chest);
            if (access == null) throw new InvalidOperationException("Source inventory is not open");
            return access.Reserve(new InventoryMoveRequest { ContainerUser = command.TargetUser,ContainerId = command.TargetId,
                Action = request.Action,Gameplay = command });
        }
        private static readonly List<InventoryMoveRpc> connections = new List<InventoryMoveRpc>();
        internal static void FinishSession()
        {
            foreach (var endpoint in connections) endpoint.Dispose();
            for (int pass = 0; pass < 4; pass++)
            {
                GamePersistence.Players?.Flush().GetAwaiter().GetResult();
                foreach (var endpoint in connections.ToArray()) endpoint.Tick();
            }
            connections.Clear(); Client = null; Access.Clear();
        }
        internal static void Broadcast(Container container, PlayerBatch effect, ZRpc except)
        {
            foreach (var endpoint in connections.ToArray()) if (endpoint.Views(container, except)) endpoint.Notify(container, effect);
        }
        internal static InventoryMoveRpc BindServer(ZRpc rpc, PlayerAdmission.Session session)
        {
            var endpoint = new InventoryMoveRpc(rpc, session, GamePersistence.Players, PlayerLayout(session.Snapshot.Rows));
            connections.Add(endpoint); return endpoint;
        }
        internal static InventoryMoveRpc BindClient(ZRpc rpc, string nonce, long revision)
        {
            Client?.Dispose(); Client = new InventoryMoveRpc(rpc, nonce, revision); connections.Add(Client); return Client;
        }
        internal static InventoryMoveLayout PlayerLayout(IEnumerable<PlayerChange> rows)
        {
            int visible = PlayerRows(rows) + Slots.ExtraRows;
            var items = Prefabs(); var cells = new Dictionary<int, IEnumerable<int>>(); var equipment = new Dictionary<int, string>(); var ammo = new List<int>(); var cosmetics = new List<int>();
            for (int y = 0; y < visible; y++) for (int x = 0; x < Slots.VanillaInventoryWidth; x++) cells.Add(y * 256 + x, null);
            foreach (var slot in Slots.slots.Where(s => s != null && s.IsActive && !s.IsEmptySlot))
            {
                int key = (visible + slot.Index / Slots.VanillaInventoryWidth) * 256 + slot.Index % Slots.VanillaInventoryWidth;
                cells[key] = items.Where(p => slot.ItemFits(p.Value)).Select(p => p.Key).ToArray();
                if (slot.IsEquipmentSlot) equipment[key] = slot.ID;
                if (slot.IsAmmoSlot) ammo.Add(key);
                if (slot.IsCosmeticSlot) cosmetics.Add(key);
            }
            return Layout(cells, items, equipment, ammo).WithCosmetics(cosmetics);
        }
        internal static int PlayerRows(IEnumerable<PlayerChange> rows)
        {
            int visible = Slots.VanillaInventoryHeight;
            foreach (var row in rows.Where(r => r.Table == "knowledge" && !r.Delete).Select(r => r.Values))
                if ((string)row[0] == "uniques")
                {
                    string[] key = ((string)row[1]).Split(' ');
                    if (key.Length >= 2 && key[0].Equals(Player.InventoryRowsKey, StringComparison.OrdinalIgnoreCase) && int.TryParse(key[1], out int count))
                        visible = Math.Max(1, Math.Min(9, count));
                }
            return visible;
        }
        internal static InventoryMoveLayout ContainerLayout(Container container)
        {
            var items = Prefabs(); var allowed = items.Where(p => TroughInventory.Allows(container.GetInventory(), p.Value)).Select(p => p.Key).ToArray();
            var cells = new Dictionary<int, IEnumerable<int>>(); var inv = container.GetInventory();
            for (int y = 0; y < inv.GetHeight(); y++) for (int x = 0; x < inv.GetWidth(); x++) cells.Add(y * 256 + x, allowed);
            return Layout(cells, items, null);
        }
        private static Dictionary<int, ItemDrop.ItemData> Prefabs() => ObjectDB.instance.m_itemByHash
            .Where(p => p.Value && p.Value.GetComponent<ItemDrop>()).ToDictionary(p => p.Key, p => p.Value.GetComponent<ItemDrop>().m_itemData);
        private static InventoryMoveLayout Layout(Dictionary<int, IEnumerable<int>> cells, Dictionary<int, ItemDrop.ItemData> items, Dictionary<int, string> equipment, IEnumerable<int> ammo = null)
            => new InventoryMoveLayout(cells, items.ToDictionary(p => p.Key, p => p.Value.m_shared.m_maxStackSize),
                items.Where(p => p.Value.m_shared.m_questItem).Select(p => p.Key), equipment,
                items.ToDictionary(p => p.Key, p => ((int)p.Value.m_shared.m_itemType).ToString("D6") + ":" +
                    (Localization.instance != null ? Localization.instance.Localize(p.Value.m_shared.m_name) : p.Value.m_shared.m_name)), ammo);

        internal sealed class Access : IDisposable
        {
            private static readonly Dictionary<Container,HashSet<Access>> viewers = new Dictionary<Container,HashSet<Access>>();
            private readonly ZRpc rpc;
            private readonly PlayerAdmission.Session session;
            private Container opened;
            internal bool Views(Container value) => opened == value;
            internal static bool HasViewers(Container value) => value && viewers.ContainsKey(value);
            internal static Access Viewer(ZRpc connection,Container value)
            {
                if (value && viewers.TryGetValue(value,out var group))
                    return group.FirstOrDefault(a => ReferenceEquals(a.rpc,connection) && a.opened == value);
                return null;
            }
            internal static void Clear() => viewers.Clear();
            internal Access(ZRpc rpc, PlayerAdmission.Session session) { this.rpc = rpc; this.session = session; }
            internal InventoryMoveLease Reserve(InventoryMoveRequest request)
            {
                var peer = ZNet.instance.GetPeer(rpc);
                var actor = PlayerSessionGame.Actor(rpc);
                var id = new ZDOID(request.ContainerUser, request.ContainerId);
                var instance = ZNetScene.instance.FindInstance(id);
                var chest = instance ? instance.GetComponent<Container>() : null;
                var zdo = ChestAccess.Data(chest);
                long playerId = actor?.GetLong(ZDOVars.s_playerID, 0) ?? 0;
                var persistentId = session.Snapshot.Rows.FirstOrDefault(r => r.Table == "state" && (string)r.Values[0] == "player_id");
                if (persistentId == null || Convert.ToInt64(persistentId.Values[1]) != playerId || playerId == 0 || zdo == null ||
                    Vector3.Distance(actor.GetPosition(), chest.transform.position) > 5f || !ChestAccess.Allows(chest, playerId) ||
                    !chest.CheckAccess(playerId) || !ChestAccess.WardAccess(chest, playerId) || ChestAccess.Leased(chest) || MoveReservation.Busy(chest) ||
                    (!viewers.ContainsKey(chest) && chest != opened && (chest.IsInUse() || zdo.GetInt(ZDOVars.s_inUse, 0) != 0)) ||
                    (chest.m_wagon && chest.m_wagon.InUse())) throw new InvalidOperationException("Container access denied");
                if (!request.Open && opened != chest) throw new InvalidOperationException("Container has not been opened by this session");
                if (request.Open && opened && opened != chest) Close();
                zdo.SetOwner(ZNet.GetUID()); chest.Load(); chest.Save();
                long objectId = GamePersistence.SnapshotInventory(zdo);
                try
                {
                    if (!viewers.TryGetValue(chest, out var group)) { group = new HashSet<Access>(); viewers.Add(chest, group); }
                    group.Add(this);
                    opened = chest; chest.m_inUse = true; zdo.Set(ZDOVars.s_inUse, 1);
                    chest.UpdateUseVisual();
                    return new InventoryMoveLease(objectId, ContainerLayout(chest).Excluding(GamePersistence.ReservedSlots(id)), effect =>
                    {
                        var contents = InventoryMovePresentation.Prepare(chest.GetInventory(), effect, false);
                        chest.GetInventory().m_inventory.Clear(); chest.GetInventory().m_inventory.AddRange(contents);
                        chest.GetInventory().Changed(); chest.Save();
                        Broadcast(chest, effect, request.Gameplay == null ? rpc : null);
                    }, keys => GamePersistence.ReserveSlots(id, keys), keys =>
                    {
                        GamePersistence.ReleaseSlots(zdo, keys);
                        UseState(chest);
                        if (peer != null) ZDOMan.instance.ForceSendZDO(peer.m_uid, id);
                    });
                }
                catch { Close(); throw; }
            }
            private static void UseState(Container chest)
            {
                var data = ChestAccess.Data(chest); if (data == null) return;
                chest.m_inUse = viewers.ContainsKey(chest) || GamePersistence.InventoryReserved(data.m_uid);
                data.Set(ZDOVars.s_inUse, chest.m_inUse ? 1 : 0);
                chest.UpdateUseVisual();
            }
            internal void Close()
            {
                if (!opened) return;
                var chest = opened; opened = null;
                if (viewers.TryGetValue(chest, out var group)) { group.Remove(this); if (group.Count == 0) viewers.Remove(chest); }
                UseState(chest);
            }
            public void Dispose() => Close();
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class Pump
        {
            private static void Postfix()
            {
                foreach (var endpoint in connections.ToArray())
                { endpoint.Tick(); if (endpoint.Finished) connections.Remove(endpoint); }
            }
        }
        [HarmonyPatch(typeof(ZNet), "StopAll")]
        private static class Stop
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            { foreach (var endpoint in connections) endpoint.Dispose(); Client = null; }
        }
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        private static class DrainBeforeSceneDestruction
        {
            [HarmonyPriority(Priority.First + 100)]
            private static void Prefix() => FinishSession();
        }
    }
}
