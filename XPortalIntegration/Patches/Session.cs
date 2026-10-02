using HarmonyLib;
namespace XPortal.Patches
{
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
    internal static class Session_End
    {
        static void Prefix() => XPortal.ClearSession();
    }
}
