using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Overhaul.Leveling
{
    public sealed class ExperienceFeed : MonoBehaviour
    {
        private sealed class Line
        {
            internal TMP_Text Text;
            internal float Born, FromY, TargetY, MoveStarted, MoveDuration;
        }
        private readonly Queue<string> pending=new Queue<string>();
        private readonly List<Line> visible=new List<Line>();
        private TMP_FontAsset font;
        private static ExperienceFeed current;
        private Player owner;
        internal static void Reward(string name,int stars,long amount,int before,int after)
        {
            if(amount<=0)return;
            if(Hud.instance && Player.m_localPlayer)
            {
                if(!current || current.owner!=Player.m_localPlayer)
                {
                    if(current)Destroy(current.gameObject);
                    current=Create(Hud.instance.transform,Hud.instance.m_hoverName.font);
                    current.owner=Player.m_localPlayer;
                }
                string localized=Localization.instance!=null?Localization.instance.Localize(name??""):name??"";
                current.pending.Enqueue(localized+(stars>0?" ("+new string('*',Mathf.Min(stars,10))+")":"")+": +"+amount+" exp");
            }
            if(after>before && MessageHud.instance)
                for(int level=before+1;level<=after;level++)
                    MessageHud.instance.ShowBiomeFoundMsg(string.Format(LevelingText.Get("level_up"),level),true);
        }
        internal static ExperienceFeed Create(Transform parent,TMP_FontAsset font)
        {
            var go=new GameObject("OverhaulExperienceFeed",typeof(RectTransform));
            var rect=(RectTransform)go.transform;rect.SetParent(parent,false);
            rect.anchorMin=rect.anchorMax=new Vector2(0,.65f);rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=new Vector2(24,0);rect.sizeDelta=new Vector2(440,300);
            go.AddComponent<UnityEngine.UI.RectMask2D>();
            var feed=go.AddComponent<ExperienceFeed>();feed.font=font;return feed;
        }
        internal void Advance(float now)
        {
            // First sample ongoing movement, so a changed slot starts at its actual position.
            foreach(var line in visible)Position(line,now);
            for(int i=visible.Count-1;i>=0;i--)if(now-visible[i].Born>=4f)
            {visible[i].Text.gameObject.SetActive(false);Destroy(visible[i].Text.gameObject);visible.RemoveAt(i);}
            for(int i=0;i<visible.Count;i++)
            {
                var line=visible[i];float target=-28*i;
                if(line.TargetY==target)continue;
                line.FromY=line.Text.rectTransform.anchoredPosition.y;line.TargetY=target;line.MoveStarted=now;
                line.MoveDuration=Mathf.Max(.3f,1f-(now-line.Born));
            }
            while(visible.Count<10 && pending.Count>0)
            {
                var go=new GameObject("Experience",typeof(RectTransform));go.transform.SetParent(transform,false);
                var label=go.AddComponent<TextMeshProUGUI>();label.font=font;label.fontSize=22;
                label.color=new Color(1,.82f,.46f);label.richText=false;label.raycastTarget=false;
                label.text=pending.Dequeue();label.textWrappingMode=TextWrappingModes.NoWrap;label.overflowMode=TextOverflowModes.Ellipsis;
                var rect=label.rectTransform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.sizeDelta=new Vector2(440,28);
                // Keep row spacing even when several rewards arrive in the same frame.
                visible.Add(new Line{Text=label,Born=now,FromY=-300-28*visible.Count,TargetY=-28*visible.Count,MoveStarted=now,MoveDuration=1});
            }
            for(int i=0;i<visible.Count;i++)
            {
                var line=visible[i];float age=now-line.Born;
                line.Text.alpha=Mathf.Clamp01(Mathf.Min(age,4f-age));
                Position(line,now);
            }
        }
        private static void Position(Line line,float now)
        {
            float t=Mathf.SmoothStep(0,1,Mathf.Clamp01((now-line.MoveStarted)/line.MoveDuration));
            line.Text.rectTransform.anchoredPosition=new Vector2(0,Mathf.Lerp(line.FromY,line.TargetY,t));
        }
        private void Update()
        {
            if(this==current && (!owner || owner!=Player.m_localPlayer)){Destroy(gameObject);return;}
            Advance(Time.unscaledTime);
        }
    }
}
