using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace OverhaulAnimationPreview
{
    // Developer tool: plays FBX animations made on the player skeleton on the local character,
    // from a folder, through a debug window. Reloads a file when it is exported again.
    [BepInPlugin(Guid, "Overhaul Animation Preview", "1.0.0")]
    [BepInDependency("plopyy.valheim.Overhaul", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "plopyy.valheim.Overhaul.AnimationPreview";
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static string Folder;
        private static string directory;

        private void Awake()
        {
            directory = Path.GetDirectoryName(Info.Location);
            ToggleKey = Config.Bind("General", "Window key", new KeyboardShortcut(KeyCode.F7), "Shows or hides the animation preview window.");
            var folder = Config.Bind("General", "Animation folder", "AnimationPreview",
                "Folder of the .fbx files, relative to the BepInEx folder (or an absolute path).");
            Folder = Path.IsPathRooted(folder.Value) ? folder.Value : Path.Combine(Paths.BepInExRootPath, folder.Value);
            Directory.CreateDirectory(Folder);
            Assimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(Path.Combine(directory, "assimp.dll"));
            new Harmony(Guid).PatchAll();
            gameObject.AddComponent<Previewer>();
        }

        // The window's texts come from Localisation/translations<language>.json next to the DLL.
        internal static void AddTranslations(Localization localization)
        {
            if (localization == null || directory == null) return;
            string code = localization.GetSelectedLanguage() == "French" ? "FR" : "EN";
            var file = Path.Combine(directory, "Localisation", "translations" + code + ".json");
            if (!File.Exists(file)) file = Path.Combine(directory, "Localisation", "translationsEN.json");
            if (!File.Exists(file)) return;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                localization.AddWord(Regex.Unescape(m.Groups[1].Value), Regex.Unescape(m.Groups[2].Value));
        }

        [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
        private static class LanguageChanged
        {
            private static void Postfix(Localization __instance) => AddTranslations(__instance);
        }
    }
}
