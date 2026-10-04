using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal sealed class InventoryMoveRequest
    {
        internal string Nonce;
        internal bool Open;
        internal long ContainerUser;
        internal uint ContainerId;
        internal InventoryMoveAction Action;
    }
    internal sealed class InventoryMoveReply
    {
        internal string Nonce;
        internal bool Accepted, Snapshot, ContainerAllowed;
        internal bool Notification;
        internal long ContainerUser;
        internal uint ContainerId;
        internal HashSet<int> PreserveContainerSlots;
        internal PlayerBatch Player, Container;
    }
    internal static class InventoryMoveProtocol
    {
        internal const int Limit = 16 * 1024 * 1024;
        internal static byte[] Encode(InventoryMoveRequest request)
        {
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream))
            {
                Header(w, request.Nonce); w.Write(request.Open); w.Write(request.ContainerUser); w.Write(request.ContainerId);
                var a = request.Action; w.Write(a.Operation); w.Write(a.PlayerRevision); w.Write(a.ContainerRevision);
                w.Write((int)a.Kind); w.Write(a.From); w.Write(a.To); w.Write(a.FromX); w.Write(a.FromY); w.Write(a.ToX); w.Write(a.ToY); w.Write(a.Amount);
                var slots = a.SlotVersions.ToArray(); w.Write(slots.Length);
                foreach (var slot in slots) { w.Write((ushort)slot.Key); w.Write(slot.Value); }
                return stream.ToArray();
            }
        }
        internal static InventoryMoveRequest Request(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 65536) throw new InvalidDataException("Inventory request exceeds limit");
            using (var r = NativeFormat.Reader(bytes))
            {
                var value = new InventoryMoveRequest { Nonce = Header(r), Open = r.ReadBoolean(), ContainerUser = r.ReadInt64(), ContainerId = r.ReadUInt32() };
                string operation = r.ReadString(); long player = r.ReadInt64(), container = r.ReadInt64(); var kind = (InventoryMoveKind)r.ReadInt32();
                int from = r.ReadInt32(), to = r.ReadInt32(), fx = r.ReadInt32(), fy = r.ReadInt32(), tx = r.ReadInt32(), ty = r.ReadInt32(), amount = r.ReadInt32();
                int count = r.ReadInt32(); if (count < 0 || count > 4096) throw new InvalidDataException("Too many slot revisions");
                var slots = new Dictionary<int,long>(); for (int i = 0; i < count; i++) slots.Add(r.ReadUInt16(), r.ReadInt64());
                value.Action = new InventoryMoveAction(operation, player, container, kind, from, to, fx, fy, tx, ty, amount, slots);
                NativeFormat.End(r); return value;
            }
        }
        internal static byte[] Encode(InventoryMoveReply reply)
        {
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream))
            {
                Header(w, reply.Nonce); w.Write(reply.Accepted); w.Write(reply.Snapshot); w.Write(reply.ContainerAllowed);
                w.Write(reply.Notification); w.Write(reply.ContainerUser); w.Write(reply.ContainerId);
                Blob(w, PlayerBatchFormat.Encode(reply.Player));
                if (reply.ContainerAllowed) Blob(w, PlayerBatchFormat.Encode(reply.Container));
                if (stream.Length > Limit) throw new InvalidDataException("Inventory reply exceeds limit");
                return stream.ToArray();
            }
        }
        internal static InventoryMoveReply Reply(byte[] bytes)
        {
            if (bytes == null || bytes.Length > Limit) throw new InvalidDataException("Inventory reply exceeds limit");
            using (var r = NativeFormat.Reader(bytes))
            {
                var value = new InventoryMoveReply { Nonce = Header(r), Accepted = r.ReadBoolean(), Snapshot = r.ReadBoolean(), ContainerAllowed = r.ReadBoolean() };
                value.Notification = r.ReadBoolean(); value.ContainerUser = r.ReadInt64(); value.ContainerId = r.ReadUInt32();
                value.Player = PlayerBatchFormat.Decode(NativeFormat.Bytes(r));
                if (value.ContainerAllowed)
                {
                    value.Container = PlayerBatchFormat.Decode(NativeFormat.Bytes(r));
                    if (value.Container.Operation != value.Player.Operation) throw new InvalidDataException("Inventory reply operations differ");
                }
                NativeFormat.End(r); return value;
            }
        }
        private static void Blob(BinaryWriter w, byte[] value) { w.Write(value.Length); w.Write(value); }
        private static void Header(BinaryWriter w, string nonce)
        { if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid session nonce"); w.Write(2); w.Write(nonce); }
        private static string Header(BinaryReader r)
        {
            if (r.ReadInt32() != 2) throw new InvalidDataException("Unsupported inventory protocol");
            string value = r.ReadString(); if (!Guid.TryParseExact(value, "N", out _)) throw new InvalidDataException("Invalid inventory session"); return value;
        }
    }
}
