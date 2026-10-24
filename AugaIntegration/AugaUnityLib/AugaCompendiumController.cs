using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class CompendiumItem : MonoBehaviour
    {
        public ItemTooltip ItemTooltip;
        public Image Icon;
        public Text Amount;

        public virtual void SetItem(ItemDrop.ItemData item, string amount = null)
        {
            ItemTooltip.Item = item;
            Icon.sprite = item.GetIcon();
            Amount.enabled = !string.IsNullOrEmpty(amount);
            Amount.text = amount;
        }
    }

    public class BestiaryStatBlock : MonoBehaviour
    {
        public Text LevelLabel;
        public GameObject LevelStar;
        public Text Health;
        // One cell per resistance column, filled by the controller (the header row holds the type names).
        public Text[] Resists;

        public virtual void SetStats(Character humanoid, int level)
        {
            LevelLabel.text = level == 1 ? "$baselevel" : (level - 1).ToString();
            LevelStar.SetActive(level >= 2);
            Health.text = GetMaxHealth(humanoid, level).ToString("0");
        }

        public virtual void SetGuardian(Character humanoid, int level)
        {
            SetStats(humanoid, level);
            Health.text = BestiaryData.GuardianHealth(humanoid).ToString("0");
        }

        public static float GetMaxHealth(Character humanoid, int level)
        {
            return BestiaryData.Health != null ? BestiaryData.Health(humanoid, level) : humanoid.m_health * level;
        }

        public static List<HitData.DamageType> GetDamageTypes(Character humanoid, HitData.DamageModifier modifier)
        {
            var result = new List<HitData.DamageType>();
            foreach (HitData.DamageType damageType in Enum.GetValues(typeof(HitData.DamageType)))
            {
                var mod = humanoid.m_damageModifiers.GetModifier(damageType);
                if (mod == modifier)
                {
                    result.Add(damageType);
                }
            }

            return result;
        }
    }

    // What Overhaul knows of a creature for the bestiary: its health at a level (its own changes included), its dungeon
    // guardian level (0: none) and health, its resistances (label key and percent, every shown type).
    public static class BestiaryData
    {
        public static Func<Character, int, float> Health;
        public static Func<Character, int> GuardianLevel;
        public static Func<Character, float> GuardianHealth;
        public static Func<Character, IList<KeyValuePair<string, float>>> Resistances;
        public static Func<Character, IList<BestiaryTrait>> Behavior;
        // "Black Forest · Captain" (text with keys), empty when unknown.
        public static Func<Character, string> Origin;
    }

    // One line of a creature's behaviour: its name, what it does, and the smart face beside it.
    public sealed class BestiaryTrait
    {
        public string Name, Description;
        public bool Smart;
    }

    public partial class AugaCompendiumController : MonoBehaviour
    {
        public TabHandler TabController;
        public TextsDialog Compendium;
        public TextsDialog LoreCompendium;
        public GameObject BestiaryContent;
        public RectTransform BestiaryList;
        public GameObject BestiaryListElementPrefab;
        public Text BestiaryName;
        public Text BestiaryDescription;
        public CompendiumItem CompendiumItemPrefab;
        public RectTransform DropsContainer;
        public List<BestiaryStatBlock> StatBlocks;
        // The header row (type names in its Resists) and the dungeon guardian row, shown when the creature has a 3 star guardian.
        public Text BestiaryOrigin;
        public BestiaryStatBlock StatHeader;
        public BestiaryStatBlock GuardianBlock;
        // Behaviour lines (name, description, smart face), shown as many as the creature has.
        public Text[] TraitNames;
        public Text[] TraitDescriptions;
        public IntelligenceFace[] TraitFaces;

        protected readonly List<KeyValuePair<string, GameObject>> _bestiaryItems = new List<KeyValuePair<string, GameObject>>();
        protected int _selectedBestiaryIndex = -1;
        protected Dictionary<string, Character> _trophyToMonsterCache;

        protected virtual void SetupTrophyToMonsterCache()
        {
            if (ZNetScene.instance == null || ZNetScene.instance.m_namedPrefabs == null)
                return;

            if (_trophyToMonsterCache != null)
                return;

            if (!ZNetScene.instance.m_namedPrefabs.Values.Any())
                return;

            _trophyToMonsterCache = new Dictionary<string, Character>();

            foreach (var prefab in ZNetScene.instance.m_namedPrefabs.Values)
            {
                // Any creature (deer, boar, neck... are not humanoids), never a player.
                var humanoid = prefab.GetComponent<Character>();
                if (humanoid is Player) continue;
                var characterDrop = prefab.GetComponent<CharacterDrop>();
                if (characterDrop == null || humanoid == null || characterDrop.m_drops == null || !characterDrop.m_drops.Any())
                {
                    continue;
                }

                foreach (var drop in characterDrop.m_drops)
                {
                    // An empty drop (a mod's or a removed item) is skipped instead of stopping the whole list.
                    if (drop == null || drop.m_prefab == null)
                        continue;

                    var itemDrop = drop.m_prefab.GetComponent<ItemDrop>();
                    if (itemDrop != null)
                    {
                        if (itemDrop.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                        {
                            if (!_trophyToMonsterCache.ContainsKey(drop.m_prefab.name))
                            {
                                _trophyToMonsterCache.Add(drop.m_prefab.name, humanoid);
                                break;
                            }
                        }
                    }
                }
            }
        }

        public virtual void ShowCompendium()
        {
            gameObject.SetActive(true);
            TabController.SetActiveTab(0);
            Compendium.Setup(Player.m_localPlayer);
            LoreCompendium.Setup(Player.m_localPlayer);
            SetupBestiary();
            Menu.instance.m_settingsInstance = gameObject;
        }

        public virtual void HideCompendium()
        {
            Menu.instance.m_settingsInstance = null;
            gameObject.SetActive(false);
        }

        public virtual void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyMenu"))
            {
                HideCompendium();
            }
        }

        public virtual void SetupBestiary()
        {
            SetupTrophyToMonsterCache();
            if (_trophyToMonsterCache == null)
            {
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                BestiaryContent.SetActive(false);
                return;
            }

            BestiaryContent.SetActive(_bestiaryItems.Count > 0);

            foreach (var bestiaryItem in _bestiaryItems)
            {
                Destroy(bestiaryItem.Value);
            }
            _bestiaryItems.Clear();
            
            var trophies = player.GetTrophies();
            var tempList = new List<Tuple<int, string, GameObject>>();

            for (var index = 0; index < trophies.Count; ++index)
            {
                var trophyName = trophies[index];
                var trophyItemPrefab = ObjectDB.instance.GetItemPrefab(trophyName);
                if (trophyItemPrefab == null)
                {
                    continue;
                }

                var trophyItem = trophyItemPrefab.GetComponent<ItemDrop>();
                var position2d = trophyItem.m_itemData.m_shared.m_trophyPos;
                var position = position2d.y * 10 + position2d.x;

                _trophyToMonsterCache.TryGetValue(trophyName, out var humanoidPrefab);
                if (humanoidPrefab != null)
                {
                    var listItem = Instantiate(BestiaryListElementPrefab, BestiaryList);
                    listItem.SetActive(true);
                    var t = listItem.transform;
                    var creatureName = Localization.instance.Localize(humanoidPrefab.m_name);
                    t.Find("name").GetComponent<TMP_Text>().text = creatureName;
                    t.Find("icon").GetComponent<Image>().sprite = trophyItem.m_itemData.GetIcon();
                    tempList.Add(new Tuple<int, string, GameObject>(position, trophyName, listItem));
                }
            }

            var orderedList = tempList.OrderBy(x => x.Item1).ToList();
            for (var index = 0; index < orderedList.Count; index++)
            {
                var entry = orderedList[index];
                var listItem = entry.Item3;
                listItem.transform.SetSiblingIndex(index);
                var i = index;
                listItem.GetComponent<Button>().onClick.AddListener(() => OnBestiaryItemClicked(i));
                _bestiaryItems.Add(new KeyValuePair<string, GameObject>(entry.Item2, listItem));
            }

            OnBestiaryItemClicked(0);
        }

        private void OnBestiaryItemClicked(int index)
        {
            _selectedBestiaryIndex = index;

            for (var i = 0; i < _bestiaryItems.Count; i++)
            {
                var bestiaryItem = _bestiaryItems[i];
                bestiaryItem.Value.transform.Find("selected").gameObject.SetActive(i == _selectedBestiaryIndex);
            }

            if (_selectedBestiaryIndex < 0 || _selectedBestiaryIndex >= _bestiaryItems.Count)
            {
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            _selectedBestiaryIndex = Mathf.Clamp(_selectedBestiaryIndex, 0, _bestiaryItems.Count - 1);
            var selectedEntry = _bestiaryItems[_selectedBestiaryIndex];
            var trophy = selectedEntry.Key;
            _trophyToMonsterCache.TryGetValue(trophy, out var humanoidPrefab);
            if (humanoidPrefab == null)
            {
                return;
            }

            var trophyPrefab = ObjectDB.instance.GetItemPrefab(trophy);
            if (trophyPrefab == null)
            {
                return;
            }

            var trophyItem = trophyPrefab.GetComponent<ItemDrop>();

            BestiaryContent.SetActive(true);
            BestiaryName.text = humanoidPrefab.m_name;
            if (BestiaryOrigin)
            {
                string origin = BestiaryData.Origin != null ? BestiaryData.Origin(humanoidPrefab) : "";
                BestiaryOrigin.gameObject.SetActive(!string.IsNullOrEmpty(origin));
                BestiaryOrigin.text = origin;
            }
            BestiaryDescription.text = trophyItem.m_itemData.m_shared.m_name + "_lore";

            foreach (Transform child in DropsContainer)
            {
                Destroy(child.gameObject);
            }

            foreach (var drop in humanoidPrefab.GetComponent<CharacterDrop>().m_drops)
            {
                var dropElement = Instantiate(CompendiumItemPrefab, DropsContainer);
                var amountText = (drop.m_amountMin == drop.m_amountMax ? $"{drop.m_amountMin}" : $"{drop.m_amountMin}-{drop.m_amountMax}") + $" ({Mathf.CeilToInt(drop.m_chance * 100)}%)";
                dropElement.SetItem(drop.m_prefab.GetComponent<ItemDrop>().m_itemData, amountText);
            }

            for (var i = 0; i < StatBlocks.Count; i++)
            {
                var statBlock = StatBlocks[i];
                statBlock.SetStats(humanoidPrefab, i + 1);
            }
            int guardian = BestiaryData.GuardianLevel != null ? BestiaryData.GuardianLevel(humanoidPrefab) : 0;
            if (GuardianBlock)
            {
                GuardianBlock.gameObject.SetActive(guardian >= 4);
                if (guardian >= 4) GuardianBlock.SetGuardian(humanoidPrefab, guardian);
            }
            ShowResistances(humanoidPrefab);
            ShowBehavior(humanoidPrefab);

            Localization.instance.Localize(BestiaryContent.transform);
        }
    }

    public partial class AugaCompendiumController
    {
        // One column per resistance, 0 included: its type over every row, the same percent on each row, green or red.
        protected virtual void ShowResistances(Character humanoid)
        {
            if (!StatHeader || StatHeader.Resists == null) return;
            var list = BestiaryData.Resistances != null ? BestiaryData.Resistances(humanoid).ToList() : new List<KeyValuePair<string, float>>();
            var rows = StatBlocks.Concat(new[] { GuardianBlock }).Where(b => b).ToList();
            for (int i = 0; i < StatHeader.Resists.Length; i++)
            {
                bool used = i < list.Count;
                StatHeader.Resists[i].gameObject.SetActive(used);
                if (used) StatHeader.Resists[i].text = list[i].Key;
                foreach (var row in rows)
                {
                    if (row.Resists == null || i >= row.Resists.Length) continue;
                    row.Resists[i].gameObject.SetActive(used);
                    if (used) row.Resists[i].text = Percent(list[i].Value);
                }
            }
        }

        protected virtual void ShowBehavior(Character humanoid)
        {
            if (TraitNames == null) return;
            var traits = BestiaryData.Behavior != null ? BestiaryData.Behavior(humanoid) : new List<BestiaryTrait>();
            for (int i = 0; i < TraitNames.Length; i++)
            {
                bool used = i < traits.Count;
                TraitNames[i].transform.parent.gameObject.SetActive(used);
                if (!used) continue;
                TraitNames[i].text = traits[i].Name;
                TraitDescriptions[i].text = traits[i].Description;
                // Hidden but still taking its place, so every description starts at the same spot.
                TraitFaces[i].enabled = traits[i].Smart;
                TraitFaces[i].Smart = true;
            }
        }

        public static string Percent(float value) => Mathf.Abs(value) < 0.05f ? "0 %" :
            "<color=" + (value >= 0 ? "#7fd97f" : "#ff6b5e") + ">" + (value >= 0 ? "+" : "−") + Mathf.Abs(value).ToString("0.#") + " %</color>";
    }

    public class AugaTextsDialogFilter : MonoBehaviour
    {
        public string Filter;
    }
}
