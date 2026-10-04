using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameStaffGuardView
    {
        private const string Rpc="Overhaul_StaffGuardView";
        internal static void Publish(Player player,SE_StaffGuard guard)
        {if(player&&player.m_nview&&player.m_nview.IsValid())player.m_nview.InvokeRPC(ZNetView.Everybody,Rpc,guard?.m_guardActive==true,guard?.m_guardStaff??0);}
        [HarmonyPatch(typeof(Player),nameof(Player.Awake))]
        private static class Register
        {
            private static void Postfix(Player __instance)
            {
                if(!__instance.m_nview||!__instance.m_nview.IsValid())return;
                var view=__instance.gameObject.AddComponent<GuardVisual>();view.Player=__instance;
                __instance.m_nview.Register<bool,int>(Rpc,view.Receive);
            }
        }
        public sealed class GuardVisual:MonoBehaviour
        {
            internal Player Player;private GameObject visual;private int staff;private double expires;
            internal void Receive(long sender,bool active,int prefab)
            {
                if(!Player||!ZNet.instance||!PlayerSessionGame.Managed&&!GameMovementRuntime.Managed(Player))return;
                long server=ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid??0;
                if(sender!=server)return;
                if(!active){Clear();return;}
                expires=Time.timeAsDouble+2;
                if(visual&&staff==prefab)return;
                Clear();if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)return;
                var item=ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>();if(!item||!GameStaffGuardRuntime.IsStaff(item.m_itemData))return;
                staff=prefab;visual=StaffShieldVfx.Create(Player,item.m_itemData);
            }
            private void LateUpdate(){if(visual&&(!Player||Player.IsDead()||Time.timeAsDouble>=expires))Clear();}
            private void Clear(){if(visual)Object.Destroy(visual);visual=null;staff=0;}
            private void OnDestroy()=>Clear();
        }
    }
}
