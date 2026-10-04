using System;

namespace Overhaul.Persistence
{
    internal sealed class PlayerActionPlan
    {
        internal readonly PlayerWorldAction Change;
        internal readonly Action Publish;
        internal PlayerActionPlan(PlayerWorldAction change, Action publish) { Change = change; Publish = publish; }
    }
}
