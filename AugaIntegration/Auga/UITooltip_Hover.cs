using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Auga
{
    // The game opens a tooltip only on pointer enter and ties it to the child first hit (an icon, a text...):
    // leaving that child while still over the element closed it for good until the pointer left and came back.
    [HarmonyPatch(typeof(UITooltip), nameof(UITooltip.OnHoverStart))]
    [UiModule(UiModule.QuickInventory)]
    public static class UITooltip_OnHoverStart_Patch
    {
        // The whole element owning the tooltip is watched, not the child under the pointer.
        public static void Prefix(UITooltip __instance, ref GameObject go) => go = __instance.gameObject;

        // The tooltip follows the pointer: it must never take the hover from the element under it.
        public static void Postfix()
        {
            if (!UITooltip.m_tooltip) return;
            var group = UITooltip.m_tooltip.GetComponent<CanvasGroup>();
            if (!group) group = UITooltip.m_tooltip.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
        }
    }

    // While the pointer rests over an element with a tooltip and none is open, it is opened again
    // (after the game's usual short delay), instead of waiting for the next pointer enter.
    [HarmonyPatch(typeof(EventSystem), "Update")]
    [UiModule(UiModule.QuickInventory)]
    public static class UITooltip_HoverWatch
    {
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();
        private static PointerEventData _pointer;
        private static EventSystem _owner;

        public static void Postfix(EventSystem __instance)
        {
            if (__instance != EventSystem.current || UITooltip.m_current != null) return;
            if (ZInput.instance == null || !ZInput.IsMouseActive() || ZInput.IsTouchActive() || Cursor.lockState == CursorLockMode.Locked) return;
            if (_pointer == null || _owner != __instance) { _pointer = new PointerEventData(__instance); _owner = __instance; }
            _pointer.position = ZInput.pointerPosition;
            Hits.Clear();
            __instance.RaycastAll(_pointer, Hits);
            if (Hits.Count == 0 || !Hits[0].gameObject) return;
            var tooltip = Hits[0].gameObject.GetComponentInParent<UITooltip>();
            if (tooltip && tooltip.isActiveAndEnabled && (tooltip.m_text != "" || tooltip.m_topic != "")) tooltip.OnHoverStart(tooltip.gameObject);
        }
    }
}
