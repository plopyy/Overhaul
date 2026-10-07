using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Overhaul.Storage
{
    internal static class ChestInteractions
    {
        internal static readonly int NameKey = "Overhaul.ChestName".GetStableHashCode();
        internal const string RenameRpc = "Overhaul_RenameChest";
        internal const int NameLimit = 64;
        static int consumedFrame = -1;
        internal static bool Eligible(Container c) => ChestAccess.Data(c) != null && c.GetComponent<Piece>() &&
            !DeviceStore.Of(c) && !c.GetComponent<TombStone>() && !c.GetComponent<Ship>() && !c.GetComponent<Vagon>();

        internal static void EnsureName(Container c)
        {
            if (c && c.GetComponent<TroughContainer>()) return;
            var data = ChestAccess.Data(c);
            if (Eligible(c) && c.m_nview.IsOwner() && string.IsNullOrEmpty(data.GetString(NameKey, "")))
                data.Set(NameKey, c.m_name);
        }
        internal static string Clean(string text)
        {
            if (text == null) return "";
            // Names are a single plain-text line, never localization or TMP markup.
            return new string(text.Where(ch => !char.IsControl(ch) && ch != '<' && ch != '>' && ch != '$')
                .Take(NameLimit).ToArray()).Trim();
        }
        internal static string Name(Container c)
        {
            if (!c) return "";
            if (c.GetComponent<TroughContainer>()) return Localization.instance.Localize(c.m_name);
            if (!Eligible(c)) return c.m_name;
            EnsureName(c);
            var name = ChestAccess.Data(c).GetString(NameKey, c.m_name);
            return name == c.m_name || string.IsNullOrWhiteSpace(name)
                ? Localization.instance.Localize(c.m_name) : Clean(name);
        }
        internal static bool Access(Container c, long playerId) => Eligible(c) &&
            ChestAccess.Allows(c, playerId) && c.CheckAccess(playerId) && ChestAccess.WardAccess(c, playerId) &&
            !ChestAccess.Leased(c) && !MoveReservation.Busy(c);

        internal static bool Rename(Container c, long sender, ZDOID actorId, string text)
        {
            if (c && c.GetComponent<TroughContainer>()) return false;
            var actor = ChestAccess.Actor(sender, actorId);
            if (actor == null || !Eligible(c) || !c.m_nview.IsOwner() ||
                Vector3.Distance(actor.GetPosition(), c.transform.position) > 5f ||
                !Access(c, actor.GetLong(ZDOVars.s_playerID, 0)) ||
                ((c.m_inUse || ChestAccess.Data(c).GetInt(ZDOVars.s_inUse, 0) != 0) && sender != ZNet.GetUID())) return false;
            var name = Clean(text);
            ChestAccess.Data(c).Set(NameKey, name.Length == 0 ? c.m_name : name);
            return true;
        }
        sealed class NameReceiver : TextReceiver
        {
            readonly Container chest;
            internal NameReceiver(Container c) { chest = c; }
            public string GetText() => Name(chest);
            public void SetText(string text)
            {
                var player = Player.m_localPlayer;
                if (!player || !Access(chest, player.GetPlayerID()) ||
                    Vector3.Distance(player.transform.position, chest.transform.position) > 5f) return;
                if (chest.m_nview.IsOwner()) Rename(chest, ZNet.GetUID(), player.GetZDOID(), text);
                else chest.m_nview.InvokeRPC(RenameRpc, player.GetZDOID(), Clean(text));
            }
        }
        internal static bool BeginRename(Container c, Player player)
        {
            if (c && c.GetComponent<TroughContainer>()) return false;
            if (!player || !TextInput.instance || !Access(c, player.GetPlayerID()) ||
                Vector3.Distance(player.transform.position, c.transform.position) > 5f) return false;
            TextInput.instance.RequestText(new NameReceiver(c), "$overhaul_chest_rename", NameLimit);
            // Native TextInput updates this one frame later; block gameplay immediately.
            TextInput.instance.m_visibleFrame = true;
            return true;
        }
        // Take-all key from Settings > Mods (printed R by default); Shift + it renames / swaps gear.
        internal static KeyControl Key(Keyboard keyboard) =>
            keyboard != null && ZInput.TryKeyCodeToKey(EquipmentAndQuickSlots.ValConfig.EffectiveTakeAllKey.MainKey, out var key) &&
            key != UnityEngine.InputSystem.Key.None ? keyboard[key] : keyboard?.rKey;
        internal static string KeyLabel => Key(Keyboard.current)?.displayName ?? "R";
        internal static string Hint() => Localization.instance.Localize(
            "\n[<color=yellow><b>" + KeyLabel + "</b></color>] $overhaul_chest_take_all" +
            "\n[<color=yellow><b>" + Localization.instance.Localize("$overhaul_key_shift") + " + " + KeyLabel + "</b></color>] $overhaul_chest_rename");

        internal static bool InputAllowed(Player p)
        {
            if (!p || p.IsDead() || p.IsTeleporting() || p.InCutscene() || p.InAttack() || p.InDodge() || RelocationClient.Active ||
                Leveling.ClassWindow.BlocksInput || Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() ||
                (TextInput.instance && TextInput.instance.m_panel && TextInput.instance.m_panel.activeSelf) ||
                StoreGui.IsVisible() || Minimap.IsOpen() || Hud.IsPieceSelectionVisible() ||
                (Chat.instance && Chat.instance.HasFocus()) || XPortal.UI.PortalConfigurationPanel.Instance.IsActive()) return false;
            var focus = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            return !focus || !focus.activeInHierarchy || (!focus.GetComponentInParent<InputField>() && !focus.GetComponentInParent<TMP_InputField>());
        }
        internal static Container Target(Player p)
        {
            if (InventoryGui.IsVisible())
            {
                var gui = InventoryGui.instance;
                var c = gui.m_currentContainer;
                return Eligible(c) && c.IsOwner() && gui.m_container && gui.m_container.gameObject.activeInHierarchy &&
                    Vector3.Distance(c.transform.position, p.transform.position) <= gui.m_autoCloseDistance ? c : null;
            }
            if (!p.TakeInput() || !GameCamera.instance) return null;
            p.FindHoverObject(out var hover, out var creature);
            var target = hover ? hover.GetComponentInParent<Container>() : null;
            return Eligible(target) ? target : null;
        }
        internal static bool TakeAll(Container chest, Player player, InventoryGui gui)
        {
            if (!player || player.IsDead() || player.IsTeleporting() || !Access(chest, player.GetPlayerID()) ||
                Vector3.Distance(player.transform.position, chest.transform.position) > 5f) return false;
            if (gui && gui.m_currentContainer == chest && chest.IsOwner())
            {
                gui.OnTakeAll();
                return true;
            }
            return chest.TakeAll(player);
        }
        internal static void Tick()
        {
            if (consumedFrame == Time.frameCount || Key(Keyboard.current)?.wasPressedThisFrame != true) return;
            if (Keyboard.current.ctrlKey.isPressed || Keyboard.current.altKey.isPressed) return;
            var p = Player.m_localPlayer;
            if (!InputAllowed(p)) return;
            if (DeviceActions.TryKey(p, Keyboard.current.shiftKey.isPressed)) { consumedFrame = Time.frameCount; return; }
            var chest = Target(p);
            if (!chest) return;
            consumedFrame = Time.frameCount;
            if (!Access(chest, p.GetPlayerID())) { p.Message(MessageHud.MessageType.Center, "$msg_cantopen"); return; }
            if (Keyboard.current.shiftKey.isPressed) BeginRename(chest, p);
            else TakeAll(chest, p, InventoryGui.IsVisible() ? InventoryGui.instance : null);
        }
        internal static void Title(InventoryGui gui)
        {
            if (gui.m_containerName && Eligible(gui.m_currentContainer)) gui.m_containerName.text = Name(gui.m_currentContainer);
        }

        [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
        static class Register
        {
            static void Postfix(Container __instance)
            {
                var c = __instance;
                if (ChestAccess.Data(c) == null) return;
                EnsureName(c);
                if (c.GetComponent<TroughContainer>()) return;
                c.m_nview.Register<ZDOID, string>(RenameRpc, (sender, actor, text) => Rename(c, sender, actor, text));
            }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.CheckForChanges))]
        static class DefaultName { static void Postfix(Container __instance) => EnsureName(__instance); }
        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        static class Hover
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var field = AccessTools.Field(typeof(Container), nameof(Container.m_name));
                foreach (var instruction in code)
                {
                    if (instruction.LoadsField(field)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ChestInteractions), nameof(Name)); }
                    yield return instruction;
                }
            }
            static void Postfix(Container __instance, ref string __result)
            {
                var player = Player.m_localPlayer;
                if (player && Access(__instance, player.GetPlayerID()))
                    __result += __instance.GetComponent<TroughContainer>()
                        ? Localization.instance.Localize("\n[<color=yellow><b>" + KeyLabel + "</b></color>] $overhaul_chest_take_all")
                        : Hint();
            }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverName))]
        static class HoverName { static void Postfix(Container __instance, ref string __result) { if (Eligible(__instance)) __result = Name(__instance); } }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
        static class Header { static void Postfix(InventoryGui __instance) => Title(__instance); }
        [HarmonyPatch(typeof(Player), nameof(Player.Update))]
        static class PlayerKeys { static void Prefix(Player __instance) { if (__instance == Player.m_localPlayer) Tick(); } }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Update))]
        static class InventoryKeys { static void Prefix() => Tick(); }
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
        static class PreventWeaponToggle
        {
            static bool Prefix(string name, ref bool __result)
            {
                if (name != "Hide" || consumedFrame != Time.frameCount) return true;
                __result = false; return false;
            }
        }
    }
}
