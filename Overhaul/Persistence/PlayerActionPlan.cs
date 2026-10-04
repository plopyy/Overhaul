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
        internal PlayerActionPlan(PlayerWorldAction change, Action publish) { Change = change; Publish = publish; }
    }
}
