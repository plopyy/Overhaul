using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EquipmentAndQuickSlots
{
    // Integrated module: no second BepInEx plugin or separate DLL.
    public static class EquipmentAndQuickSlots
    {
        public const string PluginId = "randyknapp.mods.equipmentandquickslots";
        public const string Version = "3.1.3";
        public static Sprite PaperdollMale, PaperdollFemale;
        public static GameObject Paperdolls;
        public static bool HasAuga => false; // Existing Overhaul prefab bridge owns the layout.
        public static bool IsInitialized { get; private set; }
        private static ManualLogSource logger;
        private static Harmony harmony;
        private static AssetBundle assets;

        internal static ConfigFile OpenConfig(string directory, string legacyDirectory)
        {
            var path = Path.Combine(directory, "plopyy.valheim.Overhaul.EquipmentSlots.cfg");
            var legacy = Path.Combine(legacyDirectory, PluginId + ".cfg");
            if (!File.Exists(path) && File.Exists(legacy)) File.Copy(legacy, path);
            return new ConfigFile(path, true);
        }

        internal static void Initialize(ManualLogSource log)
        {
            if (IsInitialized) return;
            // Jotunn requires custom synchronized files below BepInEx/config.
            var config = OpenConfig(Paths.ConfigPath, Paths.ConfigPath);
            Initialize(log, config);
            Jotunn.Managers.SynchronizationManager.Instance.RegisterCustomConfig(config);
        }

        internal static void Initialize(ManualLogSource log, ConfigFile config)
        {
            if (IsInitialized) return;
            logger = log;
            new ValConfig(config);
            Slots.InitializeSlots();
            if (!Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                assets = LoadAssetBundle("eaqs");
                PaperdollMale = assets.LoadAsset<Sprite>("PaperdollMale");
                PaperdollFemale = assets.LoadAsset<Sprite>("PaperdollFemale");
                Paperdolls = assets.LoadAsset<GameObject>("Paperdolls");
            }
            EpicLootCompat.Initialize();
            harmony = new Harmony("plopyy.valheim.Overhaul.EquipmentSlots");
            foreach (var type in typeof(EquipmentAndQuickSlots).Assembly.GetTypes())
                if (type.Namespace == "EquipmentAndQuickSlots" || type.Namespace?.StartsWith("EquipmentAndQuickSlots.") == true)
                    harmony.CreateClassProcessor(type).Patch();
            BetterArcheryCompat.Initialize(harmony);
            IsInitialized = true;
            LogInfo("Equipment & Quick Slots integrated into Overhaul.");
        }

        internal static void Update()
        {
            var player = Player.m_localPlayer;
            if (!IsInitialized || !player) return;
            AmmoSlots.Update(player);
            foreach (var slot in Slots.slots)
                if (slot.IsHotkeySlot && slot.IsShortcutDownWithItem() && slot.ItemFits(slot.Item)) player.UseItem(null, slot.Item, false);
        }

        internal static void LateUpdate()
        {
            if (!IsInitialized) return;
            SlotValidation.Validate();
            API.DetectSlotItemChanges();
        }

        internal static void Stop()
        {
            IsInitialized = false;
            harmony?.UnpatchSelf();
            harmony = null;
            if (assets) assets.Unload(false);
            assets = null;
        }

        public static AssetBundle LoadAssetBundle(string filename)
        {
            return global::Overhaul.Utility.EmbeddedAssets.LoadBundle("Overhaul." + filename);
        }

        public static void Log(string message) { if (ValConfig.LoggingEnabled?.Value == true) logger?.LogMessage(message); }
        public static void LogWarning(string message) => logger?.LogWarning(message);
        public static void LogError(string message) => logger?.LogError(message);
        public static void LogInfo(string message) => logger?.LogInfo(message);
    }
}
