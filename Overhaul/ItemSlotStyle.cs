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

        private static Sprite background, glow, border, quality;

        private static Sprite Load(string name)
        {
            using (var stream = typeof(ItemSlotStyle).Assembly.GetManifestResourceStream("Overhaul.Assets.Slots." + name + ".png"))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp };
                texture.LoadImage(bytes);
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f);
            }
        }

        private static bool Ready()
        {
            if (!background) { background = Load("slot_bg"); glow = Load("slot_item_bg"); border = Load("slot_item_border"); quality = Load("slot_quality_bg"); }
            return background;
        }

        // Applies the style to one cell. rarity: the rarity colour, or null for an item without rarity.
        public static void Apply(GameObject cell, Image icon, bool hasItem, Color? rarity)
        {
            if (!cell || !icon || !Ready()) return;
            Image slot = cell.GetComponent<Image>();
            if (slot && slot.sprite != background)
            {
                slot.sprite = background; slot.type = Image.Type.Simple; slot.color = Color.white;
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
                if (rarity != null) glowImage.color = rarity.Value;
            }
            if (borderImage)
            {
                borderImage.gameObject.SetActive(hasItem);
                borderImage.color = rarity ?? BaseBorder;
            }
            Quality(cell);
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
        private static void Quality(GameObject cell)
        {
            Transform badge = cell.transform.Find("quality_bkg"), number = cell.transform.Find("quality");
            if (!badge) return;
            var badgeImage = badge.GetComponent<Image>();
            if (badgeImage && quality && badgeImage.sprite != quality) { badgeImage.sprite = quality; badgeImage.color = Color.white; }
            Place((RectTransform)badge, QualitySize);
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
                foreach (var element in __instance.m_elements)
                {
                    if (!element) continue;
                    ItemDrop.ItemData item = element.m_used ? inventory.GetItemAt(element.Position.x, element.Position.y) : null;
                    Apply(element.gameObject, element.m_icon, item != null, EpicLootVisuals.RarityOf(item));
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
                    Apply(__instance.m_elements[i].m_go, __instance.m_elements[i].m_icon, item != null, EpicLootVisuals.RarityOf(item));
                }
            }
        }
    }
}
