namespace Overhaul
{
    // Systems still being built, switched per branch: main ships with both off,
    // v2.4 turns the magic system on, v2.5 the class system too.
    internal static class Features
    {
        // Overhaul staffs and their recipes, spells, magic power, eitr from food and eitr dishes,
        // the grimoire stand and the grimoire window with its key.
        internal const bool Magic = false;
        // The class window and its key.
        internal const bool Classes = false;
        // Catalyst slots on armour and shields, rarity and enchantments on trinkets and accessories.
        internal const bool FullGear = false;
    }
}
