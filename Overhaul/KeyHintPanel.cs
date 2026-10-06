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
    // Redraws the native key hints as a compact two-column box in the bottom-right corner.
    // Valheim still decides which hints apply; the box only mirrors its active rows.
    internal sealed class KeyHintPanel : MonoBehaviour
    {
        private sealed class Token { internal string Text; internal Sprite Sprite; internal bool Key; }
        private sealed class Row { internal string Label = ""; internal readonly List<Token> Keys = new List<Token>(); }

        private static readonly Regex Tags = new Regex("<[^>]+>");
        private KeyHints hints;
        private GameObject[] roots;
        private RectTransform box;
        private GridLayoutGroup grid;
        private TMP_FontAsset font;
        private Sprite keySprite;
        private Image.Type keyType;
        private Sprite[] mouseSprites;
        private readonly Dictionary<string, int> mouseButtons = new Dictionary<string, int>();
        private string primaryLabel, secondaryLabel, signature;

        [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.Awake))]
        private static class Install
        {
            private static void Postfix(KeyHints __instance)
            {
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
                var group = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
                group.alpha = 0; group.blocksRaycasts = false; group.interactable = false;
            }
            font = hints.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f);
            var keyBox = hints.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.sprite && i.GetComponentInChildren<TMP_Text>(true));
            if (keyBox) { keySprite = keyBox.sprite; keyType = keyBox.type; }
            primaryLabel = LabelOf(hints.m_primaryAttackKB);
            secondaryLabel = LabelOf(hints.m_secondaryAttackKB);
            mouseSprites = new[] { "Mouse1", "Mouse2", "Mouse3", "MouseX" }.Select(MouseSprite).ToArray();
            BuildBox();
        }

        private void OnDestroy() { if (box) Destroy(box.gameObject); }

        private static Sprite MouseSprite(string name)
        {
            var hud = Auga.Auga.Assets.Hud;
            return hud ? hud.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == name && i.sprite)?.sprite : null;
        }

        private static string LabelOf(GameObject row)
        {
            var text = row ? row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.text)) : null;
            return text ? Clean(text.text) : null;
        }

        private static string Clean(string text) => Tags.Replace(text, "").Trim();

        private void BuildBox()
        {
            var go = new GameObject("OverhaulKeyHints", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(GridLayoutGroup), typeof(ContentSizeFitter));
            box = (RectTransform)go.transform;
            box.SetParent(hints.transform.parent, false);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(1, 0);
            box.anchoredPosition = new Vector2(-24, 24);
            go.GetComponent<Image>().color = new Color(0.06f, 0.05f, 0.04f, 0.72f);
            var outline = go.GetComponent<Outline>(); outline.effectColor = new Color(0.78f, 0.62f, 0.36f, 0.55f); outline.effectDistance = new Vector2(1, -1);
            grid = go.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(178, 22); grid.spacing = new Vector2(10, 3);
            grid.padding = new RectOffset(10, 10, 7, 7);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 2;
            var fit = go.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            go.SetActive(false);
        }

        private void LateUpdate()
        {
            if (!box) return;
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
            var key = Signature(rows);
            if (key == signature) return;
            signature = key;
            Render(rows);
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
                        if (glyph) row.Keys.Add(new Token { Sprite = glyph.sprite });
                    }
                    continue;
                }
                var text = child.GetComponent<TMP_Text>();
                if (text && !string.IsNullOrWhiteSpace(text.text)) AddText(row, text.text);
                Walk(child, row);
            }
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

        private void Render(List<Row> rows)
        {
            foreach (Transform child in box) Destroy(child.gameObject);
            box.gameObject.SetActive(rows.Count > 0);
            grid.constraintCount = Mathf.Clamp(rows.Count, 1, 2);
            foreach (var row in rows)
            {
                var line = new GameObject("Hint", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                line.transform.SetParent(box, false);
                var layout = line.GetComponent<HorizontalLayoutGroup>();
                layout.spacing = 3; layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = layout.childForceExpandHeight = false;
                foreach (var token in row.Keys) AddToken(line.transform, token);
                var label = AddText(line.transform, row.Label, 14, new Color(0.93f, 0.88f, 0.78f));
                label.overflowMode = TextOverflowModes.Ellipsis;
                var element = label.GetComponent<LayoutElement>(); element.flexibleWidth = 1; element.minWidth = 0;
                label.margin = new Vector4(4, 0, 0, 0);
            }
        }

        private void AddToken(Transform parent, Token token)
        {
            if (token.Sprite)
            {
                var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                icon.transform.SetParent(parent, false);
                var image = icon.GetComponent<Image>(); image.sprite = token.Sprite; image.preserveAspect = true;
                var size = icon.GetComponent<LayoutElement>(); size.preferredWidth = 16; size.preferredHeight = 20;
                if (token.Text != null)
                {
                    var number = AddText(icon.transform, token.Text, 10, Color.white);
                    Destroy(number.GetComponent<LayoutElement>());
                    var rect = number.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                    number.alignment = TextAlignmentOptions.Center;
                }
                return;
            }
            if (!token.Key) { AddText(parent, token.Text, 12, new Color(0.8f, 0.75f, 0.65f)); return; }
            var key = new GameObject("Key", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            key.transform.SetParent(parent, false);
            var back = key.GetComponent<Image>();
            if (keySprite) { back.sprite = keySprite; back.type = keyType; } else back.color = new Color(0, 0, 0, 0.8f);
            var text = AddText(key.transform, token.Text, 12, Color.white);
            Destroy(text.GetComponent<LayoutElement>());
            text.alignment = TextAlignmentOptions.Center;
            var textRect = text.rectTransform; textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one; textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var keySize = key.GetComponent<LayoutElement>();
            keySize.preferredWidth = Mathf.Max(18, text.GetPreferredValues(token.Text).x + 8); keySize.preferredHeight = 18;
        }

        private TextMeshProUGUI AddText(Transform parent, string value, float size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            if (font) text.font = font;
            text.text = value; text.fontSize = size; text.color = color; text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap; text.alignment = TextAlignmentOptions.MidlineLeft;
            return text;
        }
    }
}
