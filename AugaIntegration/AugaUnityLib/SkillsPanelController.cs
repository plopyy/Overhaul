using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AugaUnity
{
    public class SkillsPanelController : MonoBehaviour
    {
        public GameObject SkillsContainer;
        public SkillsPanelSkillController SkillPrefab;

        protected readonly Dictionary<Skills.SkillType, SkillsPanelSkillController> _skills = new Dictionary<Skills.SkillType, SkillsPanelSkillController>();
        protected int _skillsCount;
        protected SkillsDialog _skillsDialog;

        public virtual void Start()
        {
            SkillPrefab.gameObject.SetActive(false);
            _skillsDialog = GetComponent<SkillsDialog>();
            UpdateSkillsDialog();
            InvokeRepeating(nameof(UpdateSkillsDialog), 0f, 1f);
        }

        private void UpdateSkillsDialog()
        {
            if (!isActiveAndEnabled)
                return;

            var player = Player.m_localPlayer;
            if (player != null)
            {
                UpdateSkills(player);
                
            }
        }

        public virtual void UpdateSkills(Player player)
        {
            var skills = player.GetSkills();

            var known = new HashSet<Skills.SkillType>(skills.GetSkillList().Where(x => x != null && x.m_info != null).Select(x => x.m_info.m_skill));
            foreach (var skillDef in skills.m_skills)
            {
                _skills.TryGetValue(skillDef.m_skill, out var currentSkillElement);

                if (known.Contains(skillDef.m_skill))
                {
                    if (currentSkillElement == null)
                    {
                        var effect = Instantiate(SkillPrefab, SkillsContainer.transform, false);
                        effect.SkillType = skillDef.m_skill;
                        _skills.Add(skillDef.m_skill, effect); effect.SetActive(true);
                    }
                    else
                    {
                        currentSkillElement.SetActive(true);
                    }
                }
                else if (currentSkillElement != null)
                {
                    currentSkillElement.SetActive(false);
                }
            }

            if (_skillsCount != _skills.Count)
            {
                _skillsCount = _skills.Count;
                SortSkillElements();
            }
        }

        public virtual void SortSkillElements()
        {
            var children = SkillsContainer.transform.Cast<Transform>().Select(x => x.GetComponent<SkillsPanelSkillController>()).ToList();
            children.Sort((a, b) => a.SkillType.CompareTo(b.SkillType));
            for (var i = 0; i < children.Count; ++i)
            {
                children[i].transform.SetSiblingIndex(i);
            }
        }
    }
}
