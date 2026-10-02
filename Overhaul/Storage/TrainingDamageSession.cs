using System;

namespace Overhaul.Storage
{
    // Sufficient statistics of this temporary session; no growing per-hit history.
    internal sealed class TrainingDamageSession
    {
        internal double Total, First, Last, Peak;
        internal int Count;
        internal double Cutoff = double.NegativeInfinity;
        internal double Duration => Count == 0 ? 0 : Last - First;
        internal double Dps => Duration > 0 ? Total / Duration : 0;
        internal void Reset(double time)
        {
            Total = First = Last = Peak = 0; Count = 0;
            Cutoff = Math.Max(Cutoff, time);
        }
        internal void Tick(double time)
        {
            if (Count > 0 && time - Last >= 10) Reset(time);
        }
        internal void Add(double damage, double time)
        {
            if (damage <= 0 || double.IsNaN(damage) || double.IsInfinity(damage) ||
                double.IsNaN(time) || double.IsInfinity(time) || time <= Cutoff) return;
            if (Count > 0 && time - Last >= 10) Reset(Last + 10);
            if (Count == 0) First = Last = time;
            First = Math.Min(First, time); Last = Math.Max(Last, time);
            Total += damage; Peak = Math.Max(Peak, damage); Count++;
        }
    }
}
