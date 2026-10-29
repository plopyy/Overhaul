using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// The lab's small round step buttons: Auga's diamond button (up / over / down / disabled sprites)
// with a white icon from Assets/Sprites/SplitIcons (min |<, minus, plus, max >|, drawn by
// Tools/AugaWork/split-dialog/make_icons.py). Used by the split dialog and the crafting quantity.
public static class AugaDiamondButton
{
    public const float Size = 58; // the diamond sprite has a wide transparent margin
    public const string Min = "SplitMin", Minus = "SplitMinus", Plus = "SplitPlus", Max = "SplitMax";
    const string Icons = "Assets/Sprites/SplitIcons/";
    const string Buttons = "Assets/Sprites/Buttons/";

    public static Button Create(Transform parent, string name, string icon, Vector2 position, ButtonSfx sound)
    {
        ImportIcons();
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(ButtonSfx));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(Size, Size);
        var image = go.GetComponent<Image>();
        image.sprite = Sprite(Buttons + "DiamondButtonUp.png");
        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.SpriteSwap;
        button.spriteState = new SpriteState
        {
            highlightedSprite = Sprite(Buttons + "DiamondButtonOver.png"),
            selectedSprite = Sprite(Buttons + "DiamondButtonOver.png"),
            pressedSprite = Sprite(Buttons + "DiamondButtonDown.png"),
            disabledSprite = Sprite(Buttons + "DiamondButtonDisabled.png"),
        };
        // Same click sound as the window's own buttons.
        if (sound) EditorUtility.CopySerialized(sound, go.GetComponent<ButtonSfx>());

        var glyph = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var glyphRect = (RectTransform)glyph.transform;
        glyphRect.SetParent(go.transform, false);
        glyphRect.anchorMin = glyphRect.anchorMax = glyphRect.pivot = new Vector2(0.5f, 0.5f);
        glyphRect.sizeDelta = new Vector2(16, 16);
        var glyphImage = glyph.GetComponent<Image>();
        glyphImage.sprite = Sprite(Icons + icon + ".png");
        glyphImage.raycastTarget = false;
        return button;
    }

    static Sprite Sprite(string path) => AssetDatabase.LoadAssetAtPath<Sprite>(path);

    // Single sprites: the project's default (Multiple) would hold no sprite at all.
    static void ImportIcons()
    {
        foreach (var name in new[] { Min, Minus, Plus, Max })
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(Icons + name + ".png");
            if (importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Single) continue;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }
    }
}
