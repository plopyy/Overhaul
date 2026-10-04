using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class InventoryMoveReservations
    {
        private sealed class Deferred
        {
            internal Component Target;
            internal MethodBase Method;
            internal object[] Arguments;
            internal ZDOID Id;
        }
        private static readonly List<Deferred> deferred = new List<Deferred>();
        internal static void Release(ZDOID id)
        {
            var ready = deferred.Where(d => d.Id == id).ToArray(); deferred.RemoveAll(d => d.Id == id);
            foreach (var action in ready) if (action.Target) action.Method.Invoke(action.Target, action.Arguments);
        }
        internal static void Clear() => deferred.Clear();
        [HarmonyPatch]
        private static class Destruction
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(WearNTear), "Destroy"); yield return AccessTools.Method(typeof(Destructible), "Destroy"); }
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Component __instance, MethodBase __originalMethod, object[] __args)
            {
                var view = __instance.GetComponent<ZNetView>();
                if (!view || !view.IsValid() || !GamePersistence.InventoryReserved(view.GetZDO().m_uid)) return true;
                if (!deferred.Any(d => d.Target == __instance && d.Method == __originalMethod))
                    deferred.Add(new Deferred { Target = __instance, Method = __originalMethod, Arguments = (object[])__args.Clone(), Id = view.GetZDO().m_uid });
                return false;
            }
        }
    }
}
