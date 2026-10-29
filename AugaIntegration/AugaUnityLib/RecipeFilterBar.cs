using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public enum RecipeCategory
    {
        All, Swords, Axes, Clubs, Spears, Polearms, Knives, Fists, Bows, Crossbows, Staffs, Shields,
        Helmets, Chests, Legs, Capes, Accessories, Tools, Ammo, Health, Stamina, Eitr, Balanced, Misc,
        // Head, chest, legs and cape pieces share one button (the four above are no longer used).
        Armor, Bait
    }

    // Diamond buttons along the left edge of the crafting panel plus a name search above the list.
    // Only categories present in the station's recipe list are shown; a recipe is listed when it
    // matches both the selected category and the searched text.
    public class RecipeFilterBar : MonoBehaviour
    {
        public class Entry
        {
            public RecipeCategory Category;
            public TabButton Button;
            // Gear categories show the icon of the first matching recipe of the station.
            public bool UseItemIcon;
        }

        // Plain parallel lists: a list of a custom [Serializable] class came back empty in game
        // (the bundle's components live in an assembly loaded at runtime).
        public List<TabButton> Buttons = new List<TabButton>();
        public List<RecipeCategory> Categories = new List<RecipeCategory>();
        public List<RecipeCategory> ItemIconCategories = new List<RecipeCategory>();

        private List<Entry> _entries;
        public List<Entry> Entries => _entries ?? (_entries = Buttons.Select((button, i) => new Entry
            { Button = button, Category = Categories[i], UseItemIcon = ItemIconCategories.Contains(Categories[i]) }).ToList());
        // Window backgrounds that get a diamond notch behind each visible button.
        public List<AugaPanelNotches> Notches = new List<AugaPanelNotches>();
        public float NotchMargin = 4;
        public TMP_InputField Search;

        public RecipeCategory Selected { get; private set; } = RecipeCategory.All;
        public event Action Changed;

        private readonly HashSet<RecipeCategory> _present = new HashSet<RecipeCategory>();
        private CraftingStation _station;
        private bool _stationKnown;
        private string _query = "";

        public bool SearchFocused => Search && Search.isFocused && isActiveAndEnabled;

        public void Awake()
        {
            foreach (var entry in Entries)
            {
                var category = entry.Category;
                entry.Button.Button.onClick.AddListener(() => Select(category));
            }
            if (Search) Search.onValueChanged.AddListener(OnSearch);
        }

        private void OnSearch(string text)
        {
            var query = text.Trim();
            if (query == _query) return;
            _query = query;
            Changed?.Invoke();
        }

        public void Select(RecipeCategory category)
        {
            if (Selected == category) return;
            Selected = category;
            UpdateButtons();
            Changed?.Invoke();
        }

        // Called with the full recipe list before it is shown; returns whether the station changed.
        public bool Prepare(CraftingStation station, List<Recipe> recipes)
        {
            var stationChanged = !_stationKnown || station != _station;
            _station = station;
            _stationKnown = true;

            _present.Clear();
            var icons = new Dictionary<RecipeCategory, Sprite>();
            foreach (var recipe in recipes)
            {
                var category = Categorize(recipe);
                _present.Add(category);
                if (!icons.ContainsKey(category)) icons[category] = recipe.m_item.m_itemData.GetIcon();
            }

            if (stationChanged || !_present.Contains(Selected)) Selected = RecipeCategory.All;
            if (stationChanged && Search)
            {
                Search.SetTextWithoutNotify("");
                _query = "";
            }

            foreach (var entry in Entries)
            {
                var show = entry.Category == RecipeCategory.All || _present.Contains(entry.Category);
                entry.Button.gameObject.SetActive(show);
                if (show && entry.UseItemIcon && icons.TryGetValue(entry.Category, out var icon)) entry.Button.SetIcon(icon);
            }
            UpdateButtons();
            UpdateNotches();
            return stationChanged;
        }

        public void OnEnable() => UpdateNotches();
        public void OnDisable() => SetNotches(new Vector2[0], 0);

        // Notch centres follow the visible buttons, measured from each background's top-left corner.
        public void UpdateNotches()
        {
            if (!isActiveAndEnabled) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)transform);
            var visible = Entries.Where(entry => entry.Button.gameObject.activeSelf).Select(entry => (RectTransform)entry.Button.transform).ToList();
            foreach (var notches in Notches)
            {
                if (!notches) continue;
                var target = (RectTransform)notches.transform;
                var rect = target.rect;
                var centers = visible.Select(button =>
                {
                    var local = (Vector2)target.InverseTransformPoint(button.TransformPoint(button.rect.center));
                    return new Vector2(local.x - rect.xMin, rect.yMax - local.y);
                }).ToArray();
                var radius = visible.Count > 0 ? visible[0].rect.width / 2 + NotchMargin : 0;
                Apply(notches, centers, radius);
            }
        }

        private void SetNotches(Vector2[] centers, float radius)
        {
            foreach (var notches in Notches)
                if (notches) Apply(notches, centers, radius);
        }

        private static void Apply(AugaPanelNotches notches, Vector2[] centers, float radius)
        {
            notches.CentersFromTopLeft = centers;
            notches.Radius = radius;
            notches.GetComponent<Graphic>().SetVerticesDirty();
        }

        public bool Accepts(Recipe recipe)
        {
            if (Selected != RecipeCategory.All && Categorize(recipe) != Selected) return false;
            if (_query.Length == 0) return true;
            var name = Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name);
            return name.IndexOf(_query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void UpdateButtons()
        {
            foreach (var entry in Entries)
                entry.Button.SetSelected(entry.Category == Selected);
        }

        public static RecipeCategory Categorize(Recipe recipe)
        {
            var item = recipe && recipe.m_item ? recipe.m_item.m_itemData : null;
            if (item == null) return RecipeCategory.Misc;
            var shared = item.m_shared;
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Shield: return RecipeCategory.Shields;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder: return RecipeCategory.Armor;
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket: return RecipeCategory.Accessories;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return recipe.m_item.name.StartsWith("FishingBait") ? RecipeCategory.Bait : RecipeCategory.Ammo;
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch: return RecipeCategory.Tools;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    // Tankards and the drinking horn are weapons that only drink.
                    if (shared.m_attack != null && shared.m_attack.m_attackAnimation == "emote_drink") return RecipeCategory.Misc;
                    return Weapon(shared.m_skillType);
            }
            // Uncooked dishes (preparation table) are sorted by the food they bake into, even when the
            // raw dish can be eaten too.
            var food = ComplexTooltip.GetOvenFood(item);
            if (food == null && !GrillInput(shared.m_name) && (shared.m_food > 0 || shared.m_foodStamina > 0 || shared.m_foodEitr > 0)) food = item;
            return food != null ? Food(food.m_shared) : RecipeCategory.Misc;
        }

        private static RecipeCategory Weapon(Skills.SkillType skill)
        {
            switch (skill)
            {
                case Skills.SkillType.Swords: return RecipeCategory.Swords;
                case Skills.SkillType.Axes: return RecipeCategory.Axes;
                case Skills.SkillType.Clubs: return RecipeCategory.Clubs;
                case Skills.SkillType.Spears: return RecipeCategory.Spears;
                case Skills.SkillType.Polearms: return RecipeCategory.Polearms;
                case Skills.SkillType.Knives: return RecipeCategory.Knives;
                case Skills.SkillType.Unarmed: return RecipeCategory.Fists;
                case Skills.SkillType.Bows: return RecipeCategory.Bows;
                case Skills.SkillType.Crossbows: return RecipeCategory.Crossbows;
                case Skills.SkillType.ElementalMagic:
                case Skills.SkillType.BloodMagic: return RecipeCategory.Staffs;
                case Skills.SkillType.Pickaxes:
                case Skills.SkillType.WoodCutting:
                case Skills.SkillType.Fishing: return RecipeCategory.Tools;
                default: return RecipeCategory.Misc;
            }
        }

        // Raw ingredients meant for the cooking rack (raw fish, meat...) are not dishes: misc.
        private static bool GrillInput(string name)
        {
            var rack = ZNetScene.instance ? ZNetScene.instance.GetPrefab("piece_cookingstation") : null;
            var station = rack ? rack.GetComponent<CookingStation>() : null;
            if (!station) return false;
            foreach (var conversion in station.m_conversion)
                if (conversion.m_from && conversion.m_from.m_itemData.m_shared.m_name == name) return true;
            return false;
        }

        // Eitr dishes have eitr above both other stats; otherwise the higher of health and stamina
        // wins, equal health and stamina being "balanced" (eitr ignored).
        private static RecipeCategory Food(ItemDrop.ItemData.SharedData food)
        {
            float health = food.m_food, stamina = food.m_foodStamina, eitr = food.m_foodEitr;
            if (eitr > health && eitr > stamina) return RecipeCategory.Eitr;
            if (health == stamina) return RecipeCategory.Balanced;
            return health > stamina ? RecipeCategory.Health : RecipeCategory.Stamina;
        }
    }
}
