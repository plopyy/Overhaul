using AugaUnity;
using HarmonyLib;
using UnityEngine;
namespace Auga
{
 [UiModule(UiModule.Vitals)]
 internal static class Vitals_Setup
 {
  private static bool Installed(Hud hud) => hud.transform.Find("hudroot/HealthBar") != null;
  [HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
  private static class StatusOverviewInstall
  {
   private static void Postfix(Minimap __instance)
   {
    if (!Hud.instance || !__instance.m_smallRoot) return;
    var source=Auga.Assets.Hud.transform.Find("hudroot/StatusOverview");
    if (!source) return;
    var overview=Object.Instantiate(source.gameObject,__instance.m_smallRoot.transform,false);
    overview.name="StatusOverview";
    var rect=(RectTransform)overview.transform;
    rect.anchorMin=rect.anchorMax=new Vector2(.5f,0);
    rect.pivot=new Vector2(.5f,1);
    rect.anchoredPosition=new Vector2(0,-8);
    if (__instance.m_biomeNameSmall) __instance.m_biomeNameSmall.gameObject.SetActive(false);
    __instance.m_biomeNameSmall=overview.transform.Find("Biome/Content").GetComponent<TMPro.TMP_Text>();
    overview.AddComponent<StatusOverviewClock>();
    var old=Hud.instance.transform.Find("hudroot/StatusEffects");
    if(old)old.gameObject.SetActive(false);
   }
  }
  [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateStatusEffects))]
  private static class StatusOverviewUpdate
  {
   private static bool Prefix() => !Minimap.instance || !Minimap.instance.m_smallRoot ||
       !Minimap.instance.m_smallRoot.transform.Find("StatusOverview");
  }
  [HarmonyPatch(typeof(Hud), nameof(Hud.Awake))]
  private static class Install
  {
   private static void Postfix(Hud __instance)
   {
    if (UiModules.Enabled(UiModule.Hud)) return;
    var parent=__instance.transform.Find("hudroot");
    var health=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/HealthBar").gameObject,parent,false);
    var stamina=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/StaminaBar").gameObject,parent,false);
    var eitr=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/EitrBar").gameObject,parent,false);
    health.name="HealthBar";stamina.name="StaminaBar";eitr.name="EitrBar";
    __instance.m_healthBarRoot.gameObject.SetActive(false);
    __instance.m_healthText.gameObject.SetActive(false);
    __instance.m_staminaBar2Root.gameObject.SetActive(false);
    __instance.m_eitrBarRoot.gameObject.SetActive(false);
    __instance.m_healthAnimator=health.GetComponent<Animator>();
    __instance.m_staminaAnimator=stamina.GetComponent<Animator>();
    __instance.m_eitrAnimator=eitr.GetComponent<Animator>();
        __instance.m_healthPanel.gameObject.SetActive(false);
    for(int i=0;i<3;i++) {
     var food=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/FoodPanel"+i).gameObject,parent,false);food.name="FoodPanel"+i;
    }
    var oldPower=__instance.m_gpRoot;
    var powerMaterial=__instance.m_gpIcon.material;
    var power=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/GuardianPower").gameObject,parent,false);power.name="GuardianPower";
    __instance.m_gpRoot=(RectTransform)power.transform;
    __instance.m_gpName=power.transform.Find("Name").GetComponent<TMPro.TMP_Text>();
    __instance.m_gpCooldown=power.transform.Find("TimeText").GetComponent<TMPro.TMP_Text>();
    __instance.m_gpIcon=power.transform.Find("Icon").GetComponent<UnityEngine.UI.Image>();
    __instance.m_gpIcon.material=powerMaterial;
    oldPower.gameObject.SetActive(false);
    power.SetActive(false);
    Auga.UpdateStatBars();
   }
  }
  [HarmonyPatch(typeof(Hud), "UpdateFood")]
  private static class Food { private static bool Prefix(Hud __instance) => !Installed(__instance); }
  [HarmonyPatch(typeof(Hud), "UpdateHealth")]
  private static class Health { private static bool Prefix(Hud __instance) => !Installed(__instance); }
  [HarmonyPatch(typeof(Hud), "UpdateStamina")]
  private static class Stamina { private static bool Prefix(Hud __instance) => !Installed(__instance); }
  [HarmonyPatch(typeof(Hud), "UpdateEitr")]
  private static class Eitr { private static bool Prefix(Hud __instance) => !Installed(__instance); }
 }
 internal sealed class StatusOverviewClock : MonoBehaviour
 {
  private TMPro.TMP_Text label;
  private int lastMinute=-1;
  private void Awake() { label=transform.Find("Clock").GetComponent<TMPro.TMP_Text>(); }
  private void Update()
  {
   if (!EnvMan.instance || !label) return;
   int minute=Mathf.FloorToInt(Mathf.Repeat(EnvMan.instance.GetDayFraction(),1f)*1440f);
   if(minute==lastMinute)return;
   lastMinute=minute;
   label.text=(minute/60).ToString("00")+":"+(minute%60).ToString("00");
  }
 }
}
