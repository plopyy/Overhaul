using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static class PlayerEffectClock
    {
        internal const string Key = "overhaul_effect_clock";
        internal static double Read(IEnumerable<PlayerChange> rows)
        {
            var row = rows.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == Key);
            return row == null ? 0 : Convert.ToDouble(row.Values[2]);
        }
        internal static PlayerChange Anchor(double time) => new PlayerChange("state",false,Key,null,time,null,null);
        internal static PlayerBatch Rebase(PlayerBatch batch,double now)
        {
            if (!batch.Changes.Any(r => r.Table == "effects")) return batch;
            var anchor = batch.Changes.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == Key);
            if (anchor == null || anchor.Delete) throw new InvalidDataException("Effect action has no server clock");
            double elapsed = now - Convert.ToDouble(anchor.Values[2]);
            if (elapsed < 0) throw new InvalidDataException("Effect clock moved backwards");
            return new PlayerBatch(batch.Operation,batch.ExpectedRevision,batch.Changes.Where(r => r != anchor).Select(r =>
            {
                if (r.Table != "effects" || r.Delete) return r;
                var v = r.Values; double age = Convert.ToDouble(v[3]) + elapsed;
                return age >= Convert.ToDouble(v[4]) ? new PlayerChange("effects",true,v[0]) : new PlayerChange("effects",false,v[0],v[1],v[2],age,v[4]);
            }));
        }
    }
    internal sealed partial class PlayerDatabase
    {
        internal double EffectClock
        {
            get { using (var row = db.Query("SELECT real FROM state WHERE key=?",PlayerEffectClock.Key)) return row.Read() ? Convert.ToDouble(row.Value(0)) : 0; }
        }
    }
}
