using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using AugaUnity;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Auga
{
    [HarmonyPatch]
    // Each patch carries its own UI module: Menu_Start_Patch (the old menu swap) stays off, PausePrefabInstaller replaces it.
    public static class PauseMenu_Setup
    {
        [HarmonyPatch(typeof(TextsDialog), nameof(TextsDialog.Update))]
        [UiModule(UiModule.PauseMenu)]
        public static class TextDialog_Update_Patch
        {
            public static void TextDialogUpdate(TextsDialog instance)
            {
                instance.UpdateGamepadInput();
                if (instance.m_texts.Count <= 0)
                    return;

                if (instance.m_leftScrollbar == null)
                    return;
                
                if (instance.m_leftScrollRect == null)
                    return;
                
                instance.m_leftScrollbar.size = ((RectTransform)instance.m_leftScrollRect.transform).rect.height / instance.m_listRoot.rect.height;    
            }
            
            [UsedImplicitly]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instrs = instructions.ToList();

                var counter = 0;

                CodeInstruction LogMessage(CodeInstruction instruction)
                {
                    //Debug.LogWarning($"IL_{counter}: Opcode: {instruction.opcode} Operand: {instruction.operand}");
                    return instruction;
                }

                for (int i = 0; i < instrs.Count; ++i)
                {
                    if (i == 0)
                    {
                        yield return LogMessage(new CodeInstruction(OpCodes.Ldarg_0));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Call,AccessTools.DeclaredMethod(typeof(TextDialog_Update_Patch), nameof(TextDialogUpdate))));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Ret));
                        counter++;

                    }
                }
            }
        }

        [HarmonyPatch(typeof(TextsDialog), nameof(TextsDialog.ShowText), new []{typeof(TextsDialog.TextInfo)})]
        [UiModule(UiModule.PauseMenu)]
        public static class TextDialog_ShowText_Patch
        {
            public static void ShowText(TextsDialog instance, TextsDialog.TextInfo text)
            {
                if (text == null)
                    return;
                
                instance.m_textAreaTopic.text = Localization.instance.Localize(text.m_topic);
                instance.m_textArea.text = Localization.instance.Localize(text.m_text);
                foreach (TextsDialog.TextInfo text1 in instance.m_texts)
                    text1.m_selected.SetActive(false);
                text.m_selected.SetActive(true);
                if (instance.m_leftScrollRect != null)
                {
                    instance.StartCoroutine(instance.FocusOnCurrentLevel(instance.m_leftScrollRect, instance.m_listRoot, text.m_selected.transform as RectTransform));                    
                }
            }
            
            [UsedImplicitly]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instrs = instructions.ToList();

                var counter = 0;

                CodeInstruction LogMessage(CodeInstruction instruction)
                {
                    //Debug.LogWarning($"IL_{counter}: Opcode: {instruction.opcode} Operand: {instruction.operand}");
                    return instruction;
                }

                for (int i = 0; i < instrs.Count; ++i)
                {
                    if (i == 0)
                    {
                        yield return LogMessage(new CodeInstruction(OpCodes.Ldarg_0));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Ldarg_1));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Call,AccessTools.DeclaredMethod(typeof(TextDialog_ShowText_Patch), nameof(ShowText))));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Ret));
                        counter++;

                    }
                }
            }
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.UpdateNavigation))]
        [UiModule(UiModule.PauseMenu)]
        public static class Menu_UpdateNavigation_Patch
        {
            public static void UpdateNavigation(Menu instance)
            {
                try
                {
                    Button component1;
                    Button component2;
                    Button component3;
                    Button component4;
                    Button component5;
                    
                    List<Button> buttonList = new List<Button>();

                    if (instance.name.StartsWith("Auga"))
                    {
                        component1 = instance.m_menuDialog.Find("MenuEntries/Logout").GetComponent<Button>();
                        component2 = instance.m_menuDialog.Find("MenuEntries/Exit").GetComponent<Button>();
                        component3 = instance.m_menuDialog.Find("MenuEntries/DividerMedium/CloseButton").GetComponent<Button>();
                        component4 = instance.m_menuDialog.Find("MenuEntries/Settings").GetComponent<Button>();
                        component5 = instance.m_menuDialog.Find("MenuEntries/Compendium").GetComponent<Button>();

                        instance.m_firstMenuButton = component3;
                        
                        //Settings
                        buttonList.Add(component4);
                        
                        //Compendium
                        buttonList.Add(component5);

                        //Save
                        if (instance.m_saveButton.interactable)
                            buttonList.Add(instance.m_saveButton);

                        //Logout
                        buttonList.Add(component1);

                        //Exit
                        if (component2.gameObject.activeSelf)
                            buttonList.Add(component2);

                        //Close Menu
                        buttonList.Add(component3);
                    }
                    else
                    {
                        component1 = instance.m_menuDialog.Find("MenuEntries/Logout").GetComponent<Button>();
                        component2 = instance.m_menuDialog.Find("MenuEntries/Exit").GetComponent<Button>();
                        component3 = instance.m_menuDialog.Find("MenuEntries/Continue").GetComponent<Button>();
                        component4 = instance.m_menuDialog.Find("MenuEntries/Settings").GetComponent<Button>();

                        instance.m_firstMenuButton = component3;
                        
                        buttonList.Add(component3);
                        
                        if (instance.m_saveButton.interactable)
                            buttonList.Add(instance.m_saveButton);

                        if (instance.m_playerListButton.gameObject.activeSelf)
                            buttonList.Add(instance.m_playerListButton);
                        
                        buttonList.Add(component4);

                        buttonList.Add(component1);
                        
                        if (component2.gameObject.activeSelf)
                            buttonList.Add(component2);
                    }
                    
                    for (int index = 0; index < buttonList.Count; ++index)
                    {
                        Navigation navigation = buttonList[index].navigation with
                        {
                            selectOnUp = index <= 0 ? buttonList[buttonList.Count - 1] : (Selectable) buttonList[index - 1],
                            selectOnDown = index >= buttonList.Count - 1 ? buttonList[0] : (Selectable) buttonList[index + 1]
                        };
                        buttonList[index].navigation = navigation;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Start Menu Navigation ({instance.name}) Error Caught {e.Message}");
                    Debug.LogWarning($"{e.StackTrace}");
                }
            }
            
            [UsedImplicitly]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instrs = instructions.ToList();

                var counter = 0;

                CodeInstruction LogMessage(CodeInstruction instruction)
                {
                    //Debug.LogWarning($"IL_{counter}: Opcode: {instruction.opcode} Operand: {instruction.operand}");
                    return instruction;
                }

                for (int i = 0; i < instrs.Count; ++i)
                {
                    if (i == 0)
                    {
                        yield return LogMessage(new CodeInstruction(OpCodes.Ldarg_0));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Call,AccessTools.DeclaredMethod(typeof(Menu_UpdateNavigation_Patch), nameof(UpdateNavigation))));
                        counter++;

                        yield return LogMessage(new CodeInstruction(OpCodes.Ret));
                        counter++;

                    }
                }
            }
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.Start))]
        public static class Menu_Start_Patch
        {
            [UsedImplicitly]
            public static void Postfix(Menu __instance)
            {
                if (__instance.name != "Menu")
                {
                    return;
                }

                var parent = __instance.transform.parent;
                var playerPrefab = __instance.CurrentPlayersPrefab;
                var newMenu = Object.Instantiate(Auga.Assets.MenuPrefab, parent, false).GetComponent<Menu>();
                newMenu.CurrentPlayersPrefab = playerPrefab;
                Object.Destroy(__instance.gameObject);
            }
        }

        // No manual save for a player connected to someone else's world: the server saves it.
        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        [UiModule(UiModule.PauseMenu)]
        public static class Menu_Show_Patch
        {
            [UsedImplicitly]
            public static void Postfix(Menu __instance)
            {
                if (!ZNet.instance || ZNet.instance.IsServer() || !__instance.m_saveButton) return;
                __instance.m_saveButton.gameObject.SetActive(false);
                __instance.m_saveButton.interactable = false;
                if (__instance.lastSaveText) __instance.lastSaveText.gameObject.SetActive(false);
            }
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.OnClose))]
        [UiModule(UiModule.PauseMenu)]
        public static class Menu_OnClose_Patch
        {
            [UsedImplicitly]
            public static void Postfix(Menu __instance)
            {
                var compendium = __instance.GetComponent<AugaCompendiumController>();
                if (compendium != null)
                {
                    compendium.HideCompendium();
                }
            }
        }

        [HarmonyPatch(typeof(TextsDialog))]
        [UiModule(UiModule.PauseMenu)]
        public static class TextsDialog_Patch
        {
            [HarmonyPrefix]
            [HarmonyPatch(nameof(TextsDialog.AddActiveEffects))]
            public static bool AddActiveEffects_Prefix()
            {
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch(nameof(TextsDialog.AddLog))]
            public static bool AddLog_Prefix()
            {
                return false;
            }

            [HarmonyPrefix]
            [HarmonyPatch(nameof(TextsDialog.UpdateTextsList))]
            public static bool UpdateTextsList_Prefix(TextsDialog __instance)
            {
                __instance.m_texts.Clear();

                var filter = __instance.GetComponent<AugaTextsDialogFilter>();
                foreach (var knownText in Player.m_localPlayer.GetKnownTexts())
                {
                    if (filter == null || knownText.Key.Contains(filter.Filter))
                    {
                        var keyText = Localization.instance.Localize(knownText.Key);
                        var separatorIndex = keyText.IndexOf(": ", StringComparison.Ordinal);
                        keyText = separatorIndex >= 0 ? keyText.Substring(separatorIndex + 2) : keyText;
                        __instance.m_texts.Add(new TextsDialog.TextInfo(keyText, Localization.instance.Localize(knownText.Value)));
                    }
                }

                __instance.m_texts.Sort((a, b) => string.Compare(a.m_topic, b.m_topic, StringComparison.CurrentCulture));
                return false;
            }

            // TEMPORARY diagnostic (compendium lists empty or unreadable in game): the known text keys, each tab's filter and
            // the list elements as laid out.
            [HarmonyPostfix]
            [HarmonyPatch(nameof(TextsDialog.Setup))]
            public static void Setup_Diagnostic(TextsDialog __instance)
            {
                var filter = __instance.GetComponent<AugaTextsDialogFilter>();
                var log = new System.Text.StringBuilder("[Compendium diag] " + __instance.name + " filter=" + (filter ? filter.Filter : "none") + " texts=" + __instance.m_texts.Count + " spacing=" + __instance.m_spacing);
                foreach (var known in Player.m_localPlayer.GetKnownTexts()) log.Append("\n  key=" + known.Key.Replace("\u0016", "<16>"));
                if (__instance.m_listRoot)
                {
                    var root = __instance.m_listRoot;
                    log.Append("\n  listRoot rect=" + root.rect + " pos=" + root.anchoredPosition + " active=" + root.gameObject.activeInHierarchy + " layout=" + (root.GetComponent<LayoutGroup>() ? root.GetComponent<LayoutGroup>().GetType().Name : "none"));
                    foreach (RectTransform child in root)
                    {
                        var label = child.GetComponentInChildren<TMPro.TMP_Text>(true);
                        log.Append("\n  element " + child.name + " rect=" + child.rect + " pos=" + child.anchoredPosition + " scale=" + child.localScale + " active=" + child.gameObject.activeSelf);
                        if (label) log.Append(" text='" + label.text + "' textRect=" + label.rectTransform.rect + " font=" + (label.font ? label.font.name : "NULL") + " color=" + label.color + " alpha=" + label.alpha + " enabled=" + label.enabled + " size=" + label.fontSize);
                    }
                }
                Debug.Log(log.ToString());
            }

            [HarmonyPostfix]
            [HarmonyPatch(nameof(TextsDialog.ShowText), typeof(TextsDialog.TextInfo))]
            public static void AddLog_Postfix(TextsDialog __instance)
            {
                __instance.m_textArea.text = __instance.m_textArea.text.Replace("color=yellow", $"color={Auga.Colors.Topic}");
            }
        }
    }
}

