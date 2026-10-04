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
        private readonly Dictionary<long, PlayerContainerAction> containers;
        private readonly string[] worldKeys;
        internal string[] WorldKeys => (string[])worldKeys.Clone();
        internal readonly Dictionary<long,PlayerBatch> CommittedContainers = new Dictionary<long,PlayerBatch>();
        internal IDictionary<long,PlayerContainerAction> Containers => new Dictionary<long,PlayerContainerAction>(containers);
        internal IDictionary<long, ObjectRecord> Objects => objects.ToDictionary(p => p.Key, p => Copy(p.Value));
        internal PlayerWorldAction(PlayerBatch player, IDictionary<long, ObjectRecord> objects) : this(player,objects,null) { }
        internal PlayerWorldAction(PlayerBatch player, IDictionary<long, ObjectRecord> objects, IDictionary<long,PlayerContainerAction> containers) : this(player,objects,containers,Array.Empty<string>()) { }
        internal PlayerWorldAction(PlayerBatch player, IDictionary<long, ObjectRecord> objects, IDictionary<long,PlayerContainerAction> containers, IEnumerable<string> keys)
        {
            Player = player ?? throw new ArgumentNullException(nameof(player));
            if (objects == null || objects.Count > 128 || objects.Any(p => p.Key <= 0 || p.Value != null && p.Value.Id != p.Key))
                throw new InvalidDataException("Invalid world objects in player action");
            this.objects = objects.ToDictionary(p => p.Key, p => Copy(p.Value));
            worldKeys = (keys ?? throw new ArgumentNullException(nameof(keys))).Distinct(StringComparer.Ordinal).OrderBy(k => k,StringComparer.Ordinal).ToArray();
            if (worldKeys.Length > 16 || worldKeys.Any(k => string.IsNullOrEmpty(k) || k.Length > 256 || k.Any(char.IsWhiteSpace) || k != k.ToLowerInvariant()))
                throw new InvalidDataException("Invalid world progress key");
            this.containers = containers == null ? new Dictionary<long,PlayerContainerAction>() : new Dictionary<long,PlayerContainerAction>(containers);
            if (worldKeys.Length != 0 && objects.Count + this.containers.Count == 0) throw new InvalidDataException("World progress requires a reserved target");
            if (this.containers.Count > 128 || this.containers.Any(p => p.Key <= 0 || p.Value == null || objects.ContainsKey(p.Key)))
                throw new InvalidDataException("Invalid crafting containers");
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
                foreach (var pair in containers.OrderBy(p => p.Key))
                { writer.Write(pair.Key); writer.Write(pair.Value.Before.Digest()); writer.Write(pair.Value.After.Digest()); }
                foreach (string key in worldKeys) writer.Write(key);
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
            var changes = action.Objects; var containers = action.Containers;
            foreach (string key in action.WorldKeys)
                using (var row = world.Query("SELECT 1 FROM world_keys WHERE source=? AND key=?","progress",key))
                    if (row.Read()) throw new InvalidOperationException("World progress key already exists");
            foreach (var pair in containers) pair.Value.Validate(world,pair.Key);
            var batch = action.Player.Changes.Any(r => r.Table == "food") ? PlayerFoodClock.Rebase(action.Player,player.FoodClock) : action.Player;
            if (batch.Changes.Any(r => r.Table == "effects")) batch = PlayerEffectClock.Rebase(batch,player.EffectClock);
            if (changes.Count == 0 && containers.Count == 0) player.CommitDelta(batch);
            else
            {
                PlayerTransferJournal.Stage(world, player, identity, batch, action.Digest(), changes.Keys.Concat(containers.Keys), db =>
                {
                    foreach (string key in action.WorldKeys) db.Write("INSERT INTO world_keys(source,key) VALUES(?,?)","progress",key);
                    foreach (var pair in changes)
                        if (pair.Value == null) ObjectSql.Delete(db, pair.Key); else ObjectSql.Write(db, pair.Value);
                    foreach (var pair in containers)
                        action.CommittedContainers[pair.Key] = pair.Value.Apply(db,pair.Key);
                });
                PlayerTransferJournal.Finish(world, player, identity, action.Player.Operation);
            }
            return batch;
        });
    }
}


