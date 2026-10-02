using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public sealed class ClassSkillDefinition
    {
        public string ClassId,Id,Name,Description;
        public int Tier,MaxRank;
        public int[] RequiredLevel;
        public string[] RequiredSkill;
    }
    // Temporary, local demonstration. No character data, experience or combat effects are changed.
    public sealed class ClassPreviewPanel : MonoBehaviour
    {
        public Button[] Nodes;
        public TMP_Text[] States;
        public Image[] Icons;
        public Image[] Links;
        public int[] LinkTargets;
        public Image Selection, DetailIcon;
        public Image[] LearnedFrames;
        public ScrollRect TreeScroll;
        public TMP_Text Points, DetailTitle, DetailBody, Requirements, Status, LearnLabel;
        public Button Learn, Reset, Close;
        public Action CloseRequested;
        public bool[] Learned=new bool[0];
        public int[] Ranks=new int[0];
        public ClassSkillDefinition[] Skills=new ClassSkillDefinition[0];
        public Button NodeTemplate; public TMP_Text NameTemplate, StateTemplate; public Image LinkTemplate;
        public Sprite[] DemoIcons; public Transform Templates;
        public const int PreviewLevel=6;
        public int Selected {get;private set;}
        public int FreePoints=>PreviewLevel-Ranks.Sum();
        static string T(string key)=>Localization.instance.Localize("$class_demo_"+key);
        static string Local(string key)=>Localization.instance.Localize("$"+key.TrimStart('$'));
        public void PrepareTemplates()
        {
            if(NodeTemplate)return;
            Templates=new GameObject("Templates",typeof(RectTransform)).transform;Templates.SetParent(transform,false);Templates.gameObject.SetActive(false);
            DemoIcons=Icons.Select(i=>i.sprite).ToArray();
            NodeTemplate=Instantiate(Nodes[0],Templates,false);NodeTemplate.name="SkillTemplate";
            NameTemplate=Instantiate(TreeScroll.content.Find("Name0").GetComponent<TMP_Text>(),Templates,false);
            StateTemplate=Instantiate(States[0],Templates,false);LinkTemplate=Instantiate(Links[0],Templates,false);
            Selection.transform.SetParent(Templates,false);
            foreach(Transform child in TreeScroll.content.Cast<Transform>().ToArray())DestroyImmediate(child.gameObject);
            Nodes=new Button[0];States=new TMP_Text[0];Icons=new Image[0];LearnedFrames=new Image[0];Links=new Image[0];LinkTargets=new int[0];
        }
        static void Place(RectTransform r,float x,float y,float w,float h,bool center=false){r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=center?new Vector2(.5f,.5f):new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);}
        public void Generate(ClassSkillDefinition[] definitions,string classId="Guardian")
        {
            PrepareTemplates();Selection.transform.SetParent(Templates,false);
            foreach(Transform child in TreeScroll.content.Cast<Transform>().ToArray()){child.gameObject.SetActive(false);if(Application.isPlaying)Destroy(child.gameObject);else DestroyImmediate(child.gameObject);}
            Skills=definitions.Where(s=>s.ClassId==classId).OrderBy(s=>s.Tier).ToArray();
            Nodes=new Button[Skills.Length];States=new TMP_Text[Skills.Length];Icons=new Image[Skills.Length];LearnedFrames=new Image[Skills.Length];Ranks=new int[Skills.Length];Learned=new bool[Skills.Length];
            var positions=new Vector2[Skills.Length];int rows=Skills.Length==0?1:Skills.GroupBy(s=>s.Tier).Max(g=>g.Count());
            float height=Mathf.Max(TreeScroll.viewport.rect.height,rows*100+20),width=Mathf.Max(TreeScroll.viewport.rect.width,(Skills.Length==0?1:Skills.Max(s=>s.Tier))*180+10);
            TreeScroll.content.sizeDelta=new Vector2(width,height);TreeScroll.vertical=height>TreeScroll.viewport.rect.height;
            if(TreeScroll.horizontalScrollbar)TreeScroll.horizontalScrollbar.gameObject.SetActive(width>TreeScroll.viewport.rect.width);
            foreach(var column in Skills.Select((s,i)=>i).GroupBy(i=>Skills[i].Tier)){
                int row=0;foreach(int i in column)positions[i]=new Vector2(95+(Skills[i].Tier-1)*180,column.Count()==1?height*.5f:55+(height-110)*(row++)/(column.Count()-1));
            }
            var links=new System.Collections.Generic.List<Image>();var targets=new System.Collections.Generic.List<int>();
            for(int i=0;i<Skills.Length;i++)foreach(int parent in ParentIndices(i)){
                Vector2 from=positions[parent]+Vector2.right*30,to=positions[i]-Vector2.right*30;float middle=(from.x+to.x)*.5f;
                var points=new[]{from,new Vector2(middle,from.y),new Vector2(middle,to.y),to};
                for(int segment=0;segment<3;segment++){var line=Instantiate(LinkTemplate,TreeScroll.content,false);line.gameObject.SetActive(true);var delta=points[segment+1]-points[segment];Place(line.rectTransform,points[segment].x,points[segment].y,delta.magnitude,2);line.rectTransform.pivot=new Vector2(0,.5f);line.rectTransform.localRotation=Quaternion.Euler(0,0,-Mathf.Atan2(delta.y,delta.x)*Mathf.Rad2Deg);links.Add(line);targets.Add(i);}
            }
            Links=links.ToArray();LinkTargets=targets.ToArray();
            string[] demoIds={"Oath","Anchor","Riposte","IronWall","ProtectiveWatch","PerfectCounter","LivingBulwark","ThunderStrike","EternalOath"};
            for(int i=0;i<Skills.Length;i++){
                int n=i;var pos=positions[i];var node=Instantiate(NodeTemplate,TreeScroll.content,false);node.name=Skills[i].Id;node.gameObject.SetActive(true);Place((RectTransform)node.transform,pos.x,pos.y,54,54,true);Nodes[i]=node;
                node.onClick.RemoveAllListeners();node.onClick.AddListener(()=>Select(n));Icons[i]=node.transform.Find("icon").GetComponent<Image>();int icon=Array.IndexOf(demoIds,Skills[i].Id);Icons[i].sprite=DemoIcons[icon>=0?icon:i%DemoIcons.Length];Icons[i].rectTransform.localScale*=54f/66f;
                LearnedFrames[i]=node.transform.Find("LearnedFrame").GetComponent<Image>();
            }
            Learn.onClick.RemoveAllListeners();Learn.onClick.AddListener(Unlock);Reset.onClick.RemoveAllListeners();Reset.onClick.AddListener(ResetPreview);Close.onClick.RemoveAllListeners();Close.onClick.AddListener(()=>CloseRequested?.Invoke());
            ResetPreview();
        }
        public void Initialize(){if(Skills.Length>0)ResetPreview();}
        int[] ParentIndices(int n)=>Skills[n].RequiredSkill.Select(id=>Array.FindIndex(Skills,s=>s.Id==id)).ToArray();
        public bool CanLearn(int n)=>n>=0&&n<Skills.Length&&Ranks[n]<Skills[n].MaxRank&&FreePoints>0&&Skills[n].RequiredLevel[Ranks[n]]<=PreviewLevel&&ParentIndices(n).All(p=>p>=0&&Ranks[p]>0);
        public void Select(int n){if(Skills.Length==0)return;Selected=Mathf.Clamp(n,0,Skills.Length-1);Refresh();
            float width=TreeScroll.viewport.rect.width,total=TreeScroll.content.rect.width,left=-TreeScroll.content.anchoredPosition.x,x=((RectTransform)Nodes[Selected].transform).anchoredPosition.x;
            if(total>width&&(x-34<left||x+34>left+width))TreeScroll.horizontalNormalizedPosition=Mathf.Clamp01((x-width*.5f)/(total-width));
        }
        public void ResetPreview(){Array.Clear(Ranks,0,Ranks.Length);Array.Clear(Learned,0,Learned.Length);
            // Keep the familiar sample allocations by stable ID, respecting edited prerequisites.
            foreach(var id in new[]{"Oath","Anchor","Riposte"}){int n=Array.FindIndex(Skills,s=>s.Id==id);if(CanLearn(n))Ranks[n]=1;}
            TreeScroll.StopMovement();TreeScroll.content.anchoredPosition=Vector2.zero;
            int selected=Array.FindIndex(Skills,s=>s.Id=="ProtectiveWatch");Select(Mathf.Max(0,selected));
            if(Skills.Length==0){Points.text=string.Format(T("points"),FreePoints);DetailTitle.text=DetailBody.text=Requirements.text=Status.text="";Learn.interactable=false;}
        }
        public void Unlock(){if(CanLearn(Selected)){Ranks[Selected]++;Refresh();}}
        string State(int i)=>(Ranks[i]>0?T("learned"):T(CanLearn(i)?"available":"locked"))+(Skills[i].MaxRank>1?" "+Ranks[i]+"/"+Skills[i].MaxRank:"");
        public void Refresh()
        {
            var gold=new Color(1,.73f,.22f);var pale=new Color(.86f,.81f,.68f);var dim=new Color(.44f,.42f,.38f);
            Points.text=string.Format(T("points"),FreePoints);
            for(int i=0;i<Nodes.Length;i++){
                Learned[i]=Ranks[i]>0;
                Icons[i].color=Learned[i]?Color.white:CanLearn(i)?new Color(1,1,1,.85f):new Color(.4f,.4f,.4f,.65f);
                LearnedFrames[i].color=Learned[i]?gold:CanLearn(i)?pale:dim;LearnedFrames[i].gameObject.SetActive(true);
            }
            for(int i=0;i<Links.Length;i++)Links[i].color=Learned[LinkTargets[i]]?gold:new Color(.52f,.46f,.32f,.6f);
            Selection.rectTransform.SetParent(Nodes[Selected].transform,false);Selection.gameObject.SetActive(true);Selection.rectTransform.SetAsLastSibling();Selection.color=new Color(.24f,.65f,1f);Selection.rectTransform.localScale=Vector3.one*1.12f;
            var skill=Skills[Selected];DetailIcon.sprite=Icons[Selected].sprite;DetailTitle.text=Local(skill.Name);DetailBody.text=Local(skill.Description);
            int next=Mathf.Min(Ranks[Selected],skill.MaxRank-1);
            Requirements.text=T("requires")+"\n"+string.Format(T("level"),skill.RequiredLevel[next])+"\n"+
                (skill.RequiredSkill.Length==0?T("no_parent"):string.Join("\n",ParentIndices(Selected).Select(p=>(Learned[p]?"<color=#e8b447>":"<color=#bfb5a3>")+Local(Skills[p].Name)+"</color>")))+"\n\n"+T("cost");
            Status.text=State(Selected);Learn.interactable=CanLearn(Selected);LearnLabel.text=T(Ranks[Selected]>=skill.MaxRank?"learned":"unlock");
        }
    }
}
