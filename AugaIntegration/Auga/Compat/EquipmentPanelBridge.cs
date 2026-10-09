using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Auga
{
    // Optional EAQS integration: the inventories and slot rules remain owned by EAQS.
    internal static class EquipmentPanelBridge
    {
        static FieldInfo slotsField, male, female;
        static PropertyInfo visibleRows, active, equipment, quick, custom, cosmetic, index, position;
        static Transform tabPanel;
        static bool cosmeticTab;
        internal static bool IsCosmeticTab => cosmeticTab && tabPanel && tabPanel.gameObject.activeInHierarchy;
        static MethodInfo fits, quickText;
        static readonly FieldInfo Elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
        static readonly FieldInfo Drag = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        internal static void Install(Assembly assembly, Harmony harmony)
        {
            if (!UiModules.Enabled(UiModule.QuickInventory)) return;
            var slots = assembly.GetType("EquipmentAndQuickSlots.Slots", true);
            var panel = assembly.GetType("EquipmentAndQuickSlots.EquipmentPanel", true);
            var plugin = assembly.GetType("EquipmentAndQuickSlots.EquipmentAndQuickSlots", true);
            var slot = slots.GetNestedType("Slot", BindingFlags.Public | BindingFlags.NonPublic);
            slotsField = AccessTools.Field(slots, "slots"); visibleRows = AccessTools.Property(slots, "VisibleRows");
            active = AccessTools.Property(slot, "IsActive"); equipment = AccessTools.Property(slot, "IsEquipmentSlot");
            quick = AccessTools.Property(slot, "IsQuickSlot"); custom = AccessTools.Property(slot, "IsCustomSlot");
            cosmetic = AccessTools.Property(slot, "IsCosmeticSlot");
            index = AccessTools.Property(slot, "Index"); position = AccessTools.Property(slot, "GridPosition");

            fits = AccessTools.Method(panel, "DragItemFits");
            quickText = AccessTools.Method(slots, "GetQuickSlotText");
            harmony.Patch(quickText, postfix: new HarmonyMethod(typeof(EquipmentPanelBridge), nameof(LocalizeShortcut)));
            male = AccessTools.Field(plugin, "PaperdollMale"); female = AccessTools.Field(plugin, "PaperdollFemale");
            if (new object[] { slotsField, visibleRows, active, equipment, quick, custom, index, position, fits, quickText }.Any(x => x == null)) throw new MissingMemberException("Equipment & QuickSlots slot contract changed");
            var relocation = AccessTools.Method(panel.GetNestedType("InventoryGrid_UpdateGui_RelocateSlotElements", BindingFlags.Public | BindingFlags.NonPublic), "Postfix");
            var background = AccessTools.Method(panel.GetNestedType("InventoryGui_Update_UpdateEquipmentPanel", BindingFlags.Public | BindingFlags.NonPublic), "Postfix");
            if (relocation == null || background == null) throw new MissingMethodException("Equipment & QuickSlots UI callbacks missing");
            harmony.Patch(relocation, prefix: new HarmonyMethod(typeof(EquipmentPanelBridge), nameof(Relocate)));
            harmony.Patch(background, prefix: new HarmonyMethod(typeof(EquipmentPanelBridge), nameof(UpdatePanel)));
            var bars = assembly.GetType("EquipmentAndQuickSlots.QuickSlotsHotBar", true);
            var create = AccessTools.Method(bars.GetNestedType("Hud_Awake_CreateQuickSlotsBar", BindingFlags.Public | BindingFlags.NonPublic), "Postfix");
            if(create == null)throw new MissingMethodException("Equipment & QuickSlots HUD creation callback missing");
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(EquipmentPanelBridge), nameof(BeforeQuickBar)), postfix: new HarmonyMethod(typeof(EquipmentPanelBridge), nameof(AfterQuickBar)));
        }
        static void BeforeQuickBar(Hud __0) => QuickInventory_Setup.EnsureHotbar(__0);
        static void LocalizeShortcut(int __0, ref string __result) {
            var config=slotsField.DeclaringType.Assembly.GetType("EquipmentAndQuickSlots.ValConfig");
            var labels=(BepInEx.Configuration.ConfigEntry<string>[])AccessTools.Field(config,"QuickSlotLabels").GetValue(null);
            if(__0<0||__0>=labels.Length||!string.IsNullOrEmpty(labels[__0].Value))return;
            if(AugaUnity.AugaModsSettings.ReadShortcut!=null && AugaUnity.AugaModsSettings.DisplayShortcut!=null)
                __result=AugaUnity.AugaModsSettings.DisplayShortcut(AugaUnity.AugaModsSettings.ReadShortcut(__0));
        }
        static void AfterQuickBar(Hud __0) {
            var bar=__0.transform.Find("hudroot/QuickSlotsHotkeyBar");
            var anchor=__0.transform.Find("hudroot/HotKeyBar/EquipmentQuickSlots");
            if(!bar || !anchor)return;
            foreach(var component in bar.GetComponents<MonoBehaviour>())
                if(component.GetType().FullName=="Common.ConfigPositionedElement")component.enabled=false;
            var rect=(RectTransform)bar;rect.SetParent(anchor,false);
            rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=Vector2.zero;rect.localScale=Vector3.one;
        }
        internal static bool OwnsRow(InventoryGrid grid, int row)
        {
            return visibleRows != null && InventoryGui.instance && grid == InventoryGui.instance.m_playerGrid && row >= (int)visibleRows.GetValue(null, null);
        }
        static bool Flag(PropertyInfo property, object slot) => (bool)property.GetValue(slot, null);
        internal static void SelectEquipmentTab(bool cosmetics)
        {
            cosmeticTab = cosmetics;
            if (InventoryGui.instance) Layout(InventoryGui.instance.m_playerGrid);
        }
        static void UpdateTabs(Transform panel)
        {
            if (tabPanel != panel)
            {
                tabPanel = panel; cosmeticTab = false;
                panel.Find("Tabs/Equipment").GetComponent<Button>().onClick.AddListener(() => SelectEquipmentTab(false));
                panel.Find("Tabs/Cosmetic").GetComponent<Button>().onClick.AddListener(() => SelectEquipmentTab(true));
            }
            panel.Find("Tabs/Equipment").GetComponent<Button>().interactable = cosmeticTab;
            panel.Find("Tabs/Cosmetic").GetComponent<Button>().interactable = !cosmeticTab;
            var selectedEquipment = panel.Find("Tabs/Equipment/Selected");
            var selectedCosmetic = panel.Find("Tabs/Cosmetic/Selected");
            if (selectedEquipment) selectedEquipment.gameObject.SetActive(!cosmeticTab);
            if (selectedCosmetic) selectedCosmetic.gameObject.SetActive(cosmeticTab);
            foreach (Transform child in panel)
                if (child.name != "Background" && child.name != "Tabs")
                    child.gameObject.SetActive(child.name == "CosmeticPage" ? cosmeticTab : !cosmeticTab);
        }
        static bool UpdatePanel()
        {
            if (InventoryGui.instance && InventoryGui.IsVisible()) Layout(InventoryGui.instance.m_playerGrid);
            return false;
        }
        static bool Relocate(InventoryGrid __0) { Layout(__0); return false; }
        internal static void Layout(InventoryGrid grid)
        {
            var gui = InventoryGui.instance;
            if (!gui || grid != gui.m_playerGrid || !Player.m_localPlayer) return;
            var panel = gui.m_player.Find("AugaEquipment") as RectTransform;
            var hidden = gui.m_player.Find("AugaHiddenSlots");
            if (!panel || !hidden) return;
            UpdateTabs(panel);
            var slots = ((Array)slotsField.GetValue(null)).Cast<object>().ToArray();
            var enabled = slots.Where(s => Flag(active, s)).ToArray();
            panel.gameObject.SetActive(enabled.Length > 0);
            int quickCount = enabled.Count(s => Flag(quick, s));
            int customCount = enabled.Count(s => Flag(custom, s));
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(255, quickCount * 70 + 40) + Mathf.Ceil(customCount / 4f) * 70);
            var drag = Drag.GetValue(gui) as ItemDrop.ItemData;
            int customIndex = 0, quickIndex = 0;
            foreach (var slot in slots)
            {
                var pos = (Vector2i)position.GetValue(slot, null);
                var element = ((System.Collections.Generic.List<InventoryElement>)Elements.GetValue(grid)).FirstOrDefault(e => e.Position.x == pos.x && e.Position.y == pos.y);
                if (!element) continue;
                bool isCosmetic = Flag(cosmetic, slot);
                int slotIndex = (int)index.GetValue(slot, null);
                bool isAmmo = slotIndex >= EquipmentAndQuickSlots.Slots.AmmoSlotStartIndex && slotIndex < EquipmentAndQuickSlots.Slots.AmmoSlotStartIndex + 3;
                if (!Flag(active, slot) || isCosmetic != cosmeticTab) { element.transform.SetParent(hidden, false); element.gameObject.SetActive(false); continue; }
                element.gameObject.SetActive(true);
                var r = (RectTransform)element.transform;
                Transform anchor = panel;
                Vector2 offset = Vector2.zero;
                if (isCosmetic) anchor = panel.Find("CosmeticPage/Slot" + ((int)index.GetValue(slot, null) - EquipmentAndQuickSlots.Slots.CosmeticSlotStartIndex));
                else if (Flag(equipment, slot)) anchor = panel.Find("Equipment" + ((int)index.GetValue(slot, null) - 8));
                else if (Flag(quick, slot)) { anchor = panel.Find("QuickSlots"); offset.x = (quickIndex++ - (quickCount - 1) * .5f) * 70; }
                else if (isAmmo) { anchor = panel.Find("AmmoSlots"); offset.x = (slotIndex - EquipmentAndQuickSlots.Slots.AmmoSlotStartIndex - 1) * 70; }
                else { offset = new Vector2(290 + customIndex / 4 * 70, -55 - customIndex % 4 * 70); customIndex++; }
                if (!anchor) anchor = panel;
                r.SetParent(anchor, false); r.anchorMin = r.anchorMax = anchor == panel ? new Vector2(0, 1) : new Vector2(.5f, .5f); r.pivot = new Vector2(.5f,.5f); r.anchoredPosition = offset;
                var label = element.transform.Find("binding").GetComponent<TMP_Text>();
                label.text = Flag(quick, slot) ? (string)quickText.Invoke(null, new object[] { (int)index.GetValue(slot,null) }) : "";
                // Native inventory refresh hides bindings outside the first inventory row.
                // EQS quick slots live in reserved rows: restore their binding visibility.
                label.gameObject.SetActive(Flag(quick, slot));
                label.enabled = true;
                var button = element.GetComponent<Button>();
                var colors = button.colors;
                var normal = grid.m_elementPrefab.GetComponent<Button>().colors.normalColor;
                colors.normalColor = drag != null && !(bool)fits.Invoke(null, new object[] { slot, drag }) ? new Color(.55f,.16f,.12f,normal.a) : normal;
                button.colors = colors;
                var tooltip = element.GetComponent<AugaUnity.ItemTooltip>(); if (tooltip) tooltip.Item = grid.GetInventory().GetItemAt(pos.x,pos.y);
                UpdateSlotHint(element, isAmmo ? 9 : Flag(quick, slot) ? 8 : isCosmetic ? slotIndex - EquipmentAndQuickSlots.Slots.CosmeticSlotStartIndex : Flag(equipment, slot) ? slotIndex - 8 : -1,
                    grid.GetInventory().GetItemAt(pos.x, pos.y) != null);
                UpdateAmmoSelection(element, isAmmo && slotIndex - EquipmentAndQuickSlots.Slots.AmmoSlotStartIndex == EquipmentAndQuickSlots.AmmoSlots.Selected(Player.m_localPlayer));
            }
            var image = panel.Find("Paperdoll").GetComponent<Image>();
            image.sprite = (Player.m_localPlayer.GetPlayerModel() == 1 ? female : male)?.GetValue(null) as Sprite;
            image.enabled = image.sprite && enabled.Any(s => Flag(equipment,s));
            var cosmeticBody = panel.Find("CosmeticPage/Paperdoll").GetComponent<Image>();
            cosmeticBody.sprite = image.sprite; cosmeticBody.enabled = image.sprite;
        }

        internal static void UpdateAmmoSelection(InventoryElement element, bool selected)
        {
            var child = element.transform.Find("AmmoActive");
            if (!child && selected)
            {
                // The selected ammo slot is framed with the slot border design, in a golden tint.
                var frame = new GameObject("AmmoActive", typeof(RectTransform), typeof(Image));
                child = frame.transform;
                child.SetParent(element.transform, false);
                var rect = (RectTransform)child; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                var image = frame.GetComponent<Image>();
                image.sprite = Overhaul.ItemSlotStyle.BorderSprite; image.color = new Color(1f, .78f, .3f, 1f); image.raycastTarget = false;
            }
            if (child) child.gameObject.SetActive(selected);
        }

        // Neutral outline glyphs identify empty cells without resembling real items.
        internal static void UpdateSlotHint(InventoryElement element, int slot, bool occupied)
        {
            var child = element.transform.Find("EquipmentHint");
            if (slot < 0 || occupied)
            {
                if (child) child.gameObject.SetActive(false);
                return;
            }
            if (!child)
            {
                var original = element.transform.Find("icon").GetComponent<Image>();
                var hint = new GameObject("EquipmentHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(AugaUnity.EquipmentSlotHint));
                child = hint.transform;
                child.SetParent(element.transform, false);
                var rect = (RectTransform)child;
                var source = original.rectTransform;
                rect.anchorMin = source.anchorMin; rect.anchorMax = source.anchorMax;
                rect.pivot = source.pivot; rect.sizeDelta = source.sizeDelta;
                rect.anchoredPosition = source.anchoredPosition; rect.localScale = source.localScale * .82f;
                child.SetSiblingIndex(original.transform.GetSiblingIndex());
            }
            var image = child.GetComponent<AugaUnity.EquipmentSlotHint>();
            image.Slot = slot; image.color = new Color(.82f, .77f, .67f, .28f);
            image.raycastTarget = false; image.SetVerticesDirty();
            child.gameObject.SetActive(true);
        }
    }
}
