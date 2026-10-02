using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(Incinerator), nameof(Incinerator.Incinerate))]
    internal static class EquipmentRecycler
    {
        private static readonly HashSet<string> Woods = new HashSet<string>(StringComparer.Ordinal)
            { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood", "Blackwood" };
        internal static bool IsWood(ItemDrop.ItemData item) => item.m_dropPrefab &&
            Woods.Contains(Utils.GetPrefabName(item.m_dropPrefab));
        private static bool Prefix(Incinerator __instance, long uid, ref IEnumerator __result)
        {
            __result = Run(__instance, uid);
            return false;
        }

        internal static bool Eligible(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return true;
                default: return false;
            }
        }

        internal static List<ItemDrop.ItemData> Refund(ItemDrop.ItemData item, Recipe recipe)
        {
            var result = new List<ItemDrop.ItemData>();
            // Alternative ingredients are not recorded on existing items: never invent a refund.
            if (!recipe || recipe.m_requireOnlyOneIngredient || recipe.m_amount <= 0) return result;
            var amounts = new Dictionary<ItemDrop, double>();
            foreach (var requirement in recipe.m_resources)
            {
                if (!requirement.m_resItem) continue;
                double cost = recipe.m_noCraftOnlyUpgrade ? 0 : Math.Max(0, requirement.GetAmount(1)) / (double)recipe.m_amount;
                for (int quality = 2; quality <= item.m_quality; quality++)
                    cost += Math.Max(0, requirement.GetAmount(quality));
                amounts.TryGetValue(requirement.m_resItem, out double previous);
                amounts[requirement.m_resItem] = previous + cost * item.m_stack;
            }
            foreach (var pair in amounts)
            {
                int count = (int)Math.Floor(pair.Value / 2);
                while (count > 0)
                {
                    var material = pair.Key.m_itemData.Clone();
                    material.m_dropPrefab = pair.Key.gameObject;
                    material.m_stack = Math.Min(count, Math.Max(1, material.m_shared.m_maxStackSize));
                    count -= material.m_stack;
                    result.Add(material);
                }
            }
            return result;
        }

        internal static int Recycle(Inventory inventory, Action<ItemDrop.ItemData> overflow)
        {
            // Snapshot inputs before inserting refunds, even when an ingredient is equipment.
            var inputs = inventory.GetAllItems().Where(Eligible).ToArray();
            var refunds = new List<ItemDrop.ItemData>();
            foreach (var item in inputs)
                refunds.AddRange(Refund(item, ObjectDB.instance.GetRecipe(item)));
            var wood = inventory.GetAllItems().Where(IsWood).ToArray();
            var coal = ObjectDB.instance.GetItemPrefab("Coal");
            if (coal && wood.Length > 0)
            {
                int count = (int)Math.Floor(wood.Sum(i => (double)i.m_stack) * .75);
                var template = coal.GetComponent<ItemDrop>().m_itemData;
                while (count > 0)
                {
                    var material = template.Clone(); material.m_dropPrefab = coal;
                    material.m_stack = Math.Min(count, Math.Max(1, template.m_shared.m_maxStackSize));
                    count -= material.m_stack; refunds.Add(material);
                }
                foreach (var item in wood) inventory.RemoveItem(item);
            }
            foreach (var item in inputs) inventory.RemoveItem(item);
            foreach (var material in refunds)
            {
                material.m_stack = ShipDismantle.Store(inventory, material);
                if (material.m_stack > 0) overflow(material);
            }
            return inputs.Length + (coal ? wood.Length : 0);
        }

        private static IEnumerator Run(Incinerator machine, long uid)
        {
            if (!machine.m_nview.IsValid() || !machine.m_nview.IsOwner() || machine.isInUse || machine.m_container.IsInUse()) yield break;
            machine.isInUse = true;
            machine.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_AnimateLever");
            machine.m_leverEffects.Create(machine.transform.position, machine.transform.rotation);
            yield return new WaitForSeconds(UnityEngine.Random.Range(machine.m_effectDelayMin, machine.m_effectDelayMax));
            machine.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_AnimateLeverReturn");
            if (!machine.m_nview.IsValid() || !machine.m_nview.IsOwner() || machine.m_container.IsInUse())
            {
                machine.isInUse = false;
                yield break;
            }
            machine.Invoke("StopAOE", 4f);
            UnityEngine.Object.Instantiate(machine.m_lightingAOEs, machine.transform.position, machine.transform.rotation);
            int count = Recycle(machine.m_container.GetInventory(), item =>
                ItemDrop.DropItem(item, 0, machine.transform.position + machine.transform.forward * 2f + Vector3.up, Quaternion.identity));
            machine.m_nview.InvokeRPC(uid, "RPC_IncinerateRespons", count > 0 ? 2 : 3);
        }
    }
}
