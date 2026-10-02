using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Auga
{
    internal static class EquipmentQuickSlotsCompatibility
    {
        private const string PluginType = "EquipmentAndQuickSlots.EquipmentAndQuickSlots";

        public static void Install(Harmony harmony)
        {
            if (UiModules.Enabled(UiModule.Inventory) || UiModules.Enabled(UiModule.Hud)) return;
            var assembly = typeof(EquipmentAndQuickSlots.EquipmentAndQuickSlots).Assembly;
            EquipmentPanelBridge.Install(assembly, harmony);
            InstallSettings(assembly);
        }

        public static void StopWatching() { }

        internal static void InstallSettings(Assembly assembly)
        {
            var config=assembly.GetType("EquipmentAndQuickSlots.ValConfig",true);
            var keys=AccessTools.Field(config,"QuickSlotKeys");
            var labels=AccessTools.Field(config,"QuickSlotLabels");
            if(keys==null||labels==null)throw new MissingFieldException("EQS shortcut configuration missing");
            var id=(string)AccessTools.Field(assembly.GetType(PluginType,true),"PluginId").GetRawConstantValue();
            AugaUnity.AugaModsSettings.IsEqsActive=()=>EquipmentAndQuickSlots.EquipmentAndQuickSlots.IsInitialized;
            AugaUnity.AugaModsSettings.ReadShortcut=i=>i==5?EquipmentAndQuickSlots.ValConfig.EffectiveMoveObjectKey.ToString():i==4?EquipmentAndQuickSlots.ValConfig.ClassWindowKey.Value.ToString():i==3?EquipmentAndQuickSlots.ValConfig.AmmoCycleKey.Value.ToString():((BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>[])keys.GetValue(null))[i].Value.ToString();
            AugaUnity.AugaModsSettings.FormatShortcut=values=>new BepInEx.Configuration.KeyboardShortcut(values[0],values.Skip(1).ToArray()).ToString();
            AugaUnity.AugaModsSettings.DisplayShortcut=value=>{
                if(string.IsNullOrEmpty(value))return "";
                var shortcut=BepInEx.Configuration.KeyboardShortcut.Deserialize(value);
                return string.Join(" + ",new[]{shortcut.MainKey}.Concat(shortcut.Modifiers).Select(AugaUnity.AugaModsSettings.KeyLabel));
            };
            AugaUnity.AugaModsSettings.WriteShortcut=(i,value)=>{
                if(i==5){EquipmentAndQuickSlots.ValConfig.MoveObjectKey.SetSerializedValue(value);EquipmentAndQuickSlots.ValConfig.cfg.Save();return;}
                if(i==4){EquipmentAndQuickSlots.ValConfig.ClassWindowKey.SetSerializedValue(value);EquipmentAndQuickSlots.ValConfig.cfg.Save();return;}
                if(i==3){EquipmentAndQuickSlots.ValConfig.AmmoCycleKey.SetSerializedValue(value);EquipmentAndQuickSlots.ValConfig.cfg.Save();return;}
                var key=((BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>[])keys.GetValue(null))[i];
                var label=((BepInEx.Configuration.ConfigEntry<string>[])labels.GetValue(null))[i];
                key.SetSerializedValue(value);
                // Empty is EQS's native automatic label: it always follows the real shortcut.
                label.Value="";
                key.ConfigFile.Save();
            };
        }

    }
}
