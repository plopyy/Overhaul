using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    internal sealed class PlayerWorldAction
    {
        internal readonly PlayerBatch Player;
        private readonly Dictionary<long, ObjectRecord> objects;
        internal IDictionary<long, ObjectRecord> Objects => objects.ToDictionary(p => p.Key, p => Copy(p.Value));
        internal PlayerWorldAction(PlayerBatch player, IDictionary<long, ObjectRecord> objects)
        {
            Player = player ?? throw new ArgumentNullException(nameof(player));
            if (objects == null || objects.Count > 128 || objects.Any(p => p.Key <= 0 || p.Value != null && p.Value.Id != p.Key))
                throw new InvalidDataException("Invalid world objects in player action");
            this.objects = objects.ToDictionary(p => p.Key, p => Copy(p.Value));
        }
        private static ObjectRecord Copy(ObjectRecord value)
        {
            if (value == null) return null;
            var copy = new ObjectRecord { Id = value.Id, User = value.User, NetworkId = value.NetworkId, Chunk = value.Chunk,
                Prefab = value.Prefab, Flags = value.Flags, Order = value.Order, Name = value.Name,
                Position = (float[])value.Position.Clone(), Rotation = (float[])value.Rotation.Clone(),
                ConnectionType = value.ConnectionType, ConnectionHash = value.ConnectionHash, TargetUser = value.TargetUser, TargetId = value.TargetId,
                ProtectedInventorySlots = value.ProtectedInventorySlots == null ? null : new HashSet<int>(value.ProtectedInventorySlots) };
            foreach (var p in value.Properties)
                copy.Properties.Add(new PropertyRecord { Type = p.Type, Key = p.Key, Name = p.Name,
                    Value = p.Value is byte[] bytes ? (object)bytes.Clone() : p.Value is float[] vector ? vector.Clone() : p.Value });
            return copy;
        }
        internal string Digest()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Player.Digest());
                foreach (var pair in objects.OrderBy(p => p.Key))
                {
                    writer.Write(pair.Key); writer.Write(pair.Value != null); var value = pair.Value; if (value == null) continue;
                    writer.Write(value.User); writer.Write(value.NetworkId); writer.Write(value.Chunk); writer.Write(value.Prefab); writer.Write(value.Flags); writer.Write(value.Order);
                    writer.Write(value.Name ?? ""); foreach (float n in value.Position) writer.Write(n); foreach (float n in value.Rotation) writer.Write(n);
                    writer.Write(value.ConnectionType); writer.Write(value.ConnectionHash); writer.Write(value.TargetUser.HasValue);
                    if (value.TargetUser.HasValue) writer.Write(value.TargetUser.Value); writer.Write(value.TargetId);
                    var properties = value.Properties.OrderBy(p => p.Type, StringComparer.Ordinal).ThenBy(p => p.Key).ToArray(); writer.Write(properties.Length);
                    foreach (var p in properties)
                    {
                        writer.Write(p.Type); writer.Write(p.Key); writer.Write(p.Name ?? "");
                        switch (p.Type)
                        {
                            case "int": writer.Write(Convert.ToInt32(p.Value)); break;
                            case "long": writer.Write(Convert.ToInt64(p.Value)); break;
                            case "float": writer.Write(Convert.ToSingle(p.Value)); break;
                            case "string": writer.Write((string)p.Value); break;
                            case "bytes": var bytes = (byte[])p.Value; writer.Write(bytes.Length); writer.Write(bytes); break;
                            case "vector3": case "quaternion": var vector = (float[])p.Value; writer.Write(vector.Length); foreach (float n in vector) writer.Write(n); break;
                            default: throw new InvalidDataException("Unknown action object property");
                        }
                    }
                }
                writer.Flush(); using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }
    }
    internal sealed partial class PlayerDatabaseWriter
    {
        internal Task<PlayerSnapshot> ActionState(PlayerIdentity identity) => Submit(() =>
        {
            var db = Get(identity); return new PlayerSnapshot(db.Revision, db.ActionState());
        }, true);
        internal Task<PlayerBatch> CommitAction(PlayerIdentity identity, PlayerWorldAction action) => SubmitWorld(world =>
        {
            PrepareTransfers(world); var player = Get(identity);
            if (player.Revision != action.Player.ExpectedRevision) throw new InvalidOperationException("Stale player action revision");
            var changes = action.Objects;
            if (changes.Count == 0) player.CommitDelta(action.Player);
            else
            {
                PlayerTransferJournal.Stage(world, player, identity, action.Player, action.Digest(), changes.Keys, db =>
                {
                    foreach (var pair in changes)
                        if (pair.Value == null) ObjectSql.Delete(db, pair.Key); else ObjectSql.Write(db, pair.Value);
                });
                PlayerTransferJournal.Finish(world, player, identity, action.Player.Operation);
            }
            return action.Player;
        });
    }
}
