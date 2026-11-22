using UnityEngine;

namespace AugaUnity
{
    // Chest window sized to its chest: as many columns as the chest has (never more than MaxColumns, the widest chest)
    // and as many rows as fit in MaxHeight, the rest scrolled. Background, header, grid, side buttons and weight are
    // anchored to the window's edges in the prefab and follow. MaxHeight is the room left under the inventory, given at
    // run time by the code that places the window (0 = no limit). Skipped while another window (machines) uses the place.
    public class AugaContainerFit : MonoBehaviour
    {
        public InventoryGrid Grid;
        public float CellSpace = 70;
        public Vector2 Padding = new Vector2(24, 75);   // window size minus columns x rows of cells
        public int MaxColumns = 8;
        public int MinRows = 1;
        public float MinHeight = 0;   // the side buttons' column must fit
        [System.NonSerialized] public float MaxHeight;

        // The grid's scroll goes back to the top each time a chest opens (the window shows, or another chest's
        // inventory comes in), held two frames so the game's own layout of the cells does not move it again.
        Inventory shown; int toTop;
        void OnEnable() { toTop = 2; }
        void ScrollToTop()
        {
            if (toTop <= 0) return; toTop--;
            var scroll = GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
            if (scroll) { scroll.StopMovement(); scroll.verticalNormalizedPosition = 1f; }
        }

        void LateUpdate()
        {
            if (!Grid || !Grid.gameObject.activeInHierarchy) return;
            var inventory = Grid.GetInventory();
            if (inventory == null) return;
            if (inventory != shown) { shown = inventory; toTop = 2; }
            ScrollToTop();
            int columns = Mathf.Clamp(inventory.GetWidth(), 1, MaxColumns);
            int rows = Mathf.Max(MinRows, inventory.GetHeight());
            if (MaxHeight > 0) rows = Mathf.Min(rows, Mathf.Max(MinRows, Mathf.FloorToInt((MaxHeight - Padding.y) / CellSpace)));
            var size = new Vector2(columns * CellSpace + Padding.x, Mathf.Max(MinHeight, rows * CellSpace + Padding.y));
            var rect = (RectTransform)transform;
            if (rect.sizeDelta == size) return;
            rect.sizeDelta = size;
            // The game lays the cells out once, centred on the grid's width at that moment (the window's previous size):
            // a size of 0 makes its next update lay them out again on the new width.
            Grid.m_width = 0;
        }
    }
}
