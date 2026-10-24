using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // Redraws the native key hints as a compact two-column box in the bottom-right corner, in Auga's key hint look
    // (OverhaulKeyHints prefab). Valheim still decides which hints apply; the box only mirrors its active rows.
    internal sealed class KeyHintPanel : MonoBehaviour
    {
        private sealed class Token { internal string Text; internal Sprite Sprite; internal bool Key; }
        private sealed class Row { internal string Label = ""; internal readonly List<Token> Keys = new List<Token>(); }

        private static readonly Regex Tags = new Regex("<[^>]+>");
        private KeyHints hints;
        private GameObject[] roots;
        private RectTransform box;
        private GridLayoutGroup grid;
        private Transform templates;
        private Sprite[] mouseSprites;
        private Sprite wheelSprite;
        private readonly Dictionary<string, int> mouseButtons = new Dictionary<string, int>();
        private string primaryLabel, secondaryLabel, signature;

        [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.Awake))]
        private static class Install
        {
            private static void Postfix(KeyHints __instance)
            {
                // A dedicated server has no interface (nor Auga's assets).
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
                if (!__instance.GetComponent<KeyHintPanel>()) __instance.gameObject.AddComponent<KeyHintPanel>().hints = __instance;
            }
        }

        // ZInput key names change when bindings are saved.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.Save))]
        private static class BindingsChanged
        {
            private static void Postfix() { foreach (var panel in FindObjectsOfType<KeyHintPanel>()) { panel.mouseButtons.Clear(); panel.signature = null; } }
        }

        private void Start()
        {
            // The radial menu keeps its own hints around the wheel.
            roots = new[] { hints.m_buildHints, hints.m_combatHints, hints.m_inventoryHints, hints.m_inventoryWithContainerHints, hints.m_fishingHints, hints.m_barberHints }
                .Where(r => r).ToArray();
            foreach (var root in roots)
            {
                var group = root.GetComponent<CanvasGroup>();
                if (!group) group = root.AddComponent<CanvasGroup>();
                group.alpha = 0; group.blocksRaycasts = false; group.interactable = false;
            }
            primaryLabel = LabelOf(hints.m_primaryAttackKB);
            secondaryLabel = LabelOf(hints.m_secondaryAttackKB);
            mouseSprites = new[] { "Mouse1", "Mouse2", "Mouse3", "MouseX" }.Select(MouseSprite).ToArray();
            wheelSprite = MouseSprite("MouseWheel");
            BuildBox();
        }

        private void OnDestroy() { if (box) Destroy(box.gameObject); }

        private static Sprite MouseSprite(string name)
        {
            var hud = Auga.Auga.Assets.Hud;
            if (!hud) return null;
            // Several HUD images bear the name, not all with Auga's icon: the sprite itself is looked for first.
            var images = hud.GetComponentsInChildren<Image>(true);
            return images.FirstOrDefault(i => i.sprite && i.sprite.name == name)?.sprite ?? images.FirstOrDefault(i => i.name == name && i.sprite)?.sprite;
        }

        private static string LabelOf(GameObject row)
        {
            var text = row ? row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.text)) : null;
            return text ? Clean(text.text) : null;
        }

        private static string Clean(string text) => Tags.Replace(text, "").Trim();

        // The box is the OverhaulKeyHints prefab (augaassets, OverhaulLab KeyHintPanelAuthoring).
        private void BuildBox()
        {
            var prefab = Auga.Auga.Assets.KeyHints;
            if (!prefab) { Utility.Log.LogError("Key hints: OverhaulKeyHints missing from augaassets"); return; }
            var go = Instantiate(prefab, hints.transform.parent, false);
            go.name = prefab.name;
            box = (RectTransform)go.transform;
            grid = go.GetComponent<GridLayoutGroup>();
            templates = box.Find("Templates");
            angles = box.Find("Angles");
            if (angles) angleTexts = new[] { "X", "Y", "Z" }.Select(n => angles.Find(n)?.GetComponent<Text>()).ToArray();
            go.SetActive(false);
        }

        private Transform angles;
        private Text[] angleTexts;
        private Vector3 shownAngles = new Vector3(float.NaN, 0, 0);

        // The building ghost's orientation over the box, while the gizmo shows it: X and Z between -180 and 180 (tilt and
        // roll), Y between 0 and 360 (facing).
        private void UpdateAngles()
        {
            if (!angles || angleTexts == null) return;
            bool shown = Building.BuildRotation.Shown(out var rotation);
            if (angles.gameObject.activeSelf != shown) angles.gameObject.SetActive(shown);
            if (!shown) return;
            var euler = rotation.eulerAngles;
            var values = new Vector3(Mathf.DeltaAngle(0, euler.x), Mathf.Repeat(euler.y, 360), Mathf.DeltaAngle(0, euler.z));
            for (int i = 0; i < 3; i++) values[i] = Mathf.Round(values[i] * 1000) / 1000;
            if (values == shownAngles) return;
            shownAngles = values;
            for (int i = 0; i < 3; i++)
                if (angleTexts[i]) angleTexts[i].text = "XYZ"[i] + " " + values[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "°";
        }

        private void LateUpdate()
        {
            if (!box) return;
            UpdateAngles();
            var rows = new List<Row>();
            foreach (var root in roots)
            {
                if (!root.activeInHierarchy) continue;
                var containers = root.transform.Cast<Transform>().Where(c => c.name == "Keyboard" || c.name == "Gamepad").ToList();
                if (containers.Count == 0) containers.Add(root.transform);
                foreach (var container in containers.Where(c => c.gameObject.activeInHierarchy))
                    foreach (Transform child in container)
                    {
                        if (!child.gameObject.activeInHierarchy) continue;
                        var row = new Row(); Walk(child, row);
                        if (row.Label.Length == 0 && row.Keys.Count == 0) continue;
                        if (root == hints.m_combatHints && !AttackAvailable(row.Label)) continue;
                        rows.Add(row);
                    }
            }
            BuildingRows(rows);
            var key = Signature(rows);
            if (key == signature) return;
            signature = key;
            Render(rows);
        }

        // Building on the keyboard: snapping is switched with its key, the piece copy is on the middle mouse button
        // (Building.CopyPieceShortcut) and the ghost
        // turns about its three axes, finer with the precise key (Building.BuildRotation).
        private void BuildingRows(List<Row> rows)
        {
            if (!hints.m_buildHints || !hints.m_buildHints.activeInHierarchy || ZInput.IsGamepadActive() || Localization.instance == null) return;
            string copy = Clean(Localization.instance.Localize("$hud_copypiece")), rotate = Clean(Localization.instance.Localize("$hud_rotate")),
                snapping = Clean(Localization.instance.Localize("$hud_altplace")), cycle = Clean(Localization.instance.Localize("$hud_cyclesnap"));
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Label == cycle) rows[i].Label = Clean(Localization.instance.Localize("$overhaul_build_cyclesnap"));
                // Snapping is switched with a key press (Building.SnapToggle), no longer held with Shift.
                if (rows[i].Label == snapping)
                {
                    rows[i].Label = Clean(Localization.instance.Localize("$overhaul_build_snap_toggle"));
                    rows[i].Keys.Clear();
                    rows[i].Keys.Add(new Token { Text = KeyName(Utility.OverhaulConfig.BuildSnapToggleKey.Value), Key = true });
                }
                if (rows[i].Label == copy)
                {
                    rows[i].Keys.Clear();
                    rows[i].Keys.Add(mouseSprites[2] ? new Token { Sprite = mouseSprites[2] } : new Token { Text = ZInput.KeyCodeToDisplayName(KeyCode.Mouse2), Key = true });
                }
            }
            // The game's turn row becomes three, one per axis down the left column, each with its precise turn beside it.
            int at = rows.FindIndex(r => r.Label == rotate);
            if (at < 0) return;
            // Auga's wheel icon, the game's one if Auga has none.
            var wheel = wheelSprite ? new Token { Sprite = wheelSprite } : rows[at].Keys.LastOrDefault(t => t.Sprite);
            rows.RemoveAt(at);
            if (rows.Count % 2 == 1) rows.Add(new Row());
            var precise = Utility.OverhaulConfig.BuildPreciseKey.Value;
            foreach (var axis in new[] { ("y", KeyCode.None), ("x", Utility.OverhaulConfig.BuildPitchKey.Value), ("z", Utility.OverhaulConfig.BuildRollKey.Value) })
            {
                rows.Add(Turn("$overhaul_build_turn_" + axis.Item1, wheel, axis.Item2));
                rows.Add(Turn("$overhaul_build_fine_" + axis.Item1, wheel, precise, axis.Item2));
            }
            rows.Add(Turn("$overhaul_build_reset", null, Utility.OverhaulConfig.BuildResetKey.Value));
        }

        private static Row Turn(string label, Token wheel, params KeyCode[] keys)
        {
            var row = new Row { Label = Clean(Localization.instance.Localize(label)) };
            foreach (var key in keys.Where(k => k != KeyCode.None))
            {
                row.Keys.Add(new Token { Text = KeyName(key), Key = true });
                row.Keys.Add(new Token { Text = "+" });
            }
            if (wheel != null) row.Keys.Add(wheel);
            else if (row.Keys.Count > 0) row.Keys.RemoveAt(row.Keys.Count - 1);
            return row;
        }

        // Modifiers by their short names (left and right alike), other keys as the game names them.
        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return Localization.instance.Localize("$overhaul_key_alt");
                case KeyCode.LeftControl: case KeyCode.RightControl: return Localization.instance.Localize("$overhaul_key_ctrl");
                case KeyCode.LeftShift: case KeyCode.RightShift: return Localization.instance.Localize("$overhaul_key_shift");
                case KeyCode.Less: return "<";
                default:
                    // A letter or a digit is its own name.
                    if (key >= KeyCode.A && key <= KeyCode.Z) return key.ToString();
                    if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return ((int)(key - KeyCode.Alpha0)).ToString();
                    return Clean(ZInput.KeyCodeToDisplayName(key));
            }
        }

        // Some rows show an attack the weapon does not have: check the weapon itself.
        private bool AttackAvailable(string label)
        {
            var weapon = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentWeapon() : null;
            if (weapon == null) return true;
            if (label == secondaryLabel) return weapon.HaveSecondaryAttack();
            if (label == primaryLabel) return weapon.HavePrimaryAttack();
            return true;
        }

        private void Walk(Transform node, Row row)
        {
            foreach (Transform child in node)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                var image = child.GetComponent<Image>();
                if (image && image.enabled && image.sprite)
                {
                    var inner = child.GetComponentsInChildren<TMP_Text>(false).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.text));
                    if (inner) row.Keys.Add(KeyToken(Clean(inner.text)));
                    else
                    {
                        // Icons such as the mouse wheel or a gamepad glyph over its backdrop.
                        var glyph = child.GetComponentsInChildren<Image>(false).LastOrDefault(i => i.enabled && i.sprite);
                        if (glyph) row.Keys.Add(new Token { Sprite = AugaMouse(glyph.sprite) });
                    }
                    continue;
                }
                var text = child.GetComponent<TMP_Text>();
                if (text && !string.IsNullOrWhiteSpace(text.text)) AddText(row, text.text);
                Walk(child, row);
            }
        }

        // A mouse icon drawn by the game (the favourite's middle click...) becomes Auga's; any other glyph stays as it is.
        private Sprite AugaMouse(Sprite sprite)
        {
            string name = sprite.name.ToLowerInvariant();
            if (!name.Contains("mouse")) return sprite;
            if (name.Contains("wheel") || name.Contains("scroll")) return wheelSprite ? wheelSprite : sprite;
            int index = name.Contains("left") ? 0 : name.Contains("right") ? 1 : name.Contains("middle") ? 2 : -1;
            if (index < 0) Utility.Log.LogInfo("Key hints: unknown mouse icon " + sprite.name);
            return index >= 0 && mouseSprites[index] ? mouseSprites[index] : sprite;
        }

        // Native labels may carry their keys inline: "$hud_buildmenu <mspace=0.6em> E</mspace>".
        private void AddText(Row row, string raw)
        {
            int inline = raw.IndexOf("<mspace", System.StringComparison.Ordinal);
            string label = Clean(inline >= 0 ? raw.Substring(0, inline) : raw);
            if (row.Label.Length == 0 && label.Length > 0) row.Label = label;
            else if (label.Length > 0) row.Keys.Add(label == "+" ? new Token { Text = "+" } : KeyToken(label));
            if (inline < 0) return;
            foreach (var part in Regex.Split(Clean(raw.Substring(inline)), @"\s*([+/])\s*"))
                if (part.Length > 0) row.Keys.Add(part == "+" || part == "/" ? new Token { Text = part } : KeyToken(part));
        }

        private Token KeyToken(string text)
        {
            if (mouseButtons.Count == 0) IndexMouseButtons();
            if (mouseButtons.TryGetValue(text, out int index) && mouseSprites[System.Math.Min(index, 3)])
                return new Token { Sprite = mouseSprites[System.Math.Min(index, 3)], Text = index >= 3 ? (index + 1).ToString() : null };
            return new Token { Text = text, Key = true };
        }

        // Displayed names of every key bound to a mouse button: "Souris 1", "Forward"...
        private void IndexMouseButtons()
        {
            mouseButtons[""] = -1;
            if (ZInput.instance == null || Localization.instance == null) return;
            foreach (var entry in ZInput.instance.m_buttons)
            {
                var action = entry.Value?.ButtonAction;
                if (action == null) continue;
                foreach (var binding in action.bindings)
                {
                    var path = binding.effectivePath?.ToLowerInvariant() ?? "";
                    if (!path.Contains("mouse")) continue;
                    int index = path.EndsWith("/leftbutton") ? 0 : path.EndsWith("/rightbutton") ? 1 : path.EndsWith("/middlebutton") ? 2 :
                        path.EndsWith("/forwardbutton") ? 3 : path.EndsWith("/backbutton") ? 4 : -1;
                    var name = Clean(Localization.instance.GetBoundKeyString(entry.Key) ?? "");
                    if (index >= 0 && name.Length > 0) mouseButtons[name] = index;
                }
            }
        }

        private static string Signature(List<Row> rows)
        {
            var text = new StringBuilder();
            foreach (var row in rows)
            {
                text.Append(row.Label).Append('|');
                foreach (var token in row.Keys) text.Append(token.Text).Append(token.Sprite ? token.Sprite.GetInstanceID() : 0).Append(',');
                text.Append('\n');
            }
            return text.ToString();
        }

        // Each row is copied from the prefab's templates (Auga's key, mouse icon and label).
        private void Render(List<Row> rows)
        {
            foreach (Transform child in box) if (child != templates) Destroy(child.gameObject);
            box.gameObject.SetActive(rows.Count > 0);
            grid.constraintCount = Mathf.Clamp(rows.Count, 1, 2);
            foreach (var row in rows)
            {
                var line = Instantiate(Template("Hint"), box).transform;
                foreach (var token in row.Keys) AddToken(line, token);
                Instantiate(Template("Label"), line).GetComponent<Text>().text = row.Label;
            }
        }

        private GameObject Template(string name) => templates.Find(name).gameObject;

        private void AddToken(Transform parent, Token token)
        {
            if (token.Sprite)
            {
                var icon = Instantiate(Template("Mouse"), parent);
                icon.GetComponent<Image>().sprite = token.Sprite;
                if (token.Text != null)
                {
                    var number = icon.transform.Find("Number");
                    number.gameObject.SetActive(true); number.GetComponent<Text>().text = token.Text;
                }
                return;
            }
            var copy = Instantiate(Template(token.Key ? "Key" : "Plus"), parent);
            var text = copy.GetComponentInChildren<Text>(true);
            text.text = token.Text;
            // A key keeps its whole name when the row is tight: the label gives way.
            var size = copy.GetComponent<LayoutElement>();
            if (size && token.Key) size.minWidth = Mathf.Max(size.minWidth, text.preferredWidth + 12);
        }
    }
}
