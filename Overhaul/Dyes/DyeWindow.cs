using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Overhaul.Dyes
{
    // Dyeing window (OverhaulDyeWindow prefab, OverhaulLab DyeWindowAuthoring), opened by the dye button of the equipment
    // panel. Left: the dyeable gear in the bag (worn and cosmetic included). Middle: a copy of the player's look wearing
    // the selected piece with the colours being chosen, rendered with a clear background and turned by dragging. Right:
    // the piece's zones, the 50 colours (number of dyes owned on each), the cost and Restore / Apply. Apply uses one dye
    // per zone changed to a colour; restoring a zone to its original look is free.
    internal static class DyeWindow
    {
        static GameObject root, panel, rowTemplate;
        static Player owner;
        static int closedFrame = -1;
        internal static bool Visible => root && root.activeInHierarchy;
        internal static bool BlocksInput => Visible || closedFrame == Time.frameCount;

        static readonly List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
        static ItemDrop.ItemData selected;
        static string[] pending;
        static int zone;

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
            if (!Auga.Auga.Assets.DyeWindow || !Hud.instance || !Player.m_localPlayer) return;
            if (!root)
            {
                root = new GameObject("OverhaulDyeWindow", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                root.SetActive(false); root.transform.SetParent(Hud.instance.transform, false);
                var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 1200;
                var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f; scaler.referencePixelsPerUnit = 50;
                panel = Object.Instantiate(Auga.Auga.Assets.DyeWindow, root.transform, false);
                var rect = (RectTransform)panel.transform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
                foreach (var t in panel.GetComponentsInChildren<TMP_Text>(true)) t.text = Localization.instance.Localize(t.text);
                Wire(panel.transform);
            }
            if (InventoryGui.instance && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
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
        internal static void Clear() { if (root) { Close(); Object.Destroy(root); } root = panel = rowTemplate = null; owner = null; selected = null; Preview.Destroy(); }

        static void Collect()
        {
            items.Clear();
            var player = Player.m_localPlayer; if (!player) return;
            // Shown pieces first (worn or cosmetic), then the rest of the bag, by name.
            items.AddRange(player.GetInventory().GetAllItems().Where(i => DyeGear.Of(i) != null)
                .OrderByDescending(i => i.m_equipped || EquipmentAndQuickSlots.CosmeticEquipment.IsCosmetic(player, i)).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name)));
        }

        // ---------------------------------------------------------------- controls
        static Transform T(string path) => panel.transform.Find(path);
        static void Wire(Transform p)
        {
            p.Find("Close").GetComponent<Button>().onClick.AddListener(Close);
            rowTemplate = p.Find("ItemPanel/List/Content/Row").gameObject; rowTemplate.SetActive(false);
            for (int i = 0; i < 4; i++) { int z = i; p.Find("ColourPanel/Zones/Zone" + i).GetComponent<Button>().onClick.AddListener(() => { zone = z; Refresh(); }); }
            var palette = p.Find("ColourPanel/Palette");
            for (int i = 0; i < DyePalette.All.Count; i++)
            {
                var c = DyePalette.All[i];
                palette.Find("Swatch" + i).GetComponent<Button>().onClick.AddListener(() => Pick(c.ID));
            }
            p.Find("ColourPanel/Reset").GetComponent<Button>().onClick.AddListener(() => Pick(null));
            p.Find("ColourPanel/Apply").GetComponent<Button>().onClick.AddListener(Apply);
            p.Find("PreviewPanel/View").gameObject.AddComponent<DyeDrag>();
        }

        static void Select(ItemDrop.ItemData item)
        {
            selected = item; zone = 0;
            var gear = DyeGear.Of(item);
            pending = gear != null ? DyeVisuals.Read(item, gear.Zones.Length) : null;
            Refresh();
        }
        static void Pick(string id)
        {
            if (pending == null || zone >= pending.Length) return;
            pending[zone] = id; Refresh();
        }

        // Dyes needed by the colours chosen: one per zone set to a new colour.
        static Dictionary<DyePalette.Colour, int> Needed()
        {
            var need = new Dictionary<DyePalette.Colour, int>();
            var gear = DyeGear.Of(selected); if (gear == null) return need;
            var current = DyeVisuals.Read(selected, gear.Zones.Length);
            for (int z = 0; z < pending.Length; z++)
            {
                if (pending[z] == null || pending[z] == current[z]) continue;
                var c = DyePalette.Get(pending[z]); need[c] = (need.TryGetValue(c, out int n) ? n : 0) + 1;
            }
            return need;
        }
        static bool Changed()
        {
            var gear = DyeGear.Of(selected); if (gear == null) return false;
            var current = DyeVisuals.Read(selected, gear.Zones.Length);
            return pending.Where((id, z) => id != current[z]).Any();
        }

        static void Apply()
        {
            var player = Player.m_localPlayer; var gear = DyeGear.Of(selected);
            if (!player || gear == null || !player.GetInventory().ContainsItem(selected) || !Changed()) return;
            var inv = player.GetInventory(); var need = Needed();
            if (need.Any(n => DyeItems.Count(inv, n.Key) < n.Value)) { player.Message(MessageHud.MessageType.Center, "$overhaul_dye_missing"); return; }
            foreach (var n in need) inv.RemoveItem("$overhaul_dyeitem_" + n.Key.ID, n.Value);
            DyeVisuals.Write(selected, pending.ToArray());
            inv.Changed();
            if (player.m_visEquipment) player.SetupVisEquipment(player.m_visEquipment, false);
            player.Message(MessageHud.MessageType.TopLeft, "$overhaul_dye_applied");
            Refresh();
        }

        // ---------------------------------------------------------------- filling
        static void Refresh()
        {
            if (!panel) return;
            var player = Player.m_localPlayer; var inv = player ? player.GetInventory() : null;
            // Gear list.
            var content = T("ItemPanel/List/Content");
            foreach (Transform child in content) if (child.gameObject != rowTemplate) Object.Destroy(child.gameObject);
            foreach (var item in items)
            {
                var row = Object.Instantiate(rowTemplate, content, false); row.SetActive(true);
                var icon = row.transform.Find("icon").GetComponent<Image>(); icon.sprite = item.GetIcon(); icon.enabled = icon.sprite;
                row.transform.Find("name").GetComponent<TMP_Text>().text = Localization.instance.Localize(item.m_shared.m_name);
                row.transform.Find("selected").gameObject.SetActive(item == selected);
                var it = item; var button = row.GetComponent<Button>() ?? row.AddComponent<Button>(); button.onClick.RemoveAllListeners(); button.onClick.AddListener(() => Select(it));
            }
            T("ItemPanel/Empty").gameObject.SetActive(items.Count == 0);

            var gear = DyeGear.Of(selected);
            T("PreviewPanel/ItemName").GetComponent<TMP_Text>().text = selected != null ? Localization.instance.Localize(selected.m_shared.m_name) : "";
            // Zones.
            var current = gear != null ? DyeVisuals.Read(selected, gear.Zones.Length) : new string[0];
            for (int z = 0; z < 4; z++)
            {
                var row = T("ColourPanel/Zones/Zone" + z);
                bool on = gear != null && z < gear.Zones.Length; row.gameObject.SetActive(on);
                if (!on) continue;
                var c = DyePalette.Get(pending[z]);
                row.Find("Name").GetComponent<TMP_Text>().text = Localization.instance.Localize("$overhaul_dye_zone_" + gear.Zones[z]);
                row.Find("ColourName").GetComponent<TMP_Text>().text = Localization.instance.Localize(c != null ? c.NameKey : "$overhaul_dye_original");
                var swatch = row.Find("Colour").GetComponent<Image>(); swatch.color = c != null ? c.Value : new Color(1, 1, 1, .15f);
                row.Find("Selected").GetComponent<Image>().enabled = z == zone;
            }
            // Colours: dyes owned, picked colour of the zone.
            string picked = pending != null && zone < pending.Length ? pending[zone] : null;
            var palette = T("ColourPanel/Palette");
            for (int i = 0; i < DyePalette.All.Count; i++)
            {
                var c = DyePalette.All[i]; var s = palette.Find("Swatch" + i);
                int owned = DyeItems.Count(inv, c);
                s.Find("Count").GetComponent<TMP_Text>().text = owned > 0 ? owned.ToString() : "";
                s.Find("Locked").GetComponent<Image>().enabled = owned == 0;
                s.Find("Selected").GetComponent<Image>().enabled = c.ID == picked;
            }
            var pickedColour = DyePalette.Get(picked);
            T("ColourPanel/ColourName")?.GetComponent<TMP_Text>()?.SetText(pickedColour != null ? Localization.instance.Localize(pickedColour.NameKey) : "");
            // Cost: the dye icon in the zone's colour, the number of dyes used.
            var need = Needed(); int total = need.Values.Sum(); bool enough = need.All(n => DyeItems.Count(inv, n.Key) >= n.Value);
            var costIcon = T("ColourPanel/Cost/Icon").GetComponent<Image>();
            costIcon.sprite = DyeItems.Icon(pickedColour != null ? pickedColour.Value : Color.white, pickedColour != null ? pickedColour.ID : "white");
            var amount = T("ColourPanel/Cost/Amount").GetComponent<TMP_Text>(); amount.text = "× " + total; amount.color = enough ? Color.white : new Color(1f, .35f, .3f);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)T("ColourPanel/Cost"));
            T("ColourPanel/Apply").GetComponent<Button>().interactable = selected != null && Changed() && enough;
            T("ColourPanel/Reset").GetComponent<Button>().interactable = pending != null && zone < pending.Length && pending[zone] != null;
            // Character.
            Preview.Show(T("PreviewPanel/View").GetComponent<RawImage>(), selected, pending);
        }

        // ---------------------------------------------------------------- the dressed character
        internal static class Preview
        {
            static GameObject puppet, cameraGo;
            static Camera camera;
            static RenderTexture texture;
            static VisEquipment vis;
            static int layer = -1;
            internal static float Yaw = 0;   // facing the camera, which stands in front (+z)
            static readonly Vector3 Place = new Vector3(0, 6000, 0);

            static int Layer()
            {
                if (layer >= 0) return layer;
                for (int i = 31; i > 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return layer = i;
                return layer = 31;
            }

            internal static void Show(RawImage view, ItemDrop.ItemData item, string[] ids)
            {
                var player = Player.m_localPlayer;
                if (!player || !Game.instance) return;
                if (!puppet) Build(player);
                if (!puppet) return;
                bool wake = !puppet.activeSelf;
                puppet.SetActive(true); cameraGo.SetActive(true);
                if (wake) Stand();   // a reactivated animator starts over from its first (falling) state
                var size = ((RectTransform)view.transform).rect.size;
                int w = Mathf.Max(64, Mathf.RoundToInt(size.x * 1.5f)), h = Mathf.Max(64, Mathf.RoundToInt(size.y * 1.5f));
                if (!texture || texture.width != w || texture.height != h)
                {
                    if (texture) texture.Release();
                    texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "DyePreview" };
                    camera.targetTexture = texture;
                }
                view.texture = texture; view.color = Color.white;
                Dress(player, item, ids);
                Frame();
            }
            // Standing idle at once: no rigidbody or collider is kept on the copy (no gravity, nothing to fall), and its
            // animator is told it is on the ground then run a few seconds ahead past the transition.
            static void Stand()
            {
                foreach (var a in puppet.GetComponentsInChildren<Animator>(true)) { a.SetBool(OnGround, true); for (int i = 0; i < 20; i++) a.Update(.25f); }
            }
            internal static void Hide() { if (puppet) puppet.SetActive(false); if (cameraGo) cameraGo.SetActive(false); }
            internal static void Destroy()
            {
                if (puppet) Object.Destroy(puppet); if (cameraGo) Object.Destroy(cameraGo); if (texture) texture.Release();
                puppet = cameraGo = null; camera = null; texture = null; vis = null;
            }
            static readonly int OnGround = Animator.StringToHash("onGround");
            static bool warned;
            // Each frame: turned as dragged, standing (the animator's ground flag, no character sets it here), and every
            // part on the preview layer (gear and hair the game attaches later come with their prefab's layer).
            internal static void Update()
            {
                if (!puppet) return;
                puppet.transform.rotation = Quaternion.Euler(0, Yaw, 0);
                foreach (var a in puppet.GetComponentsInChildren<Animator>()) a.SetBool(OnGround, true);
                // The game's updater does not reach this copy: its visuals (body, hair, gear) are updated here.
                try { vis.CustomUpdate(Time.deltaTime, Time.time); }
                catch (Exception e) { if (!warned) { warned = true; Utility.Log.LogWarning("Dyes preview: " + e); } }
                int l = Layer();
                foreach (var t in puppet.GetComponentsInChildren<Transform>(true)) if (t.gameObject.layer != l) t.gameObject.layer = l;
            }

            // A copy of the player prefab without its game components: only the look (body, hair, worn gear) and its animator.
            static void Build(Player player)
            {
                var holder = new GameObject("DyePreviewHolder"); holder.SetActive(false);
                puppet = Object.Instantiate(Game.instance.m_playerPrefab, holder.transform, false);
                puppet.name = "DyePreview";
                foreach (var c in puppet.GetComponentsInChildren<Component>(true).Reverse())
                {
                    if (!c || c is Transform || c is Renderer || c is MeshFilter || c is Animator || c is LODGroup || c is VisEquipment || c is ZNetView) continue;
                    if (c.GetType().Namespace != null && c.GetType().Namespace.StartsWith("MagicaCloth")) continue;
                    Object.DestroyImmediate(c);
                }
                vis = puppet.GetComponent<VisEquipment>(); vis.m_playerComponent = null;
                int l = Layer(); foreach (var t in puppet.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = l;
                // Awake runs here, when the copy becomes active: its ZNetView must not create a ZDO (no network object).
                ZNetView.m_forceDisableInit = true;
                try { puppet.transform.SetParent(null, false); }
                finally { ZNetView.m_forceDisableInit = false; }
                Object.Destroy(holder);
                puppet.transform.SetPositionAndRotation(Place, Quaternion.Euler(0, Yaw, 0));
                // Standing at once: the animator starts on its ground flag off (sitting/falling), then is run a few
                // seconds ahead so the transition to the standing idle is already over when the window shows.
                foreach (var a in puppet.GetComponentsInChildren<Animator>(true)) { a.updateMode = AnimatorUpdateMode.Normal; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                Stand();

                cameraGo = new GameObject("DyePreviewCamera");
                camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0, 0, 0, 0);
                camera.cullingMask = 1 << l; camera.fieldOfView = 26; camera.nearClipPlane = .1f; camera.farClipPlane = 20; camera.allowHDR = false;
                var light = new GameObject("DyePreviewLight").AddComponent<Light>(); light.transform.SetParent(cameraGo.transform, false);
                light.type = LightType.Directional; light.intensity = 1.1f; light.cullingMask = 1 << l; light.transform.localRotation = Quaternion.Euler(25, -20, 0);
            }

            // The player's look and shown gear, the selected piece put on, and the colours being chosen on it.
            static void Dress(Player player, ItemDrop.ItemData item, string[] ids)
            {
                var me = player.m_visEquipment; var zdo = player.m_nview ? player.m_nview.GetZDO() : null;
                if (zdo != null)
                {
                    vis.m_modelIndex = zdo.GetInt(ZDOVars.s_modelIndex, 0);
                    vis.m_skinColor = zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one); vis.m_hairColor = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
                    vis.m_hairItem = zdo.GetInt(ZDOVars.s_hairItem, 0); vis.m_beardItem = zdo.GetInt(ZDOVars.s_beardItem, 0);
                }
                var shown = new ItemDrop.ItemData[4];
                for (int s = 0; s < 4; s++) shown[s] = ShownItem(player, s);
                int slot = item == null ? -1 : SlotOf(item);
                if (slot >= 0) shown[slot] = item;
                int Hash(ItemDrop.ItemData i) => i?.m_dropPrefab ? i.m_dropPrefab.name.GetStableHashCode() : 0;
                vis.m_helmetItem = Hash(shown[0]); vis.m_chestItem = Hash(shown[1]); vis.m_legItem = Hash(shown[2]); vis.m_shoulderItem = Hash(shown[3]);
                vis.m_shoulderItemVariant = shown[3]?.m_variant ?? 0;
                vis.m_leftItem = vis.m_rightItem = 0;
                var overrides = new string[4];
                for (int s = 0; s < 4; s++)
                {
                    var it = shown[s]; if (it == null || DyeGear.Of(it) == null) continue;
                    var dyes = it == item && ids != null ? ids : DyeVisuals.Read(it, DyeGear.Of(it).Zones.Length);
                    overrides[s] = dyes.Any(d => d != null) ? it.m_dropPrefab.name + ":" + string.Join(",", dyes.Select(d => d ?? "-")) : "";
                }
                DyeVisuals.Overrides.Remove(vis); DyeVisuals.Overrides.Add(vis, overrides);
            }
            static ItemDrop.ItemData ShownItem(Player p, int slot)
            {
                var cosmetic = EquipmentAndQuickSlots.CosmeticEquipment.Get(p, slot);
                if (cosmetic != null) return cosmetic;
                return slot == 0 ? p.m_helmetItem : slot == 1 ? p.m_chestItem : slot == 2 ? p.m_legItem : p.m_shoulderItem;
            }
            static int SlotOf(ItemDrop.ItemData i)
            {
                switch (i.m_shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.Helmet: return 0;
                    case ItemDrop.ItemData.ItemType.Chest: return 1;
                    case ItemDrop.ItemData.ItemType.Legs: return 2;
                    case ItemDrop.ItemData.ItemType.Shoulder: return 3;
                    default: return -1;
                }
            }
            // Whole body in view, a little from above.
            static void Frame()
            {
                cameraGo.transform.position = Place + new Vector3(0, 1.05f, 0) + Quaternion.Euler(8, 0, 0) * new Vector3(0, 0, 4.6f);
                cameraGo.transform.LookAt(Place + new Vector3(0, .95f, 0));
            }
        }

        // ---------------------------------------------------------------- input while open
        [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
        static class PlayerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        static class ControllerInput { static bool Prefix(ref bool __result) { if (!BlocksInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        static class CursorInput { static bool Prefix() { if (!Visible) return true; ZCursor.LockState = CursorLockMode.None; ZCursor.Show(); return false; } }
        [HarmonyPatch(typeof(Menu), "Update")]
        static class MenuInput { static bool Prefix() => !BlocksInput; }
        // The inventory does not open over the window.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        static class NoInventory { static bool Prefix() => !Visible; }
    }

    // Dragging on the character turns it.
    internal sealed class DyeDrag : MonoBehaviour, IDragHandler
    {
        public void OnDrag(PointerEventData e) { DyeWindow.Preview.Yaw -= e.delta.x * .5f; }
    }
}
