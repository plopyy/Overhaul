using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerTradeGame
    {
        private static ItemDrop Coins()
        {
            if (StoreGui.instance && StoreGui.instance.m_coinPrefab) return StoreGui.instance.m_coinPrefab;
            var prefab = ObjectDB.instance.GetItemPrefab("Coins");
            return prefab ? prefab.GetComponent<ItemDrop>() : null;
        }
        private static ItemDrop.ItemData Grant(ItemDrop prefab,int amount,bool cheated)
        {
            var item = prefab.m_itemData.Clone(); item.m_dropPrefab = prefab.gameObject; item.m_stack = amount;
            item.m_equipped = false; item.m_durability = item.GetMaxDurability(); item.m_crafterID = 0; item.m_crafterName = "";
            item.m_worldLevel = Game.m_worldLevel; item.m_pickedUp = false; item.m_cheated = cheated; return item;
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var command = request.Gameplay; var target = command.TargetId == 0 ? null : ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            var trader = target ? target.GetComponent<Trader>() : null;
            if (!trader || Vector3.Distance(actor.GetPosition(),trader.transform.position) > 5) throw new InvalidOperationException("Trader is out of reach");
            var coin = Coins(); if (!coin) throw new InvalidOperationException("Trader currency is unavailable");
            int currency = coin.gameObject.name.GetStableHashCode();
            var extra = new List<PlayerChange>();
            if (command.Kind == PlayerActionKind.Buy)
            {
                if (request.Action.Amount != 1 || command.Variant < 0 || command.Variant >= trader.m_items.Count) throw new InvalidOperationException("Unknown trade offer");
                var offer = trader.m_items[command.Variant];
                var unique = snapshot.Rows.Where(r => r.Table == "knowledge" && (string)r.Values[0] == "uniques").Select(r => (string)r.Values[1]).ToArray();
                if (offer == null || offer.m_price < 0 || (!string.IsNullOrEmpty(offer.m_requiredGlobalKey) && !ZoneSystem.instance.GetGlobalKey(offer.m_requiredGlobalKey)) ||
                    (!string.IsNullOrEmpty(offer.m_buyKey) && unique.Contains(offer.m_buyKey)) ||
                    (!offer.m_prefab && string.IsNullOrEmpty(offer.m_buyKey))) throw new InvalidOperationException("Trade offer is unavailable");
                if (offer.m_incrementKey == Player.InventoryRowsKey) throw new InvalidOperationException("Inventory expansion requires the authoritative layout handler");
                var coins = inventory.Keys.Where(k => inventory.Available(k)).Select(k => new { Key = k,Values = inventory.Item(k) })
                    .Where(p => Convert.ToInt32(p.Values[3]) == currency && !Convert.ToBoolean(p.Values[7]) && Convert.ToInt32(p.Values[11]) >= Game.m_worldLevel).ToArray();
                if (coins.Sum(p => Convert.ToInt64(p.Values[4])) < offer.m_price) throw new InvalidOperationException("Not enough server currency");
                bool cheated = coins.Any(p => Convert.ToBoolean(p.Values[13]));
                if (offer.m_prefab)
                {
                    int count = Math.Min(offer.m_stack,offer.m_prefab.m_itemData.m_shared.m_maxStackSize);
                    if (count < 1) throw new InvalidOperationException("Invalid trade output");
                    var item = Grant(offer.m_prefab,count,cheated);
                    // Native buying needs output room before the currency is removed.
                    inventory.Add(PlayerActionGame.Row(item).Values,item.m_customData);
                    extra.Add(new PlayerChange("knowledge",false,"materials",item.m_shared.m_name,""));
                }
                int cost = offer.m_price;
                foreach (var source in coins)
                { int take = Math.Min(cost,Convert.ToInt32(source.Values[4])); if (take > 0) inventory.Remove(source.Key,take); cost -= take; if (cost == 0) break; }
                if (!string.IsNullOrEmpty(offer.m_buyKey))
                {
                    extra.Add(new PlayerChange("knowledge",false,"uniques",offer.m_buyKey,""));
                    if (!string.IsNullOrEmpty(offer.m_incrementKey))
                    {
                        var old = unique.Where(k => k.Split(' ').Length >= 2 && k.Split(' ')[0].Equals(offer.m_incrementKey,StringComparison.OrdinalIgnoreCase)).ToArray();
                        int value = 0; if (old.Length > 0) int.TryParse(old[0].Split(' ')[1],out value);
                        long next = (long)value + offer.m_incrementAmount;
                        if (next < int.MinValue || next > int.MaxValue) throw new InvalidOperationException("Trade key is out of range");
                        foreach (string key in old) extra.Add(new PlayerChange("knowledge",true,"uniques",key));
                        extra.Add(new PlayerChange("knowledge",false,"uniques",offer.m_incrementKey.ToLowerInvariant()+" "+next.ToString(CultureInfo.InvariantCulture),""));
                    }
                }
            }
            else if (command.Kind == PlayerActionKind.Sell)
            {
                int slot = request.Action.FromY*256+request.Action.FromX; var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
                if (item.m_shared.m_value <= 0 || item.m_shared.m_name == coin.m_itemData.m_shared.m_name || item.m_equipped || request.Action.Amount != item.m_stack)
                    throw new InvalidOperationException("Item is not sellable");
                long amount = (long)item.m_shared.m_value * item.m_stack;
                if (amount < 1 || amount > ushort.MaxValue) throw new InvalidOperationException("Sale value is out of range");
                inventory.Remove(slot,item.m_stack);
                var payment = Grant(coin,(int)amount,item.m_cheated);
                inventory.Add(PlayerActionGame.Row(payment).Values,payment.m_customData);
            }
            else throw new InvalidOperationException("Invalid trade action");
            var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.Concat(extra);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),new Dictionary<long,ObjectRecord>()),() => { });
        }
        internal static void Feedback(PlayerActionCommand command)
        {
            var gui = StoreGui.instance; if (!gui || !gui.m_trader) return;
            var view = gui.m_trader.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || view.GetZDO().m_uid != new ZDOID(command.TargetUser,command.TargetId)) return;
            try
            {
                if (command.Kind == PlayerActionKind.Buy && command.Variant < gui.m_trader.m_items.Count)
                {
                    var offer = gui.m_trader.m_items[command.Variant]; gui.m_trader.OnBought(offer);
                    gui.m_buyEffects.Create(gui.transform.position,Quaternion.identity,null,1,-1,default(ZDOID));
                    offer.m_buyPlayerEffects?.Create(Player.m_localPlayer.transform.position,Quaternion.identity,null,1,-1,default(ZDOID));
                    if (offer.m_levelUpEffect) Player.m_localPlayer.OnSkillLevelup(Skills.SkillType.None,0);
                }
                else if (command.Kind == PlayerActionKind.Sell)
                { gui.m_trader.OnSold(); gui.m_sellEffects.Create(gui.transform.position,Quaternion.identity,null,1,-1,default(ZDOID)); }
                gui.FillList();
            }
            catch (Exception error) { ZLog.LogWarning("[Overhaul trade visual] " + error.Message); }
        }
        private static bool Intent(StoreGui gui,bool buy)
        {
            if (!PlayerSessionGame.Managed) return true;
            var trader = gui.m_trader; var view = trader ? trader.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid()) return false;
            var id = view.GetZDO().m_uid;
            var command = new PlayerActionCommand { Kind = buy ? PlayerActionKind.Buy : PlayerActionKind.Sell,TargetUser = id.UserID,TargetId = id.ID };
            if (buy)
            { int index = trader.m_items.IndexOf(gui.m_selectedItem); if (index >= 0) { command.Variant = index; InventoryMoveGame.Client?.Controller.Act(command); } }
            else
            { var item = gui.GetSellableItem(); if (item != null) InventoryMoveGame.Client?.Controller.Act(command,item.m_gridPos.x,item.m_gridPos.y,item.m_stack); }
            return false;
        }
        [HarmonyPatch(typeof(StoreGui),"BuySelectedItem")]
        private static class Buy { [HarmonyPriority(Priority.First + 200)] private static bool Prefix(StoreGui __instance) => Intent(__instance,true); }
        [HarmonyPatch(typeof(StoreGui),"SellItem")]
        private static class Sell { [HarmonyPriority(Priority.First + 200)] private static bool Prefix(StoreGui __instance) => Intent(__instance,false); }
    }
}
