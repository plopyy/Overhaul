using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    internal sealed partial class PlayerDatabaseWriter
    {
        private bool transferSchema;
        private void PrepareTransfers(SqliteDatabase world)
        {
            if (transferSchema) return;
            PlayerTransferJournal.PrepareSchema(world);
            world.Execute("CREATE TABLE IF NOT EXISTS container_revisions(object_id INTEGER PRIMARY KEY REFERENCES containers(object_id) ON DELETE CASCADE,revision INTEGER NOT NULL)");
            world.Execute("CREATE TABLE IF NOT EXISTS container_slot_revisions(object_id INTEGER NOT NULL REFERENCES containers(object_id) ON DELETE CASCADE,slot INTEGER NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(object_id,slot)) WITHOUT ROWID");
            world.InventoryRevisions = true;
            PlayerTransferJournal.Recover(world, Get);
            transferSchema = true;
        }
        internal Task<bool> RecoverTransfers()
        {
            if (shared == null) throw new InvalidOperationException("Transfers require the world executor");
            return SubmitWorld(world => { PrepareTransfers(world); PlayerTransferJournal.Recover(world, Get); return true; });
        }
        internal Task<PlayerSnapshot> ContainerState(long objectId)
        {
            if (shared == null) throw new InvalidOperationException("Container reads require the world executor");
            return SubmitWorld(world =>
            {
                PrepareTransfers(world);
                if (PlayerTransferJournal.IsLocked(world, objectId)) throw new InvalidOperationException("Container transfer requires recovery");
                return new PlayerSnapshot(ContainerVersions.Revision(world, objectId), WorldInventorySlots.ReadAll(world, objectId));
            });
        }
        internal Task<InventoryMoveResult> PlanTransfer(PlayerIdentity identity, InventoryMoveAction action,
            InventoryMoveLayout playerLayout, long objectId = 0, InventoryMoveLayout containerLayout = null)
        {
            if (shared == null) throw new InvalidOperationException("Inventory transfers require the world executor");
            return SubmitWorld(world =>
            {
                PrepareTransfers(world);
                var player = Get(identity);
                if (player.Revision != action.PlayerRevision) throw new InvalidOperationException("Stale player inventory revision");
                if (action.UsesContainer && (objectId <= 0 ||
                    PlayerTransferJournal.IsLocked(world, objectId))) throw new InvalidOperationException("Stale or locked container inventory");
                var result = InventoryMoveEngine.Prepare(action, player.InventoryState().Rows, playerLayout,
                    action.UsesContainer ? WorldInventorySlots.ReadAll(world, objectId) : null, containerLayout);
                if (action.UsesContainer) ContainerVersions.Validate(world, objectId, action, ContainerVersions.Slots(result.Container));
                return result;
            });
        }
        internal Task<InventoryMoveResult> CommitTransfer(PlayerIdentity identity, InventoryMoveAction action, InventoryMoveResult result, long objectId = 0)
        {
            return SubmitWorld(world =>
            {
                PrepareTransfers(world);
                var player = Get(identity);
                if (player.Revision != action.PlayerRevision) throw new InvalidOperationException("Stale player inventory revision");
                if (result.Player.Operation != action.Operation || result.Player.ExpectedRevision != action.PlayerRevision)
                    throw new InvalidDataException("Prepared move does not belong to this action");
                if (!action.UsesContainer) { player.CommitInventory(result.Player); return result; }
                int[] slots = ContainerVersions.Slots(result.Container);
                ContainerVersions.Validate(world, objectId, action, slots);
                var committed = new InventoryMoveResult(result.Player, new PlayerBatch(action.Operation,
                    ContainerVersions.Revision(world, objectId), result.Container.Changes), result.Moved);
                string digest;
                using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    objectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + result.Container.Digest()))).Replace("-", "");
                PlayerTransferJournal.Stage(world, player, identity, result.Player, digest, new[] { objectId }, db =>
                {
                    ContainerVersions.Validate(db, objectId, action, slots);
                    foreach (var group in result.Container.Changes.GroupBy(r => Convert.ToInt32(r.Values[2]) * 256 + Convert.ToInt32(r.Values[1])))
                        WorldInventorySlots.Write(db, objectId, group.Key % 256, group.Key / 256, group.Where(r => !r.Delete));
                    ContainerVersions.Advance(db, objectId, slots);
                });
                PlayerTransferJournal.Finish(world, player, identity, action.Operation);
                return committed;
            });
        }
    }
}
