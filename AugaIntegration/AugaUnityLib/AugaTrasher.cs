using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Destruction requires an explicit button click, never a drop or mouse release alone.
    public class AugaTrasher : MonoBehaviour
    {
        public Button Button;
        public ZSFX SFX;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo Item = typeof(InventoryGui).GetField("m_dragItem", Flags);
        static readonly FieldInfo Amount = typeof(InventoryGui).GetField("m_dragAmount", Flags);
        static readonly FieldInfo Source = typeof(InventoryGui).GetField("m_dragInventory", Flags);
        static readonly MethodInfo ClearDrag = typeof(InventoryGui).GetMethod("SetupDragItem", Flags);
        public void Awake() { if (!Button) Button = GetComponent<Button>(); Button.onClick.AddListener(OnClick); }
        public void OnDestroy() { if (Button) Button.onClick.RemoveListener(OnClick); }
        private void OnClick()
        {
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (!gui || !player || !Button || !Button.IsInteractable()) return;
            var item = Item.GetValue(gui) as ItemDrop.ItemData;
            var inventory = Source.GetValue(gui) as Inventory;
            int amount = (int)Amount.GetValue(gui);
            if (item == null || inventory == null || !inventory.ContainsItem(item) || amount <= 0) return;
            amount = Mathf.Min(amount, item.m_stack);
            if (amount <= 0) return;
            if (amount == item.m_stack && inventory == player.GetInventory())
            {
                player.RemoveEquipAction(item);
                player.UnequipItem(item, false);
            }
            ClearDrag.Invoke(gui, new object[] { null, null, 0 });
            Item.SetValue(gui, null);
            Source.SetValue(gui, null);
            Amount.SetValue(gui, 0);
            if (!inventory.RemoveItem(item, amount)) return;
            if (SFX) Instantiate(SFX);
            InventorySelection.RefreshCrafting(gui);
        }
    }
}