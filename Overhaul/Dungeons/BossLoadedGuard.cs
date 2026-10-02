using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    // Keep the spawn anchor across streaming, ownership changes and saved-world reloads.
    internal sealed class BossLoadedGuard : MonoBehaviour
    {
        internal static readonly int AnchorKey = "overhaul_boss_room_anchor_v1".GetStableHashCode();
        private Character character;
        private Rigidbody body;
        private bool wasKinematic;
        private Vector3 anchor;
        private float nextCheck;
        internal bool Waiting { get; private set; }
        internal static bool IsInterior(Vector3 position) => position.y >= 11000f;
        internal static bool NeedsRecovery(Vector3 position, Vector3 origin) => IsInterior(origin) &&
            (Mathf.Abs(position.y-origin.y)>48f || new Vector2(position.x-origin.x,position.z-origin.z).sqrMagnitude>96f*96f);
        internal static void Hold(Character character)
        {
            if (!character || !character.m_body || character.GetComponent<BossLoadedGuard>()) return;
            var guard = character.gameObject.AddComponent<BossLoadedGuard>();
            guard.character=character; guard.body=character.m_body;
            var zdo=character.m_nview.GetZDO();
            guard.anchor=zdo.GetVec3(AnchorKey,zdo.GetVec3(ZDOVars.s_spawnPoint,character.transform.position));
            if(IsInterior(guard.anchor) && character.m_nview.IsOwner()) zdo.Set(AnchorKey,guard.anchor);
            guard.Freeze();
        }
        private void Freeze()
        {
            if(Waiting)return;
            wasKinematic=body.isKinematic;
            if(!body.isKinematic)body.linearVelocity=Vector3.zero;
            body.isKinematic=true; Waiting=true;
        }
        private void Release()
        {
            if(!Waiting)return;
            body.isKinematic=wasKinematic; Waiting=false;
        }
        internal static bool HasFloor(Vector3 position, int mask)
        {
            Physics.SyncTransforms();
            foreach(var hit in Physics.RaycastAll(position+Vector3.up,Vector3.down,16f,mask,QueryTriggerInteraction.Ignore))
                if(hit.normal.y>.25f && !hit.collider.attachedRigidbody && hit.point.y<=position.y+.3f) return true;
            return false;
        }
        private void Update()
        {
            if(!body || !character || Time.unscaledTime<nextCheck)return;
            nextCheck=Time.unscaledTime+.25f;
            if(character.IsDead()){Release();return;}
            if(!character.m_nview || !character.m_nview.IsValid())return;
            // Only the network owner may relocate or publish the boss position.
            if(!character.m_nview.IsOwner())return;
            if(!ZoneSystem.instance)return;
            bool displaced=NeedsRecovery(body.position,anchor);
            bool floor=HasFloor(body.position,ZoneSystem.instance.m_solidRayMask);
            if(displaced || !floor)
            {
                Freeze();
                if(IsInterior(anchor))
                {
                    body.position=anchor; transform.position=anchor;
                    var zdo=character.m_nview.GetZDO();
                    zdo.Set(AnchorKey,anchor); zdo.SetPosition(anchor);
                    zdo.Set(ZDOVars.s_bodyVelHash,Vector3.zero);
                    floor=HasFloor(anchor,ZoneSystem.instance.m_solidRayMask);
                }
                if(displaced)global::Overhaul.Utility.Log.LogInfo("Dungeon : gardien replace dans sa salle : "+name+" -> "+anchor);
            }
            if(floor)Release();
        }
        private void OnDestroy(){if(body)Release();}
    }
    [HarmonyPatch(typeof(Character), "Awake")]
    internal static class BossLoadPhysicsPatch
    {
        private static void Postfix(Character __instance)
        {
            if (__instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.GetZDO().GetBool(BossEncounter.BossKey, false))
                BossLoadedGuard.Hold(__instance);
        }
    }
    [HarmonyPatch(typeof(Character), "CustomFixedUpdate")]
    internal static class BossWaitForFloorPatch
    {
        private static bool Prefix(Character __instance)
        {
            var guard=__instance.GetComponent<BossLoadedGuard>();
            return !guard || !guard.Waiting;
        }
    }
}

