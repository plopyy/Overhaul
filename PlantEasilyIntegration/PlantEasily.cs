namespace Advize_PlantEasily;

using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Integrated from Advize's PlantEasily 2.2.2 (GPL-3.0); no second plugin.
internal static class PlantEasily
{
    internal const string PluginName = "PlantEasily";
    internal static Harmony Patches;
    internal static void Initialize(ManualLogSource logger)
    {
        ModContext.ModLogger = logger;
        ModContext.config = new ModConfig(new ConfigFile(Path.Combine(Paths.ConfigPath,
            "plopyy.valheim.Overhaul.PlantEasily.cfg"), true));
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        Patches = new Harmony("plopyy.valheim.Overhaul.PlantEasily");
        foreach (var type in typeof(PlantEasily).Assembly.GetTypes())
            if (type.Namespace == "Advize_PlantEasily") Patches.CreateClassProcessor(type).Patch();
    }
    internal static void Stop()
    {
        ClearSession();
        Patches?.UnpatchSelf();
    }
    internal static void ClearSession()
    {
        PlacementController.ClearSession();
        GhostGrid.ClearSession();

    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
internal static class PlantSessionCleanup
{
    private static void Prefix() => PlantEasily.ClearSession();
}
