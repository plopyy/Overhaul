namespace Overhaul.Persistence
{
    internal static class GameActorCleanup
    {
        internal static void Forget(ZDOID actor)
        {
            if(actor.IsNone())return;
            GameArrivalRuntime.ForgetActor(actor);
            GameAttachmentRuntime.Forget(actor);
            PlayerFishingCastGame.Forget(actor);GameAttackRuntime.Forget(actor);GameAttackRuntime.ForgetControls(actor);
            GameBowDraw.Forget(actor);GameWeaponReload.Forget(actor);GameBlockControl.Forget(actor);GameHitFeedback.Forget(actor);
            GameStatusRuntime.Forget(actor);GameStaffGuardRuntime.Forget(actor);GameGuardianPower.Forget(actor);GameMovementControl.Forget(actor);
        }
    }
}
