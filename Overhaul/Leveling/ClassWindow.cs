using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EquipmentAndQuickSlots;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Overhaul.Leveling
{
    // Class windows (prefabs OverhaulClassChoice, OverhaulClassTree, OverhaulClassHud from OverhaulLab's
    // ClassWindowsAuthoring). The class key opens the choice screen while no class is chosen (16 classes, the first row
    // pickable; a click shows the class's three paths, then "Choisir la classe"), the tree window afterwards (its
    // "Changer de classe" goes back to the choice). The HUD button under the hotbar messages blinks with "[P] pour
    // ouvrir" while no class is chosen, then shows the class emblem.
    internal static class ClassWindow
    {
        static GameObject root, choice, treePanel, hud;
        static Player owner;
        static int closedFrame = -1;
        // Choice screen: class shown on the paths screen (-1 = grid), hovered path column.
        static int shownClass = -1;
        // Tree window selection: path index, kind and skill.
        static int selPath;
        static ClassSkillKind selKind = ClassSkillKind.Normal;
        static string selSkill;
        static int pendingPath = -1;

        internal static bool Visible => root && root.activeInHierarchy;
        internal static bool BlocksInput => Visible || closedFrame == Time.frameCount;
        internal static bool Shortcut(bool held = false) => Features.Classes && ValConfig.ClassWindowKey != null &&
            (held ? PreventSimilarHotkeys.IsShortcutPressed(ValConfig.ClassWindowKey.Value) : PreventSimilarHotkeys.IsShortcutDown(ValConfig.ClassWindowKey.Value));

        static OverhaulCharacter Character => Player.m_localPlayer ? OverhaulCharacter.Get(Player.m_localPlayer) : null;
        static ClassSystem Classes => Character?.Classes;

        // ---------------------------------------------------------------- opening
        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (!player || player != owner) { Clear(); owner = player; }
            if (!player) return;
            UpdateHud();
            if (Visible)
            {
                if (player.IsDead() || player.IsTeleporting() || Menu.IsVisible() || Console.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || TextInput.IsVisible() || (Chat.instance && Chat.instance.HasFocus())) { Close(); return; }
                if (Shortcut() || ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    // Escape first closes the confirmation box.
                    if (pendingPath >= 0 && !Shortcut()) { pendingPath = -1; Refresh(); } else Close();
                }
            }
            else if (player.TakeInput() && !player.IsDead() && !player.InPlaceMode() && !InventoryGui.IsVisible() && !Minimap.IsOpen() && !StoreGui.IsVisible() && Shortcut()) Open();
        }

        internal static void Open()
        {
            var classes = Classes;
            if (classes == null || Character.InvalidClassSave || !Auga.Auga.Assets.ClassChoice || !Auga.Auga.Assets.ClassTree || !Hud.instance) return;
            if (!root)
            {
                root = new GameObject("OverhaulClasses", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.SetActive(false); root.transform.SetParent(Hud.instance.transform, false);
                var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 1200;
                var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f; scaler.referencePixelsPerUnit = 50;
                choice = Object.Instantiate(Auga.Auga.Assets.ClassChoice, root.transform, false);
                treePanel = Object.Instantiate(Auga.Auga.Assets.ClassTree, root.transform, false);
                Localization.instance.Localize(choice.transform); Localization.instance.Localize(treePanel.transform);
                WireChoice(choice); WireTree(treePanel);
            }
            shownClass = -1; pendingPath = -1; selSkill = null; selPath = 0; selKind = ClassSkillKind.Normal;
            if (classes.HasClass) { var p = classes.Active; var data = ClassSkills.GetClass(classes.ActiveClassID); if (p?.ChosenPathID != null && data != null) selPath = Mathf.Max(0, data.Paths.FindIndex(x => x.ID == p.ChosenPathID)); }
            ShowTree(classes.HasClass);
            root.SetActive(true); ZCursor.LockState = CursorLockMode.None; ZCursor.Show();
        }
        internal static void Close() { if (root) root.SetActive(false); pendingPath = -1; closedFrame = Time.frameCount; if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture(); }
        internal static void Clear() { if (root) { Close(); Object.Destroy(root); } if (hud) Object.Destroy(hud); root = choice = treePanel = hud = null; owner = null; }

        static void ShowTree(bool tree)
        {
            choice.SetActive(!tree); treePanel.SetActive(tree);
            Refresh();
        }
        static void Refresh()
        {
            var classes = Classes; if (classes == null || !root) return;
            if (choice.activeSelf) PopulateChoice(choice, shownClass);
            if (treePanel.activeSelf) PopulateTree(treePanel, classes, selPath, selKind, selSkill, pendingPath);
        }
        static void Changed()
        {
            Character?.Store();
            Effects.Effects.Invalidate();
            Refresh();
        }

        // ---------------------------------------------------------------- helpers
        static string L(string key) => string.IsNullOrEmpty(key) ? "" : Localization.instance.Localize(key);
        static Color ColorOf(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        static Sprite Sprite(Transform panel, string name) { var t = panel.Find("Library/" + name); return t ? t.GetComponent<Image>().sprite : null; }
        static T Get<T>(Transform t, string path) where T : Component => t.Find(path).GetComponent<T>();
        static void SetText(Transform t, string path, string text) => Get<TMP_Text>(t, path).text = text;
        // A description's per-rank series ("5/10/15/20") reduced to the value of one rank (1-based), the last value past its end.
        internal static string AtRank(string text, int rank) => Regex.Replace(text, @"\d+(?:[.,]\d+)?(?:/\d+(?:[.,]\d+)?)+", m => { var v = m.Value.Split('/'); return v[Mathf.Min(rank, v.Length) - 1]; });
        static void Emblem(Transform emblem, Transform panel, ClassData c, Color colour, bool locked)
        {
            emblem.Find("Rune").GetComponent<Image>().sprite = Sprite(panel, "Rune_" + c.Emblem);
            var grey = new Color(.42f, .40f, .37f);
            emblem.Find("Rune").GetComponent<Image>().color = locked ? grey : colour;
            emblem.Find("Border").GetComponent<Image>().color = locked ? grey : colour;
            var chain = emblem.Find("LockChain"); if (chain) chain.gameObject.SetActive(locked);
        }

        // ---------------------------------------------------------------- choice screen
        static void WireChoice(GameObject p)
        {
            var grid = p.transform.Find("Grid"); var paths = p.transform.Find("Paths");
            Get<Button>(grid, "Close").onClick.AddListener(Close);
            Get<Button>(paths, "Close").onClick.AddListener(Close);
            for (int k = 0; k < 16; k++)
            {
                int index = k; var tile = grid.Find("Tile" + k);
                Get<Button>(grid, "Tile" + k).onClick.AddListener(() => { var c = ClassSkills.Classes.ElementAtOrDefault(index); if (c != null && c.Pickable) { shownClass = index; Refresh(); } });
                Hover(tile.gameObject, on => { var c = ClassSkills.Classes.ElementAtOrDefault(index); tile.Find("Emblem/Glow").gameObject.SetActive(on && c != null && c.Pickable); });
            }
            for (int i = 0; i < 3; i++)
            {
                var col = paths.Find("Path" + i);
                Hover(col.Find("Hit").gameObject, on => { for (int r = 0; r < 5; r++) col.Find("Hot" + r).gameObject.SetActive(on); col.Find("Emblem/Glow").gameObject.SetActive(on); });
            }
            Get<Button>(paths, "Back").onClick.AddListener(() => { shownClass = -1; Refresh(); });
            Get<Button>(paths, "Pick").onClick.AddListener(PickShownClass);
        }
        static void Hover(GameObject go, System.Action<bool> changed)
        {
            var trigger = go.GetComponent<UnityEngine.EventSystems.EventTrigger>() ?? go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var enter = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter }; enter.callback.AddListener(_ => changed(true));
            var exit = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit }; exit.callback.AddListener(_ => changed(false));
            trigger.triggers.Add(enter); trigger.triggers.Add(exit);
        }
        static void PickShownClass()
        {
            var classes = Classes; var c = ClassSkills.Classes.ElementAtOrDefault(shownClass);
            if (classes == null || c == null || !c.Pickable) return;
            if (classes.ActiveClassID != c.ID) { classes.ActiveClassID = c.ID; classes.Buffs.Clear(); }
            classes.Get(c.ID);
            selSkill = null; selKind = ClassSkillKind.Normal; selPath = 0; shownClass = -1;
            var chosen = classes.Active.ChosenPathID; if (chosen != null) selPath = Mathf.Max(0, c.Paths.FindIndex(x => x.ID == chosen));
            Character?.Store(); Effects.Effects.Invalidate();
            ShowTree(true);
        }

        internal static void PopulateChoice(GameObject p, int shown)
        {
            var grid = p.transform.Find("Grid"); var paths = p.transform.Find("Paths");
            var all = ClassSkills.Classes;
            grid.gameObject.SetActive(shown < 0); paths.gameObject.SetActive(shown >= 0);
            if (shown < 0)
            {
                for (int k = 0; k < 16; k++)
                {
                    var tile = grid.Find("Tile" + k); var c = all.ElementAtOrDefault(k);
                    tile.gameObject.SetActive(c != null); if (c == null) continue;
                    Emblem(tile.Find("Emblem"), p.transform, c, ColorOf(c.Color), !c.Pickable);
                    var name = Get<TMP_Text>(tile, "Name"); name.text = L(c.NameKey); name.color = c.Pickable ? new Color(.84f, .80f, .72f) : new Color(.45f, .42f, .38f);
                    tile.GetComponent<Button>().interactable = c.Pickable;
                }
                return;
            }
            var cls = all[shown];
            SetText(paths, "Title", L(cls.NameKey));
            SetText(paths, "Role", L(cls.RoleKey));
            SetText(paths, "Description", L(cls.DescriptionKey));
            for (int i = 0; i < 3; i++)
            {
                var col = paths.Find("Path" + i); var path = cls.Paths.ElementAtOrDefault(i);
                col.gameObject.SetActive(path != null); if (path == null) continue;
                var colour = ColorOf(path.Color);
                Emblem(col.Find("Emblem"), p.transform, cls, colour, false);
                var name = Get<TMP_Text>(col, "Name"); name.text = L(path.NameKey); name.color = colour;
                SetText(col, "Description", L(path.DescriptionKey));
                for (int r = 0; r < 5; r++)
                    for (int d = 0; d < 5; d++)
                        Get<Image>(col, "Gauge" + r + "/Pip" + d).sprite = Sprite(p.transform, path.Gauges != null && r < path.Gauges.Length && d < path.Gauges[r] ? "PipOn" : "PipOff");
            }
        }

        // ---------------------------------------------------------------- tree window
        static void WireTree(GameObject p)
        {
            var win = p.transform.Find("Window");
            Get<Button>(win, "Close").onClick.AddListener(Close);
            for (int b = 0; b < 3; b++)
            {
                int path = b; var tree = win.Find("Tree" + b);
                Get<Button>(tree, "Ultimate/Bg").onClick.AddListener(() => Select(path, ClassSkillKind.Ultimate, null));
                Get<Button>(tree, "Split/Active").onClick.AddListener(() => Select(path, ClassSkillKind.Active, null));
                Get<Button>(tree, "Split/Passive").onClick.AddListener(() => Select(path, ClassSkillKind.Passive, null));
                Get<Button>(tree, "Choose").onClick.AddListener(() => { pendingPath = path; Refresh(); });
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        int row = r, col = c; var slot = tree.Find("Skill" + c + "_" + r);
                        slot.Find("Bg").GetComponent<Button>().onClick.AddListener(() => Select(path, ClassSkillKind.Normal, SkillAt(path, col, row)?.ID));
                        slot.Find("Plus").GetComponent<Button>().onClick.AddListener(() => Spend(path, col, row, true));
                        slot.Find("Minus").GetComponent<Button>().onClick.AddListener(() => Spend(path, col, row, false));
                    }
            }
            var box = win.Find("Confirm/Box");
            Get<Button>(box, "Cancel").onClick.AddListener(() => { pendingPath = -1; Refresh(); });
            Get<Button>(box, "Confirm").onClick.AddListener(ConfirmPath);
            Get<Button>(win, "Bottom/Change").onClick.AddListener(() => { shownClass = -1; pendingPath = -1; ShowTree(false); });
            // Before a path is chosen the bottom row is BottomNew, with its own Changer de classe.
            Get<Button>(win, "BottomNew/Change").onClick.AddListener(() => { shownClass = -1; pendingPath = -1; ShowTree(false); });
            Get<Button>(win, "Bottom/Reset").onClick.AddListener(() => { var pr = Classes?.Active; if (pr == null) return; ClassRules.ResetAll(pr); Changed(); });
        }
        static ClassData ActiveData => ClassSkills.GetClass(Classes?.ActiveClassID);
        static ClassSkill SkillAt(int path, int col, int row) => SkillAt(ActiveData, path, col, row);
        static ClassSkill SkillAt(ClassData c, int path, int col, int row)
        {
            var pd = c?.Paths.ElementAtOrDefault(path); if (pd == null) return null;
            return ClassRules.PathSkills(c.ID, pd.ID).FirstOrDefault(s => s.Data.Kind == ClassSkillKind.Normal && s.Data.Cell.x == col && s.Data.Cell.y == row);
        }
        static ClassSkill KindSkill(ClassData c, int path, ClassSkillKind kind)
        {
            var pd = c?.Paths.ElementAtOrDefault(path); if (pd == null) return null;
            return ClassRules.PathSkills(c.ID, pd.ID).FirstOrDefault(s => s.Data.Kind == kind);
        }
        static void Select(int path, ClassSkillKind kind, string skill) { selPath = path; selKind = kind; selSkill = skill; Refresh(); }
        static void Spend(int path, int col, int row, bool add)
        {
            var pr = Classes?.Active; var s = SkillAt(path, col, row); if (pr == null || s == null) return;
            selPath = path; selKind = ClassSkillKind.Normal; selSkill = s.ID;
            if (add ? ClassRules.Add(pr, s) : ClassRules.Remove(pr, s)) Changed(); else Refresh();
        }
        static void ConfirmPath()
        {
            var pr = Classes?.Active; var pd = ActiveData?.Paths.ElementAtOrDefault(pendingPath);
            if (pr != null && pd != null && pr.ChosenPathID == null) { pr.ChosenPathID = pd.ID; selPath = pendingPath; }
            pendingPath = -1; Changed();
        }

        internal static void PopulateTree(GameObject p, ClassSystem classes, int sPath, ClassSkillKind sKind, string sSkill, int pending)
        {
            var win = p.transform.Find("Window");
            var c = ClassSkills.GetClass(classes.ActiveClassID); var pr = classes.Active;
            if (c == null || pr == null) return;
            // Default selection (the mock-up's): the selected path's first bottom-row skill, framed in gold like a click.
            if (sKind == ClassSkillKind.Normal && ClassSkills.Get(sSkill) == null) sSkill = SkillAt(c, sPath, 0, 0)?.ID;
            SetText(win, "Title", L(c.NameKey));
            // Left side: level and experience to the next level.
            SetText(win, "Side/Level", pr.Level.ToString(CultureInfo.InvariantCulture));
            long need = pr.Level >= ClassProgress.MaxLevel ? 0 : ClassProgress.ExperienceToNextLevel(pr.Level);
            SetText(win, "Side/Experience", need > 0 ? string.Format(L("$overhaul_class_exp"), pr.CurrentExperience.ToString("N0", CultureInfo.CurrentCulture), need.ToString("N0", CultureInfo.CurrentCulture)) : "");
            ((RectTransform)win.Find("Side/Bar/Fill")).anchorMax = new Vector2(need > 0 ? Mathf.Clamp01(pr.CurrentExperience / (float)need) : 1, 1);
            bool chosen = pr.ChosenPathID != null;
            for (int b = 0; b < 3; b++)
            {
                var tree = win.Find("Tree" + b); var pd = c.Paths.ElementAtOrDefault(b);
                tree.gameObject.SetActive(pd != null); if (pd == null) continue;
                var colour = ColorOf(pd.Color);
                Get<Image>(tree, "Head").color = colour;
                SetText(tree, "Head/Name", L(pd.NameKey));
                Get<CanvasGroup>(tree, "Head").alpha = chosen && pr.ChosenPathID != pd.ID ? .6f : 1;
                bool empty = !ClassRules.PathSkills(c.ID, pd.ID).Any();
                tree.Find("Empty").gameObject.SetActive(empty);
                tree.Find("Ultimate").gameObject.SetActive(!empty); tree.Find("Split").gameObject.SetActive(!empty);
                // Ultimate.
                var ult = KindSkill(c, b, ClassSkillKind.Ultimate); var ul = tree.Find("Ultimate");
                bool learned = ClassRules.UltimateLearned(pr, c.ID, pd.ID), locked = chosen && pr.ChosenPathID != pd.ID;
                Icon(ul.Find("IconMask/Icon").GetComponent<Image>(), p.transform, ult, learned);
                ul.GetComponent<CanvasGroup>().alpha = locked ? .35f : 1;
                ul.Find("Bg").GetComponent<Image>().sprite = Sprite(p.transform, locked ? "UltBg_Grey" : "91a76baf68a2");
                ul.Find("Border").GetComponent<Image>().sprite = Sprite(p.transform, locked ? "UltBorder_Grey" : "7f01a097e533");
                if (locked) ul.Find("IconMask/Icon").GetComponent<Image>().sprite = Sprite(p.transform, ult != null && Sprite(p.transform, ult.Data.Icon) ? ult.Data.Icon + "_Off" : "Placeholder_Off");
                ul.Find("Lock").gameObject.SetActive(locked);
                ul.Find("Picked").gameObject.SetActive(sPath == b && sKind == ClassSkillKind.Ultimate);
                // Skills.
                for (int r = 0; r < 4; r++)
                    for (int col = 0; col < 3; col++)
                    {
                        var slot = tree.Find("Skill" + col + "_" + r); var s = SkillAt(c, b, col, r);
                        slot.gameObject.SetActive(s != null); if (s == null) continue;
                        int rank = pr.SkillPoints.TryGetValue(s.ID, out int v) ? v : 0;
                        Icon(slot.Find("IconMask/Icon").GetComponent<Image>(), p.transform, s, rank > 0);
                        slot.GetComponent<CanvasGroup>().alpha = ClassRules.RowOpen(pr, c.ID, pd.ID, r) ? 1 : .45f;
                        SetText(slot, "Rank", rank + "/" + s.Data.MaxRank);
                        slot.Find("Plus").GetComponent<Button>().interactable = ClassRules.CanAdd(pr, s);
                        slot.Find("Minus").GetComponent<Button>().interactable = ClassRules.CanRemove(pr, s);
                        slot.Find("Picked").gameObject.SetActive(sKind == ClassSkillKind.Normal && sSkill == s.ID);
                    }
                // Bottom bar: each half's image (greyed while the path is not the chosen one), the bar dimmed then, the picked
                // half's border gold.
                bool lit = pr.ChosenPathID == pd.ID;
                var dim = lit ? Color.white : new Color(.75f, .75f, .75f, 1);
                foreach (var (half, kind) in new[] { ("Active", ClassSkillKind.Active), ("Passive", ClassSkillKind.Passive) })
                {
                    MainIcon(tree.Find("Split/" + half), p.transform, KindSkill(c, b, kind), lit);
                    Get<Image>(tree, "Split/" + half + "Border").color = dim;
                    tree.Find("Split/" + half + "Picked").gameObject.SetActive(sPath == b && sKind == kind);
                }
                Get<Image>(tree, "Split/Bg").color = dim;
                tree.Find("Choose").gameObject.SetActive(!chosen && !empty);
            }
            // Bottom: points / change / reset once a path is chosen; before, the Choisir row then Changer de classe alone,
            // one row lower (the mock-up's window grows from 908 to 964, with the 85 px bottom bar).
            var bottom = win.Find("Bottom");
            bottom.gameObject.SetActive(chosen); win.Find("BottomNew").gameObject.SetActive(!chosen);
            var wrt = (RectTransform)win; wrt.sizeDelta = new Vector2(wrt.sizeDelta.x, chosen ? 908 : 964);
            SetText(bottom, "Points", string.Format(L("$overhaul_class_points"), pr.AvailablePoints, ClassProgress.TotalPointsAtLevel(pr.Level)));
            Detail(win, p.transform, c, pr, sPath, sKind, sSkill);
            // Confirmation box.
            var confirm = win.Find("Confirm"); var pending0 = c.Paths.ElementAtOrDefault(pending);
            confirm.gameObject.SetActive(pending0 != null);
            if (pending0 != null)
            {
                SetText(confirm, "Box/Title", string.Format(L("$overhaul_class_path_confirm_title"), L(pending0.NameKey)));
                SetText(confirm, "Box/Text", string.Format(L("$overhaul_class_path_confirm_text"), L(pending0.NameKey)));
            }
        }
        // Skill icon from the prefab library: its greyscale copy (filter grayscale(1) brightness(.55)) while not learned;
        // without an icon, the mock-up's placeholder at 70 % of the slot.
        static void Icon(Image image, Transform panel, ClassSkill s, bool lit)
        {
            var own = s == null ? null : Sprite(panel, s.Data.Icon);
            var sprite = own ? Sprite(panel, s.Data.Icon + (lit ? "" : "_Off")) : Sprite(panel, lit ? "e6dfaae8fab1" : "Placeholder_Off");
            image.sprite = sprite; image.enabled = sprite; image.color = Color.white;
            image.rectTransform.localScale = Vector3.one * (own ? 1 : .7f);
        }
        // Bottom bar / detail frame image: the skill's picture placed by the prefab (210 px wide, its rule), greyed while not
        // lit; without one, the grey placeholder.
        static void MainIcon(Transform holder, Transform panel, ClassSkill s, bool lit)
        {
            bool own = s != null && Sprite(panel, s.Data.Icon);
            var icon = Get<Image>(holder, "Icon"); icon.gameObject.SetActive(own);
            holder.Find("Placeholder").gameObject.SetActive(!own);
            if (own) icon.sprite = Sprite(panel, s.Data.Icon + (lit ? "" : "_Off"));
        }

        // Left panel: the selected skill (default the chosen or first path's first bottom-row skill).
        static void Detail(Transform win, Transform panel, ClassData c, ClassProgress pr, int b, ClassSkillKind kind, string skillID)
        {
            var pd = c.Paths.ElementAtOrDefault(b);
            var s = kind == ClassSkillKind.Normal ? (ClassSkills.Get(skillID) ?? SkillAt(c, b, 0, 0)) : KindSkill(c, b, kind);
            var side = win.Find("Side");
            bool main = kind == ClassSkillKind.Active || kind == ClassSkillKind.Passive;
            side.Find("DetailSkill").gameObject.SetActive(s != null && kind == ClassSkillKind.Normal);
            side.Find("DetailMain").gameObject.SetActive(s != null && main);
            side.Find("DetailUltimate").gameObject.SetActive(s != null && kind == ClassSkillKind.Ultimate);
            side.Find("DetailName").gameObject.SetActive(s != null); side.Find("Effects").gameObject.SetActive(s != null);
            if (s == null || pd == null) return;
            bool chosen = pr.ChosenPathID != null;
            int rank = pr.SkillPoints.TryGetValue(s.ID, out int v) ? v : 0;
            bool ultLearned = ClassRules.UltimateLearned(pr, c.ID, pd.ID);
            if (kind == ClassSkillKind.Ultimate) Icon(Get<Image>(side, "DetailUltimate/IconMask/Icon"), panel, s, ultLearned);
            else if (main) MainIcon(side.Find("DetailMain/IconMask"), panel, s, pr.ChosenPathID == pd.ID);
            else Icon(Get<Image>(side, "DetailSkill/IconMask/Icon"), panel, s, rank > 0);
            SetText(side, "DetailName", L(s.Data.NameKey));
            var fx = side.Find("Effects");
            var path = Get<TMP_Text>(fx, "Path"); path.text = L(pd.NameKey); path.color = ColorOf(pd.Color);
            string text = L(s.Data.DescriptionKey), need = "", later = "", rankLine = "";
            if (kind == ClassSkillKind.Normal)
            {
                rankLine = string.Format(L("$overhaul_class_rank"), rank + "/" + s.Data.MaxRank);
                var lines = new List<string>();
                for (int r = Mathf.Max(rank, 1) + 1; r <= s.Data.MaxRank; r++) lines.Add(string.Format(L("$overhaul_class_rank_next"), r, AtRank(text, r)));
                later = string.Join("\n", lines);
                text = AtRank(text, Mathf.Max(rank, 1));
                if (rank < s.Data.MaxRank)
                    need = !chosen ? L("$overhaul_class_need_path") : !ClassRules.RowOpen(pr, c.ID, pd.ID, s.Data.Cell.y) ? L("$overhaul_class_need_row") : pr.AvailablePoints < 1 ? L("$overhaul_class_need_points") : "";
            }
            else if (kind == ClassSkillKind.Ultimate)
            {
                rankLine = string.Format(L("$overhaul_class_rank"), (ultLearned ? 1 : 0) + "/1");
                need = ultLearned ? "" : !chosen ? L("$overhaul_class_need_path_this") : pr.ChosenPathID != pd.ID ? L("$overhaul_class_need_chosen") : L("$overhaul_class_need_top");
            }
            else rankLine = L(pr.ChosenPathID == pd.ID ? "$overhaul_class_with_tree" : "$overhaul_class_with_choice");
            Get<TMP_Text>(fx, "Rank").text = rankLine;
            Get<TMP_Text>(fx, "Text").text = text;
            var needText = Get<TMP_Text>(fx, "Need"); needText.text = need; needText.gameObject.SetActive(need != "");
            var rows = later == "" ? new string[0] : later.Split('\n');
            var laterBox = fx.Find("Later"); laterBox.gameObject.SetActive(rows.Length > 0); fx.Find("LaterGap").gameObject.SetActive(rows.Length > 0);
            for (int i = 0; i < 6; i++)
            {
                var row = Get<TMP_Text>(laterBox, "Rank" + i);
                row.gameObject.SetActive(i < rows.Length);
                if (i < rows.Length) row.text = rows[i].Replace("<b>", "<b><color=#d6ccb8>").Replace("</b>", "</color></b>");
            }
        }

        // ---------------------------------------------------------------- HUD button
        static float blink;
        static void UpdateHud()
        {
            var classes = Classes;
            if (!hud)
            {
                if (!Hud.instance || !Auga.Auga.Assets.ClassHud || classes == null) return;
                hud = Object.Instantiate(Auga.Auga.Assets.ClassHud, Hud.instance.m_rootObject.transform, false);
                Localization.instance.Localize(hud.transform);
                hud.transform.Find("Emblem/Base").GetComponent<Button>().onClick.AddListener(() => { if (!Visible) Open(); });
            }
            bool none = !classes.HasClass;
            var data = ClassSkills.GetClass(classes.ActiveClassID);
            var rune = hud.transform.Find("Emblem/Rune").GetComponent<Image>();
            rune.sprite = Sprite(hud.transform, none || data == null ? "Rune_plus" : "Rune_" + data.Emblem);
            var colour = none || data == null ? new Color(.84f, .80f, .72f) : ColorOf(data.Color);
            rune.color = colour; hud.transform.Find("Emblem/Border").GetComponent<Image>().color = colour;
            var hint = hud.transform.Find("Hint").GetComponent<TMP_Text>();
            hint.text = string.Format(L("$overhaul_class_hud_hint"), KeyLabel());
            // Blink: the gold glow pulses every 1.2 s while no class is chosen.
            var glow = hud.transform.Find("Emblem/Glow").GetComponent<Image>();
            glow.gameObject.SetActive(none);
            if (none) { blink += Time.unscaledDeltaTime; glow.color = new Color(1, 1, 1, .5f - .5f * Mathf.Cos(blink * Mathf.PI * 2 / 1.2f)); }
        }
        static string KeyLabel()
        {
            var key = ValConfig.ClassWindowKey?.Value.MainKey ?? KeyCode.P;
            return key.ToString().ToUpperInvariant();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
        static class PlayerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        static class ControllerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        static class CursorInput { static bool Prefix() { if (!Visible) return true; ZCursor.LockState = CursorLockMode.None; ZCursor.Show(); return false; } }
        [HarmonyPatch(typeof(Menu), "Update")]
        static class MenuInput { static bool Prefix() => !BlocksInput; }
    }
}
