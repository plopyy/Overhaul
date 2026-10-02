using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class PvpToggle : MonoBehaviour
    {
        public GameObject Enabled;
        public GameObject Disabled;
        public GameObject Inactive;
        public Text PleaseWaitText;

        public void Awake()
        {
            Update();
        }

        public virtual void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            
            var canTogglePvp = player.CanSwitchPVP();
            Inactive.SetActive(!canTogglePvp);
            Enabled.SetActive(canTogglePvp && player.IsPVPEnabled());
            Disabled.SetActive(canTogglePvp && !player.IsPVPEnabled());
            if (Inactive.activeSelf)
            {
                PleaseWaitText.text = $"{Localization.instance.Localize("$pvp_wait_text")}: {Mathf.Max(0f, 10f - (float)typeof(Humanoid).GetField("m_lastCombatTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(player)):0}";
            }
        }

        public void EnablePvp()
        {
            Player.m_localPlayer.SetPVP(true);
        }

        public void DisablePvp()
        {
            Player.m_localPlayer.SetPVP(false);
        }
    }
}
