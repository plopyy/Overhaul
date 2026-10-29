using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    [RequireComponent(typeof(Selectable))]
    public class TabButton : MonoBehaviour
    {
        public Color BackgroundColor = Color.white;
        public Color SelectedBackgroundColor = Color.white;
        public Color IconColor = Color.white;
        public Color SelectedIconColor = Color.white;

        public Button Button;
        public Image Background;
        public Image Icon;
        // Optional: background sprites per state (the base sprite under the button's own transitions).
        public Sprite NormalSprite;
        public Sprite SelectedSprite;

        protected bool _selected;

        public bool Selected => _selected;

        public virtual void Awake()
        {
            SetColor();
        }

        public virtual void SetSelected(bool selected)
        {
            if (_selected == selected)
            {
                return;
            }

            _selected = selected;
            SetColor();
        }

        public virtual void SetIcon(Sprite icon)
        {
            Icon.sprite = icon;
        }

        public virtual void SetColor()
        {
            Background.color = _selected ? SelectedBackgroundColor : BackgroundColor;
            if (NormalSprite && SelectedSprite) Background.sprite = _selected ? SelectedSprite : NormalSprite;
            Icon.color = _selected ? SelectedIconColor : IconColor;
        }
    }
}
