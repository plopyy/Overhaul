using HarmonyLib;

namespace Auga
{
    [UiModule(UiModule.TextInput)]
    public static class TextInput_Setup
    {
        [HarmonyPatch(typeof(TextInput), nameof(TextInput.Awake))]
        public static class TextInput_Awake_Patch
        {
            public static bool Prefix(TextInput __instance)
            {
                if (__instance.name.StartsWith("AugaTextInput")) return true;
                var original = __instance.gameObject;
                var replacement = UnityEngine.Object.Instantiate(Auga.Assets.TextInput, original.transform.parent, false);
                replacement.transform.SetSiblingIndex(original.transform.GetSiblingIndex());
                PausePrefabInstaller.CopyCanvas(original, replacement);
                original.SetActive(false);
                UnityEngine.Object.Destroy(original);
                return false;
            }
        }

        [HarmonyPatch(typeof(TextInput), "OnDestroy")]
        internal static class PreserveActiveInput
        {
            private static bool Prefix(TextInput __instance)
            {
                return ReferenceEquals(AccessTools.Field(typeof(TextInput), "m_instance").GetValue(null), __instance);
            }
        }
    }
}
