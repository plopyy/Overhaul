using System.Collections.Generic;
using UnityEngine;
using Overhaul.Dungeons;

namespace Overhaul.Commands
{
    internal static class MudCollisionTest
    {
        private static readonly HashSet<Collider> ignored = new HashSet<Collider>();
        private static Collider playerCollider;
        private static float nextScan;
        internal static bool Enabled { get; private set; }

        internal static void Set(bool enabled)
        {
            Restore(); Enabled = enabled; nextScan = 0;
        }

        private static void Restore()
        {
            foreach (var collider in ignored)
                if (collider && playerCollider) Physics.IgnoreCollision(playerCollider, collider, false);
            ignored.Clear(); playerCollider = null;
        }

        internal static bool IsCryptMud(string name, Vector3 position)
        {
            var prefab = DungeonPolicy.PrefabName(name);
            return Character.InInterior(position) && (prefab == "mudpile" || prefab == "mudpile2");
        }

        internal static void Tick()
        {
            if (!Enabled) return;
            var player = Player.m_localPlayer;
            if (!player || !ZNet.instance) { Set(false); return; }
            var collider = player.GetComponent<Collider>();
            if (playerCollider != collider) { Restore(); playerCollider = collider; }
            if (!collider || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + .25f;
            if (!Character.InInterior(player.transform.position)) { Restore(); return; }
            if (!ZNetScene.instance) return;
            foreach (var view in ZNetScene.instance.m_instances.Values)
            {
                if (!view || !IsCryptMud(view.name, view.transform.position) ||
                    (view.transform.position-player.transform.position).sqrMagnitude > 10000f) continue;
                foreach (var mud in view.GetComponentsInChildren<Collider>())
                {
                    if (!mud.enabled || mud.isTrigger) continue;
                    if (!ignored.Contains(mud) && Physics.GetIgnoreCollision(collider,mud)) continue;
                    Physics.IgnoreCollision(collider,mud,true); ignored.Add(mud);
                }
            }
        }
    }
}
