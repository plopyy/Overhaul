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
            string prefab = store.Adapter is ArmorInventory ? "StationArmorStand" : store.Adapter.SingleItems ? "StationSingleItemStand" : store.Adapter.Dual ? "StationDualVertical_Separation2" : "StationSingleLateral_Separation2";
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
            materialGrid = panel.GetComponentsInChildren<InventoryGrid>(true).FirstOrDefault(g => g.name == "MaterialInventory");
            if (store.Adapter is ArmorInventory) PrepareArmor();
            fuelGrid = panel.GetComponentsInChildren<InventoryGrid>(true).FirstOrDefault(g => g.name == "FuelInventory");
            Bind(materialGrid, store.Items); if (fuelGrid) Bind(fuelGrid, store.Fuel);
            gui.m_containerGrid = materialGrid; gui.m_containerName = panel.transform.Find("Title").GetComponent<TMP_Text>();
            gui.m_firstContainerUpdate = false; gui.m_containerHoldState = -1; gui.m_containerHoldTime = float.NegativeInfinity; gui.m_waitForContainerStack = false;
            Refresh();
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
            var rect = (RectTransform)grid.transform;
            if (armorAnchors == null || grid != materialGrid) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, inventory.GetWidth() * grid.m_elementSpace);
            var scroll = grid.GetComponent<ScrollRect>(); if (scroll) scroll.vertical = inventory.GetHeight() > (grid == materialGrid ? store.Adapter.Height : 2);
            grid.UpdateInventory(inventory, null, gui.m_dragItem); grid.ResetView();
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
            gui.m_containerGrid = originalGrid; gui.m_containerName = originalName;
            gui.m_container.sizeDelta = originalSize; gui.m_container.anchoredPosition = originalPosition;
            foreach (var pair in hidden) if (pair.Key) pair.Key.SetActive(pair.Value);
            hidden.Clear(); panel.SetActive(false); Destroy(panel); panel = null; store = null; armorAnchors = null;
        }
        private void OnDestroy() { if (panel) Destroy(panel); }

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
