using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AugaUnity;
using BepInEx;
using TMPro;
using UnityEngine;

namespace Overhaul.Commands
{
    // Builds the real Overhaul/Auga tooltip of every item in the ObjectDB (vanilla, Overhaul and other mods)
    // and reports every text left untranslated: a raw "$key" or a "[key]" the localization did not find.
    internal static class TooltipAudit
    {
        private static readonly Regex Raw = new Regex(@"\$[A-Za-z_][A-Za-z0-9_]*");
        private static readonly Regex Missing = new Regex(@"\[[A-Za-z_][A-Za-z0-9_]*\]");

        internal static string Run()
        {
            if (!ObjectDB.instance || !Player.m_localPlayer) return "Overhaul : commande a lancer depuis un personnage connecte.";
            GameObject prefab = Auga.Auga.Assets?.InventoryTooltip;
            if (!prefab) return "Overhaul : infobulle Auga introuvable.";
            var host = new GameObject("OverhaulTooltipAudit");
            host.SetActive(false);
            var report = new StringBuilder();
            int checkedItems = 0, faulty = 0;
            try
            {
                foreach (GameObject itemPrefab in ObjectDB.instance.m_items)
                {
                    ItemDrop drop = itemPrefab ? itemPrefab.GetComponent<ItemDrop>() : null;
                    if (drop == null || drop.m_itemData?.m_shared == null) continue;
                    checkedItems++;
                    GameObject instance = UnityEngine.Object.Instantiate(prefab, host.transform, false);
                    try
                    {
                        var tooltip = instance.GetComponent<ComplexTooltip>();
                        tooltip.Start();
                        ItemDrop.ItemData item = drop.m_itemData.Clone();
                        item.m_dropPrefab = itemPrefab;
                        item.m_durability = item.GetMaxDurability();
                        tooltip.SetItem(item);
                        var problems = new SortedSet<string>();
                        foreach (TMP_Text text in instance.GetComponentsInChildren<TMP_Text>(true))
                        {
                            if (string.IsNullOrEmpty(text.text)) continue;
                            foreach (Match match in Raw.Matches(text.text)) problems.Add(match.Value);
                            foreach (Match match in Missing.Matches(text.text)) problems.Add(match.Value);
                        }
                        if (problems.Count == 0) continue;
                        faulty++;
                        report.AppendLine(itemPrefab.name + " (" + Localization.instance.Localize(item.m_shared.m_name) + ") : " + string.Join(", ", problems));
                    }
                    catch (Exception e)
                    {
                        faulty++;
                        report.AppendLine(itemPrefab.name + " : infobulle en erreur : " + e.GetType().Name + " " + e.Message);
                    }
                    finally { UnityEngine.Object.Destroy(instance); }
                }
            }
            finally { UnityEngine.Object.Destroy(host); }
            string path = Path.Combine(Paths.BepInExRootPath, "OverhaulTooltipAudit.txt");
            File.WriteAllText(path, "Objets verifies : " + checkedItems + ", avec probleme : " + faulty + "\n\n" + report);
            Utility.Log.LogInfo("Tooltip audit : " + checkedItems + " objets, " + faulty + " avec probleme\n" + report);
            return "Overhaul : " + checkedItems + " objets verifies, " + faulty + " avec un texte non traduit. Rapport : " + path;
        }
    }
}
