using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AugaUnity
{
    // Tells the layout system how tall an inventory grid is: one cell per row of its inventory, up to
    // MaxRows (more rows scroll inside the grid). The native grid is no layout element, so without this
    // a window laid out by a VerticalLayoutGroup + ContentSizeFitter would see it as 0 high.
    [RequireComponent(typeof(RectTransform))]
    public class GridRowsLayout : UIBehaviour, ILayoutElement
    {
        public InventoryGrid Grid;
        public int MaxRows = 2;

        private int _rows;

        private int Rows
        {
            get
            {
                var inventory = Grid ? Grid.GetInventory() : null;
                return Mathf.Clamp(inventory != null ? inventory.GetHeight() : MaxRows, 1, MaxRows);
            }
        }

        public float preferredHeight => Grid ? Rows * Grid.m_elementSpace : 0;
        public float minHeight => preferredHeight;
        public float flexibleHeight => -1;
        public float preferredWidth => -1;
        public float minWidth => -1;
        public float flexibleWidth => -1;
        public int layoutPriority => 1;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }

        // The window resizes itself when the grid gets an inventory with another row count.
        public void LateUpdate()
        {
            var rows = Rows;
            if (rows == _rows) return;
            _rows = rows;
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
        }
    }
}
