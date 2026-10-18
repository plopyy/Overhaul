using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // The parameters of the one item slot prefab (InventoryElement), used everywhere: windows, the hotbar and the quick slots
    // (HotKeyElement is a prefab variant of it with IsHUD on). What differs between those places is set here, the rest of
    // the slot is shared, so any change to the prefab applies everywhere.
    //  IsHUD on: a transparent black ground (HudBackground), no window ground and no rarity ground.
    //  IsHUD off: the brown window ground (WindowBackground, shaded by Overhaul from the window behind it) and the rarity
    //  ground (RarityBackground, shown by Overhaul for an item with a rarity).
    //  HotKey: the key shown in the top left corner (a hotbar or quick slot), null for none.
    [DisallowMultipleComponent]
    public class ItemSlot : MonoBehaviour
    {
        [SerializeField] private bool _isHUD;
        [SerializeField] private string _hotKey;

        public Image WindowBackground;
        public GameObject HudBackground;
        public GameObject RarityBackground;
        public TMP_Text HotKeyText;

        public bool IsHUD
        {
            get => _isHUD;
            set { if (_isHUD == value) return; _isHUD = value; Refresh(); }
        }

        public string HotKey
        {
            get => string.IsNullOrEmpty(_hotKey) ? null : _hotKey;
            set { _hotKey = string.IsNullOrEmpty(value) ? null : value; Refresh(); }
        }

        public virtual void Awake() => Refresh();
        public virtual void OnValidate() => Refresh();

        public void Refresh()
        {
            if (WindowBackground) WindowBackground.enabled = !_isHUD;
            if (HudBackground) HudBackground.SetActive(_isHUD);
            if (_isHUD && RarityBackground) RarityBackground.SetActive(false);
            if (HotKeyText)
            {
                bool shown = HotKey != null;
                HotKeyText.gameObject.SetActive(shown);
                if (shown) { HotKeyText.enabled = true; HotKeyText.text = _hotKey; }
            }
        }

        // The slot of a cell (null for a cell built without it).
        public static ItemSlot Of(GameObject cell) => cell ? cell.GetComponent<ItemSlot>() : null;

        // Sets a cell's hot key; a cell without the parameters gets its "binding" text set directly.
        public static void SetHotKey(GameObject cell, string key)
        {
            var slot = Of(cell);
            if (slot) { slot.HotKey = key; return; }
            var binding = cell ? cell.transform.Find("binding") : null;
            var text = binding ? binding.GetComponent<TMP_Text>() : null;
            if (!text) return;
            text.gameObject.SetActive(!string.IsNullOrEmpty(key));
            text.enabled = true;
            if (!string.IsNullOrEmpty(key)) text.text = key;
        }
    }
}
