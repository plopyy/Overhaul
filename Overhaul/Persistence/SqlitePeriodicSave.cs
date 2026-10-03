using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Overhaul.Persistence
{
    // Keep the native character timer and save path. Only its world work is redundant
    // while SQLite is active; explicit saves, logout and shutdown keep their flushes.
    [HarmonyPatch(typeof(Game), "UpdateSaving")]
    internal static class SqlitePeriodicSavePatch
    {
        [ThreadStatic] private static bool characterDiskCheck;
        private static bool UsesSqlite(ZNet net) => GamePersistence.Active && net && net.IsServer();

        private static void AutomaticSave(ZNet net, bool sync, bool saveOtherPlayerProfiles, bool waitForNextFrame)
        {
            if (!UsesSqlite(net)) { net.Save(sync, saveOtherPlayerProfiles, waitForNextFrame); return; }
            // Preserve the server's existing request to save remote characters.
            if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld) || ZNet.m_loadError ||
                ZoneSystem.instance.SkipSaving() || DungeonDB.instance.SkipSaving() || ZNet.m_world == null) return;
            if (saveOtherPlayerProfiles) net.SaveOtherPlayerProfiles();
        }

        private static void WorldWarning(MessageHud hud, MessageHud.MessageType type, string text)
        {
            if (!UsesSqlite(ZNet.instance)) hud.MessageAll(type, text);
        }

        private static bool CheckDisk(ZNet net, out bool exitGamePopupShown, bool exitGamePrompt, Action<bool> onDecisionMade)
        {
            exitGamePopupShown = false;
            // A dedicated server has no local character to save at this timer.
            if (UsesSqlite(net) && net.IsDedicated()) return true;
            bool previous = characterDiskCheck;
            characterDiskCheck = UsesSqlite(net);
            try { return net.EnoughDiskSpaceAvailable(out exitGamePopupShown, exitGamePrompt, onDecisionMade); }
            finally { characterDiskCheck = previous; }
        }

        internal static World WorldForDiskCheck() => characterDiskCheck ? null : ZNet.GetWorldIfIsHost();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var targets = new[] {
                AccessTools.Method(typeof(ZNet), nameof(ZNet.Save)),
                AccessTools.Method(typeof(MessageHud), nameof(MessageHud.MessageAll)),
                AccessTools.Method(typeof(ZNet), nameof(ZNet.EnoughDiskSpaceAvailable)) };
            var replacements = new[] { "AutomaticSave", "WorldWarning", "CheckDisk" };
            var counts = new int[targets.Length];
            var result = instructions.Select(i => new CodeInstruction(i)).ToList();
            foreach (var instruction in result)
                for (int i = 0; i < targets.Length; i++)
                    if (instruction.Calls(targets[i]))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(SqlitePeriodicSavePatch), replacements[i]);
                        counts[i]++;
                        break;
                    }
            if (counts.Any(count => count != 1))
                throw new InvalidOperationException("Unsupported Game.UpdateSaving: SQLite automatic save call sites changed");
            return result;
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.EnoughDiskSpaceAvailable))]
    internal static class SqliteCharacterDiskCheckPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetWorldIfIsHost));
            var replacement = AccessTools.Method(typeof(SqlitePeriodicSavePatch), nameof(SqlitePeriodicSavePatch.WorldForDiskCheck));
            var result = instructions.Select(i => new CodeInstruction(i)).ToList();
            int count = 0;
            foreach (var instruction in result)
                if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
            if (count != 1) throw new InvalidOperationException("Unsupported ZNet.EnoughDiskSpaceAvailable: world lookup changed");
            return result;
        }
    }
}