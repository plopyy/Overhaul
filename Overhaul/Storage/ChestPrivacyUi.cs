using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class ChestPrivacyUi
    {
        private static Button bound;
        private static void Postfix(InventoryGui __instance)
        {
            if (StationStorage.Busy && __instance.m_craftButton) __instance.m_craftButton.interactable = false;
            if (!__instance.m_container) return;
            var node = __instance.m_container.transform.Find("Privacy"); if (!node) return;
            var button = node.GetComponent<Button>();
            if (bound != button)
            {
                bound = button; button.onClick.AddListener(() =>
                {
                    var gui = InventoryGui.instance; var c = gui ? gui.m_currentContainer : null; var player = Player.m_localPlayer;
                    if (!player || !ChestAccess.Eligible(c) || ChestAccess.Creator(c) != player.GetPlayerID()) return;
                    if (c.m_nview.IsOwner()) ChestAccess.SetPrivacy(c, ZNet.GetUID(), player.GetZDOID(), !ChestAccess.Private(c));
                    else c.m_nview.InvokeRPC(ChestAccess.PrivacyRpc, player.GetZDOID(), !ChestAccess.Private(c));
                });
            }
            var chest = __instance.m_currentContainer; var local = Player.m_localPlayer;
            bool visible = local && ChestAccess.Eligible(chest) && ChestAccess.Creator(chest) == local.GetPlayerID();
            node.gameObject.SetActive(visible);
            if (visible)
            {
                bool locked = ChestAccess.Private(chest);
                node.Find("Locked").gameObject.SetActive(locked); node.Find("Public").gameObject.SetActive(!locked);
                button.interactable = !ChestAccess.Leased(chest);
                button.GetComponent<UITooltip>().Set(Leveling.LevelingText.Get(locked ? "chest_private" : "chest_public"),
                    Leveling.LevelingText.Get(locked ? "chest_make_public" : "chest_make_private"), (RectTransform)__instance.m_container.transform, Vector2.zero);
            }
            foreach (var name in new[] { "Bkg", "selected_frame", "Darken" })
            {
                var layer = __instance.m_container.transform.Find(name); if (!layer) continue;
                var notches = layer.GetComponent<AugaUnity.AugaPanelNotches>(); if (!notches) continue;
                var rect = (RectTransform)layer;
                var centers = new[] { "Privacy", "Sort", "StackAll", "TakeAll" }.Select(n => __instance.m_container.transform.Find(n))
                    .Where(t => t && t.gameObject.activeSelf).Select(t => { var p = layer.InverseTransformPoint(t.position); return new Vector2(p.x - rect.rect.xMin, rect.rect.yMax - p.y); }).ToArray();
                if (!notches.CentersFromTopLeft.SequenceEqual(centers)) { notches.CentersFromTopLeft = centers; layer.GetComponent<Graphic>().SetVerticesDirty(); }
            }
        }
    }
}
