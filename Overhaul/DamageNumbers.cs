using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Overhaul
{
    // Damage numbers in the world, Overhaul's look (OverhaulDamageText, OverhaulLab): bigger, Norse bold with an outline,
    // a pop as they appear, then the game's rise and fade. One colour per kind of hit, whatever the element: cream normal,
    // yellow weakness, grey resisted or immune, golden and bigger for a critical (it shakes and stays longer), red damage
    // taken (falling), green heal.
    internal static class DamageNumbers
    {
        // Appended to a critical hit's text (Leveling.CriticalHits): every client draws it as a critical.
        internal const string CriticalMark = "⁣";
        const int Near = 30, Far = 22;
        static readonly Color Cream = new Color(1f, .93f, .82f), Yellow = new Color(1f, .85f, .25f), Grey = new Color(.68f, .66f, .63f),
            Red = new Color(1f, .3f, .25f), Green = new Color(.45f, 1f, .5f);
        static readonly VertexGradient Gold = new VertexGradient(new Color(1f, .95f, .55f), new Color(1f, .95f, .55f), new Color(1f, .55f, .1f), new Color(1f, .55f, .1f));

        sealed class State { internal bool Critical, Taken; }
        static readonly Dictionary<DamageText.WorldTextInstance, State> states = new Dictionary<DamageText.WorldTextInstance, State>();

        [HarmonyPatch(typeof(DamageText), "Awake")]
        private static class Setup
        {
            private static void Postfix(DamageText __instance)
            {
                var prefab = Auga.Auga.Assets.OverhaulDamageText;
                if (!prefab) return;
                __instance.m_worldTextBase = prefab;
                __instance.m_largeFontSize = Near; __instance.m_smallFontSize = Far;
            }
        }

        [HarmonyPatch(typeof(DamageText), "AddInworldText")]
        private static class Style
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(DamageText __instance, DamageText.TextType type, bool mySelf)
            {
                if (__instance.m_worldTexts.Count == 0) return;
                var text = __instance.m_worldTexts[__instance.m_worldTexts.Count - 1];
                if (states.ContainsKey(text) || !text.m_textField) return;
                var field = text.m_textField;
                var state = new State { Critical = field.text.Contains(CriticalMark), Taken = mySelf && type <= DamageText.TextType.Immune };
                field.text = field.text.Replace(CriticalMark, "");
                field.enableVertexGradient = false;
                if (state.Taken) field.color = field.text == "0" ? Grey : Red;
                else if (state.Critical)
                {
                    field.color = Color.white; field.enableVertexGradient = true; field.colorGradient = Gold;
                    field.fontSize *= 1.6f; text.m_duration *= 1.5f;
                }
                else switch (type)
                {
                    case DamageText.TextType.Normal: field.color = Cream; break;
                    case DamageText.TextType.Weak: field.color = Yellow; break;
                    case DamageText.TextType.Resistant: case DamageText.TextType.Immune: field.color = Grey; break;
                    case DamageText.TextType.Heal: field.color = Green; break;
                }
                text.m_gui.transform.localScale = Vector3.one * .4f;
                states[text] = state;
            }
        }

        // After the game moved and faded them: the pop, the critical's shake, damage taken falling instead of rising.
        [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
        private static class Motion
        {
            private static void Postfix(DamageText __instance, float dt)
            {
                foreach (var text in __instance.m_worldTexts)
                {
                    if (!states.TryGetValue(text, out var state) || !text.m_gui) continue;
                    float age = text.m_timer;
                    float pop = age < .08f ? Mathf.Lerp(.4f, 1.35f, age / .08f) : age < .18f ? Mathf.Lerp(1.35f, 1f, (age - .08f) / .1f) : 1f;
                    text.m_gui.transform.localScale = Vector3.one * pop;
                    if (state.Taken) text.m_worldPos.y -= 2 * dt;
                    if (state.Critical && age < .25f)
                        text.m_gui.transform.position += new Vector3(Mathf.Sin(age * 90) * 4, Mathf.Cos(age * 70) * 3, 0);
                }
                if (states.Count > __instance.m_worldTexts.Count)
                {
                    var gone = new List<DamageText.WorldTextInstance>();
                    foreach (var text in states.Keys) if (!__instance.m_worldTexts.Contains(text)) gone.Add(text);
                    foreach (var text in gone) states.Remove(text);
                }
            }
        }
    }
}
