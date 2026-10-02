using System;
using HarmonyLib;
using GUIFramework;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Auga
{
    [UiModule(UiModule.WorldSelection)]
    internal static class WorldSelection_Setup
    {
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
        private static class Install
        {
            private static void Prefix(FejdStartup __instance)
            {
                if (UiModules.Enabled(UiModule.MainMenu)) return;
                var old = __instance.m_startGamePanel;
                var oldServer = __instance.m_serverListPanel.GetComponent<ServerListGui>();
                var host = new GameObject("Auga world installation"); host.SetActive(false);
                var panel = UnityEngine.Object.Instantiate(Auga.Assets.WorldSelectionPrefab, host.transform, false);
                panel.SetActive(false); panel.name = old.name;
                var root = panel.transform;
                T Get<T>(string path) where T : Component
                {
                    var node = root.Find(path); var component = node ? node.GetComponent<T>() : null;
                    if (!component) throw new InvalidOperationException("Missing Auga world binding: " + path + " / " + typeof(T).Name);
                    return component;
                }
                var world = root.Find("Panel/WorldPanel");
                var server = Get<ServerListGui>("Panel/JoinPanel");
                server.m_startup = __instance; server.m_connectIcons = oldServer.m_connectIcons;
                var crossplaySprite = oldServer.m_serverListElement.transform.Find("crossplay").GetComponent<Image>().sprite;
                server.m_serverListElement.transform.Find("crossplay").GetComponent<Image>().sprite = crossplaySprite;
                // Preserve the current, not-yet-reviewed world modifiers dialog and its native callbacks.
                if (__instance.m_serverOptions.transform.IsChildOf(old.transform))
                    __instance.m_serverOptions.transform.SetParent(old.transform.parent, false);
                __instance.m_startGamePanel = panel;
                __instance.m_worldListPanel = world.gameObject;
                __instance.m_serverListPanel = server.gameObject;
                __instance.m_createWorldPanel = root.Find("NewWorldDialog").gameObject;
                __instance.m_removeWorldDialog = root.Find("RemoveWorldDialog").gameObject;
                __instance.m_removeWorldName = Get<TMP_Text>("RemoveWorldDialog/Text");
                __instance.m_worldListRoot = Get<RectTransform>("Panel/WorldPanel/ScrollRect/ItemList");
                __instance.m_worldListElement = Auga.Assets.WorldListElement;
                __instance.m_worldListElementStep = 42;
                __instance.m_worldListEnsureVisible = Get<ScrollRectEnsureVisible>("Panel/WorldPanel/ScrollRect");
                __instance.m_worldSourceInfoPanel = world.Find("SourceInfo").gameObject;
                __instance.m_worldSourceInfo = Get<TextMeshProUGUI>("Panel/WorldPanel/SourceInfo/Text");
                __instance.m_openServerToggle = Get<Toggle>("Panel/WorldPanel/CheckboxRow/StartServerToggle");
                __instance.m_publicServerToggle = Get<Toggle>("Panel/WorldPanel/CheckboxRow/StartPublicGameToggle");
                __instance.m_crossplayServerToggle = Get<Toggle>("Panel/WorldPanel/CheckboxRow/CrossplayToggle");
                __instance.m_samePlatformOnlyToggleWorldPanel = Get<Toggle>("Panel/WorldPanel/NativeBindings/SamePlatformWorld");
                __instance.m_samePlatformOnlyToggleJoinPanel = Get<Toggle>("Panel/WorldPanel/NativeBindings/SamePlatformJoin");
                __instance.m_serverPassword = Get<GuiInputField>("Panel/WorldPanel/ServerPassword");
                __instance.m_passwordError = Get<TMP_Text>("Panel/WorldPanel/ServerPassword/Tooltip/ErrorText");
                __instance.m_newWorldName = Get<GuiInputField>("NewWorldDialog/WorldName");
                __instance.m_newWorldSeed = Get<GuiInputField>("NewWorldDialog/WorldSeed");
                __instance.m_newWorldDone = Get<Button>("NewWorldDialog/Done");
                __instance.m_worldStart = Get<Button>("Panel/WorldPanel/Start");
                __instance.m_worldRemove = Get<Button>("Panel/WorldPanel/RemoveButton");
                __instance.m_serverOptionsButton = Get<Button>("Panel/WorldPanel/ModifyWorld");
                void Bind(string path, UnityAction action) { var b=Get<Button>(path);b.onClick=new Button.ButtonClickedEvent();b.onClick.AddListener(action); }
                Bind("Panel/WorldPanel/Back", __instance.OnStartGameBack);
                Bind("Panel/WorldPanel/Start", __instance.OnWorldStart);
                Bind("Panel/WorldPanel/RemoveButton", __instance.OnWorldRemove);
                Bind("Panel/WorldPanel/NewButton", __instance.OnWorldNew);
                Bind("Panel/WorldPanel/ManageSaves", () => __instance.OnManageSaves(0));
                Bind("Panel/WorldPanel/ModifyWorld", __instance.OnServerOptions);
                Bind("RemoveWorldDialog/ButtonYes", __instance.OnButtonRemoveWorldYes);
                Bind("RemoveWorldDialog/ButtonNo", __instance.OnButtonRemoveWorldNo);
                Bind("NewWorldDialog/Done", () => __instance.OnNewWorldDone(false));
                Bind("NewWorldDialog/Cancel", __instance.OnNewWorldBack);
                Bind("Panel/JoinPanel/Back", __instance.OnStartGameBack);
                Bind("Panel/JoinPanel/Connect", __instance.OnJoinStart);
                Bind("Panel/JoinPanel/RefreshButton", server.RequestServerList);
                Bind("Panel/JoinPanel/JoinIPButton", server.OnAddServerOpen);
                Bind("JoinIP/Connect", server.OnAddServer);
                Bind("JoinIP/Cancel", server.OnAddServerClose);
                // FejdStartup.Start registers the native open-server callback once.
                var tabs = Get<TabHandler>("Panel");
                tabs.m_tabs[0].m_onClick = new UnityEvent(); tabs.m_tabs[0].m_onClick.AddListener(__instance.OnSelectWorldTab);
                tabs.m_tabs[1].m_onClick = new UnityEvent(); tabs.m_tabs[1].m_onClick.AddListener(__instance.OnServerListTab);
                old.SetActive(false);
                root.SetParent(old.transform.parent, false); root.SetSiblingIndex(old.transform.GetSiblingIndex());
                if(Application.isPlaying) { UnityEngine.Object.Destroy(old); UnityEngine.Object.Destroy(host); }
                else { UnityEngine.Object.DestroyImmediate(old); UnityEngine.Object.DestroyImmediate(host); }
                if (Localization.instance != null) Localization.instance.Localize(root);
            }
        }
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.UpdatePasswordError))]
        private static class PasswordNotice
        {
            private static void Postfix(FejdStartup __instance)
            {
                if (!UiModules.Enabled(UiModule.MainMenu) && __instance.m_passwordError.transform.parent.name == "Tooltip")
                    __instance.m_passwordError.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(__instance.m_passwordError.text));
            }
        }
    }
}
