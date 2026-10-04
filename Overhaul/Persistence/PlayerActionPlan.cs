using System;

namespace Overhaul.Persistence
{
    internal sealed class PlayerActionPlan
    {
        internal readonly PlayerWorldAction Change;
        internal readonly Action Publish;
        internal InventoryMoveLayout NextLayout;
        // Optional main-thread timer/validation. No database transaction is held while waiting.
        internal Func<bool> Ready;
        // Release only uncommitted reservations when a timed action is cancelled.
        internal Action Cancel;
        internal PlayerActionPlan(PlayerWorldAction change, Action publish) { Change = change; Publish = publish; }
    }
}

