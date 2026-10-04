using System;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal enum PlayerGearKind { None, OneHand, Shield, Torch, BothHands, Helmet, Chest, Legs, Cape, Utility, Trinket, Ammo }
    internal sealed class PlayerGearDefinition
    {
        internal PlayerGearKind Kind;
        internal string Name;
        internal bool Usable;
    }
    // Resolve worn-item conflicts and paperdoll placement against copied server rows.
    internal static class PlayerEquipment
    {
        private static bool Hand(PlayerGearKind kind) => kind >= PlayerGearKind.OneHand && kind <= PlayerGearKind.BothHands;
        internal static int Apply(PlayerActionInventory inventory,int source,bool equip,Func<object[],PlayerGearDefinition> definition,int utilityLimit)
        {
            var selected = inventory.Item(source); bool worn = Convert.ToBoolean(selected[7]);
            if (worn == equip || !inventory.Available(source)) throw new InvalidOperationException("Equipment state is already applied");
            if (!equip) { inventory.Equip(source,false); return source; }
            var target = definition(selected);
            if (target.Kind == PlayerGearKind.None || !target.Usable) throw new InvalidOperationException("Item cannot be equipped");
            var equipped = inventory.Keys.Where(k => Convert.ToBoolean(inventory.Item(k)[7])).ToDictionary(k => k,k => definition(inventory.Item(k)));
            var remove = new HashSet<int>();
            if (Hand(target.Kind))
            {
                int? keep = null;
                if (target.Kind == PlayerGearKind.OneHand)
                    keep = equipped.Where(p => p.Value.Kind == PlayerGearKind.Shield || p.Value.Kind == PlayerGearKind.Torch)
                        .OrderBy(p => p.Value.Kind == PlayerGearKind.Shield ? 0 : 1).Select(p => (int?)p.Key).FirstOrDefault();
                else if (target.Kind == PlayerGearKind.Shield)
                    keep = equipped.Where(p => p.Value.Kind == PlayerGearKind.OneHand || p.Value.Kind == PlayerGearKind.Torch)
                        .OrderBy(p => p.Value.Kind == PlayerGearKind.OneHand ? 0 : 1).Select(p => (int?)p.Key).FirstOrDefault();
                else if (target.Kind == PlayerGearKind.Torch)
                    keep = equipped.Where(p => p.Value.Kind == PlayerGearKind.Shield || p.Value.Kind == PlayerGearKind.OneHand && !equipped.Any(e => e.Value.Kind == PlayerGearKind.Torch))
                        .OrderBy(p => p.Value.Kind == PlayerGearKind.Shield ? 0 : 1).Select(p => (int?)p.Key).FirstOrDefault();
                foreach (var pair in equipped.Where(p => Hand(p.Value.Kind) && p.Key != keep)) remove.Add(pair.Key);
            }
            else if (target.Kind == PlayerGearKind.Utility)
            {
                var utilities = equipped.Where(p => p.Value.Kind == PlayerGearKind.Utility).ToArray();
                var duplicate = utilities.Where(p => p.Value.Name == target.Name).ToArray();
                string cell = inventory.Layout.Equipment(source);
                if (duplicate.Length != 0 && (cell == "Utility2" || cell == "Utility3"))
                    throw new InvalidOperationException("Duplicate utility item in extra slot");
                foreach (var pair in duplicate) remove.Add(pair.Key);
                if (remove.Count == 0 && utilities.Length >= Math.Max(1,Math.Min(3,utilityLimit)))
                    remove.Add(utilities.OrderBy(p => inventory.Layout.Equipment(p.Key) == "Utility" ? 0 : 1).ThenBy(p => p.Key).First().Key);
            }
            else foreach (var pair in equipped.Where(p => p.Value.Kind == target.Kind)) remove.Add(pair.Key);
            foreach (int key in remove) inventory.Equip(key,false);
            inventory.Equip(source,true);
            // Keep an explicitly selected paperdoll cell. Otherwise use a compatible free
            // or unequipped cell, swapping its resident back to the item's former position.
            if (inventory.Layout.Equipment(source) != null) return source;
            int prefab = Convert.ToInt32(selected[3]);
            var candidates = inventory.Layout.Slots.Where(k => inventory.Layout.Equipment(k) != null && inventory.Layout.Accepts(k,prefab))
                .Where(k => !inventory.Keys.Contains(k) || !Convert.ToBoolean(inventory.Item(k)[7]) && inventory.Layout.Accepts(source,Convert.ToInt32(inventory.Item(k)[3])))
                .OrderBy(k => inventory.Keys.Contains(k) ? 1 : 0).ThenBy(k => k).Select(k => (int?)k).ToArray();
            if (candidates.Length == 0) return source;
            int destination = candidates[0].Value; inventory.Swap(source,destination); return destination;
        }
    }
}
