using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Storage
{
    internal sealed class DeviceWindow : MonoBehaviour
    {
        private static AssetBundle bundle;
        private InventoryGui gui;
        private DeviceStore store;
        private GameObject panel;
        private InventoryGrid originalGrid, materialGrid, fuelGrid;
        private InventoryGrid activeGrid;
        private int inputFrame = -1;
        private TMP_Text originalName;
        private Vector2 originalSize, originalPosition;
        private readonly List<KeyValuePair<GameObject, bool>> hidden = new List<KeyValuePair<GameObject, bool>>();
        private Transform[] armorAnchors;
        internal static GameObject Prefab(string name)
        {
            if (!bundle)
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Overhaul.stationwindows"))
                using (var memory = new MemoryStream())
                {
                    if (stream == null) throw new InvalidOperationException("Missing station window bundle");
                    stream.CopyTo(memory); bundle = AssetBundle.LoadFromMemory(memory.ToArray());
                }
            }
            return bundle.LoadAsset<GameObject>(name);
        }
        private static DeviceWindow Get(InventoryGui gui)
        {
            var window = gui.GetComponent<DeviceWindow>();
            if (!window) { window = gui.gameObject.AddComponent<DeviceWindow>(); window.gui = gui; }
            return window;
        }
        private static void Text(Transform root, string child, string text)
        {
            var node = root.Find(child); if (node) node.GetComponent<TMP_Text>().text = Localization.instance.Localize(text);
        }
        internal void Show(DeviceStore device)
        {
            Restore(); store = device;
            string prefab = store.Adapter is ArmorInventory ? "StationArmorStand" : store.Adapter is StandInventory ? "StationSingleItemStand" : store.Adapter.Dual ? "StationDualVertical_Separation2" : "StationSingleLateral_Separation2";
            originalGrid = gui.m_containerGrid; originalName = gui.m_containerName;
            originalSize = gui.m_container.sizeDelta; originalPosition = gui.m_container.anchoredPosition;
            foreach (Transform child in gui.m_container)
            { hidden.Add(new KeyValuePair<GameObject, bool>(child.gameObject, child.gameObject.activeSelf)); child.gameObject.SetActive(false); }
            panel = Instantiate(Prefab(prefab), gui.m_container, false);
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = Vector2.zero;
            gui.m_container.sizeDelta = rect.sizeDelta;
            // Independent window dimensions; put it beside the player panel instead of stretching it to that panel.
            var corners = new Vector3[4]; gui.m_player.GetWorldCorners(corners);
            var rightTop = gui.m_container.parent.InverseTransformPoint(corners[2]);
            var containerRect = gui.m_container;
            var parentRect = containerRect.parent as RectTransform;
            if (parentRect)
            {
                float left = rightTop.x + 18f;
                float top = Mathf.Min(parentRect.rect.yMax - 20f, rightTop.y);
                var position = containerRect.localPosition;
                position.x = left + containerRect.pivot.x * rect.sizeDelta.x;
                position.y = top - (1f - containerRect.pivot.y) * rect.sizeDelta.y;
                containerRect.localPosition = position;
            }
            Text(panel.transform, "Title", store.Adapter.Title);
            Text(panel.transform, "MaterialLabel", store.Adapter.MaterialLabel);
            Text(panel.transform, "FuelLabel", store.Adapter.FuelLabel);
            foreach (var text in panel.transform.Find("Close").GetComponentsInChildren<TMP_Text>(true)) text.text = Localization.instance.Localize("$menu_close");
            panel.transform.Find("Close").GetComponent<Button>().onClick.AddListener(gui.CloseContainer);
            AddActions();
            materialGrid = panel.GetComponentsInChildren<InventoryGrid>(true).FirstOrDefault(g => g.name == "MaterialInventory");
            if (store.Adapter is ArmorInventory) PrepareArmor();
            fuelGrid = panel.GetComponentsInChildren<InventoryGrid>(true).FirstOrDefault(g => g.name == "FuelInventory");
            activeGrid = materialGrid; inputFrame = -1;
            Bind(materialGrid, store.Items); if (fuelGrid) Bind(fuelGrid, store.Fuel);
            materialGrid.OnMoveToUpperInventoryGrid = gui.MoveToUpperInventoryGrid;
            if (fuelGrid)
            {
                materialGrid.OnMoveToLowerInventoryGrid = pos => SelectGrid(fuelGrid, pos.x, 0);
                fuelGrid.OnMoveToUpperInventoryGrid = pos => SelectGrid(materialGrid, pos.x, store.Items.GetHeight() - 1);
            }
            gui.m_containerGrid = materialGrid; gui.m_containerName = panel.transform.Find("Title").GetComponent<TMP_Text>();
            gui.m_firstContainerUpdate = false; gui.m_containerHoldState = -1; gui.m_containerHoldTime = float.NegativeInfinity; gui.m_waitForContainerStack = false;
            Refresh();
            DeviceActions.OnShown(store);
        }
        private void AddActions()
        {
            var close = panel.transform.Find("Close").GetComponent<Button>();
            var buttons = new List<Button>();
            var take = Instantiate(close, close.transform.parent, false); take.name = "TakeAll";
            take.onClick = new Button.ButtonClickedEvent(); take.onClick.AddListener(gui.OnTakeAll);
            ButtonText(take, "$overhaul_chest_take_all"); buttons.Add(take);
            if (store.Adapter is ArmorInventory)
            {
                var equip = Instantiate(close, close.transform.parent, false); equip.name = "PlaceEquipment";
                equip.onClick = new Button.ButtonClickedEvent(); equip.onClick.AddListener(() => DeviceActions.PlaceEquipment(store, Player.m_localPlayer));
                ButtonText(equip, "$overhaul_device_worn_equipment"); buttons.Add(equip);
            }
            buttons.Add(close);
            var template = (RectTransform)close.transform;
            float total = buttons.Count * template.rect.width + (buttons.Count - 1) * 12f;
            float start = (((RectTransform)panel.transform).rect.width - total) / 2;
            for (int i = 0; i < buttons.Count; i++)
            {
                var rect = (RectTransform)buttons[i].transform;
                rect.anchoredPosition = new Vector2(start + i * (template.rect.width + 12f), template.anchoredPosition.y);
            }
        }
        private static void ButtonText(Button button, string key)
        {
            foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
            { text.text = Localization.instance.Localize(key); text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 18; }
        }
        private void PrepareArmor()
        {
            var slots = panel.transform.Find("EquipmentSlots");
            var names = new[] { "Equipment0", "Equipment1", "Equipment2", "Equipment3", "Equipment4", "BackRight", "BackLeft" };
            armorAnchors = names.Select(n => slots.Find(n)).ToArray();
            var donor = Prefab("StationSingleLateral_Separation2").GetComponentInChildren<InventoryGrid>(true);
            materialGrid = Instantiate(donor, slots, false); materialGrid.name = "EquipmentInventory";
            var rect = (RectTransform)materialGrid.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var mask = materialGrid.GetComponent<RectMask2D>(); if (mask) mask.enabled = false;
            materialGrid.GetComponent<ScrollRect>().enabled = false;
        }
        private void Bind(InventoryGrid grid, Inventory inventory)
        {
            grid.m_uiGroup = originalGrid.m_uiGroup;
            grid.m_onSelected = gui.OnSelectedItem; grid.m_onRightClick = gui.OnRightClickItem;
            grid.m_onReleased = gui.OnReleasedItem;
            grid.m_onEnter = gui.OnEnterElement;
            grid.CanDropDragOntoItem = gui.CanDropDragOntoItem;
            grid.OnSetTouchSelection = selected => { activeGrid = selected; gui.SetTouchSelection(selected); };
            var rect = (RectTransform)grid.transform;
            if (armorAnchors == null || grid != materialGrid) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, inventory.GetWidth() * grid.m_elementSpace);
            var scroll = grid.GetComponent<ScrollRect>(); if (scroll) scroll.vertical = inventory.GetHeight() > (grid == materialGrid ? store.Adapter.Height : 2);
            grid.UpdateInventory(inventory, null, gui.m_dragItem);
            // These prefabs anchor content at the top. Native ResetView switches
            // its pivot to the center and clips the second row in this layout.
            grid.m_gridRoot.anchorMin = grid.m_gridRoot.anchorMax = grid.m_gridRoot.pivot = new Vector2(0, 1);
            grid.m_gridRoot.anchoredPosition = Vector2.zero;
        }
        private void Refresh()
        {
            if (!panel || !store || !store.Owner) return;
            if (fuelGrid) fuelGrid.UpdateInventory(store.Fuel, null, gui.m_dragItem);
            if (armorAnchors == null) return;
            for (int i = 0; i < Math.Min(7, materialGrid.m_elements.Count); i++)
            {
                var element = materialGrid.m_elements[i]; var anchor = armorAnchors[i];
                if (element.transform.parent != anchor)
                {
                    var old = anchor.Find("Slot");
                    if (old)
                    {
                        var hint = old.Find("EquipmentHint"); if (hint) hint.SetParent(element.transform, false);
                        old.gameObject.SetActive(false); Destroy(old.gameObject);
                    }
                    var rect = (RectTransform)element.transform; rect.SetParent(anchor, false);
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
                }
                var graphic = element.transform.Find("EquipmentHint"); if (graphic) graphic.gameObject.SetActive(store.Items.GetItemAt(i, 0) == null);
            }
        }
        private void Restore()
        {
            if (!panel) return;
            // Hide() leaves the container active during the inventory's closing
            // animation. Hide its root before restoring the ordinary chest UI.
            if (!gui.m_currentContainer) gui.m_container.gameObject.SetActive(false);
            gui.m_containerGrid = originalGrid; gui.m_containerName = originalName;
            gui.m_container.sizeDelta = originalSize; gui.m_container.anchoredPosition = originalPosition;
            foreach (var pair in hidden) if (pair.Key) pair.Key.SetActive(pair.Value);
            hidden.Clear(); panel.SetActive(false); Destroy(panel); panel = null; store = null; armorAnchors = null;
        }
        private void SelectGrid(InventoryGrid grid, int x, int y)
        {
            activeGrid = grid;
            grid.SetGamepadSelection(new Vector2i(Mathf.Clamp(x, 0, grid.GetInventory().GetWidth() - 1), y));
        }
        private void OnDestroy() { if (panel) Destroy(panel); }

        // Both reserves belong to the native container UI group. Only its selected
        // grid may handle a controller press, including the frame of a transition.
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGamepad")]
        private static class GamepadInput
        {
            private static bool Prefix(InventoryGrid __instance)
            {
                var window = __instance.GetComponentInParent<DeviceWindow>();
                if (!window || !window.panel || (__instance != window.materialGrid && __instance != window.fuelGrid)) return true;
                if (__instance != window.activeGrid) return false;
                if (!ZInput.IsExclusiveGamepadActive() || !__instance.m_uiGroup.IsActive) return true;
                if (window.inputFrame == Time.frameCount) return false;
                window.inputFrame = Time.frameCount; return true;
            }
        }
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        private static class GamepadHighlight
        {
            private static void Postfix(InventoryGrid __instance)
            {
                var inventory = __instance.GetInventory();
                if (DeviceInventoryRules.Get(inventory)?.Single == true)
                    foreach (var element in __instance.m_elements)
                    {
                        var item = inventory.GetItemAt(element.Position.x, element.Position.y);
                        if (item != null) element.m_amount.text = item.m_stack.ToString();
                    }
                var window = __instance.GetComponentInParent<DeviceWindow>();
                if (!window || !window.panel || __instance == window.activeGrid ||
                    (__instance != window.materialGrid && __instance != window.fuelGrid) || !ZInput.IsExclusiveGamepadActive()) return;
                foreach (var element in __instance.m_elements) element.m_selected.SetActive(false);
            }
        }
        [HarmonyPatch(typeof(InventoryGui), "GetSelectedGamepadElement")]
        private static class GamepadSelection
        {
            private static bool Prefix(InventoryGui __instance, ref RectTransform __result)
            {
                var window = __instance.GetComponent<DeviceWindow>();
                if (!window || !window.panel || !window.activeGrid || !window.activeGrid.m_uiGroup.IsActive) return true;
                __result = window.activeGrid.GetGamepadSelectedElement(); return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui), "MoveToLowerInventoryGrid")]
        private static class EnterFromPlayer
        {
            private static void Prefix(InventoryGui __instance)
            {
                var window = __instance.GetComponent<DeviceWindow>();
                if (window && window.panel) window.activeGrid = window.materialGrid;
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "Show")]
        private static class ShowWindow
        {
            private static void Prefix(InventoryGui __instance) => Get(__instance).Restore();
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(InventoryGui __instance)
            {
                var store = DeviceStore.Of(__instance.m_currentContainer);
                if (store && store.Ready && store.Visible) Get(__instance).Show(store);
            }
        }
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        private static class UpdateWindow
        {
            private static void Prefix(InventoryGui __instance)
            {
                var window = __instance.GetComponent<DeviceWindow>(); if (!window || !window.panel) return;
                if (!__instance.m_currentContainer || DeviceStore.Of(__instance.m_currentContainer) != window.store) { window.Restore(); return; }
                __instance.m_containerHoldState = -1; __instance.m_containerHoldTime = float.NegativeInfinity; __instance.m_waitForContainerStack = false;
            }
            private static void Postfix(InventoryGui __instance) => __instance.GetComponent<DeviceWindow>()?.Refresh();
        }
        [HarmonyPatch(typeof(InventoryGui), "Hide")]
        private static class HideWindow { private static void Postfix(InventoryGui __instance) => __instance.GetComponent<DeviceWindow>()?.Restore(); }
        [HarmonyPatch(typeof(InventoryGui), "CloseContainer")]
        private static class CloseWindow { private static void Postfix(InventoryGui __instance) => __instance.GetComponent<DeviceWindow>()?.Restore(); }
        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        private static class Move
        {
            private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod)
            {
                var store = DeviceStore.Of(__instance.m_currentContainer);
                if (!store || !store.Ready || __instance.m_dragGo || mod != InventoryGrid.Modifier.Move || item == null) return true;
                if (!store.Owner || item.m_shared.m_questItem) return false;
                var player = Player.m_localPlayer; var source = grid.GetInventory(); Inventory target;
                if (source == store.Items || source == store.Fuel) target = player.GetInventory();
                else if (source == player.GetInventory()) target = DeviceInventoryRules.Allows(store.Items, item) ? store.Items : store.Adapter.Dual && DeviceInventoryRules.Allows(store.Fuel, item) ? store.Fuel : null;
                else return false;
                if (target == null || !source.ContainsItem(item)) return false;
                player.RemoveEquipAction(item); player.UnequipItem(item, true);
                target.MoveItemToThis(source, item);
                __instance.m_moveItemEffects.Create(__instance.transform.position, Quaternion.identity);
                return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        private static class RightClick
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos)
            {
                var rule = DeviceInventoryRules.Get(grid.GetInventory()); if (rule == null) return true;
                __instance.OnSelectedItem(grid, item, pos, InventoryGrid.Modifier.Move); return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui), "OnTakeAll")]
        private static class TakeAll
        {
            private static bool Prefix(InventoryGui __instance)
            {
                var store = DeviceStore.Of(__instance.m_currentContainer); if (!store || !store.Ready) return true;
                if (!store.Owner) return false;
                Player.m_localPlayer.GetInventory().MoveAll(store.Items);
                if (store.Adapter.Dual) Player.m_localPlayer.GetInventory().MoveAll(store.Fuel);
                return false;
            }
        }
    }
}
