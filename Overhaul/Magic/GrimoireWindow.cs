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
    // Spell book (grimoire): the OverhaulGrimoire prefab (mock-up E1) opened with its own key. The right page shows the
    // spells of Spells.cfg (SpellSystem) for the open element, by type (rows: 2 normal, 1 special, 1 shield) and rank
    // (columns); known spells are dragged onto the 4 slots of the equipped staff (2 normal spells, 1 special, 1 shield),
    // locked ones show the class level that unlocks them. The left page shows the spell last clicked.
    internal static class GrimoireWindow
    {
        static readonly string[] Elements = { "fire", "ice", "lightning" };
        static readonly SpellSystem.Kind[] RowKinds = { SpellSystem.Kind.Normal, SpellSystem.Kind.Normal, SpellSystem.Kind.Special, SpellSystem.Kind.Shield };
        // Installed spells are those of the equipped Overhaul staff (StaffMagic slots): light attack, heavy attack (both
        // normal spells), special attack, block (shield). Without one, the slots are empty and nothing can be installed.
        static ItemDrop.ItemData Staff { get { var w = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentWeapon() : null; return StaffMagic.IsStaff(w) ? w : null; } }
        static string[] Installed()
        {
            var staff = Staff;
            return Enumerable.Range(0, StaffMagic.SlotCount).Select(i => StaffMagic.Spell(staff, (StaffMagic.Slot)i)).Select(id => SpellSystem.Get(id)?.Id).ToArray();
        }
        static string element = "fire", selected;

        static GameObject root, panel;
        static Player owner;
        static int closedFrame = -1;
        static Image ghost;
        internal static bool Visible => root && root.activeInHierarchy;
        internal static bool BlocksInput => Visible || closedFrame == Time.frameCount;

        static GrimoireWindow() => SpellSystem.Changed += () => { if (Visible) Populate(panel); };

        // The element's page: each spell in its type's first free row at its rank's column (spells beyond the page are left out).
        static SpellSystem.Spell[,] Page(string el)
        {
            var grid = new SpellSystem.Spell[4, 3];
            foreach (var s in SpellSystem.All.Where(s => s.Element == el))
            {
                for (int row = 0; row < 4; row++)
                    if (RowKinds[row] == s.Kind && grid[row, s.Rank - 1] == null) { grid[row, s.Rank - 1] = s; break; }
            }
            return grid;
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
        internal static SpellSystem.Spell At(int row, int col) => Page(element)[row, col];
        internal static void Select(SpellSystem.Spell s) { if (s == null) return; selected = s.Id; Populate(panel); }
        internal static void BeginDrag(SpellSystem.Spell s)
        {
            if (!ghost) return;
            ghost.sprite = Sprite(panel.transform, "Icon_" + s.Icon); ghost.gameObject.SetActive(true); ghost.transform.SetAsLastSibling();
            ghost.rectTransform.position = Input.mousePosition;
        }
        internal static void EndDrag() { if (ghost) ghost.gameObject.SetActive(false); }
        // Normal spells go on either normal slot (moving between them swaps), the special spell and the shield on their own.
        internal static void Drop(SpellSystem.Spell s, int slot)
        {
            if (s == null || !s.Known) return;
            if ((s.Kind == SpellSystem.Kind.Normal) != (slot < 2) || (s.Kind == SpellSystem.Kind.Special && slot != 2) || (s.Kind == SpellSystem.Kind.Shield && slot != 3)) return;
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
        static string Text(string key) => Localization.instance.Localize(key.StartsWith("$") ? key : "$" + key);
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
            var grid = Page(element);

            // Right page: the element's spells.
            var page = t.Find("RightPage");
            page.Find("Header/ElementIcon").GetComponent<Image>().sprite = Sprite(t, "Icon_Element_" + (element == "fire" ? "Fire" : element == "ice" ? "Ice" : "Lightning"));
            page.Find("Header/ElementTitle").GetComponent<TMP_Text>().text = Loc("element_" + element);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.Find("Header"));
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 3; col++) Card(t, page.Find("Card" + row + "_" + col), grid[row, col], installed);

            // Left page: selected spell (the first one of the book until one is clicked).
            var s = SpellSystem.Get(selected) ?? SpellSystem.All.FirstOrDefault();
            var left = t.Find("LeftPage");
            var detail = left.Find("DetailIcon").GetComponent<Image>(); detail.enabled = s != null;
            if (s != null) detail.sprite = Sprite(t, "Icon_" + s.Icon);
            left.Find("DetailTitle").GetComponent<TMP_Text>().text = s != null ? Text(s.NameKey) : "";
            left.Find("DetailSubtitle").GetComponent<TMP_Text>().text = s != null ? Loc("element_" + s.Element) + " · " + Loc(KindKeys[(int)s.Kind]) + " · " + Loc("rank" + s.Rank) : "";
            left.Find("DetailDescription").GetComponent<TMP_Text>().text = s != null ? Text(s.DescriptionKey) : "";
            float area = s != null ? Area(s) : 0;
            var values = s == null ? new[] { "—", "—", "—", "—" } : new[] { Number(s.Eitr), s.Power > 0 ? Number(s.Power) + " %" : "—", "—", area > 0 ? Number(area) + " m" : "—" };
            for (int i = 0; i < 4; i++) left.Find("Stat" + i + "/Value").GetComponent<TMP_Text>().text = values[i];
            for (int i = 0; i < 4; i++)
            {
                var slot = left.Find("Slot" + i); var spell = SpellSystem.Get(installed[i]);
                var icon = slot.Find("Icon").GetComponent<Image>(); icon.enabled = spell != null;
                if (spell != null) icon.sprite = Sprite(t, "Icon_" + spell.Icon);
                slot.Find("Name").GetComponent<TMP_Text>().text = spell != null ? Text(spell.NameKey) : Loc("slot_empty");
            }
        }

        // Radius of the spell's explosion or area: its projectile's, else the area it leaves on impact.
        static float Area(SpellSystem.Spell s)
        {
            Projectile projectile;
            try { projectile = SpellSystem.AttackFor(s)?.m_attackProjectile?.GetComponent<Projectile>(); }
            catch (Exception) { return 0; }   // no game prefabs (Unity preview)
            if (!projectile) return 0;
            if (projectile.m_aoe > 0) return projectile.m_aoe;
            var aoe = projectile.m_spawnOnHit ? projectile.m_spawnOnHit.GetComponent<Aoe>() : null;
            return aoe ? aoe.m_radius : 0;
        }

        static void Card(Transform p, Transform card, SpellSystem.Spell s, string[] installedIds)
        {
            card.gameObject.SetActive(s != null);
            if (s == null) return;
            bool known = s.Known, sel = s.Id == selected, installed = installedIds.Contains(s.Id);
            card.GetComponent<CanvasGroup>().alpha = known ? 1 : .55f;
            card.Find("SelectedGlow").gameObject.SetActive(sel);
            card.Find("Background").GetComponent<Image>().sprite = Sprite(p, known ? "CardGlow" : "CardPlain");
            card.Find("EdgeGold").gameObject.SetActive(sel); card.Find("EdgeDim").gameObject.SetActive(!sel);
            card.Find("Equipped").gameObject.SetActive(installed);
            card.Find("Diamond").GetComponent<Image>().sprite = Sprite(p, sel || installed ? "DiamondCardGold" : "DiamondCardDim");
            var icon = card.Find("Icon").GetComponent<Image>();
            icon.sprite = Sprite(p, "Icon_" + s.Icon + (known ? "" : "_Locked")); icon.color = new Color(1, 1, 1, known ? 1 : .6f);
            card.Find("Lock").gameObject.SetActive(!known);
            var name = card.Find("Name").GetComponent<TMP_Text>(); name.text = Text(s.NameKey);
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
        internal static SpellSystem.Spell Dragged;
        public void OnPointerClick(PointerEventData e) { if (!e.dragging) GrimoireWindow.Select(GrimoireWindow.At(Row, Col)); }
        public void OnBeginDrag(PointerEventData e) { var s = GrimoireWindow.At(Row, Col); if (s == null || !s.Known) return; Dragged = s; GrimoireWindow.BeginDrag(s); }
        public void OnDrag(PointerEventData e) { }
        public void OnEndDrag(PointerEventData e) { Dragged = null; GrimoireWindow.EndDrag(); }
    }

    internal sealed class GrimoireSlot : MonoBehaviour, IDropHandler
    {
        public int Index;
        public void OnDrop(PointerEventData e) { if (GrimoireCard.Dragged != null) GrimoireWindow.Drop(GrimoireCard.Dragged, Index); }
    }
}
