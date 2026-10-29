using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // The character tab's sheet of cumulated bonuses: sections (a header, then label / value rows on two columns)
    // built from the prefab's templates. The lines come from Provider (set by Overhaul, which knows the effects);
    // only the lines it returns are shown. Refreshed twice a second while visible; objects are reused.
    public class CharacterSheet : MonoBehaviour
    {
        public class Line
        {
            public string Label, Value;
            // 1 = bonus (green), -1 = malus (red), 0 = plain value.
            public int Sign;
            // Optional detail shown when hovering the row (where a total comes from).
            public string Tooltip;
        }

        public class Section
        {
            public string Title;
            public readonly List<Line> Lines = new List<Line>();
        }

        public static Func<List<Section>> Provider;

        public RectTransform Content;
        // Inactive templates, children of the sheet: a section header (Text "Name"), a two-column grid, a row
        // (Texts "Label" and "Value", a UITooltip).
        public GameObject HeaderTemplate;
        public GameObject GridTemplate;
        public GameObject RowTemplate;
        public Color Positive = new Color(0.55f, 0.85f, 0.45f);
        public Color Negative = new Color(0.95f, 0.45f, 0.4f);
        public Color Neutral = new Color(0.92f, 0.88f, 0.8f);

        private const float RefreshSeconds = 0.5f;
        private readonly List<GameObject> _headers = new List<GameObject>();
        private readonly List<GameObject> _grids = new List<GameObject>();
        private readonly List<List<GameObject>> _rows = new List<List<GameObject>>();
        private float _nextRefresh;

        public void OnEnable() => _nextRefresh = 0;

        public void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            List<Section> sections = null;
            try { sections = Provider?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[Overhaul] Character sheet: " + e.Message); }
            Show(sections ?? new List<Section>());
        }

        public void Show(List<Section> sections)
        {
            int shown = 0;
            foreach (var section in sections)
            {
                if (section.Lines.Count == 0) continue;
                if (shown == _headers.Count)
                {
                    _headers.Add(Instantiate(HeaderTemplate, Content, false));
                    _grids.Add(Instantiate(GridTemplate, Content, false));
                    _rows.Add(new List<GameObject>());
                }
                var header = _headers[shown];
                var grid = _grids[shown];
                header.SetActive(true);
                grid.SetActive(true);
                header.transform.SetSiblingIndex(shown * 2);
                grid.transform.SetSiblingIndex(shown * 2 + 1);
                header.transform.Find("Name").GetComponent<Text>().text = section.Title;

                var rows = _rows[shown];
                for (int i = 0; i < section.Lines.Count; i++)
                {
                    if (i == rows.Count) rows.Add(Instantiate(RowTemplate, grid.transform, false));
                    var row = rows[i];
                    row.SetActive(true);
                    var line = section.Lines[i];
                    row.transform.Find("Label").GetComponent<Text>().text = line.Label;
                    var value = row.transform.Find("Value").GetComponent<Text>();
                    value.text = line.Value;
                    value.color = line.Sign > 0 ? Positive : line.Sign < 0 ? Negative : Neutral;
                    var tooltip = row.GetComponent<UITooltip>();
                    if (tooltip)
                    {
                        tooltip.m_topic = line.Label;
                        tooltip.m_text = line.Tooltip ?? "";
                        tooltip.enabled = !string.IsNullOrEmpty(line.Tooltip);
                    }
                }
                for (int i = section.Lines.Count; i < rows.Count; i++) rows[i].SetActive(false);
                shown++;
            }
            for (int i = shown; i < _headers.Count; i++)
            {
                _headers[i].SetActive(false);
                _grids[i].SetActive(false);
            }
        }
    }
}
