using System.Reflection;
namespace AugaUnity
{
    // The selected recipe is private in current Valheim. Keep that boundary here.
    public static class InventorySelection
    {
        static readonly FieldInfo Selection = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly PropertyInfo RecipeProperty = Selection.FieldType.GetProperty("Recipe");
        static readonly PropertyInfo ItemProperty = Selection.FieldType.GetProperty("ItemData");
        static readonly FieldInfo VariantField = typeof(InventoryGui).GetField("m_selectedVariant", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo TimerField = typeof(InventoryGui).GetField("m_craftTimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        public static Recipe Recipe(InventoryGui gui) => gui ? (Recipe)RecipeProperty.GetValue(Selection.GetValue(gui), null) : null;
        public static ItemDrop.ItemData Item(InventoryGui gui) => gui ? (ItemDrop.ItemData)ItemProperty.GetValue(Selection.GetValue(gui), null) : null;
        public static int Variant(InventoryGui gui) => gui ? (int)VariantField.GetValue(gui) : 0;
        public static float Timer(InventoryGui gui) => gui ? (float)TimerField.GetValue(gui) : -1;
        static readonly MethodInfo Refresh = typeof(InventoryGui).GetMethod("UpdateCraftingPanel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo Repair = typeof(InventoryGui).GetMethod("OnRepairPressed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo FoodValues = typeof(Player).GetMethod("GetTotalFoodValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        public static void RefreshCrafting(InventoryGui gui) => Refresh.Invoke(gui, new object[] { false });
        public static void RepairItem(InventoryGui gui) => Repair.Invoke(gui, null);
        static readonly FieldInfo BarValue = typeof(GuiBar).GetField("m_value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo BarMaximum = typeof(GuiBar).GetField("m_maxValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        public static float CraftProgress(GuiBar bar) => UnityEngine.Mathf.Clamp01((float)BarValue.GetValue(bar) / UnityEngine.Mathf.Max(.001f, (float)BarMaximum.GetValue(bar)));
        public static void GetFoodValues(Player player, out float health, out float stamina, out float eitr)
        {
            object[] values = { 0f, 0f, 0f }; FoodValues.Invoke(player, values);
            health = (float)values[0]; stamina = (float)values[1]; eitr = (float)values[2];
        }
    }
}
