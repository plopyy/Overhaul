using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class SkillsPanelSkillController : MonoBehaviour
    {
        public Skills.SkillType SkillType = Skills.SkillType.None;
        public Image Icon;
        public Image ProgressBarLevel;
        public Image ProgressBarAccumulator;
        public Text NameText;
        public Text LevelText;
        public float StartFill;
        public float EndFill;

        protected SkillTooltip _skillTooltip;

        public void Awake()
        {
            _skillTooltip = GetComponent<SkillTooltip>();
        }

        public void UpdateSkill()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            var skills = player.GetSkills();
            var skillData = skills.GetSkillList().FirstOrDefault(x => x != null && x.m_info != null && x.m_info.m_skill == SkillType);
            if (skillData != null)
            {
                if (!_skillTooltip) _skillTooltip = GetComponent<SkillTooltip>();
                _skillTooltip.Skill = skillData;

                Icon.sprite = skillData.m_info.m_icon;
                NameText.text = Localization.instance.Localize("$skill_" + SkillType.ToString().ToLower());
                LevelText.text = Localization.instance.Localize("$level") + $" {Mathf.FloorToInt(skillData.m_level)}";
                float bonus = skills.GetSkillLevel(SkillType) - Mathf.Floor(skillData.m_level);
                if (Mathf.Abs(bonus) > .001f)
                    LevelText.text += $" <color=orange>({bonus:+0;-0})</color>";
                ProgressBarLevel.fillAmount = Mathf.Lerp(StartFill, EndFill, skillData.m_level / 100f);
                ProgressBarAccumulator.fillAmount = Mathf.Lerp(StartFill, EndFill, skillData.GetLevelPercentage());
            }

            var uiToolTip = GetComponent<UITooltip>();
            if (uiToolTip && _skillTooltip.Skill != null) uiToolTip.m_text = _skillTooltip.Skill.m_info.m_description;
        }

        public virtual void SetActive(bool active)
        {
            gameObject.SetActive(active);
            if (active)
            {
                UpdateSkill();
            }
        }
    }
}
