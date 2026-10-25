using System;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // A tip's looping clips in the compendium (Hugin tab): a sheet of frames (Columns × Rows, Count of them) shown one after
    // the other at Rate per second on Picture. Overhaul gives the clips of a tip by its title (Source); no clip hides the area.
    // A tip with several clips shows one at a time, the Previous and Next arrows (hidden otherwise) going round them.
    public class TipClip : MonoBehaviour
    {
        public RawImage Picture;
        public GameObject Previous, Next;

        public sealed class Clip
        {
            public Texture Sheet;
            public int Columns, Rows, Count;
            public float Rate = 15;
            // The tip's next clip.
            public Clip Next;
        }

        public static Func<string, Clip> Source;

        Clip first, clip;
        float started;

        public virtual void Show(string topic)
        {
            first = Source != null ? Source(topic) : null;
            gameObject.SetActive(first != null && first.Sheet);
            if (!gameObject.activeSelf) return;
            bool several = first.Next != null;
            Previous.SetActive(several); Next.SetActive(several);
            Play(first);
        }

        // Arrow buttons (wired in the prefab).
        public void ShowNext() { if (clip != null) Play(clip.Next ?? first); }
        public void ShowPrevious()
        {
            if (clip == null) return;
            // The clip before this one, or the last one when this is the first.
            var before = first;
            while (before.Next != null && before.Next != clip) before = before.Next;
            Play(before);
        }

        void Play(Clip shown)
        {
            clip = shown;
            Picture.texture = clip.Sheet;
            started = Time.unscaledTime;
            Frame(0);
        }

        protected virtual void Update()
        {
            if (clip == null) return;
            Frame(Mathf.FloorToInt((Time.unscaledTime - started) * clip.Rate) % clip.Count);
        }

        void Frame(int index)
        {
            float w = 1f / clip.Columns, h = 1f / clip.Rows;
            int column = index % clip.Columns, row = index / clip.Columns;
            // Frames read left to right from the top row.
            Picture.uvRect = new Rect(column * w, 1 - (row + 1) * h, w, h);
        }
    }
}
