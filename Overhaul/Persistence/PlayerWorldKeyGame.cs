using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class PlayerWorldKeyGame
    {
        private static readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private static bool publishing;
        internal static HashSet<string> Protected => new HashSet<string>(pending,StringComparer.Ordinal);
        internal static void Clear() { pending.Clear(); publishing = false; }
        internal sealed class Reservation : IDisposable
        {
            private readonly string key;
            private bool submitted;
            internal Reservation(string value)
            {
                key = value?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key) || key.Length > 256 || key.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Unsupported world progress key");
                ZoneSystem.GetKeyValue(key,out _,out var type);
                if (type < GlobalKeys.NonServerOption || !ZoneSystem.instance || ZoneSystem.instance.GetGlobalKey(key) || !pending.Add(key))
                    throw new InvalidOperationException("World progress is already unlocked or pending");
            }
            internal PlayerActionPlan Finish(PlayerActionPlan original)
            {
                if (original.Change.Containers.Count != 0 || original.Ready != null || original.Change.WorldKeys.Length != 0)
                    throw new InvalidOperationException("Unsupported world progress participant");
                var change = new PlayerWorldAction(original.Change.Player,original.Change.Objects,null,new[] { key });
                var plan = new PlayerActionPlan(change,() =>
                {
                    publishing = true;
                    try { ZoneSystem.instance.RPC_SetGlobalKey(ZNet.GetUID(),key); }
                    finally { publishing = false; }
                    original.Publish(); pending.Remove(key);
                });
                submitted = true; return plan;
            }
            public void Dispose() { if (!submitted) pending.Remove(key); }
        }
        [HarmonyPatch]
        private static class Guard
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(ZoneSystem),"GlobalKeyAdd"); yield return AccessTools.Method(typeof(ZoneSystem),"GlobalKeyRemove"); }
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(string keyStr) => publishing || keyStr == null || !pending.Contains(keyStr.Trim().ToLowerInvariant());
        }
    }
}
