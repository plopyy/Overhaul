using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Leveling
{
    public sealed class LevelingWindow : MonoBehaviour
    {
        internal static LevelingWindow Current;
        private Dictionary<string,int> draft;
        private Dictionary<string,int> committedSnapshot;
        private OverhaulCharacter state;
        public static bool HasEmbeddedPanel => Current;
        public static void MountAuga(GameObject root)
        {
            var window=root.GetComponent<LevelingWindow>()??root.AddComponent<LevelingWindow>();
            Current=window;window.Bind();
            if(root.activeInHierarchy)window.OnEnable();
        }
        private void OnEnable()
        {
            if(!Player.m_localPlayer)return;
            state=OverhaulCharacter.Get(Player.m_localPlayer);chosen=null;Refresh(true);
        }
        private void OnDisable(){draft=null;chosen=null;}
        private string chosen;
        private float nextRefresh;
        private Transform FindUi(string path)
        {
            var direct=transform.Find(path);if(direct)return direct;
            int split=path.LastIndexOf('/');var parent=transform.Find(path.Substring(0,split));
            return parent.GetComponentsInChildren<Transform>(true).First(t=>t.name==path.Substring(split+1));
        }
        private sealed class UiLabel
        {
            internal Transform Node;
            public string text {get=>Node.GetComponent<Text>()?Node.GetComponent<Text>().text:Node.GetComponent<TMPro.TMP_Text>().text;
                set{var legacy=Node.GetComponent<Text>();if(legacy)legacy.text=value;else Node.GetComponent<TMPro.TMP_Text>().text=value;}}
        }
        private UiLabel Label(string path)=>new UiLabel{Node=FindUi(path)};
        private Button Button(string path)=>transform.Find(path).GetComponent<Button>();
        private string T(string id)=>LevelingText.Get(id);
        internal static bool Visible=>Current && Current.gameObject.activeInHierarchy;
        internal static void RefreshCurrent(){if(Visible)Current.Refresh(false);}
        private void Bind()
        {
            Button("Footer/Validate").onClick.AddListener(()=>LevelingNetwork.Action("allocate",new Dictionary<string,int>(draft)));
            Button("Footer/Reset").onClick.AddListener(()=>{chosen=null;LevelingNetwork.Action("reset");});
            Button("Passives/Confirm").onClick.AddListener(()=>LevelingNetwork.Action("passive",null,chosen));
            for(int i=0;i<LevelingConfig.StatIds.Length;i++)
            {
                string id=LevelingConfig.StatIds[i],path="Progression/Stats/"+id;
                Button(path+"/Plus").onClick.AddListener(()=>Change(id,1));
                Button(path+"/Minus").onClick.AddListener(()=>Change(id,-1));
                var target=transform.Find(path).gameObject;
                var tooltip=target.GetComponent<UITooltip>()??target.AddComponent<UITooltip>();
                tooltip.Set(T("stat_"+id),T("tip_"+id),(RectTransform)transform,Vector2.zero);
            }
            foreach(string id in LevelingConfig.Passives)
            {
                Button("Passives/"+id).onClick.AddListener(()=>{chosen=id;Refresh(false);});
                if(!Button("Passives/"+id).GetComponent<UITooltip>())Button("Passives/"+id).gameObject.AddComponent<UITooltip>();
            }
        }
        private void Change(string id,int delta)
        {
            if(state==null || !state.Ready)return;
            Refresh(false);
            int committed=state.Data.AllocatedStats.TryGetValue(id,out int rank)?rank:0;
            int old=draft.TryGetValue(id,out rank)?rank:0;
            bool shift=Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift);
            bool control=Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl);
            delta=ClickDelta(delta,shift,control,old,committed,LevelingConfig.Current.Stats[id].MaxRank,
                LevelingSystem.Budget(state.Level)-draft.Values.Sum());
            draft[id]=old+delta;
            if(!LevelingSystem.ValidAllocation(draft,LevelingSystem.Budget(state.Level)))draft[id]=old;
            Refresh(false);
        }
        internal static int ClickDelta(int direction,bool shift,bool control,int rank,int committed,int maxRank,int free)
        {
            int requested=shift?int.MaxValue:control?5:1;
            int available=direction>0?Math.Max(0,Math.Min(free,maxRank-rank)):Math.Max(0,rank-committed);
            int amount=Math.Min(requested,available);
            return direction>0?amount:-amount;
        }
        internal void Close(){gameObject.SetActive(false);draft=null;chosen=null;}
        private void Update()
        {
            if(Player.m_localPlayer && state!=OverhaulCharacter.Get(Player.m_localPlayer))OnEnable();
            if(Time.unscaledTime>=nextRefresh){nextRefresh=Time.unscaledTime+.25f;Refresh(false);}
        }
        private string FormatStat(string id,double value)=>"+"+((id=="carry"||id.StartsWith("element_"))?value:value*100).ToString("0.###",System.Globalization.CultureInfo.CurrentCulture)+((id=="carry"||id.StartsWith("element_"))?"":" %");
        private void Refresh(bool discard)
        {
            if(state==null)return;
            bool allocationChanged=committedSnapshot==null || committedSnapshot.Count!=state.Data.AllocatedStats.Count || committedSnapshot.Any(p=>!state.Data.AllocatedStats.TryGetValue(p.Key,out int rank)||rank!=p.Value);
            if(discard || draft==null || allocationChanged || !LevelingSystem.ValidAllocation(draft,LevelingSystem.Budget(state.Level)))draft=new Dictionary<string,int>(state.Data.AllocatedStats);
            committedSnapshot=new Dictionary<string,int>(state.Data.AllocatedStats);
            Label("Progression/Summary/LevelCaption").text=T("overhaul_level");Label("Progression/Summary/Level").text=state.Level.ToString();
            Label("Progression/Summary/XP").text=state.Level>=LevelingConfig.Current.MaxLevel?T("max_level"):state.CurrentExperience.ToString("N0")+" / "+LevelingSystem.GetExperienceToNextLevel(state.Level).ToString("N0")+" EXP";
            var fill=(RectTransform)transform.Find("Progression/Summary/Bar/Fill");
            float ratio=state.Level>=LevelingConfig.Current.MaxLevel?1:(float)((double)state.CurrentExperience/LevelingSystem.GetExperienceToNextLevel(state.Level));
            fill.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,((RectTransform)fill.parent).rect.width*ratio);
            int free=LevelingSystem.Budget(state.Level)-draft.Values.Sum();
            Label("Progression/Summary/Points").text=free+"\n"+T("available");
            Label("Progression/Summary/Help").text=T("allocation_help").Replace("\n\n"," ");
            foreach(string id in LevelingConfig.StatIds)
            {
                string path="Progression/Stats/"+id;int old=state.Data.AllocatedStats.TryGetValue(id,out int n)?n:0;int rank=draft.TryGetValue(id,out n)?n:0;
                var radial=FindUi(path+"/RadialLevel");if(radial)radial.GetComponent<Image>().fillAmount=.75f*(float)rank/LevelingConfig.Current.Stats[id].MaxRank;
                Label(path+"/Name").text=T("short_"+id);
                Label(path+"/Rank").text=(rank==old?old.ToString():old+" → "+rank)+" / "+LevelingConfig.Current.Stats[id].MaxRank;
                Label(path+"/Bonus").text=(id=="carry" || id.StartsWith("element_"))?"+"+(rank*LevelingConfig.Current.Stats[id].PerPoint).ToString("0.#"):"+"+(100*rank*LevelingConfig.Current.Stats[id].PerPoint).ToString("0.#")+" %";
                transform.Find(path).GetComponent<UITooltip>().Set(T("stat_"+id),string.Format(T("stat_tooltip"),T("effect_"+id),FormatStat(id,LevelingConfig.Current.Stats[id].PerPoint),FormatStat(id,rank*LevelingConfig.Current.Stats[id].PerPoint),LevelingConfig.Current.Stats[id].MaxRank),(RectTransform)transform,Vector2.zero);
                Button(path+"/Minus").interactable=state.Ready && rank>old;
                var trial=new Dictionary<string,int>(draft);trial[id]=rank+1;
                Button(path+"/Plus").interactable=state.Ready && LevelingSystem.ValidAllocation(trial,LevelingSystem.Budget(state.Level));
            }
            Label("Footer/Status").text=!state.Ready?T("sync"):state.InCombat?T("combat_lock"):T("pending");
            Label("Footer/Validate/Text").text=T("short_validate");Label("Footer/Reset/Text").text=T("short_reset");
            Button("Footer/Validate").interactable=state.Ready && !state.InCombat && draft.Values.Sum()>state.Data.AllocatedStats.Values.Sum();
            Button("Footer/Reset").interactable=state.Ready && !state.InCombat && state.Data.AllocatedStats.Values.Sum()>0;
            Label("Passives/Title").text=T("passives");
            long remaining=Math.Max(0,state.Data.PassiveChangeAfterUtc-DateTime.UtcNow.Ticks);
            Label("Passives/Status").text=state.Level<LevelingConfig.Current.PassiveLevel?string.Format(T("passive_locked"),LevelingConfig.Current.PassiveLevel):remaining>0?T("cooldown")+" "+TimeSpan.FromTicks(remaining).ToString(@"hh\:mm\:ss"):T("passive_ready");
            foreach(string id in LevelingConfig.Passives)
            {
                Label("Passives/"+id+"/Name").text=(state.Data.Passive==id?"● ":chosen==id?"○ ":"")+T("passive_"+id);
                string passiveTip=id=="vitality"?string.Format(T("passive_tip_"+id),LevelingConfig.Current.VitalityHealth,LevelingConfig.Current.VitalityRegen):T("passive_tip_"+id);
                Label("Passives/"+id+"/Description").text=(id=="vitality"?string.Format(T("passive_card_"+id),LevelingConfig.Current.VitalityHealth,LevelingConfig.Current.VitalityRegen):T("passive_card_"+id));
                Button("Passives/"+id).GetComponent<UITooltip>().Set(T("passive_"+id),passiveTip,(RectTransform)transform,Vector2.zero);
                Button("Passives/"+id).interactable=state.Ready && !state.InCombat && remaining==0 && state.Level>=LevelingConfig.Current.PassiveLevel;
            }
            Label("Passives/Confirm/Text").text=T("short_choose");
            Button("Passives/Confirm").interactable=state.Ready && !state.InCombat && remaining==0 && state.Level>=LevelingConfig.Current.PassiveLevel && chosen!=null && chosen!=state.Data.Passive;
        }
    }
}
