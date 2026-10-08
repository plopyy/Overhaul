using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OverhaulAnimationPreview
{
    // Debug window on the left of the screen, styled like Overhaul's training dummy meter.
    public sealed class PreviewWindow : MonoBehaviour
    {
        private const float Width = 300, Inner = 268, Left = 16;
        private static readonly Color Gold = new Color(.86f, .76f, .55f);
        private static readonly Color Dim = new Color(.75f, .72f, .66f);

        private Previewer previewer;
        private GameObject panelBase, buttonSmall, buttonMedium;
        private TMP_FontAsset font, titleFont;
        private RectTransform root, list;
        private TMP_Text folder, info, status, speedValue, moveValue, hint;
        private TMP_Text playLabel, loopLabel, modeLabel;
        private Slider timeline, speed, move;
        private bool ignoreSlider;
        private readonly List<(Button button, TMP_Text label, string file)> rows = new List<(Button, TMP_Text, string)>();
        public static bool IsVisible { get; private set; }

        public bool Visible
        {
            get => root && root.gameObject.activeSelf;
            set { if (!root) return; root.gameObject.SetActive(value); IsVisible = value; if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture(); }
        }

        public static PreviewWindow Create(Hud hud, Previewer previewer)
        {
            var host = hud.transform.Find("hudroot"); if (!host) host = hud.transform;
            var window = hud.gameObject.AddComponent<PreviewWindow>();
            window.previewer = previewer;
            Plugin.AddTranslations(Localization.instance);
            window.Build(host);
            previewer.Changed += window.Refresh;
            window.Visible = false;
            return window;
        }

        public void Build(Transform host)
        {
            LoadAugaStyle();
            var canvas = host.GetComponentInParent<Canvas>();
            if (canvas && !canvas.GetComponent<GraphicRaycaster>()) canvas.gameObject.AddComponent<GraphicRaycaster>();
            root = Rect(host, "OverhaulAnimationPreview", new Vector2(22, 0), new Vector2(Width, 500));
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0, .5f);
            Background();

            Label("Title", "$animpreview_title", 10, 28, 20, Gold, TextAlignmentOptions.Center, titleFont);
            folder = Label("Folder", "", 40, 18, 12, Dim, TextAlignmentOptions.Center);
            folder.text = Localization.instance.Localize("$animpreview_folder") + " : " + Shorten(Plugin.Folder);

            var scroll = Rect(root, "Files", new Vector2(Left, -62), new Vector2(Inner, 150)).gameObject.AddComponent<ScrollRect>();
            var shade = scroll.gameObject.AddComponent<Image>(); shade.color = new Color(0, 0, 0, .35f);
            scroll.gameObject.AddComponent<RectMask2D>();
            list = Rect(scroll.transform, "Content", Vector2.zero, new Vector2(Inner, 0));
            var layout = list.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4); layout.spacing = 2;
            layout.childControlHeight = layout.childControlWidth = true; layout.childForceExpandHeight = false;
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = list; scroll.horizontal = false; scroll.scrollSensitivity = 20; scroll.movementType = ScrollRect.MovementType.Clamped;

            info = Label("Info", "", 216, 54, 14, Color.white, TextAlignmentOptions.Center);
            timeline = Slider("Timeline", 274, 0, 1, v => { if (!ignoreSlider && previewer.Clip != null) previewer.Seek(v * previewer.Clip.Duration); });

            playLabel = Button(buttonMedium, "Play", new Vector2(Left, -298), new Vector2(130, 32), "$animpreview_play", previewer.TogglePlay);
            Button(buttonMedium, "Stop", new Vector2(Left + 138, -298), new Vector2(130, 32), "$animpreview_stop", previewer.Stop);
            loopLabel = Button(buttonSmall, "Loop", new Vector2(Left, -340), new Vector2(130, 26), "", () => { previewer.Loop = !previewer.Loop; Refresh(); });
            modeLabel = Button(buttonSmall, "Mode", new Vector2(Left + 138, -340), new Vector2(130, 26), "", () => { previewer.RootMotion = !previewer.RootMotion; Refresh(); });

            Label("SpeedLabel", "$animpreview_speed", 376, 18, 13, Dim, TextAlignmentOptions.Left);
            speedValue = Label("SpeedValue", "", 376, 18, 13, Color.white, TextAlignmentOptions.Right);
            speed = Slider("Speed", 398, .1f, 2f, v => { previewer.Speed = Mathf.Round(v * 20) / 20; Refresh(); });
            Label("MoveLabel", "$animpreview_move_speed", 420, 18, 13, Dim, TextAlignmentOptions.Left);
            moveValue = Label("MoveValue", "", 420, 18, 13, Color.white, TextAlignmentOptions.Right);
            move = Slider("Move", 442, 0f, 3f, v => { previewer.MoveSpeed = Mathf.Round(v * 20) / 20; Refresh(); });

            status = Label("Status", "", 460, 18, 12, new Color(1f, .55f, .45f), TextAlignmentOptions.Center);
            hint = Label("Hint", "", 474, 18, 11, Dim, TextAlignmentOptions.Center);
            ignoreSlider = true; speed.value = previewer.Speed; move.value = previewer.MoveSpeed; ignoreSlider = false;
            Refresh();
        }

        private void Update()
        {
            if (!Visible || previewer.Clip == null) return;
            ignoreSlider = true;
            timeline.value = previewer.Clip.Duration > 0 ? previewer.Time / previewer.Clip.Duration : 0;
            ignoreSlider = false;
            info.text = Info();
        }

        public void Refresh()
        {
            if (!root) return;
            RefreshList();
            var on = Localization.instance.Localize(previewer.Loop ? "$animpreview_on" : "$animpreview_off");
            loopLabel.text = Localization.instance.Localize("$animpreview_loop") + " : " + on;
            modeLabel.text = Localization.instance.Localize(previewer.RootMotion ? "$animpreview_root_motion" : "$animpreview_in_place");
            playLabel.text = Localization.instance.Localize(previewer.Playing ? "$animpreview_pause" : "$animpreview_play");
            speedValue.text = "×" + previewer.Speed.ToString("0.00");
            // Clip with travel: multiplier of that travel. In-place clip: forward speed in m/s.
            bool travel = previewer.Clip != null && previewer.Clip.HasRootMotion;
            ignoreSlider = true;
            move.maxValue = travel ? 3f : 10f;
            move.value = previewer.MoveSpeed;
            ignoreSlider = false;
            moveValue.text = travel ? "×" + previewer.MoveSpeed.ToString("0.00") : previewer.MoveSpeed.ToString("0.0") + " m/s";
            move.interactable = previewer.RootMotion;
            info.text = Info();
            status.text = previewer.Status;
            hint.text = (Plugin.ToggleKey != null ? Plugin.ToggleKey.Value.ToString() : "F7") + " : " + Localization.instance.Localize("$animpreview_toggle");
        }

        private string Info()
        {
            if (previewer.Files.Count == 0) return Localization.instance.Localize("$animpreview_empty");
            if (previewer.Clip == null) return Localization.instance.Localize("$animpreview_no_selection");
            var clip = previewer.Clip;
            var travel = clip.HasRootMotion
                ? Localization.instance.Localize("$animpreview_travel") + " : " + previewer.TravelMeters.ToString("0.00") + " m"
                : Localization.instance.Localize("$animpreview_no_travel");
            return clip.Name + " — " + clip.Frames + " " + Localization.instance.Localize("$animpreview_frames") + "\n" + travel + "\n"
                + Localization.instance.Localize("$animpreview_time") + " " + previewer.Time.ToString("0.00") + " / " + clip.Duration.ToString("0.00") + " s";
        }

        private void RefreshList()
        {
            if (rows.Count != previewer.Files.Count || rows.Exists(r => !previewer.Files.Contains(r.file)))
            {
                foreach (var row in rows) Destroy(row.button.gameObject);
                rows.Clear();
                foreach (var file in previewer.Files)
                {
                    var path = file;
                    var label = Button(buttonSmall, "File", Vector2.zero, new Vector2(Inner - 8, 24), "", () => previewer.Select(path), list);
                    label.text = Path.GetFileNameWithoutExtension(file);
                    label.GetComponentInParent<Button>().gameObject.AddComponent<LayoutElement>().preferredHeight = 24;
                    rows.Add((label.GetComponentInParent<Button>(), label, file));
                }
            }
            foreach (var row in rows) row.label.color = row.file == previewer.Selected ? Gold : Color.white;
        }

        // ---- building blocks ----

        // Reuses Auga's panel, buttons and fonts loaded by Overhaul, when present.
        private void LoadAugaStyle()
        {
            var assets = AccessTools.TypeByName("Auga.Auga")?.GetField("Assets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            GameObject Get(string name) => assets?.GetType().GetField(name)?.GetValue(assets) as GameObject;
            panelBase = Get("PanelBase"); buttonSmall = Get("ButtonSmall"); buttonMedium = Get("ButtonMedium");
            titleFont = buttonMedium ? buttonMedium.GetComponentInChildren<TMP_Text>(true)?.font : null;
            font = buttonSmall ? buttonSmall.GetComponentInChildren<TMP_Text>(true)?.font : null;
            if (!font && Hud.instance && Hud.instance.m_hoverName) font = Hud.instance.m_hoverName.font;
            if (!titleFont) titleFont = font;
        }

        private void Background()
        {
            if (panelBase)
            {
                var frame = Instantiate(panelBase, root, false);
                var r = (RectTransform)frame.transform; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;
                frame.AddComponent<CanvasGroup>().alpha = .62f;
                foreach (var g in frame.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            }
            var block = root.gameObject.AddComponent<Image>(); // catches clicks so they don't reach the game
            block.color = panelBase ? new Color(0, 0, 0, 0.01f) : new Color(.05f, .05f, .05f, .8f);
        }

        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var r = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = position; r.sizeDelta = size;
            return r;
        }

        private TMP_Text Label(string name, string key, float top, float height, float size, Color color, TextAlignmentOptions align, TMP_FontAsset face = null)
        {
            var t = Rect(root, name, new Vector2(Left, -top), new Vector2(Inner, height)).gameObject.AddComponent<TextMeshProUGUI>();
            if (face || font) t.font = face ? face : font;
            t.text = key.Length > 0 ? Localization.instance.Localize(key) : ""; t.fontSize = size; t.color = color; t.alignment = align;
            t.raycastTarget = false; t.textWrappingMode = TextWrappingModes.Normal; t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private TMP_Text Button(GameObject prefab, string name, Vector2 position, Vector2 size, string key, Action click, Transform parent = null)
        {
            GameObject go;
            if (prefab) go = Instantiate(prefab, parent ? parent : root, false);
            else
            {
                go = Rect(parent ? parent : root, name, position, size).gameObject;
                go.AddComponent<Image>().color = new Color(.2f, .18f, .15f, .9f);
                go.AddComponent<Button>();
                var text = Rect(go.transform, "Label", Vector2.zero, size).gameObject.AddComponent<TextMeshProUGUI>();
                if (font) text.font = font; text.fontSize = 14; text.alignment = TextAlignmentOptions.Center;
            }
            go.name = name;
            var r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = position; r.sizeDelta = size;
            foreach (var tip in go.GetComponentsInChildren<UITooltip>(true)) Destroy(tip);
            var button = go.GetComponent<Button>(); if (!button) button = go.AddComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(() => click());
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var label = go.GetComponentInChildren<TMP_Text>(true);
            label.text = key.Length > 0 ? Localization.instance.Localize(key) : "";
            label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        private Slider Slider(string name, float top, float min, float max, Action<float> changed)
        {
            var r = Rect(root, name, new Vector2(Left, -top), new Vector2(Inner, 14));
            var track = r.gameObject.AddComponent<Image>(); track.color = new Color(0, 0, 0, .55f);
            var fillArea = Rect(r, "Fill Area", Vector2.zero, Vector2.zero); Stretch(fillArea, 2);
            var fill = Rect(fillArea, "Fill", Vector2.zero, Vector2.zero); Stretch(fill, 0);
            fill.gameObject.AddComponent<Image>().color = new Color(Gold.r, Gold.g, Gold.b, .85f);
            var handleArea = Rect(r, "Handle Area", Vector2.zero, Vector2.zero); Stretch(handleArea, 0);
            var handle = Rect(handleArea, "Handle", Vector2.zero, new Vector2(10, 0));
            handle.anchorMin = new Vector2(0, 0); handle.anchorMax = new Vector2(0, 1); handle.offsetMin = new Vector2(-5, -3); handle.offsetMax = new Vector2(5, 3);
            var knob = handle.gameObject.AddComponent<Image>(); knob.color = Color.white;
            var slider = r.gameObject.AddComponent<Slider>();
            slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = knob;
            slider.minValue = min; slider.maxValue = max; slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.onValueChanged.AddListener(v => changed(v));
            return slider;
        }

        private static void Stretch(RectTransform r, float inset)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(.5f, .5f);
            r.offsetMin = new Vector2(inset, inset); r.offsetMax = new Vector2(-inset, -inset);
        }

        private static string Shorten(string path)
        {
            var bepinex = BepInEx.Paths.BepInExRootPath;
            return !string.IsNullOrEmpty(bepinex) && path.StartsWith(bepinex, StringComparison.OrdinalIgnoreCase)
                ? "BepInEx" + path.Substring(bepinex.Length).Replace('\\', '/') : path;
        }

        // While the window is shown: free cursor, and no attacks or camera turns from clicks on it.
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        private static class Cursor
        {
            private static bool Prefix() { if (!IsVisible) return true; ZCursor.LockState = CursorLockMode.None; ZCursor.Show(); return false; }
        }
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        private static class Input
        {
            private static bool Prefix(ref bool __result) { if (!IsVisible) return true; __result = false; return false; }
        }
    }
}
