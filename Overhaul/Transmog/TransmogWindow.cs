using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Overhaul.Transmog
{
    // Transmogrification window (OverhaulTransmogWindow prefab, OverhaulLab TransmogWindowAuthoring), opened by the button
    // under the dye button of the equipment panel. Left: the weapons, tools and shields of the bag (equipped first).
    // Middle: the final item in 3D (the look picked, else its current look), turned by dragging and zoomed with the wheel,
    // and its description. Right: the bag's items of exactly the same type, usable as look, the cost and Remove / Apply.
    // Apply destroys the item used as look and costs 5 mystic dyes; Remove gives the item its own look back for free.
    internal static class TransmogWindow
    {
        static GameObject root, panel, rowTemplate, lookTemplate;
        static Player owner;
        static int closedFrame = -1;
        internal static bool Visible => root && root.activeInHierarchy;
        internal static bool BlocksInput => Visible || closedFrame == Time.frameCount;

        static readonly List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>(), looks = new List<ItemDrop.ItemData>();
        static ItemDrop.ItemData selected, source;

        // ---------------------------------------------------------------- opening
        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (!player || player != owner) { Clear(); owner = player; }
            if (!player || !Visible) return;
            if (player.IsDead() || player.IsTeleporting() || Menu.IsVisible() || Console.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || TextInput.IsVisible() || (Chat.instance && Chat.instance.HasFocus())) { Close(); return; }
            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) Close();
            else Preview.Update();
        }

        internal static void Open()
        {
            if (!Auga.Auga.Assets.TransmogWindow || !Hud.instance || !Player.m_localPlayer) return;
            if (!root)
            {
                root = new GameObject("OverhaulTransmogWindow", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.SetActive(false); root.transform.SetParent(Hud.instance.transform, false);
                var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 1200;
                var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f; scaler.referencePixelsPerUnit = 50;
                panel = Object.Instantiate(Auga.Auga.Assets.TransmogWindow, root.transform, false);
                var rect = (RectTransform)panel.transform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
                foreach (var t in panel.GetComponentsInChildren<TMP_Text>(true)) t.text = Localization.instance.Localize(t.text);
                Wire(panel.transform);
            }
            if (InventoryGui.instance && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            Preview.Distance = 1f;
            Collect();
            Select(items.Contains(selected) ? selected : items.FirstOrDefault());
            root.SetActive(true); ZCursor.LockState = CursorLockMode.None; ZCursor.Show();
        }
        internal static void Close()
        {
            if (root) root.SetActive(false);
            Preview.Hide();
            closedFrame = Time.frameCount;
            if (GameCamera.instance) GameCamera.instance.UpdateMouseCapture();
        }
        internal static void Clear() { if (root) { Close(); Object.Destroy(root); } root = panel = rowTemplate = lookTemplate = null; owner = null; selected = source = null; Preview.Destroy(); }

        static void Collect()
        {
            items.Clear();
            var player = Player.m_localPlayer; if (!player) return;
            items.AddRange(player.GetInventory().GetAllItems().Where(i => Transmog.Kind(i) != null)
                .OrderByDescending(i => i.m_equipped).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name)));
        }
        static void CollectLooks()
        {
            looks.Clear();
            var player = Player.m_localPlayer; if (!player || selected == null) return;
            looks.AddRange(player.GetInventory().GetAllItems().Where(i => Transmog.Compatible(selected, i))
                .OrderBy(i => Localization.instance.Localize(i.m_shared.m_name)));
        }

        // ---------------------------------------------------------------- controls
        static Transform T(string path) => panel.transform.Find(path);
        static void Wire(Transform p)
        {
            p.Find("Close").GetComponent<Button>().onClick.AddListener(Close);
            rowTemplate = p.Find("ItemPanel/List/Content/Row").gameObject; rowTemplate.SetActive(false);
            lookTemplate = p.Find("LookPanel/List/Content/Row").gameObject; lookTemplate.SetActive(false);
            p.Find("LookPanel/Remove").GetComponent<Button>().onClick.AddListener(Remove);
            p.Find("LookPanel/Apply").GetComponent<Button>().onClick.AddListener(Apply);
            p.Find("PreviewPanel/View").gameObject.AddComponent<TransmogDrag>();
        }
        static void Select(ItemDrop.ItemData item)
        {
            selected = item; source = null;
            CollectLooks();
            source = looks.FirstOrDefault();
            Preview.Spin = Quaternion.identity;
            Refresh();
        }
        static void Pick(ItemDrop.ItemData look) { source = look; Preview.Spin = Quaternion.identity; Refresh(); }

        static void Apply()
        {
            var player = Player.m_localPlayer; var inv = player ? player.GetInventory() : null;
            if (inv == null || selected == null || source == null || !inv.ContainsItem(selected) || !inv.ContainsItem(source) || !Transmog.Compatible(selected, source)) return;
            bool free = player.NoCostCheat();
            if (!free && Dyes.DyeItems.Count(inv) < Transmog.Cost) { player.Message(MessageHud.MessageType.Center, "$overhaul_transmog_missing"); return; }
            var look = Transmog.Shown(source);
            if (player.IsItemEquiped(source)) player.UnequipItem(source, false);
            if (!inv.RemoveItem(source)) return;
            if (!free) Dyes.DyeItems.Remove(inv, Transmog.Cost);
            Transmog.Set(selected, look);
            inv.Changed();
            if (player.m_visEquipment) player.SetupVisEquipment(player.m_visEquipment, false);
            player.Message(MessageHud.MessageType.TopLeft, "$overhaul_transmog_applied");
            Collect(); Select(selected);
        }
        static void Remove()
        {
            var player = Player.m_localPlayer; var inv = player ? player.GetInventory() : null;
            if (inv == null || selected == null || !inv.ContainsItem(selected) || !Transmog.TryGet(selected, out _)) return;
            Transmog.Clear(selected);
            inv.Changed();
            if (player.m_visEquipment) player.SetupVisEquipment(player.m_visEquipment, false);
            player.Message(MessageHud.MessageType.TopLeft, "$overhaul_transmog_removed");
            Select(selected);
        }

        // ---------------------------------------------------------------- filling
        static void Fill(Transform content, GameObject template, List<ItemDrop.ItemData> list, ItemDrop.ItemData current, Action<ItemDrop.ItemData> click, bool tag)
        {
            foreach (Transform child in content) if (child.gameObject != template) Object.Destroy(child.gameObject);
            foreach (var item in list)
            {
                var row = Object.Instantiate(template, content, false); row.SetActive(true);
                var icon = row.transform.Find("icon").GetComponent<Image>(); icon.sprite = item.GetIcon(); icon.enabled = icon.sprite;
                row.transform.Find("name").GetComponent<TMP_Text>().text = Localization.instance.Localize(item.m_shared.m_name);
                row.transform.Find("selected").gameObject.SetActive(item == current);
                var label = row.transform.Find("Tag");
                if (label)
                {
                    string text = !tag ? "" : item.m_equipped ? "$overhaul_transmog_equipped" : Transmog.TryGet(item, out _) ? "$overhaul_transmog_changed" : "";
                    label.GetComponent<TMP_Text>().text = Localization.instance.Localize(text);
                }
                var it = item; var button = row.GetComponent<Button>() ?? row.AddComponent<Button>(); button.onClick.RemoveAllListeners(); button.onClick.AddListener(() => click(it));
            }
        }
        static void Refresh()
        {
            if (!panel) return;
            var player = Player.m_localPlayer; var inv = player ? player.GetInventory() : null;
            Fill(T("ItemPanel/List/Content"), rowTemplate, items, selected, Select, true);
            T("ItemPanel/Empty").gameObject.SetActive(items.Count == 0);
            Fill(T("LookPanel/List/Content"), lookTemplate, looks, source, Pick, false);
            T("LookPanel/Empty").gameObject.SetActive(selected != null && looks.Count == 0);

            // Current look of the selected item.
            var lookName = Transmog.LookName(selected);
            T("LookPanel/Current").GetComponent<TMP_Text>().text = selected == null ? "" :
                Localization.instance.Localize("$overhaul_transmog_current ") + (lookName != null ? "<color=#7CC0FF>" + Localization.instance.Localize(lookName) + "</color>" : Localization.instance.Localize("$overhaul_transmog_original"));

            // Final item: the look picked, else what the item shows now; its description is the selected item's own.
            var shown = source != null ? Transmog.Shown(source) : selected != null ? Transmog.Shown(selected) : default;
            T("PreviewPanel/ItemName").GetComponent<TMP_Text>().text = selected != null ? Localization.instance.Localize(selected.m_shared.m_name) : "";
            var lookLine = "";
            if (source != null) lookLine = "<color=#7CC0FF>" + Localization.instance.Localize("$overhaul_transmog_look_line " + (Transmog.PrefabItem(shown.Prefab)?.m_shared.m_name ?? shown.Prefab)) + "</color>\n";
            string desc = "";
            if (selected != null)
            {
                desc = ItemDrop.ItemData.GetTooltip(selected, selected.m_quality, false, Game.m_worldLevel);
                if (source != null && lookName != null) desc = desc.Substring(desc.IndexOf('\n') + 1);   // the look line of the current look is replaced by the new one
            }
            T("PreviewPanel/Description/Viewport/Text").GetComponent<TMP_Text>().text = selected == null ? "" : lookLine + Localization.instance.Localize(desc);

            // Cost and buttons.
            bool enough = (player && player.NoCostCheat()) || Dyes.DyeItems.Count(inv) >= Transmog.Cost;
            var icon = T("LookPanel/Cost/Icon").GetComponent<Image>(); var sprite = Dyes.DyeItems.MysticIcon(); if (sprite) icon.sprite = sprite;
            var amount = T("LookPanel/Cost/Amount").GetComponent<TMP_Text>(); amount.text = "×" + Transmog.Cost;
            amount.color = enough ? Color.white : new Color(1f, .35f, .3f);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)T("LookPanel/Cost"));
            var warning = T("LookPanel/Warning").GetComponent<TMP_Text>();
            warning.text = source != null ? Localization.instance.Localize("$overhaul_transmog_warning", Localization.instance.Localize(source.m_shared.m_name)) : "";
            Enable(T("LookPanel/Apply"), selected != null && source != null && enough);
            Enable(T("LookPanel/Remove"), selected != null && Transmog.TryGet(selected, out _));

            Preview.Show(T("PreviewPanel/View").GetComponent<RawImage>(), selected != null ? shown : default);
        }
        static void Enable(Transform button, bool on)
        {
            button.GetComponent<Button>().interactable = on;
            var group = button.GetComponent<CanvasGroup>() ?? button.gameObject.AddComponent<CanvasGroup>();
            group.alpha = on ? 1f : .45f;
        }

        // ---------------------------------------------------------------- the item in 3D
        internal static class Preview
        {
            static GameObject model, cameraGo;
            static Camera camera;
            static RenderTexture texture;
            static Transmog.Look shownLook;
            static int layer = -1;
            static Bounds bounds;
            internal static Quaternion Spin = Quaternion.identity;
            internal static float Distance = 1f;   // Distance: 1 = whole item in view, smaller = closer
            static readonly Vector3 Place = new Vector3(0, 6100, 0);
            // Free rotation: sideways drag turns about the screen's vertical, up/down drag about its horizontal
            // (flat items like shields and swords can be seen from any side).
            internal static void Turn(Vector2 delta)
            {
                var right = cameraGo ? cameraGo.transform.right : Vector3.left;
                var up = cameraGo ? cameraGo.transform.up : Vector3.up;
                Spin = Quaternion.AngleAxis(-delta.x * .5f, up) * Quaternion.AngleAxis(delta.y * .5f, right) * Spin;
            }
            internal static void Zoom(float steps) => Distance = Mathf.Clamp(Distance * Mathf.Pow(.7f, steps), .12f, 1f);

            static int Layer()
            {
                if (layer >= 0) return layer;
                for (int i = 31; i > 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return layer = i;
                return layer = 31;
            }

            internal static void Show(RawImage view, Transmog.Look look)
            {
                if (!cameraGo) Build();
                var size = ((RectTransform)view.transform).rect.size;
                int w = Mathf.Max(64, Mathf.RoundToInt(size.x * 1.5f)), h = Mathf.Max(64, Mathf.RoundToInt(size.y * 1.5f));
                if (!texture || texture.width != w || texture.height != h)
                {
                    if (texture) texture.Release();
                    texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "TransmogPreview" };
                    camera.targetTexture = texture;
                }
                view.texture = texture; view.color = Color.white;
                cameraGo.SetActive(true);
                if (!model || look != shownLook) Dress(look);
                Update();
            }
            internal static void Hide() { if (model) model.SetActive(false); if (cameraGo) cameraGo.SetActive(false); }
            internal static void Destroy()
            {
                if (model) Object.Destroy(model); if (cameraGo) Object.Destroy(cameraGo); if (texture) texture.Release();
                model = cameraGo = null; camera = null; texture = null; shownLook = default;
            }

            static void Build()
            {
                int l = Layer();
                cameraGo = new GameObject("TransmogPreviewCamera");
                camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0, 0, 0, 0);
                camera.cullingMask = 1 << l; camera.fieldOfView = 26; camera.nearClipPlane = .02f; camera.farClipPlane = 30; camera.allowHDR = false;
                var light = new GameObject("TransmogPreviewLight").AddComponent<Light>(); light.transform.SetParent(cameraGo.transform, false);
                light.type = LightType.Directional; light.intensity = 1.2f; light.cullingMask = 1 << l; light.transform.localRotation = Quaternion.Euler(25, -20, 0);
                var probe = new GameObject("TransmogPreviewReflection").AddComponent<ReflectionProbe>();
                probe.transform.position = Place; probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
                probe.customBakedTexture = RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture : ReflectionProbe.defaultTexture;
                probe.size = new Vector3(10, 10, 10); probe.importance = 100; probe.transform.SetParent(cameraGo.transform, true);
            }

            // A copy of the look's model as held in the hand (its "attach" child when it has one), without game components.
            static void Dress(Transmog.Look look)
            {
                if (model) Object.Destroy(model);
                model = null; shownLook = look;
                var prefab = Transmog.Prefab(look.Prefab); if (!prefab) return;
                var source = prefab.transform.Find("attach") ?? prefab.transform;
                var holder = new GameObject("TransmogPreviewHolder"); holder.SetActive(false);
                model = Object.Instantiate(source.gameObject, holder.transform, false); model.name = "TransmogPreview";
                foreach (var c in model.GetComponentsInChildren<Component>(true).Reverse())
                {
                    if (!c || c is Transform || c is Renderer || c is MeshFilter || c is LODGroup || c is IEquipmentVisual) continue;
                    if (c is ParticleSystem || c is Light) continue;
                    Object.DestroyImmediate(c);
                }
                int l = Layer(); foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = l;
                ZNetView.m_forceDisableInit = true;
                try { model.transform.SetParent(null, false); model.SetActive(true); }
                finally { ZNetView.m_forceDisableInit = false; }
                Object.Destroy(holder);
                foreach (var v in model.GetComponentsInChildren<IEquipmentVisual>(true)) v.Setup(look.Variant);
                model.transform.position = Place; model.transform.rotation = Quaternion.identity; model.transform.localScale = Vector3.one;
                // Centre the item on its pivot: bounds of the drawn parts (LOD 0) measured upright.
                bool any = false; bounds = new Bounds(Place, Vector3.zero);
                foreach (var r in model.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
                }
                if (!any) bounds = new Bounds(Place, Vector3.one * .5f);
            }

            internal static void Update()
            {
                if (!cameraGo) return;
                if (model)
                {
                    model.SetActive(true);
                    // Turned about the item's centre.
                    var offset = bounds.center - Place;
                    var rot = Spin;
                    model.transform.SetPositionAndRotation(Place - rot * offset, rot);
                    int l = Layer(); foreach (var t in model.GetComponentsInChildren<Transform>(true)) if (t.gameObject.layer != l) t.gameObject.layer = l;
                }
                float radius = Mathf.Max(.15f, bounds.extents.magnitude);
                float dist = radius / Mathf.Sin(camera.fieldOfView * .5f * Mathf.Deg2Rad) * Distance;
                cameraGo.transform.position = Place + Quaternion.Euler(10, 0, 0) * new Vector3(0, 0, dist);
                cameraGo.transform.LookAt(Place);
            }
        }

        // ---------------------------------------------------------------- input while open
        [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
        static class PlayerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        static class ControllerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        static class CursorInput { static bool Prefix() { if (!Visible) return true; ZCursor.LockState = CursorLockMode.None; ZCursor.Show(); return false; } }
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        static class NoWorldZoom
        {
            static void Prefix(GameCamera __instance, out float __state) { __state = -1; if (Visible) { __state = __instance.m_zoomSens; __instance.m_zoomSens = 0; } }
            static void Finalizer(GameCamera __instance, float __state) { if (__state >= 0) __instance.m_zoomSens = __state; }
        }
        [HarmonyPatch(typeof(Menu), "Update")]
        static class MenuInput { static bool Prefix() => !BlocksInput; }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        static class NoInventory { static bool Prefix() => !Visible; }
    }

    // Dragging on the item turns it in every direction; the wheel zooms.
    internal sealed class TransmogDrag : MonoBehaviour, IDragHandler, IScrollHandler
    {
        public void OnDrag(PointerEventData e) => TransmogWindow.Preview.Turn(e.delta);
        public void OnScroll(PointerEventData e) => TransmogWindow.Preview.Zoom(e.scrollDelta.y);
    }
}
