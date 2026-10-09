using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AugaUnity
{
    // A tooltip row: a label and an optional value for the right column.
    public sealed class TooltipRow
    {
        public string Left, Right;
        public TooltipRow(string left, string right = null) { Left = left; Right = right; }
    }

    // Rows in a native two-column box, so values use exactly the regular value style. The right column
    // gets empty lines for every wrapped line of a label, so each value stays in front of its label.
    public sealed class TooltipRowAligner : MonoBehaviour
    {
        public List<TooltipRow> Rows;
        private TooltipTextBox box;
        private float width = -1;

        public static void Add(ComplexTooltip tooltip, List<TooltipRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            var box = tooltip.AddTextBox(tooltip.TwoColumnTextBoxPrefab);
            box.gameObject.AddComponent<TooltipRowAligner>().Rows = rows;
            Fill(box, rows);
        }

        private void LateUpdate()
        {
            if (!box) box = GetComponent<TooltipTextBox>();
            float current = box && box.Text ? box.Text.rectTransform.rect.width : 0;
            if (current <= 0 || Mathf.Approximately(current, width)) return;
            width = current;
            Fill(box, Rows);
        }

        public static void Fill(TooltipTextBox box, List<TooltipRow> rows)
        {
            box.Text.text = string.Join("\n", rows.Select(r => r.Left));
            box.RightColumnText.text = string.Join("\n", rows.Select(r => r.Right ?? ""));
            if (box.Text.rectTransform.rect.width <= 0) return;
            box.Text.ForceMeshUpdate();
            TMPro.TMP_TextInfo info = box.Text.textInfo;
            var right = new StringBuilder();
            int cursor = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                // A sprite is one character; every other tag is invisible.
                int length = Regex.Replace(Regex.Replace(rows[i].Left, "<sprite[^>]*>", "#"), "<[^>]+>", "").Length;
                int first = Mathf.Min(cursor, info.characterCount - 1), last = Mathf.Min(cursor + Mathf.Max(0, length - 1), info.characterCount - 1);
                int lines = first < 0 ? 1 : info.characterInfo[last].lineNumber - info.characterInfo[first].lineNumber + 1;
                if (i > 0) right.Append('\n');
                right.Append(rows[i].Right ?? "");
                for (int extra = 1; extra < lines; extra++) right.Append('\n');
                cursor += length + 1;
            }
            box.RightColumnText.text = right.ToString();
        }
    }
}
