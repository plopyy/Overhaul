using System;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // A tip's looping clip in the compendium (Hugin tab): a sheet of frames (Columns × Rows, Count of them) shown one after
    // the other at Rate per second on Picture. Overhaul gives the clip of a tip by its title (Source); no clip hides the area.
    public class TipClip : MonoBehaviour
    {
        public RawImage Picture;

        public sealed class Clip
        {
            public Texture Sheet;
            public int Columns, Rows, Count;
            public float Rate = 15;
            // The clip played after this one (the tip's first clip comes back after its last).
            public Clip Next;
        }

        public static Func<string, Clip> Source;

        Clip first, clip;
        float started;

        public virtual void Show(string topic)
        {
            first = clip = Source != null ? Source(topic) : null;
            gameObject.SetActive(clip != null && clip.Sheet);
            if (!gameObject.activeSelf) return;
            Picture.texture = clip.Sheet;
            started = Time.unscaledTime;
            Frame(0);
        }

        protected virtual void Update()
        {
            if (clip == null) return;
            int index = Mathf.FloorToInt((Time.unscaledTime - started) * clip.Rate);
            if (index >= clip.Count)
            {
                // A single clip loops on itself; several play one after the other.
                clip = clip.Next ?? first; started = Time.unscaledTime; index = 0;
                Picture.texture = clip.Sheet;
            }
            Frame(index);
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
