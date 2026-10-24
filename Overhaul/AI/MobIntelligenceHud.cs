using HarmonyLib;
using UnityEngine;
using AugaUnity;
using UnityEngine.UI;

namespace Overhaul.AI
{
    [HarmonyPatch(typeof(EnemyHud),"UpdateHuds")]
    internal static class MobIntelligenceHud
    {
        private static void Postfix(EnemyHud __instance)
        {foreach(var hud in __instance.m_huds.Values)Update(hud);}
        internal static void Update(EnemyHud.HudData hud)
        {
            if(!hud.m_gui||!hud.m_character||hud.m_character is Player)return;
            var health=hud.m_gui.transform.Find("Health") as RectTransform;if(!health)return;
            var icon=health.Find("OverhaulIntelligence");
            // Only display a replicated decision, never a locally guessed roll on a remote mob.
            var view=hud.m_character.m_nview;
            if(view&&view.IsValid()&&view.IsOwner())MobBehaviorConfig.Rule(hud.m_character);
            int value=view&&view.IsValid()?view.GetZDO().GetInt(MobBehaviorConfig.IntelligenceKey,-1):-1;
            bool show=!MobBehaviorConfig.Excluded(hud.m_character)&&(value==(int)Intelligence.Dumb||value==(int)Intelligence.Smart);
            if(!icon&&!show)return;
            if(!icon)
            {
                var go=new GameObject("OverhaulIntelligence",typeof(RectTransform),typeof(CanvasRenderer),typeof(IntelligenceFace));
                go.layer=health.gameObject.layer;go.transform.SetParent(health,false);icon=go.transform;
                var rect=(RectTransform)icon;rect.anchorMin=rect.anchorMax=new Vector2(1,.5f);rect.pivot=new Vector2(0,.5f);
                rect.sizeDelta=new Vector2(20,20);rect.anchoredPosition=new Vector2(6,0);
                go.GetComponent<IntelligenceFace>().raycastTarget=false;
            }
            var face=icon.GetComponent<IntelligenceFace>();bool smart=value==(int)Intelligence.Smart;
            if(face.Smart!=smart){face.Smart=smart;face.SetVerticesDirty();}
            icon.gameObject.SetActive(show);
        }
    }
}
