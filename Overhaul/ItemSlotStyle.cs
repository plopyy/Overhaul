using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // Overhaul item slots (design: SlotDesign, built into the Auga cell prefabs): this sets what depends on the item
    // and the cell's place: the rarity glow and border, the hover border, the quality badge border and the slot
    // shade following its window.
    public static class ItemSlotStyle
    {
        private static readonly Color BaseBorder = new Color32(0x70, 0x63, 0x56, 0xFF);
        private const string GlowName = "OverhaulItemGlow", BorderName = "OverhaulItemBorder";

        // Applies the style to one cell. rarity: the rarity colour, or null for an item without rarity.
        // hud: a hotbar cell (its background is not shaded).
        private const float HoverLighten = .4f;

        // position: where the cell sits in its window, 0 = left edge, 1 = right edge (-1: unknown, design colour).
        public static void Apply(GameObject cell, Image icon, bool hasItem, Color? rarity, bool hud = false, bool upgradable = false, bool hovered = false, float position = -1, bool equipped = false, Color[] gradient = null)
        {
            if (!cell || !icon) return;
            Image slot = hud ? null : cell.GetComponent<Image>();
            if (slot) slot.color = Shade(position);

            Image glowImage = cell.transform.Find(GlowName)?.GetComponent<Image>();
            Image borderImage = cell.transform.Find(BorderName)?.GetComponent<Image>();
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
            Image badgeBorder = cell.transform.Find("quality_bkg/OverhaulQualityBorder")?.GetComponent<Image>();
            // Shown like Auga's own badge: only for an item that can be upgraded (max quality above 1).
            if (badgeBorder) { Tint(badgeBorder, rarity ?? BaseBorder, hasItem ? gradient : null, 0); badgeBorder.enabled = hasItem && upgradable; }
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

        // 45° gradient over an image, top left to bottom right. Reference: an area shared by several images
        // (a divider made of strokes and an ornament) so they form one gradient; by default the image itself.
        internal sealed class SlotGradient : BaseMeshEffect
        {
            public Color[] Colors;
            public RectTransform Reference;
            public override void ModifyMesh(VertexHelper mesh)
            {
                if (!IsActive() || Colors == null || Colors.Length < 2 || mesh.currentVertCount == 0) return;
                var vertex = new UIVertex();
                Rect area;
                Matrix4x4 toArea = Matrix4x4.identity;
                if (Reference)
                {
                    area = Reference.rect;
                    toArea = Reference.worldToLocalMatrix * transform.localToWorldMatrix;
                }
                else
                {
                    float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                    for (int i = 0; i < mesh.currentVertCount; i++)
                    {
                        mesh.PopulateUIVertex(ref vertex, i);
                        minX = Mathf.Min(minX, vertex.position.x); maxX = Mathf.Max(maxX, vertex.position.x);
                        minY = Mathf.Min(minY, vertex.position.y); maxY = Mathf.Max(maxY, vertex.position.y);
                    }
                    area = Rect.MinMaxRect(minX, minY, maxX, maxY);
                }
                for (int i = 0; i < mesh.currentVertCount; i++)
                {
                    mesh.PopulateUIVertex(ref vertex, i);
                    Vector3 p = toArea.MultiplyPoint3x4(vertex.position);
                    vertex.color = (Color32)((Color)vertex.color * Diagonal(Colors, area, p));
                    mesh.SetUIVertex(vertex, i);
                }
            }
        }

        // Colour at a point of a 45° gradient over an area: it runs along the top-left to bottom-right direction
        // at a true 45°, whatever the proportions of the area.
        internal static Color Diagonal(Color[] colors, Rect area, Vector2 point)
        {
            float span = area.width + area.height;
            float t = span <= 0 ? .5f : ((point.x - area.xMin) + (area.yMax - point.y)) / span;
            float position = Mathf.Clamp01(t) * (colors.Length - 1);
            int index = Mathf.Min((int)position, colors.Length - 2);
            return Color.Lerp(colors[index], colors[index + 1], position - index);
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
