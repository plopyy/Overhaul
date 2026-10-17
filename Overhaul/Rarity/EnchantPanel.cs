using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Overhaul.Storage;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Rarity
{
    // The enchanting table window (OverhaulLab EnchantTableWindow, shown by DeviceWindow in place of the chest panel): the item
    // on the left, three tabs on the right.
    // Enchant: the trophy, its tier's odds per rarity, and Enchant (uses the trophy up, draws the item's rarity and lines again).
    // Change: the item's lines; the first line changed is locked as the only one that can change again. Reroll pays the cost
    // and offers the current line and two other effects; clicking one puts it on the item (the current one stays selected
    // until another is clicked, so closing keeps it). Missing cost items blink red like crafting requirements.
    // Catalysts: the item's catalyst slots (the table's second inventory, written into the item).
    internal sealed class EnchantPanel : MonoBehaviour
    {
        internal const string LockKey = "overhaul_enchant_lock";
        static readonly string[] TabNames = { "Enchant", "Reroll", "Catalysts" };
        static readonly string[] PageNames = { "EnchantPage", "RerollPage", "CatalystPage" };

        DeviceStore store; EnchantInventory table; InventoryGrid catalystGrid;
        int tab, line = -1, choice;
        string[] choices; ItemDrop.ItemData shownItem;
        float nextRefresh;
        TMP_Text itemName, itemRarity, trophyName, trophyRank, lockedHint;
        Button enchantButton, rollButton;
        readonly List<TMP_Text> costAmounts = new List<TMP_Text>();

        Transform Find(string path) => transform.Find(path);
        TMP_Text Text(string path) => Find(path)?.GetComponent<TMP_Text>();
        static Color Muted => new Color(.92f, .88f, .80f, .45f);

        internal void Init(DeviceStore deviceStore, InventoryGrid fuelGrid)
        {
            store = deviceStore; table = (EnchantInventory)store.Adapter; catalystGrid = fuelGrid;
            foreach (var text in GetComponentsInChildren<TMP_Text>(true)) if (text.text.Contains("$")) text.text = Localization.instance.Localize(text.text);
            itemName = Text("ItemPanel/ItemName"); itemRarity = Text("ItemPanel/ItemRarity");
            trophyName = Text("EnchantPage/TrophyName"); trophyRank = Text("EnchantPage/TrophyRank"); lockedHint = Text("RerollPage/LockedHint");
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i; var button = Find("SubTabs/" + TabNames[i] + "Tab")?.GetComponent<Button>();
                if (button) { button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(() => ShowTab(index)); }
            }
            enchantButton = Find("EnchantPage/EnchantButton")?.GetComponent<Button>();
            if (enchantButton) { enchantButton.onClick = new Button.ButtonClickedEvent(); enchantButton.onClick.AddListener(Enchant); }
            rollButton = Find("RerollPage/Cost/RollButton")?.GetComponent<Button>();
            if (rollButton) { rollButton.onClick = new Button.ButtonClickedEvent(); rollButton.onClick.AddListener(Reroll); }
            for (int i = 0; i < 5; i++)
            {
                int index = i; var row = Find("RerollPage/Lines/Line" + i)?.GetComponent<Button>();
                if (row) { row.onClick = new Button.ButtonClickedEvent(); row.onClick.AddListener(() => SelectLine(index)); }
            }
            for (int i = 0; i < 3; i++)
            {
                int index = i; var row = Find("RerollPage/Choices/Choice" + i)?.GetComponent<Button>();
                if (row) { row.onClick = new Button.ButtonClickedEvent(); row.onClick.AddListener(() => Pick(index)); }
            }
            var cost = EnchantCost.Items();
            for (int i = 0; i < 2; i++)
            {
                var icon = Find("RerollPage/Cost/Icon" + i)?.GetComponent<Image>(); var amount = Text("RerollPage/Cost/Amount" + i);
                bool shown = i < cost.Length;
                if (icon) { icon.enabled = shown; if (shown) icon.sprite = cost[i].Icon; }
                if (amount) { amount.text = shown ? cost[i].Amount.ToString() : ""; costAmounts.Add(amount); }
            }
            ShowTab(0);
        }

        void ShowTab(int index)
        {
            tab = index;
            for (int i = 0; i < TabNames.Length; i++)
            {
                var selected = Find("SubTabs/" + TabNames[i] + "Tab/Selected"); if (selected) selected.gameObject.SetActive(i == tab);
                var page = Find(PageNames[i]); if (page) page.gameObject.SetActive(i == tab);
            }
            Refresh();
        }

        void Update()
        {
            EnchantCost.Tick();
            if (Time.unscaledTime >= nextRefresh) Refresh();
            // Missing cost items blink red and white, as in the crafting panel.
            var player = Player.m_localPlayer; var cost = EnchantCost.Items();
            for (int i = 0; i < costAmounts.Count && i < cost.Length; i++)
            {
                bool missing = player && !player.NoCostCheat() && EnchantCost.Count(player, table.Station, cost[i].Name) < cost[i].Amount;
                costAmounts[i].color = missing && Mathf.Sin(Time.time * 10f) > 0f ? Color.red : Color.white;
            }
        }

        static int Lock(ItemDrop.ItemData item) =>
            item != null && item.m_customData.TryGetValue(LockKey, out string value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;

        void Refresh()
        {
            nextRefresh = Time.unscaledTime + .2f;
            if (!store || !store.Ready) return;
            var item = table.Item; var rarity = item != null ? ItemRarity.Of(item) : null;
            if (itemName) { itemName.text = item != null ? Localization.instance.Localize(item.m_shared.m_name) : ""; itemName.color = rarity?.Color ?? Color.white; }
            if (itemRarity) { itemRarity.text = rarity != null ? rarity.Name : ""; itemRarity.color = rarity?.Color ?? Color.white; }
            // Another item on the table: no line chosen and no offers left from the previous one.
            if (shownItem != item) { shownItem = item; choices = null; line = -1; }
            bool owner = store.Owner && !EnchantCost.Busy;

            // Enchant tab.
            var trophy = table.Trophy; var source = EnchantOdds.Of(trophy);
            if (trophyName) trophyName.text = trophy != null ? Localization.instance.Localize(trophy.m_shared.m_name) : "";
            if (trophyRank) trophyRank.text = source == null ? "" : Localization.instance.Localize("$biome_" + source.Biome.ToLowerInvariant()) + " · " +
                Localization.instance.Localize("$overhaul_enchant_rank_" + source.Rank.ToLowerInvariant());
            var odds = source != null ? EnchantOdds.Odds(source.Tier) : null; var rarities = RarityConfig.Current.Rarities;
            for (int i = 0; i < rarities.Count; i++)
            {
                var chance = Text("EnchantPage/Odds/" + rarities[i].Id.ToLowerInvariant() + "/Chance");
                if (chance) chance.text = odds == null ? "–" : odds[i].ToString(odds[i] > 0 && odds[i] < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " %";
            }
            if (enchantButton) enchantButton.interactable = owner && EnchantInventory.Gear(item) && source != null;

            // Change tab.
            var lines = item != null ? ItemRarity.Enchants(item) : new string[0];
            int locked = Lock(item); if (locked >= lines.Length) locked = -1;
            if (locked >= 0) line = locked; else if (line >= lines.Length) line = -1;
            int biome = item != null ? Enchantments.Biome(item) : 0;
            for (int i = 0; i < 5; i++)
            {
                var row = Find("RerollPage/Lines/Line" + i); if (!row) continue;
                row.gameObject.SetActive(i < lines.Length);
                if (i >= lines.Length) continue;
                var text = row.Find("Text")?.GetComponent<TMP_Text>();
                if (text) { text.text = LineText(lines[i], biome); text.color = i == line ? Color.white : locked >= 0 ? Muted : new Color(.92f, .88f, .80f); }
                var highlight = row.Find("Highlight")?.GetComponent<Image>(); if (highlight) highlight.enabled = i == line;
                var button = row.GetComponent<Button>(); if (button) button.interactable = owner && (locked < 0 || i == locked) && choices == null;
            }
            if (lockedHint) lockedHint.gameObject.SetActive(locked >= 0);
            for (int i = 0; i < 3; i++)
            {
                var row = Find("RerollPage/Choices/Choice" + i); if (!row) continue;
                bool shown = choices != null && i < choices.Length;
                row.gameObject.SetActive(shown);
                if (!shown) continue;
                var text = row.Find("Text")?.GetComponent<TMP_Text>(); if (text) text.text = LineText(choices[i], biome);
                var selected = row.Find("Selected")?.GetComponent<Image>(); if (selected) selected.enabled = i == choice;
                var button = row.GetComponent<Button>(); if (button) button.interactable = owner;
            }
            if (rollButton) rollButton.interactable = owner && line >= 0 && line < lines.Length && EnchantCost.Affordable(Player.m_localPlayer, table.Station);

            // Catalysts tab: only the item's own slots.
            if (catalystGrid)
            {
                int slots = table.CatalystSlots;
                for (int i = 0; i < catalystGrid.m_elements.Count; i++) catalystGrid.m_elements[i].gameObject.SetActive(i < slots);
            }
        }

        static string LineText(string id, int biome)
        {
            if (id == ItemRarity.EmptyEnchant || !Effects.EffectConfig.Current.Effects.TryGetValue(id, out var effect))
                return Localization.instance.Localize("$overhaul_rarity_empty_enchant");
            return effect.Name + " " + effect.Format(effect.EnchantValue(biome));
        }

        void SelectLine(int index) { if (choices == null) { line = index; Refresh(); } }

        void Enchant()
        {
            var item = table.Item; var trophy = table.Trophy; var source = EnchantOdds.Of(trophy);
            if (!store.Owner || !EnchantInventory.Gear(item) || source == null) return;
            var rarity = EnchantOdds.Roll(source.Tier); if (rarity == null) return;
            var player = Player.m_localPlayer;
            store.Change(() =>
            {
                // Catalysts kept up to the new rarity's slots; the others go back to the player.
                string[] socketed = item.m_customData.TryGetValue(ItemRarity.CatalystKey, out string saved) ? saved.Split(',') : new string[0];
                ItemRarity.Assign(item, rarity);
                item.m_customData.Remove(LockKey);
                int slots = ItemRarity.Catalysts(item).Length;
                foreach (var extra in socketed.Skip(slots).Where(n => n.Length > 0 && ObjectDB.instance.GetItemPrefab(n)))
                {
                    var back = DeviceStore.Item(extra);
                    if (!player.GetInventory().AddItem(back)) ItemDrop.DropItem(back, 1, player.transform.position + Vector3.up, Quaternion.identity);
                }
                var kept = socketed.Take(slots).ToArray();
                if (kept.Any(n => n.Length > 0)) item.m_customData[ItemRarity.CatalystKey] = string.Join(",", kept); else item.m_customData.Remove(ItemRarity.CatalystKey);
                store.Items.RemoveItem(trophy, 1);
                table.ShowCatalysts();
            });
            choices = null; line = -1;
            player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(item.m_shared.m_name) + " : " + rarity.Paint(rarity.Name));
            Refresh();
        }

        void Reroll()
        {
            var item = table.Item; var rarity = ItemRarity.Of(item);
            if (!store.Owner || item == null || rarity == null || line < 0) return;
            int index = line;
            EnchantCost.Pay(Player.m_localPlayer, table.Station, paid =>
            {
                if (!paid || !store || table.Item != item) { Refresh(); return; }
                var lines = ItemRarity.Enchants(item); if (index >= lines.Length) return;
                // Two other effects the item can carry, none of its current lines.
                var pool = Enchantments.Pool(item, rarity).Where(e => !lines.Any(l => string.Equals(l, e.Id, StringComparison.OrdinalIgnoreCase))).ToList();
                var picked = new List<string> { lines[index] };
                while (picked.Count < 3 && pool.Count > 0) { int pick = UnityEngine.Random.Range(0, pool.Count); picked.Add(pool[pick].Id); pool.RemoveAt(pick); }
                store.Change(() => item.m_customData[LockKey] = index.ToString(CultureInfo.InvariantCulture));
                choices = picked.ToArray(); choice = 0; line = index;
                Refresh();
            });
        }

        void Pick(int index)
        {
            var item = table.Item;
            if (!store.Owner || choices == null || index >= choices.Length || item != shownItem || line < 0) return;
            var lines = ItemRarity.Enchants(item); if (line >= lines.Length) return;
            lines[line] = choices[index];
            store.Change(() => item.m_customData[ItemRarity.EnchantKey] = string.Join(",", lines));
            choice = index;
            Refresh();
        }
    }
}
