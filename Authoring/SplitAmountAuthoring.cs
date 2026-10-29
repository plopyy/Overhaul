using System.Linq;
using AugaUnity;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Split stack dialog: under the item, the typed amount and the stack size; the slider is flanked by
// minimum |< and minus on its left, plus and maximum >| on its right (Auga's diamond buttons, white
// icons, AugaDiamondButton). The native "17/50" text stays the controller's m_splitAmount but is hidden.
// Icons drawn by Tools/AugaWork/split-dialog/make_icons.py. Idempotent.
public static class SplitAmountAuthoring
{
    const string Path = "Assets/Prefabs/SplitDialog.prefab";
    const float Height = 330;

    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(Path);
        try
        {
            var dialog = (RectTransform)root.transform.Find("Dialog");
            foreach (var name in new[] { "AmountRow", "Shortcuts", "SliderButtons" })
            {
                var old = dialog.Find(name);
                if (old) Object.DestroyImmediate(old.gameObject);
            }
            dialog.sizeDelta = new Vector2(dialog.sizeDelta.x, Height);

            var item = (RectTransform)dialog.Find("InventoryElement");
            item.anchoredPosition = new Vector2(item.anchoredPosition.x, 95);
            var native = item.Find("amount").GetComponent<TMP_Text>();
            native.gameObject.SetActive(false);
            var slider = dialog.Find("Slider").GetComponent<Slider>();
            var sliderRect = (RectTransform)slider.transform;
            sliderRect.anchoredPosition = new Vector2(0, -48);
            sliderRect.sizeDelta = new Vector2(220, sliderRect.sizeDelta.y);
            foreach (var name in new[] { "ButtonCancel", "ButtonOk" })
            {
                var button = (RectTransform)dialog.Find(name);
                button.anchoredPosition = new Vector2(button.anchoredPosition.x, -112);
            }

            var controls = root.GetComponent<SplitAmountControls>();
            if (!controls) controls = root.AddComponent<SplitAmountControls>();
            controls.Slider = slider;
            controls.Ok = dialog.Find("ButtonOk").GetComponent<Button>();

            // Typed amount and stack size.
            var row = Rect(new GameObject("AmountRow", typeof(RectTransform)), dialog, new Vector2(0, 2), new Vector2(240, 36));
            controls.Amount = Field(row);
            var total = Object.Instantiate(native.gameObject, row, false).GetComponent<TMP_Text>();
            total.name = "Total";
            total.gameObject.SetActive(true);
            Rect(total.gameObject, row, new Vector2(78, 0), new Vector2(70, 36));
            total.alignment = TextAlignmentOptions.MidlineLeft;
            total.enableAutoSizing = false;
            total.fontSize = 20;
            total.text = "/ 100";
            controls.Total = total;

            // Buttons around the slider.
            var buttons = Rect(new GameObject("SliderButtons", typeof(RectTransform)), dialog, sliderRect.anchoredPosition, new Vector2(400, AugaDiamondButton.Size));
            var sound = dialog.Find("ButtonCancel").GetComponent<ButtonSfx>();
            controls.Min = AugaDiamondButton.Create(buttons, "Min", AugaDiamondButton.Min, new Vector2(-168, 0), sound);
            controls.Minus = AugaDiamondButton.Create(buttons, "Minus", AugaDiamondButton.Minus, new Vector2(-132, 0), sound);
            controls.Plus = AugaDiamondButton.Create(buttons, "Plus", AugaDiamondButton.Plus, new Vector2(132, 0), sound);
            controls.Max = AugaDiamondButton.Create(buttons, "Max", AugaDiamondButton.Max, new Vector2(168, 0), sound);

            PrefabUtility.SaveAsPrefabAsset(root, Path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("SPLIT AMOUNT AUTHORING DONE");
    }

    static RectTransform Rect(GameObject go, Transform parent, Vector2 position, Vector2 size)
    {
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        return rt;
    }

    // The construction window's Auga search field, as a number field.
    static TMP_InputField Field(Transform parent)
    {
        var construction = PrefabUtility.LoadPrefabContents("Assets/OverhaulBuild/AugaConstruction.prefab");
        try
        {
            var source = construction.GetComponentsInChildren<Transform>(true).First(t => t.name == "AugaSearch").gameObject;
            var go = Object.Instantiate(source, parent, false);
            go.name = "Amount";
            foreach (var submit in go.GetComponentsInChildren<GuiInputFieldSubmit>(true)) Object.DestroyImmediate(submit);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-38, 0);
            rt.sizeDelta = new Vector2(140 / rt.localScale.x, 36 / rt.localScale.y);
            var field = go.GetComponent<TMP_InputField>();
            field.contentType = TMP_InputField.ContentType.IntegerNumber;
            field.characterLimit = 4;
            field.text = "50";
            if (field.textComponent)
            {
                field.textComponent.alignment = TextAlignmentOptions.Center;
                field.textComponent.fontStyle = FontStyles.Normal;
                field.textComponent.fontSize = 22;
            }
            if (field.placeholder) field.placeholder.gameObject.SetActive(false);
            return field;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(construction);
        }
    }
}
