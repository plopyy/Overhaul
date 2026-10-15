using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EquipmentAndQuickSlots;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Overhaul.Magic
{
    // Spell book (grimoire), first version: the OverhaulGrimoire prefab (mock-up E1) opened with its own key. The
    // spells are a demo table until the magic system exists: rank I of each spell is known, ranks II and III show the
    // class level that will unlock them. Known spells are dragged onto the 4 installed slots (2 normal spells, 1
    // special, 1 shield); the left page shows the spell last clicked.
    internal static class GrimoireWindow
    {
        internal enum Kind { Normal, Special, Shield }
        internal sealed class Spell
        {
            public string Id, Element, Icon, LockedIcon; public Kind Kind; public int Rank, Level; public float Eitr, Damage, Cast, Area; public bool PerSecond;
            public bool Known => Rank == 1;
        }

        static readonly string[] Elements = { "fire", "ice", "lightning" };
        static readonly Kind[] RowKinds = { Kind.Normal, Kind.Normal, Kind.Special, Kind.Shield };
        static readonly Dictionary<string, Spell[,]> Book = new Dictionary<string, Spell[,]>();
        static readonly Dictionary<string, Spell> ById = new Dictionary<string, Spell>();
        // Installed spells are those of the equipped Overhaul staff (StaffMagic slots): light attack, heavy attack (both
        // normal spells), special attack, block (shield). Without one, the slots are empty and nothing can be installed.
        static ItemDrop.ItemData Staff { get { var w = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentWeapon() : null; return StaffMagic.IsStaff(w) ? w : null; } }
        static string[] Installed()
        {
            var staff = Staff;
            return Enumerable.Range(0, StaffMagic.SlotCount).Select(i => StaffMagic.Spell(staff, (StaffMagic.Slot)i)).Select(id => id != null && ById.ContainsKey(id) ? id : null).ToArray();
        }
        static string element = "fire", selected = "fireball";

        static GameObject root, panel;
        static Player owner;
        static int closedFrame = -1;
        static Image ghost;
        internal static bool Visible => root && root.activeInHierarchy;
        internal static bool BlocksInput => Visible || closedFrame == Time.frameCount;

        static GrimoireWindow()
        {
            // id, icon, locked icon, eitr, damage, cast time, area for ranks I, II, III of each row.
            Row("fire", 0, "Burning", "Burning_Locked", ("firebolt", 8, 14, .3f, 0), ("searingbolt", 10, 20, .3f, 0), ("emberburst", 14, 30, .4f, 2));
            Row("fire", 1, "StaffFireball", "StaffFireball_Locked", ("fireball", 35, 72, 1.4f, 4), ("meteor", 45, 110, 1.8f, 5), ("comet", 60, 160, 2.2f, 6));
            Row("fire", 2, "Burning_Nova", "Burning_Locked", ("flamenova", 50, 60, .8f, 6), ("flamering", 60, 85, .9f, 7), ("blaze", 75, 120, 1f, 9));
            Row("fire", 3, "shield_banded2", "shield_banded2_Locked", ("emberaegis", 2, 0, 0, 0), ("blazingaegis", 3, 0, 0, 0), ("ashmantle", 4, 0, 0, 0));
            Row("ice", 0, "Frost", "Frost_Locked", ("iceshard", 8, 12, .3f, 0), ("frostblade", 10, 18, .3f, 0), ("shardstorm", 14, 26, .4f, 2));
            Row("ice", 1, "Cold", "Cold_Locked", ("icejavelin", 32, 64, 1.3f, 0), ("glacialspear", 42, 96, 1.6f, 0), ("avalanche", 58, 140, 2f, 5));
            Row("ice", 2, "Frost", "Frost_Locked", ("hail", 45, 50, 1f, 6), ("blizzard", 58, 75, 1.2f, 8), ("eternalwinter", 72, 105, 1.4f, 10));
            Row("ice", 3, "Shield_Frost", "shield_banded2_Locked", ("frostaegis", 2, 0, 0, 0), ("rimeaegis", 3, 0, 0, 0), ("glacialmantle", 4, 0, 0, 0));
            Row("lightning", 0, "Lightning", "Lightning_Locked", ("spark", 7, 13, .2f, 0), ("arc", 9, 19, .2f, 0), ("stormbolt", 13, 28, .3f, 0));
            Row("lightning", 1, "StaffLightning", "StaffLightning_Locked", ("lightningstrike", 30, 60, 1.2f, 2), ("thunderbolt", 40, 92, 1.5f, 3), ("skywrath", 55, 135, 1.9f, 4));
            Row("lightning", 2, "Lightning_Bright", "Lightning_Locked", ("chainlightning", 48, 55, .9f, 8), ("storm", 60, 80, 1.1f, 10), ("tempest", 75, 115, 1.3f, 12));
            Row("lightning", 3, "Shield_Storm", "shield_banded2_Locked", ("stormaegis", 2, 0, 0, 0), ("thunderaegis", 3, 0, 0, 0), ("stormmantle", 4, 0, 0, 0));
        }
        static void Row(string el, int row, string icon, string locked, params (string id, float eitr, float damage, float cast, float area)[] ranks)
        {
            if (!Book.TryGetValue(el, out var grid)) Book[el] = grid = new Spell[4, 3];
            for (int r = 0; r < 3; r++)
            {
                var (id, eitr, damage, cast, area) = ranks[r];
                var spell = new Spell { Id = id, Element = el, Icon = icon, LockedIcon = locked, Kind = RowKinds[row], Rank = r + 1, Level = r == 0 ? 0 : r == 1 ? 20 : 35, Eitr = eitr, Damage = damage, Cast = cast, Area = area, PerSecond = RowKinds[row] == Kind.Shield };
                grid[row, r] = spell; ById[id] = spell;
            }
        }

        // ---------------------------------------------------------------- opening
        internal static bool Shortcut() => ValConfig.GrimoireWindowKey != null && PreventSimilarHotkeys.IsShortcutDown(ValConfig.GrimoireWindowKey.Value);
        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (!player || player != owner) { Clear(); owner = player; }
            if (!player) return;
            if (ghost && ghost.gameObject.activeSelf) ghost.rectTransform.position = Input.mousePosition;
            if (Visible)
            {
                if (player.IsDead() || player.IsTeleporting() || Menu.IsVisible() || Console.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || TextInput.IsVisible() || (Chat.instance && Chat.instance.HasFocus())) { Close(); return; }
                if (Shortcut() || ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) Close();
            }
            else if (player.TakeInput() && !player.IsDead() && !player.InPlaceMode() && !InventoryGui.IsVisible() && !Minimap.IsOpen() && !StoreGui.IsVisible() && Shortcut()) Open();
        }
        static void Open()
        {
            if (!Auga.Auga.Assets.Grimoire || !Hud.instance) return;
            if (!root)
            {
                root = new GameObject("OverhaulGrimoire", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.SetActive(false); root.transform.SetParent(Hud.instance.transform, false);
                var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 1200;
                var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f; scaler.referencePixelsPerUnit = 50;
                panel = Object.Instantiate(Auga.Auga.Assets.Grimoire, root.transform, false);
                Wire(panel);
            }
            Populate(panel);
            root.SetActive(true); ZCursor.LockState = CursorLockMode.None; ZCursor.Show();
        }
        internal static void Close() { if (root) root.SetActive(false); if (ghost) ghost.gameObject.SetActive(false); closedFrame = Time.frameCount; if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture(); }
        internal static void Clear() { if (root) { Close(); Object.Destroy(root); } root = panel = null; ghost = null; owner = null; }

        // ---------------------------------------------------------------- controls
        static void Wire(GameObject p)
        {
            var t = p.transform;
            t.Find("Close").GetComponent<Button>().onClick.AddListener(Close);
            for (int i = 0; i < 3; i++) { int tab = i; t.Find("Tab" + i).GetComponent<Button>().onClick.AddListener(() => { element = Elements[tab]; Populate(p); }); }
            var page = t.Find("RightPage");
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 3; col++)
                {
                    var c = page.Find("Card" + row + "_" + col + "/Background").gameObject.AddComponent<GrimoireCard>();
                    c.Row = row; c.Col = col;
                }
            var left = t.Find("LeftPage");
            for (int i = 0; i < 4; i++) left.Find("Slot" + i).gameObject.AddComponent<GrimoireSlot>().Index = i;
            // Icon following the cursor while a spell is dragged.
            ghost = new GameObject("DragIcon", typeof(RectTransform)).AddComponent<Image>();
            ghost.transform.SetParent(root.transform, false); ghost.raycastTarget = false; ghost.rectTransform.sizeDelta = new Vector2(38, 38) * 1.32f;
            ghost.gameObject.SetActive(false);
        }
        internal static Spell At(int row, int col) => Book[element][row, col];
        internal static void Select(Spell s) { selected = s.Id; Populate(panel); }
        internal static void BeginDrag(Spell s)
        {
            if (!ghost) return;
            ghost.sprite = Sprite(panel.transform, "Icon_" + s.Icon); ghost.gameObject.SetActive(true); ghost.transform.SetAsLastSibling();
            ghost.rectTransform.position = Input.mousePosition;
        }
        internal static void EndDrag() { if (ghost) ghost.gameObject.SetActive(false); }
        // Normal spells go on either normal slot (moving between them swaps), the special spell and the shield on their own.
        internal static void Drop(Spell s, int slot)
        {
            if (s == null || !s.Known) return;
            if ((s.Kind == Kind.Normal) != (slot < 2) || (s.Kind == Kind.Special && slot != 2) || (s.Kind == Kind.Shield && slot != 3)) return;
            var staff = Staff;
            if (staff == null) { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "$overhaul_grimoire_no_staff"); return; }
            var installed = Installed();
            int from = Array.IndexOf(installed, s.Id);
            if (from >= 0 && from != slot) StaffMagic.SetSpell(staff, (StaffMagic.Slot)from, installed[slot]);
            StaffMagic.SetSpell(staff, (StaffMagic.Slot)slot, s.Id);
            Populate(panel);
        }

        // ---------------------------------------------------------------- display
        static string Loc(string key) => Localization.instance.Localize("$overhaul_grimoire_" + key);
        static string Number(float v) => v.ToString("0.#", CultureInfo.InvariantCulture).Replace(".", Loc("decimal"));
        static Sprite Sprite(Transform p, string name) { var lib = p.Find("Library/" + name); return lib ? lib.GetComponent<Image>().sprite : null; }
        static readonly string[] KindKeys = { "type_normal", "type_special", "type_shield" };

        // Fills the window for the current element, the selected spell and the installed slots (public for the Unity preview).
        public static void Populate(GameObject p)
        {
            if (!p) return;
            var t = p.transform;
            Localization.instance.Localize(t);
            for (int i = 0; i < 3; i++)
            {
                var tab = t.Find("Tab" + i); bool on = Elements[i] == element;
                foreach (var n in new[] { "Active", "IconActive" }) tab.Find(n).gameObject.SetActive(on);
                foreach (var n in new[] { "Inactive", "IconInactive" }) tab.Find(n).gameObject.SetActive(!on);
            }

            var installed = Installed();

            // Right page: the element's spells.
            var page = t.Find("RightPage");
            page.Find("Header/ElementIcon").GetComponent<Image>().sprite = Sprite(t, "Icon_" + (element == "fire" ? "Burning" : element == "ice" ? "Frost" : "Lightning"));
            page.Find("Header/ElementTitle").GetComponent<TMP_Text>().text = Loc("element_" + element);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.Find("Header"));
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 3; col++) Card(t, page.Find("Card" + row + "_" + col), Book[element][row, col], installed);

            // Left page: selected spell.
            var s = ById[selected];
            var left = t.Find("LeftPage");
            left.Find("DetailIcon").GetComponent<Image>().sprite = Sprite(t, "Icon_" + s.Icon);
            left.Find("DetailTitle").GetComponent<TMP_Text>().text = Loc("spell_" + s.Id);
            left.Find("DetailSubtitle").GetComponent<TMP_Text>().text = Loc("element_" + s.Element) + " · " + Loc(KindKeys[(int)s.Kind]) + " · " + Loc("rank" + s.Rank);
            left.Find("DetailDescription").GetComponent<TMP_Text>().text = Loc("spell_" + s.Id + "_desc");
            var values = new[] { Number(s.Eitr) + (s.PerSecond ? "/s" : ""), s.Damage > 0 ? Number(s.Damage) : "—", s.Cast > 0 ? Number(s.Cast) + " s" : "—", s.Area > 0 ? Number(s.Area) + " m" : "—" };
            for (int i = 0; i < 4; i++) left.Find("Stat" + i + "/Value").GetComponent<TMP_Text>().text = values[i];
            for (int i = 0; i < 4; i++)
            {
                var slot = left.Find("Slot" + i); var spell = installed[i] == null ? null : ById[installed[i]];
                var icon = slot.Find("Icon").GetComponent<Image>(); icon.enabled = spell != null;
                if (spell != null) icon.sprite = Sprite(t, "Icon_" + spell.Icon);
                slot.Find("Name").GetComponent<TMP_Text>().text = spell != null ? Loc("spell_" + spell.Id) : Loc("slot_empty");
            }
        }

        static void Card(Transform p, Transform card, Spell s, string[] installedIds)
        {
            bool known = s.Known, sel = s.Id == selected, installed = installedIds.Contains(s.Id);
            card.GetComponent<CanvasGroup>().alpha = known ? 1 : .55f;
            card.Find("SelectedGlow").gameObject.SetActive(sel);
            card.Find("Background").GetComponent<Image>().sprite = Sprite(p, known ? "CardGlow" : "CardPlain");
            card.Find("EdgeGold").gameObject.SetActive(sel); card.Find("EdgeDim").gameObject.SetActive(!sel);
            card.Find("Equipped").gameObject.SetActive(installed);
            card.Find("Diamond").GetComponent<Image>().sprite = Sprite(p, sel || installed ? "DiamondCardGold" : "DiamondCardDim");
            var icon = card.Find("Icon").GetComponent<Image>();
            icon.sprite = Sprite(p, "Icon_" + (known ? s.Icon : s.LockedIcon)); icon.color = new Color(1, 1, 1, known ? 1 : .6f);
            card.Find("Lock").gameObject.SetActive(!known);
            var name = card.Find("Name").GetComponent<TMP_Text>(); name.text = Loc("spell_" + s.Id);
            // The rank pips or the level line follow the name, one line lower for a two-line name (flex column of the mock-up).
            float width = ((RectTransform)name.transform).rect.width, one = name.GetPreferredValues("A", width, 0).y;
            float below = name.GetPreferredValues(name.text, width, 0).y > one * 1.5f ? 14.3f : 0;
            var pips = (RectTransform)card.Find("Pips"); pips.gameObject.SetActive(known); pips.anchoredPosition = new Vector2(pips.anchoredPosition.x, -(81.3f + below));
            for (int i = 0; i < 3; i++) pips.Find("Pip" + i).GetComponent<Image>().sprite = Sprite(p, i < s.Rank ? "PipFull" : "PipEmpty");
            var level = card.Find("Level").GetComponent<TMP_Text>(); level.gameObject.SetActive(!known);
            level.text = string.Format(Loc("level"), s.Level); level.rectTransform.anchoredPosition = new Vector2(level.rectTransform.anchoredPosition.x, -(80.3f + below));
        }

        // ---------------------------------------------------------------- input while open
        [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
        static class PlayerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        static class ControllerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        static class CursorInput { static bool Prefix() { if (!Visible) return true; ZCursor.LockState = CursorLockMode.None; ZCursor.Show(); return false; } }
        [HarmonyPatch(typeof(Menu), "Update")]
        static class MenuInput { static bool Prefix() => !BlocksInput; }
    }

    // Spell card of the right page: click shows it on the left page, drag installs it.
    internal sealed class GrimoireCard : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public int Row, Col;
        internal static GrimoireWindow.Spell Dragged;
        public void OnPointerClick(PointerEventData e) { if (!e.dragging) GrimoireWindow.Select(GrimoireWindow.At(Row, Col)); }
        public void OnBeginDrag(PointerEventData e) { var s = GrimoireWindow.At(Row, Col); if (!s.Known) return; Dragged = s; GrimoireWindow.BeginDrag(s); }
        public void OnDrag(PointerEventData e) { }
        public void OnEndDrag(PointerEventData e) { Dragged = null; GrimoireWindow.EndDrag(); }
    }

    internal sealed class GrimoireSlot : MonoBehaviour, IDropHandler
    {
        public int Index;
        public void OnDrop(PointerEventData e) { if (GrimoireCard.Dragged != null) GrimoireWindow.Drop(GrimoireCard.Dragged, Index); }
    }
}
