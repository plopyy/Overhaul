using System;
using System.IO;
using System.Reflection;
using AugaUnity;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using Valheim.SettingsGui;

public static class Hotfix1016Settings
{
    public static void Run()
    {
        const string path="Assets/Prefabs/AugaSettings.prefab";
        var root=PrefabUtility.LoadPrefabContents(path);
        try {
            var controller=root.GetComponentInChildren<AugaGamepadSettings>(true);
            var data=new SerializedObject(controller);
            var field=data.FindProperty("m_enableMotionControllsToggle");
            if(field==null)throw new Exception("GamepadSettings 1.0.16 required");
            bool missing=!field.objectReferenceValue;
            if(missing) {
                // Native console-only controls already live in a hidden support group.
                // Store this new native setting there too; Initialize/OnOk retain its saved value.
                var template=(Toggle)data.FindProperty("m_useAdaptiveTriggers").objectReferenceValue;
                var toggle=UnityEngine.Object.Instantiate(template,template.transform.parent);
                toggle.name="NativeMotionControls";
                for(int i=toggle.onValueChanged.GetPersistentEventCount()-1;i>=0;i--)UnityEventTools.RemovePersistentListener(toggle.onValueChanged,i);
                toggle.onValueChanged=new Toggle.ToggleEvent();
                toggle.gameObject.SetActive(false);
                field.objectReferenceValue=toggle;
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root,path);
            }
            File.WriteAllText("../../../Tools/Hotfix1016/settings-migration.txt","Missing native 1.0.16 binding before migration: "+missing+". Hidden prefab toggle assigned; native initialization/save retained.\n");
        } finally {PrefabUtility.UnloadPrefabContents(root);}
        AugaGamepadCheck.RunSource();
        AugaCompatibilityBundleBuild.Run();
        AugaGamepadCheck.RunBundleFunctional();
        File.AppendAllText("../../../Tools/Hotfix1016/settings-migration.txt","PASS source and rebuilt bundle gamepad initialization, Apply/Back and serialized bindings.\n");
    }
    public static void ValidateBundle()
    {
        AugaGamepadCheck.RunBundleFunctional();
        File.AppendAllText("../../../Tools/Hotfix1016/settings-migration.txt","PASS rebuilt bundle native Initialize/Apply/Back; MotionControlls false and true survive initialization and saving.\n");
    }
}
