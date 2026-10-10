using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // Overhaul item slots (design: SlotDesign): own slot background, a border on every item (base colour,
    // or the rarity colour), a rarity glow under the icon for items with a rarity, a 64 px icon and a larger
    // quality badge. Sizes are in cell units: the 64-unit Auga cell is 85 px at 1440p with 100 % UI scale.
    public static class ItemSlotStyle
    {
        private const float PixelsPerUnit = 85f / 64f;
        private const float IconSize = 64f / PixelsPerUnit;              // 64 px
        private const float QualitySize = 33f / PixelsPerUnit;           // 33 px
        private const float QualityAbove = 14f / PixelsPerUnit;          // badge top 14 px above the cell
        private const float QualityFont = 18f / PixelsPerUnit;           // 18 px, bold
        private const float QualityRaise = 1.5f / PixelsPerUnit;         // number 1.5 px higher than the badge centre
        private static readonly Color BaseBorder = new Color32(0x70, 0x63, 0x56, 0xFF);
        private const string GlowName = "OverhaulItemGlow", BorderName = "OverhaulItemBorder";

        private static Sprite background, hudBackground, glow, border, quality, qualityBorder;

        private static Sprite Load(string name)
        {
            using (var stream = typeof(ItemSlotStyle).Assembly.GetManifestResourceStream("Overhaul.Assets.Slots." + name + ".png"))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp };
                texture.LoadImage(bytes);
                Bleed(texture);
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f);
            }
        }

        // Transparent pixels take the colour of their nearest visible neighbour (alpha bleeding): the
        // smoothing applied when the UI is scaled then never mixes a hidden colour (white here) into the edges.
        private static void Bleed(Texture2D texture)
        {
            int w = texture.width, h = texture.height;
            Color32[] pixels = texture.GetPixels32();
            var known = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) known[i] = pixels[i].a > 0;
            for (int pass = 0; pass < 8; pass++)
            {
                var next = (bool[])known.Clone();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (known[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h || !known[ny * w + nx]) continue;
                                Color32 c = pixels[ny * w + nx]; r += c.r; g += c.g; b += c.b; n++;
                            }
                        if (n == 0) continue;
                        pixels[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
                        next[i] = true; changed = true;
                    }
                known = next;
                if (!changed) break;
            }
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        private static bool Ready()
        {
            if (!background) { background = Load("slot_bg"); hudBackground = Load("slot_HUD_bg"); glow = Load("slot_item_bg"); border = Load("slot_item_border"); quality = Load("slot_quality_bg"); qualityBorder = Load("slot_quality_border"); }
            return background;
        }

        // Applies the style to one cell. rarity: the rarity colour, or null for an item without rarity.
        // hud: a hotbar cell, whose background is its "bkg" child and uses the HUD background design.
        // The slot border image, also used for the selection frames (ammo selection, gamepad selection).
        private const float HoverLighten = .4f;
        public static Sprite BorderSprite => Ready() ? border : null;

        // position: where the cell sits in its window, 0 = left edge, 1 = right edge (-1: unknown, design colour).
        public static void Apply(GameObject cell, Image icon, bool hasItem, Color? rarity, bool hud = false, bool upgradable = false, bool hovered = false, float position = -1, bool equipped = false, Color[] gradient = null)
        {
            if (!cell || !icon || !Ready()) return;
            Sprite design = hud && hudBackground ? hudBackground : background;
            Image slot = hud ? cell.transform.Find("bkg")?.GetComponent<Image>() : cell.GetComponent<Image>();
            if (slot && slot.sprite != design)
            {
                slot.sprite = design; slot.type = Image.Type.Simple; slot.color = Color.white;
                // The cell's button tints the background; its normal state shows the design colour as is,
                // hover and press keep their relative change.
                Button button = cell.GetComponent<Button>();
                if (button && button.transition == Selectable.Transition.ColorTint && button.colors.normalColor != Color.white)
                {
                    ColorBlock colors = button.colors;
                    Color normal = colors.normalColor;
                    Color Relative(Color c) => new Color(normal.r > 0 ? Mathf.Min(1, c.r / normal.r) : 1, normal.g > 0 ? Mathf.Min(1, c.g / normal.g) : 1, normal.b > 0 ? Mathf.Min(1, c.b / normal.b) : 1, c.a);
                    colors.highlightedColor = Relative(colors.highlightedColor); colors.pressedColor = Relative(colors.pressedColor);
                    colors.selectedColor = Relative(colors.selectedColor); colors.disabledColor = Relative(colors.disabledColor);
                    colors.normalColor = Color.white;
                    button.colors = colors;
                }
                slot.canvasRenderer.SetColor(Color.white);
            }
            if (slot && !hud) slot.color = Shade(position);

            var iconRect = (RectTransform)icon.transform;
            if (iconRect.sizeDelta != new Vector2(IconSize, IconSize))
            {
                iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(.5f, .5f);
                iconRect.anchoredPosition = Vector2.zero; iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            }

            Image glowImage = Child(cell, GlowName, glow, icon.transform.GetSiblingIndex());
            Image borderImage = Child(cell, BorderName, border, icon.transform.GetSiblingIndex() + 1);
            if (glowImage)
            {
                glowImage.gameObject.SetActive(hasItem && rarity != null);
                if (rarity != null) Tint(glowImage, rarity.Value, gradient, 0);
            }
            // The border marks items with a rarity; any hovered cell, empty or not, shows it too (base colour).
            // In the hotbar, the equipped weapon or tool has a pure white border instead of the blue background.
            if (borderImage)
            {
                borderImage.gameObject.SetActive((hasItem && (rarity != null || equipped)) || hovered);
                Color tint = hasItem && rarity != null ? rarity.Value : BaseBorder;
                // A rarity border is always shown: hovering lightens it instead.
                if (hasItem && equipped) Tint(borderImage, Color.white, null, 0);
                else Tint(borderImage, tint, hasItem && rarity != null ? gradient : null, hovered && hasItem && rarity != null ? HoverLighten : 0);
            }
            Quality(cell, rarity ?? BaseBorder, hasItem && upgradable, hasItem ? gradient : null);
            Selection(cell);
        }

        // Auga windows darken from right to left (#241E17 to #4B3E33). A fixed slot colour vanishes into
        // the middle of them, so each slot is kept at 72 % of the window colour behind it.
        private static readonly Color WindowLeft = new Color32(0x24, 0x1E, 0x17, 255), WindowRight = new Color32(0x4B, 0x3E, 0x33, 255);
        private static readonly Color Design = new Color32(0x39, 0x30, 0x26, 255);
        private const float SlotDarkness = 0.72f;
        public static Color Shade(float position)
        {
            if (position < 0) return Color.white;
            Color want = Color.Lerp(WindowLeft, WindowRight, Mathf.Clamp01(position)) * SlotDarkness;
            return new Color(Mathf.Min(1, want.r / Design.r), Mathf.Min(1, want.g / Design.g), Mathf.Min(1, want.b / Design.b), 1);
        }

        // The window a cell belongs to: the nearest ancestor holding a "Bkg" or "Background" image.
        private static readonly Vector3[] corners = new Vector3[4];
        public static float WindowPosition(Transform cell)
        {
            for (Transform parent = cell.parent; parent; parent = parent.parent)
            {
                Transform background = parent.Find("Bkg") ?? parent.Find("Background");
                if (!(background is RectTransform rect)) continue;
                rect.GetWorldCorners(corners);
                float width = corners[2].x - corners[0].x;
                return width > 0 ? (cell.position.x - corners[0].x) / width : -1;
            }
            return -1;
        }

        // The selection frame takes the slot border shape instead of Auga's octagon (its colour is kept).
        private static void Selection(GameObject cell)
        {
            Transform selected = cell.transform.Find("selected");
            Image frame = selected ? selected.GetComponent<Image>() : null;
            if (!frame || frame.sprite == border) return;
            frame.sprite = border; frame.type = Image.Type.Simple;
            var rect = (RectTransform)selected;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // A rarity with several colours is drawn as a diagonal gradient (top left to bottom right) on the image;
        // lighten: hover lightening toward white.
        private static void Tint(Image image, Color color, Color[] gradient, float lighten)
        {
            var effect = image.GetComponent<SlotGradient>();
            if (gradient != null && gradient.Length > 1)
            {
                if (!effect) effect = image.gameObject.AddComponent<SlotGradient>();
                var colors = gradient.Select(c => Color.Lerp(c, Color.white, lighten)).ToArray();
                if (!effect.enabled || effect.Colors == null || !effect.Colors.SequenceEqual(colors)) { effect.Colors = colors; effect.enabled = true; image.SetVerticesDirty(); }
                image.color = Color.white;
                return;
            }
            if (effect && effect.enabled) { effect.enabled = false; image.SetVerticesDirty(); }
            image.color = Color.Lerp(color, Color.white, lighten);
        }

        internal sealed class SlotGradient : BaseMeshEffect
        {
            public Color[] Colors;
            public override void ModifyMesh(VertexHelper mesh)
            {
                if (!IsActive() || Colors == null || Colors.Length < 2 || mesh.currentVertCount == 0) return;
                var vertex = new UIVertex();
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < mesh.currentVertCount; i++)
                {
                    mesh.PopulateUIVertex(ref vertex, i);
                    minX = Mathf.Min(minX, vertex.position.x); maxX = Mathf.Max(maxX, vertex.position.x);
                    minY = Mathf.Min(minY, vertex.position.y); maxY = Mathf.Max(maxY, vertex.position.y);
                }
                for (int i = 0; i < mesh.currentVertCount; i++)
                {
                    mesh.PopulateUIVertex(ref vertex, i);
                    float t = (Mathf.InverseLerp(minX, maxX, vertex.position.x) + Mathf.InverseLerp(maxY, minY, vertex.position.y)) / 2f;
                    vertex.color = (Color32)((Color)vertex.color * Evaluate(t));
                    mesh.SetUIVertex(vertex, i);
                }
            }

            private Color Evaluate(float t)
            {
                float position = Mathf.Clamp01(t) * (Colors.Length - 1);
                int index = Mathf.Min((int)position, Colors.Length - 2);
                return Color.Lerp(Colors[index], Colors[index + 1], position - index);
            }
        }

        private static Image Child(GameObject cell, string name, Sprite sprite, int index)
        {
            if (!sprite) return null;
            Transform existing = cell.transform.Find(name);
            if (existing) return existing.GetComponent<Image>();
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(cell.transform, false);
            go.transform.SetSiblingIndex(Mathf.Min(index, cell.transform.childCount - 1));
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.sprite = sprite; image.raycastTarget = false;
            return image;
        }

        // Quality badge: the design's diamond, 33 px, its top 14 px above the cell, centred; the number follows.
        private static void Quality(GameObject cell, Color borderColor, bool shown, Color[] gradient)
        {
            Transform badge = cell.transform.Find("quality_bkg"), number = cell.transform.Find("quality");
            if (!badge) return;
            var badgeImage = badge.GetComponent<Image>();
            if (badgeImage && quality && badgeImage.sprite != quality) { badgeImage.sprite = quality; badgeImage.color = Color.white; }
            Place((RectTransform)badge, QualitySize);
            // The badge border, coloured like the cell border (base colour, or the rarity colour).
            Image badgeBorder = Child(badge.gameObject, "OverhaulQualityBorder", qualityBorder, badge.childCount);
            // Shown like Auga's own badge: only for an item that can be upgraded (max quality above 1).
            if (badgeBorder) { Tint(badgeBorder, borderColor, gradient, 0); badgeBorder.enabled = shown; }
            if (number)
            {
                Place((RectTransform)number, ((RectTransform)number).sizeDelta.y);
                ((RectTransform)number).anchoredPosition += new Vector2(0, QualityRaise);
                var text = number.GetComponent<TMPro.TMP_Text>();
                if (text) { text.enableAutoSizing = false; text.fontSize = QualityFont; text.fontStyle |= TMPro.FontStyles.Bold; text.alignment = TMPro.TextAlignmentOptions.Center; }
            }
        }

        private static void Place(RectTransform rect, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, QualityAbove - QualitySize / 2f);
            if (rect.name == "quality_bkg") rect.sizeDelta = new Vector2(QualitySize, QualitySize);
        }

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
        private static class Grid
        {
            private static void Postfix(InventoryGrid __instance)
            {
                Inventory inventory = __instance.GetInventory();
                if (inventory == null) return;
                var hovered = __instance.GetHoveredElement();
                foreach (var element in __instance.m_elements)
                {
                    if (!element) continue;
                    // The equipment window shows what is worn: no "equipped" marker in the grids.
                    if (element.m_equiped) element.m_equiped.enabled = false;
                    ItemDrop.ItemData item = element.m_used ? inventory.GetItemAt(element.Position.x, element.Position.y) : null;
                    Apply(element.gameObject, element.m_icon, item != null, Rarity.ItemRarity.ColorOf(item), false, item != null && item.m_shared.m_maxQuality > 1, element == hovered, WindowPosition(element.transform), gradient: Rarity.ItemRarity.GradientOf(item));
                }
            }
        }

        [HarmonyPatch(typeof(HotkeyBar), nameof(HotkeyBar.UpdateIcons))]
        private static class Hotbar
        {
            private static void Postfix(HotkeyBar __instance, Player player)
            {
                if (!player) return;
                for (int i = 0; i < __instance.m_elements.Count; i++)
                {
                    ItemDrop.ItemData item = __instance.m_items.FirstOrDefault(it => it.m_gridPos.x == i);
                    var element = __instance.m_elements[i];
                    if (element.m_equiped) element.m_equiped.SetActive(false);
                    Apply(element.m_go, element.m_icon, item != null, Rarity.ItemRarity.ColorOf(item), true, equipped: item != null && item.m_equipped, gradient: Rarity.ItemRarity.GradientOf(item));
                }
            }
        }
    }
}
