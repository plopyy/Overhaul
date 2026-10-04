using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    // Elapsed active play time, supplied by the server. Wall-clock time while offline is excluded.
    // Timer checkpoints do not invalidate inventory revisions. Food actions carry the clock at
    // preparation so a checkpoint between preparation and commit cannot restore elapsed time.
    internal static class PlayerFoodClock
    {
        internal const string Key = "overhaul_food_clock";
        internal static double Read(IEnumerable<PlayerChange> rows)
        {
            var row = rows.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == Key);
            return row == null ? 0 : Convert.ToDouble(row.Values[2]);
        }
        internal static PlayerChange Anchor(double time) => new PlayerChange("state",false,Key,null,time,null,null);
        internal static PlayerBatch Rebase(PlayerBatch batch,double now)
        {
            if (!batch.Changes.Any(r => r.Table == "food")) return batch;
            var anchor = batch.Changes.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == Key);
            if (anchor == null || anchor.Delete) throw new InvalidDataException("Food action has no server clock");
            double elapsed = now - Convert.ToDouble(anchor.Values[2]);
            if (elapsed < 0) throw new InvalidDataException("Food clock moved backwards");
            return new PlayerBatch(batch.Operation,batch.ExpectedRevision,batch.Changes.Where(r => r != anchor).Select(r =>
            {
                if (r.Table != "food" || r.Delete) return r;
                var v = r.Values; double remaining = Convert.ToDouble(v[2]) - elapsed;
                return remaining <= 0 ? new PlayerChange("food",true,v[0]) : new PlayerChange("food",false,v[0],v[1],remaining);
            }));
        }
    }
    internal sealed partial class PlayerDatabase
    {
        internal double FoodClock => PlayerFoodClock.Read(ReadTables("state"));
        internal void AdvanceFood(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (!Complete) throw new InvalidOperationException("Incomplete food clock");
            if (seconds == 0) return;
            db.Transaction(() =>
            {
                ApplyRows(new[] { PlayerFoodClock.Anchor(FoodClock + seconds) });
                db.Write("UPDATE food SET remaining=MAX(0,remaining-?)",seconds);
                db.Write("DELETE FROM food WHERE remaining<=0");
            });
        }
    }
    internal sealed partial class PlayerDatabaseWriter
    {
        internal Task<bool> AdvanceFood(PlayerIdentity identity,double seconds) => Submit(() =>
        { Get(identity).AdvanceFood(seconds); return true; },true);
    }
}
