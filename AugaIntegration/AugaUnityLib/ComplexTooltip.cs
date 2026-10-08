using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
// ReSharper disable InconsistentNaming

namespace AugaUnity
{
    public class ItemTooltip : MonoBehaviour
    {
        [NonSerialized]
        public ItemDrop.ItemData Item;
    }

    public class FoodTooltip : MonoBehaviour
    {
        [NonSerialized]
        public Player.Food Food;
    }

    public class StatusTooltip : MonoBehaviour
    {
        [NonSerialized]
        public StatusEffect StatusEffect;
    }

    public class SkillTooltip : MonoBehaviour
    {
        [NonSerialized]
        public Skills.Skill Skill;
    }

    public class TooltipTextBox : MonoBehaviour
    {
        public TMP_Text Text;
        public TMP_Text RightColumnText;
        public TMP_Text ThirdColumnText;

        public virtual void AddLine(TMP_Text t, object s, bool localize = true, bool overwrite = false)
        {
            if (t == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(t.text)  && !overwrite)
            {
                t.text += "\n";
            }

            if (overwrite)
                t.text = (localize ? Localization.instance.Localize(s.ToString()) : s).ToString().Trim();
            else
                t.text += (localize ? Localization.instance.Localize(s.ToString()) : s).ToString().Trim();
        }

        public virtual void AddLine(object a, bool localize = true, bool overwrite = false)
        {
            AddLine(Text, a, localize, overwrite);
            AddLine(RightColumnText, "", false, overwrite);
            AddLine(ThirdColumnText, "", false, overwrite);
        }

        public virtual void AddLine(object a, object b, bool localize = true, bool overwrite = false)
        {
            AddLine(Text, a, localize, overwrite);
            AddLine(RightColumnText, b, localize, overwrite);
            AddLine(ThirdColumnText, b, false, overwrite);
        }

        public virtual string GenerateParenthetical(object b, object parenthetical)
        {
            return $"{b} <color={ComplexTooltip.ParentheticalColor}>({parenthetical})</color>";
        }

        public virtual void AddLine(object a, object b, object parenthetical, bool localize = true, bool overwrite = false)
        {
            AddLine(Text, a, localize, overwrite);
            AddLine(RightColumnText, GenerateParenthetical(b, parenthetical), localize, overwrite);
            AddLine(ThirdColumnText, b, false, overwrite);
        }

        public virtual void AddUpgradeLine(object label, object value1, object value2, string color2, bool localize = true, bool overwrite = false)
        {
            AddLine(Text, label, localize, overwrite);
            AddLine(RightColumnText, value1, localize, overwrite);
            AddLine(ThirdColumnText, string.IsNullOrEmpty(color2) ? value2 : $"<color={color2}>{value2}</color>", localize, overwrite);
        }
    }

    public class ComplexTooltip : MonoBehaviour
    {
        public static Func<Player, string, float> ElementStatBonus;
        public static Func<Player, bool> HasProjectileStat;
        // Overhaul staff shield absorb for an item and quality (null when the item is not a staff).
        public static Func<ItemDrop.ItemData, int, float?> StaffShield;
        private const string StatLabelColor = "#FFF0AD";
        public static bool UsesProjectiles(ItemDrop.ItemData item)
        {
            if(item==null||!item.IsWeapon())return false;
            var primary=item.m_shared.m_attack;var secondary=item.m_shared.m_secondaryAttack;
            // Ammunition can supply the projectile prefab (bows and crossbows).
            return (primary!=null&&primary.m_attackType==Attack.AttackType.Projectile)||
                (secondary!=null&&secondary.m_attackType==Attack.AttackType.Projectile);
        }
        public void AddProjectileStatLine(TooltipTextBox box, Player player, ItemDrop.ItemData item)
        {
            if(!UsesProjectiles(item)||HasProjectileStat?.Invoke(player)!=true)return;
            float chance=ElementStatBonus?.Invoke(player,"projectile") ?? 0f;
            box.AddLine("<color="+StatLabelColor+">$overhaul_projectile_extra</color>",
                (Mathf.Clamp01(chance)*100f).ToString("0.##")+"%");
        }
        public static float DisplayedEitrCost(ItemDrop.ItemData item,Player player)
        {
            var attack=item.m_shared.m_attack;
            float cost=player?attack.GetAttackEitr(player,item):attack.m_attackEitr;
            return Mathf.Max(0,cost)*(1-Mathf.Clamp01(ElementStatBonus?.Invoke(player,"eitr_cost")??0f));
        }
        public static string ElementColor(string label)
        {
            switch(label)
            {
                case "$inventory_fire": return "#FF703D";
                case "$inventory_frost": return "#65B5FF";
                case "$inventory_poison": return "#78D65A";
                case "$inventory_lightning": return "#FFE45C";
                case "$inventory_spirit": return "#FFFFFF";
                case "$inventory_blood": return "#B83246";
                default: return null;
            }
        }

        public void AddElementStatLines(TooltipTextBox box, Player player)
        {
            if(ElementStatBonus==null)return;
            var ids=new[]{"element_fire","element_frost","element_poison","element_spirit"};
            // The historical element_spirit stat is named Foudre and deals lightning damage.
            var labels=new[]{"$inventory_fire","$inventory_frost","$inventory_poison","$inventory_lightning"};
            for(int i=0;i<ids.Length;i++)
            {
                float value=ElementStatBonus(player,ids[i]);
                if(value<=0)continue;
                box.AddLine("<color="+StatLabelColor+">"+labels[i]+"(stat)</color>",
                    "<color="+ElementColor(labels[i])+">"+value.ToString("0.##")+"</color>");
            }
        }
        public const string ParentheticalColor = "#A39689";
        public const string UpgradeColor = "#1B9B37";

        public enum ObjectBackgroundType { Item, Diamond, Skill }

        [CanBeNull] public Image Icon;
        [CanBeNull] public Image ItemBackground;
        [CanBeNull] public Image DiamondBackground;
        [CanBeNull] public Image SkillBackground;
        [CanBeNull] public GameObject NormalDivider;
        public TMP_Text Topic;
        public TMP_Text Subtitle;
        [CanBeNull] public TMP_Text DescriptionText;
        [CanBeNull] public GameObject BottomDivider;
        [CanBeNull] public TMP_Text ItemQuality;
        public RectTransform TextBoxContainer;
        [CanBeNull] public RectTransform LowerTextBoxContainer;
        public TooltipTextBox TwoColumnTextBoxPrefab;
        public TooltipTextBox CenteredTextBoxPrefab;
        public TooltipTextBox LeftAlignedTextBoxPrefab;
        [CanBeNull] public TooltipTextBox UpgradeLabelsPrefab;
        [CanBeNull] public TooltipTextBox UpgradeTwoColumnTextBoxPrefab;
        [CanBeNull] public TooltipTextBox CheckBoxTextBoxPrefab;

        public static event Action<ComplexTooltip, ItemDrop.ItemData> OnComplexTooltipGeneratedForItem;
        public static event Action<ComplexTooltip, Player.Food> OnComplexTooltipGeneratedForFood;
        public static event Action<ComplexTooltip, StatusEffect> OnComplexTooltipGeneratedForStatusEffect;
        public static event Action<ComplexTooltip, Skills.Skill> OnComplexTooltipGeneratedForSkill;

        protected static readonly StringBuilder _stringBuilder = new StringBuilder();
        protected static List<Func<ItemDrop.ItemData, string, string, Tuple<string, string>>> _itemStatPreprocessors = new List<Func<ItemDrop.ItemData, string, string, Tuple<string, string>>>();

        protected readonly List<GameObject> _textBoxes = new List<GameObject>();
        protected ItemDrop.ItemData _item;
        protected Player.Food _food;
        protected StatusEffect _statusEffect;
        protected Skills.Skill _skill;
        protected int _quality;
        protected int _variant;
        protected string _originalTooltip;

        public virtual void Start()
        {
            TwoColumnTextBoxPrefab.gameObject.SetActive(false);
            CenteredTextBoxPrefab.gameObject.SetActive(false);

            if (UpgradeLabelsPrefab != null)
            {
                UpgradeLabelsPrefab.gameObject.SetActive(false);
            }

            if (UpgradeTwoColumnTextBoxPrefab != null)
            {
                UpgradeTwoColumnTextBoxPrefab.gameObject.SetActive(false);
            }

            if (CheckBoxTextBoxPrefab != null)
            {
                CheckBoxTextBoxPrefab.gameObject.SetActive(false);
            }
        }

        public virtual void ClearTextBoxes()
        {
            foreach (var textBox in _textBoxes)
            {
                textBox.SetActive(false);
                Destroy(textBox);
            }

            _textBoxes.Clear();
            if (LowerTextBoxContainer != null)
            {
                LowerTextBoxContainer.gameObject.SetActive(false);
            }
        }

        public virtual TooltipTextBox AddTextBox(TooltipTextBox prefab, RectTransform container = null)
        {
            if (container == null)
            {
                container = TextBoxContainer;
            }

            container.gameObject.SetActive(true);
            var textBox = Instantiate(prefab, container, false);
            textBox.gameObject.SetActive(true);
            _textBoxes.Add(textBox.gameObject);
            return textBox;
        }

        public GameObject AddDivider(RectTransform container = null)
        {
            if (container == null)
            {
                container = TextBoxContainer;
            }

            container.gameObject.SetActive(true);
            var divider = Instantiate(BottomDivider, container, false);
            divider.SetActive(true);
            _textBoxes.Add(divider);
            return divider;
        }

        public virtual void ClearData()
        {
            _item = null;
            _food = null;
            _statusEffect = null;
            _skill = null;
            _originalTooltip = null;

            if (ItemQuality != null)
            {
                ItemQuality.transform.parent.gameObject.SetActive(false);
            }
        }

        public virtual void SetTopic(string topic)
        {
            Topic.text = topic;
        }

        public virtual void SetSubtitle(string topic)
        {
            Subtitle.text = topic;
            Subtitle.gameObject.SetActive(!string.IsNullOrEmpty(topic));
        }

        protected virtual void SetItemBaseData(ItemDrop.ItemData item, int quality = -1, int variant = -1)
        {
            ClearData();
            _originalTooltip = item.GetTooltip();

            _item = item;
            _quality = quality;
            _variant = variant;
            quality = quality < 0 ? item.m_quality : quality;

            if (ItemQuality != null)
            {
                ItemQuality.transform.parent.gameObject.SetActive(item.m_shared.m_maxQuality > 1);
                ItemQuality.text = quality.ToString();
            }

            EnableObjectBackground(ObjectBackgroundType.Item);

            SetIcon(variant < 0 ? item.GetIcon() : item.m_shared.m_icons[variant]);
            SetTopic(Localization.instance.Localize(item.m_shared.m_name));
            SetSubtitle(GenerateItemSubtext(item));
            SetDescription(Localization.instance.Localize(item.m_shared.m_description));
        }

        public virtual void SetItem(ItemDrop.ItemData item, int quality = -1, int variant = -1)
        {
            if (_item == item && _quality == quality && _variant == variant)
                return;

            SetItemBaseData(item, quality, variant);
            GenerateItemTextBoxes(item, quality);
            Localization.instance.Localize(transform);

            OnComplexTooltipGeneratedForItem?.Invoke(this, item);
            AddCrafterBox(item, quality);
        }

        // The crafter goes at the very bottom, under the boxes other mods add.
        protected virtual void AddCrafterBox(ItemDrop.ItemData item, int quality)
        {
            if ((quality >= 0 && quality != item.m_quality) || item.m_crafterID == 0L) return;
            var textBox = AddTextBox(TwoColumnTextBoxPrefab);
            TextBoxAddPreprocessedLine(textBox, item, "$item_crafter", item.m_crafterName);
            Localization.instance.Localize(textBox.transform);
        }

        public virtual void SetItemNoTextBoxes(ItemDrop.ItemData item, int quality = -1, int variant = -1)
        {
            if (_item == item && _quality == quality && _variant == variant)
                return;

            SetItemBaseData(item, quality, variant);
            Localization.instance.Localize(transform);

            OnComplexTooltipGeneratedForItem?.Invoke(this, item);
        }

        public virtual void EnableObjectBackground(ObjectBackgroundType type)
        {
            if (ItemBackground != null)
            {
                ItemBackground.enabled = type == ObjectBackgroundType.Item;
            }
            if (DiamondBackground != null)
            {
                DiamondBackground.enabled = type == ObjectBackgroundType.Diamond;
            }
            if (SkillBackground != null)
            {
                SkillBackground.enabled = type == ObjectBackgroundType.Skill;
            }
        }

        public virtual void EnableDescription(bool enable)
        {
            if (BottomDivider != null)
            {
                BottomDivider.SetActive(enable);
            }
            if (DescriptionText != null)
            {
                DescriptionText.gameObject.SetActive(enable);
            }
        }

        public virtual void SetIcon(Sprite icon)
        {
            if (Icon != null)
            {
                Icon.sprite = icon;
            }
        }

        public virtual void SetDescription(string description)
        {
            var hasDescription = !string.IsNullOrEmpty(description);
            EnableDescription(hasDescription);
            if (hasDescription && DescriptionText != null)
            {
                DescriptionText.text = description;
            }
        }

        public virtual string GenerateItemSubtext(ItemDrop.ItemData item)
        {
            _stringBuilder.Clear();
            _stringBuilder.Append($"$itemtype_{item.m_shared.m_itemType.ToString().ToLowerInvariant()}");

            if (item.m_shared.m_food > 0)
            {
                _stringBuilder.Append(", $item_food");
            }

            if ((item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon 
                || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon
                || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft
                || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch)
                && item.m_shared.m_skillType != Skills.SkillType.None)
            {
                _stringBuilder.Append(", ");
                _stringBuilder.Append($"$skill_singular_{item.m_shared.m_skillType.ToString().ToLowerInvariant()}");
            }

            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch)
            {
                _stringBuilder.Append(", $item_onehanded");
            }

            return Localization.instance.Localize(_stringBuilder.ToString());
        }

        public virtual void GenerateItemTextBoxes(ItemDrop.ItemData item, int quality)
        {
            quality = quality < 0 ? item.m_quality : quality;

            ClearTextBoxes();

            var upgrade = item.m_quality != quality;
            var ovenFood = GetOvenFood(item);
            if (ovenFood != null)
            {
                var heading = AddTextBox(CenteredTextBoxPrefab);
                heading.AddLine(Localization.instance.Localize("$overhaul_food_after_baking") + " : " + Localization.instance.Localize(ovenFood.m_shared.m_name));
                AddFoodTextBox(ovenFood, false);
            }
            if (upgrade)
            {
                var labels = AddTextBox(UpgradeLabelsPrefab);
                labels.AddLine(item.m_quality, quality, false);
            }

            if (!upgrade && item.m_shared.m_dlc.Length > 0)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                TextBoxAddPreprocessedLine(textBox, item, "<color=aqua>$item_dlc</color>");
            }

            var skillLevel = Player.m_localPlayer.GetSkillLevel(item.m_shared.m_skillType);
            var statusEffectTooltip = item.GetStatusEffectTooltip(quality, skillLevel);
            var showExtraText = true;
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Consumable:
                    if (item.m_shared.m_food > 0)
                    {
                        AddFoodTextBox(item);
                        showExtraText = false;
                    }
                    else if (!string.IsNullOrEmpty(statusEffectTooltip))
                    {
                        var textBox = AddTextBox(CenteredTextBoxPrefab);
                        textBox.AddLine(statusEffectTooltip);
                        showExtraText = false;
                    }
                    break;

                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Ammo:
                    AddDamageTextbox(item, quality, upgrade);
                    AddBlockingTextBox(item, quality, upgrade);
                    AddProjectileTextBox(item, quality);
                    AddResourceUseTextbox(item);
                    AddAttackStatusTextBox(item);
                    break;

                case ItemDrop.ItemData.ItemType.Shield:
                    AddBlockingTextBox(item, quality, upgrade);
                    break;

                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                    AddArmorTextBox(item, quality, upgrade);
                    break;
            }

            AddTrinketActivationTextBox(item);

            // Other info
            {
                var textBox = AddTextBox(upgrade ? UpgradeTwoColumnTextBoxPrefab : TwoColumnTextBoxPrefab);

                if (item.m_shared.m_value > 0)
                {
                    TextBoxAddPreprocessedLine(textBox, item, "$item_value", item.GetValue(), item.m_shared.m_value);
                }
                

                if (item.m_shared.m_useDurability)
                {
                    var maxDurability = item.GetMaxDurability(quality);
                    var durability = item.m_durability.ToString("0");
                    var durabilityPercent = Mathf.CeilToInt(item.GetDurabilityPercentage() * 100);

                    if (upgrade)
                    {
                        var prevMaxDurability = item.GetMaxDurability(item.m_quality);
                        var better = prevMaxDurability < maxDurability;
                        textBox.AddUpgradeLine("$item_durability", prevMaxDurability, maxDurability, better ? UpgradeColor : null);
                    }
                    else
                    {
                        TextBoxAddPreprocessedLine(textBox, item, "$item_durability", $"{durabilityPercent}%", $"{durability}/{maxDurability}");
                    }
                }

                TextBoxAddPreprocessedLine(textBox, item, "$item_weight", item.GetWeight().ToString("0.0"));

                if (item.m_shared.m_eitrRegenModifier != 0)
                {
                    var eitrRegenModifier = (item.m_shared.m_eitrRegenModifier * 100).ToString("+0;-0");
                    var totalEitrRegenModifier = (Player.m_localPlayer.GetEquipmentEitrRegenModifier() * 100).ToString("+0;-0");
                    TextBoxAddPreprocessedLine(textBox, item, "$overhaul_eitrregen_short", $"{eitrRegenModifier}%", $"$item_total {totalEitrRegenModifier}%");
                }

                if (item.m_shared.m_movementModifier != 0)
                {
                    var movementModifier = (item.m_shared.m_movementModifier * 100).ToString("+0;-0");
                    var totalEquipmentMovementModifier = (Player.m_localPlayer.GetEquipmentMovementModifier() * 100).ToString("+0;-0");
                    TextBoxAddPreprocessedLine(textBox, item, "$overhaul_movespeed_short", $"{movementModifier}%", $"$item_total {totalEquipmentMovementModifier}%");
                }
            }

            if (!item.m_shared.m_teleportable)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                TextBoxAddPreprocessedLine(textBox, item, "$item_noteleport");
            }

            var setStatusEffect = item.GetSetStatusEffectTooltip(quality, skillLevel);
            if (!string.IsNullOrEmpty(setStatusEffect))
            {
                var textBox = AddTextBox(LeftAlignedTextBoxPrefab);
                var equipped = Player.m_localPlayer.GetInventory().GetEquippedItems().Count(i => i.m_shared.m_setName == item.m_shared.m_setName);
                TextBoxAddPreprocessedLine(textBox, item, $"<color=orange>{item.m_shared.m_setStatusEffect.m_name}</color> ({equipped}/{item.m_shared.m_setSize})");
                TextBoxAddPreprocessedLine(textBox, item, setStatusEffect);
                // The set bonus has its own Auga box; do not copy it again as mod-added text.
                var nativeSetBlock = $"\n\n$item_seteffect (<color=orange>{item.m_shared.m_setSize}</color> $item_parts):<color=orange>{item.m_shared.m_setStatusEffect.m_name}</color>\n{setStatusEffect}";
                _originalTooltip = _originalTooltip.Replace(nativeSetBlock, "");
            }

            var extraText = GetExtraTextFromTooltip();
            if (!string.IsNullOrEmpty(extraText) && showExtraText)
            {
                if (LeftAlignedTextBoxPrefab != null)
                {
                    AddDivider();
                    var textBox = AddTextBox(LeftAlignedTextBoxPrefab);
                    textBox.Text.text = extraText;
                }
            }
        }

        private string GetExtraTextFromTooltip()
        {
            var outputString = new StringBuilder();

            using (StringReader reader = new StringReader(_originalTooltip))
            {
                string line;
                bool foundWeight = false;
                bool foundFirstColor = false;
                
                while ((line = reader.ReadLine()) != null)
                {
                    if ((line.Contains("$item_") || line.Contains("$inventory_")) && !foundWeight)
                    {
                        if (line.Contains("$item_weight"))
                            foundWeight = true;
                    }
                    else
                    {
                        if (foundWeight)
                        {
                            if ((line.StartsWith("<color") || (!line.StartsWith("$item_") && !line.StartsWith("$inventory_"))) && !foundFirstColor)
                                foundFirstColor = true;
                            
                            if (!foundFirstColor)
                                continue;
                            
                            if (!string.IsNullOrEmpty(line.Trim()))
                                outputString.AppendLine(line);
                        }
                    }
                }
            }
            
            return outputString.ToString();
        }

        private void AddResourceUseTextbox(ItemDrop.ItemData item)
        {
            if (item.m_shared.m_attack.m_attackStamina <= 0.0 &&
                item.m_shared.m_attack.m_attackEitr <= 0.0 &&
                item.m_shared.m_attack.m_attackHealth <= 0.0 &&
                item.m_shared.m_attack.m_attackHealthPercentage <= 0.0 &&
                item.m_shared.m_attack.m_drawStaminaDrain <= 0.0)
                return;

            var textBox = AddTextBox(TwoColumnTextBoxPrefab);

            if (item.m_shared.m_attack.m_attackStamina > 0.0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_staminause", item.m_shared.m_attack.m_attackStamina);
            if (item.m_shared.m_attack.m_attackEitr > 0.0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_eitruse", DisplayedEitrCost(item,Player.m_localPlayer).ToString("0.##"));
            if (item.m_shared.m_attack.m_attackHealth > 0.0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_healthuse", item.m_shared.m_attack.m_attackHealth);
            if (item.m_shared.m_attack.m_attackHealthPercentage > 0.0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_healthuse", item.m_shared.m_attack.m_attackHealthPercentage.ToString("0.0"));
            if (item.m_shared.m_attack.m_drawStaminaDrain > 0.0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_staminahold", item.m_shared.m_attack.m_drawStaminaDrain);
        }

        public virtual void AddDamageTextbox(ItemDrop.ItemData item, int quality, bool upgrade)
        {
            if (Player.m_localPlayer == null)
            {
                return;
            }

            var textBox = AddTextBox(upgrade ? UpgradeTwoColumnTextBoxPrefab : TwoColumnTextBoxPrefab);

            var damage = item.GetDamage(quality, Game.m_worldLevel);
            var previousDamage = item.GetDamage(item.m_quality, Game.m_worldLevel);
            var skillType = item.m_shared.m_skillType;

            Player.m_localPlayer.GetSkills().GetRandomSkillRange(out var min, out var max, skillType);

            if (damage.m_damage != 0.0f)
                AddDamageLine(textBox, item, "$inventory_damage", damage.m_damage, previousDamage.m_damage, min, max, upgrade);
            if (damage.m_blunt != 0.0f)
                AddDamageLine(textBox, item, "$inventory_blunt", damage.m_blunt, previousDamage.m_blunt, min, max, upgrade);
            if (damage.m_slash != 0.0f)
                AddDamageLine(textBox, item, "$inventory_slash", damage.m_slash, previousDamage.m_slash, min, max, upgrade);
            if (damage.m_pierce != 0.0f)
                AddDamageLine(textBox, item, "$inventory_pierce", damage.m_pierce, previousDamage.m_pierce, min, max, upgrade);
            // The Overhaul element stat adds flat damage to every hit: it is added to the range of the same
            // element, or shown as that element's line when the weapon has none of it.
            float Bonus(string id) => item.IsWeapon() && ElementStatBonus != null ? ElementStatBonus(Player.m_localPlayer, id) : 0f;
            float fire = Bonus("element_fire"), frost = Bonus("element_frost"), poison = Bonus("element_poison"), lightning = Bonus("element_spirit");
            if (damage.m_fire != 0.0f || fire > 0)
                AddDamageLine(textBox, item, "$inventory_fire", damage.m_fire, previousDamage.m_fire, min, max, upgrade, fire);
            if (damage.m_frost != 0.0f || frost > 0)
                AddDamageLine(textBox, item, "$inventory_frost", damage.m_frost, previousDamage.m_frost, min, max, upgrade, frost);
            if (damage.m_lightning != 0.0f || lightning > 0)
                AddDamageLine(textBox, item, "$inventory_lightning", damage.m_lightning, previousDamage.m_lightning, min, max, upgrade, lightning);
            if (damage.m_poison != 0.0f || poison > 0)
                AddDamageLine(textBox, item, "$inventory_poison", damage.m_poison, previousDamage.m_poison, min, max, upgrade, poison);
            if (damage.m_spirit != 0.0f)
                AddDamageLine(textBox, item, "$inventory_spirit", damage.m_spirit, previousDamage.m_spirit, min, max, upgrade);
            if (item.m_shared.m_attackForce > 0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_knockback", item.m_shared.m_attackForce);
            AddProjectileStatLine(textBox, Player.m_localPlayer,item);
            if (item.m_shared.m_backstabBonus > 0)
                TextBoxAddPreprocessedLine(textBox, item, "$item_backstab", $"{item.m_shared.m_backstabBonus}x");
        }

        private void AddDamageLine(TooltipTextBox textBox, ItemDrop.ItemData item, string label, float damage, float previousDamage, float min, float max, bool upgrade, float bonus = 0f)
        {
            var color=ElementColor(label);
            if (upgrade)
            {
                textBox.AddUpgradeLine(label, color==null?(object)previousDamage:$"<color={color}>{previousDamage}</color>", damage, color ?? (damage > previousDamage ? UpgradeColor : null));
            }
            else
            {
                var low = Mathf.RoundToInt(damage * min + bonus);
                var high = Mathf.RoundToInt(damage * max + bonus);
                var value = low == high ? low.ToString() : $"{low}-{high}";
                if(color!=null)value=$"<color={color}>"+value.Replace($"<color={ParentheticalColor}>","").Replace("</color>","")+"</color>";
                TextBoxAddPreprocessedLine(textBox, item, label, value);
            }
        }

        public virtual string GetDamageRangeString(float damage, float minFactor, float maxFactor)
        {
            var min = Mathf.RoundToInt(damage * minFactor);
            var max = Mathf.RoundToInt(damage * maxFactor);
            // Only the damage actually dealt with the current skill; the base value is left out.
            return $"{min}-{max}";
        }

        public virtual void AddBlockingTextBox(ItemDrop.ItemData item, int quality, bool upgrade)
        {
            // Overhaul staffs cast a shield instead of blocking: one line replaces the blocking stats.
            var shield = StaffShield?.Invoke(item, quality);
            if (shield != null)
            {
                var shieldBox = AddTextBox(TwoColumnTextBoxPrefab);
                TextBoxAddPreprocessedLine(shieldBox, item, "$overhaul_staff_shield", shield.Value.ToString("0"));
                return;
            }

            var blockPower = item.GetBlockPowerTooltip(quality);
            var previousBlockPower = item.GetBlockPowerTooltip(item.m_quality);

            var textBox = AddTextBox(upgrade ? UpgradeTwoColumnTextBoxPrefab : TwoColumnTextBoxPrefab);

            if (blockPower > 0)
            {
                if (upgrade)
                {
                    textBox.AddUpgradeLine("$item_blockarmor", previousBlockPower.ToString("0"), blockPower.ToString("0"), blockPower > previousBlockPower ? UpgradeColor : null);
                }
                else
                {
                    TextBoxAddPreprocessedLine(textBox, item, "$item_blockarmor", blockPower.ToString("0"));
                }
            }

            var deflectForce = item.GetDeflectionForce(quality);
            var previousDeflectForce = item.GetDeflectionForce(item.m_quality);
            if (upgrade)
            {
                textBox.AddUpgradeLine("$item_blockforce", previousDeflectForce.ToString("0"), deflectForce.ToString("0"), deflectForce > previousDeflectForce ? UpgradeColor : null);
            }
            else
            {
                TextBoxAddPreprocessedLine(textBox, item, "$item_blockforce", deflectForce);
            }

            if (item.m_shared.m_timedBlockBonus > 1)
            {
                TextBoxAddPreprocessedLine(textBox, item, "$item_parrybonus", $"{item.m_shared.m_timedBlockBonus}x");
            }

            var modifiersTooltipString = SE_Stats.GetDamageModifiersTooltipString(item.m_shared.m_damageModifiers).Split(new [] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var damageModifier in modifiersTooltipString)
            {
                var fullString = damageModifier.Replace("<color=orange>", "").Replace("$inventory_dmgmod: ", "").Replace("</color>", "");
                TextBoxAddPreprocessedLine(textBox, item, "$inventory_dmgmod", fullString);
            }
        }

        public virtual void AddTrinketActivationTextBox(ItemDrop.ItemData item)
        {
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trinket || !item.m_shared.m_fullAdrenalineSE) return;
            var box = AddTextBox(LeftAlignedTextBoxPrefab);
            box.AddLine("<color=orange>$auga_trinket_activation</color>");
            box.AddLine("$auga_trinket_trigger");
            box.AddLine("$auga_trinket_adrenaline_help");
            box.AddLine("$auga_trinket_adrenaline_location");
            if (item.m_equipped && Player.m_localPlayer)
                box.AddLine(Localization.instance.Localize("$auga_trinket_adrenaline_current") + ": " +
                    Mathf.FloorToInt(Player.m_localPlayer.GetAdrenaline()) + " / " +
                    Player.m_localPlayer.GetMaxAdrenaline().ToString("0.#"));
            else
                box.AddLine("$auga_trinket_equip_hint");
        }

        public virtual void AddProjectileTextBox(ItemDrop.ItemData item, int quality)
        {
            var projectileTooltip = item.GetProjectileTooltip(quality);
            if (projectileTooltip.Length > 0)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                TextBoxAddPreprocessedLine(textBox, item, projectileTooltip);
            }
        }

        public virtual void AddAttackStatusTextBox(ItemDrop.ItemData item)
        {
            var skillLevel = Player.m_localPlayer.GetSkillLevel(item.m_shared.m_skillType);
            var statusEffectTooltip = item.GetStatusEffectTooltip(item.m_quality, skillLevel);
            if (statusEffectTooltip.Length > 0)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                TextBoxAddPreprocessedLine(textBox, item, statusEffectTooltip);
            }
        }

        public virtual void AddArmorTextBox(ItemDrop.ItemData item, int quality, bool upgrade)
        {
            var textBox = AddTextBox(upgrade ? UpgradeTwoColumnTextBoxPrefab : TwoColumnTextBoxPrefab);
            var armor = item.GetArmor(quality, Game.m_worldLevel);
            var previousArmor = item.GetArmor(item.m_quality, Game.m_worldLevel);
            if (upgrade)
            {
                textBox.AddUpgradeLine("$item_armor", previousArmor, armor, armor > previousArmor ? UpgradeColor : null);
            }
            else
            {
                TextBoxAddPreprocessedLine(textBox, item, "$item_armor", armor);
            }

            var modifiersTooltipString = SE_Stats.GetDamageModifiersTooltipString(item.m_shared.m_damageModifiers).Split(new [] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var damageModifier in modifiersTooltipString)
            {
                var fullString = damageModifier.Replace("<color=orange>", "").Replace("$inventory_dmgmod: ", "").Replace("</color>", "");
                TextBoxAddPreprocessedLine(textBox, item, "$inventory_dmgmod", fullString);
            }
        }

        public static ItemDrop.ItemData GetOvenFood(ItemDrop.ItemData item)
        {
            if (item == null || !ZNetScene.instance) return null;
            var oven = ZNetScene.instance.GetPrefab("piece_oven");
            var station = oven ? oven.GetComponent<CookingStation>() : null;
            if (!station) return null;
            foreach (var conversion in station.m_conversion)
                if (conversion.m_from && conversion.m_to &&
                    conversion.m_from.m_itemData.m_shared.m_name == item.m_shared.m_name &&
                    conversion.m_to.m_itemData.m_shared.m_food > 0)
                    return conversion.m_to.m_itemData;
            return null;
        }

        public static Func<ItemDrop.ItemData, float> FoodDuration;
        public void RefreshFoodDuration()
        {
            if (_food != null)
            {
                var food = _food; _food = null;
                if (Player.m_localPlayer && Player.m_localPlayer.GetFoods().Contains(food)) SetFood(food);
                else ClearData();
            }
            else if (_item != null && (_item.m_shared.m_food > 0 || GetOvenFood(_item) != null))
            {
                var item = _item; var quality = _quality; var variant = _variant; _item = null;
                SetItem(item, quality, variant);
            }
        }
        public virtual void AddFoodTextBox(ItemDrop.ItemData item) => AddFoodTextBox(item, true);

        public virtual void AddFoodTextBox(ItemDrop.ItemData item, bool showCurrentMeal)
        {
            var textBox = AddTextBox(TwoColumnTextBoxPrefab);

            const string subValueColor = "#706457";
            var healingText = $"+{item.m_shared.m_foodRegen:0.#}<color={subValueColor}> $healing_tick</color>";
            var durationText = PlayerPanelFoodController.FormatFoodTime((FoodDuration?.Invoke(item) ?? item.m_shared.m_foodBurnTime) / Mathf.Max(.001f, Game.m_foodRate));

            if (showCurrentMeal && Player.m_localPlayer != null && Player.m_localPlayer.GetFoods().Find(x => x.m_item.m_shared.m_name == item.m_shared.m_name) is Player.Food food)
            {
                var currentTime = PlayerPanelFoodController.FormatFoodTime(food.m_time / Mathf.Max(.001f, Game.m_foodRate));
                var percent = food.m_health / food.m_item.m_shared.m_food;
                textBox.AddLine("$item_food_health", $"<color=#FF8080>{item.m_shared.m_food:0} ({food.m_health:0})</color>");
                textBox.AddLine("$item_food_stamina", $"<color=#FFFF80>{item.m_shared.m_foodStamina:0} ({food.m_stamina:0})</color>");
                if (food.m_eitr > 0)
                    textBox.AddLine("$item_food_eitr", $"<color=#9C5ACF>{item.m_shared.m_foodEitr:0} ({food.m_eitr:0})</color>");
                textBox.AddLine("$item_food_regen", healingText);
                textBox.AddLine("$item_food_duration", $"{currentTime}/{durationText}");
                textBox.AddLine("$percent_effective", (percent * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%");
            }
            else
            {
                textBox.AddLine("$item_food_health", $"<color=#FF8080>{item.m_shared.m_food:0}</color>");
                textBox.AddLine("$item_food_stamina", $"<color=#FFFF80>{item.m_shared.m_foodStamina:0}</color>");
                if (item.m_shared.m_foodEitr > 0)
                    textBox.AddLine("$item_food_eitr", $"<color=#9C5ACF>{item.m_shared.m_foodEitr:0}</color>");
                textBox.AddLine("$item_food_regen", healingText);
                textBox.AddLine("$item_food_duration", durationText);
            }
        }

        public virtual void SetFood(Player.Food food)
        {
            if (_food == food)
                return;
            
            ClearData();
            _food = food;
            
            EnableObjectBackground(ObjectBackgroundType.Diamond);

            SetIcon(food.m_item.GetIcon());
            SetTopic(food.m_item.m_shared.m_name);
            SetSubtitle("$item_food");
            SetDescription(food.m_item.m_shared.m_description);

            ClearTextBoxes();
            AddFoodTextBox(food.m_item);

            Localization.instance.Localize(transform);
            OnComplexTooltipGeneratedForFood?.Invoke(this, food);
        }

        public virtual void SetStatusEffect(StatusEffect statusEffect)
        {
            if (_statusEffect == statusEffect)
                return;

            ClearData();
            _statusEffect = statusEffect;
            
            EnableObjectBackground(ObjectBackgroundType.Diamond);

            SetIcon(statusEffect.m_icon);
            SetTopic(statusEffect.m_name);
            SetSubtitle("$effect");
            SetDescription(statusEffect.m_startMessage);

            ClearTextBoxes();
            var tooltipString = statusEffect.GetTooltipString();
            var parts = tooltipString.Split(new []{ "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                textBox.Text.text = $"<color=#D1C9C2>{string.Join("\n", parts.Skip(1))}</color>";
            }
            if (parts.Length > 0)
            {
                var textBox = AddTextBox(CenteredTextBoxPrefab);
                textBox.Text.text = parts[0];
            }

            Localization.instance.Localize(transform);
            OnComplexTooltipGeneratedForStatusEffect?.Invoke(this, statusEffect);
        }

        public virtual void SetDefault(UITooltip tooltip)
        {
            if (tooltip == null)
                return;

            ClearData();

            var icon = tooltip.transform.Find("SkillGraphics/IconBG/Icon")?.GetComponent<Image>()
                ?? tooltip.transform.Find("IconBG/Icon")?.GetComponent<Image>()
                ?? tooltip.transform.Find("Icon")?.GetComponent<Image>()
                ?? tooltip.transform.Find("icon")?.GetComponent<Image>();
            if (icon != null)
            {
                EnableObjectBackground(ObjectBackgroundType.Diamond);
                SetIcon(icon.sprite);                
            }
            
            SetTopic(Localization.instance.Localize(tooltip.m_topic));
            SetDescription("");
            SetSubtitle("");
            
            var outputString = new StringBuilder();
            var foundSubtitle = false;

            using (StringReader reader = new StringReader(tooltip.m_text))
            {
                string line;
                
                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrEmpty(line.Trim()))
                    {
                        if (line.Contains("description"))
                            SetDescription(Localization.instance.Localize(line));
                        else if (line.Contains("$item_") && !line.Contains(":") && !foundSubtitle)
                        {
                            SetSubtitle(Localization.instance.Localize(line));
                            foundSubtitle = true;
                        }
                        else
                            outputString.AppendLine(line);
                    }
                }
            }

            ClearTextBoxes();
            var textBox = AddTextBox(LeftAlignedTextBoxPrefab);
            textBox.Text.text = Localization.instance.Localize(outputString.ToString());
        }
        
        public virtual void SetSkill(Skills.Skill skill)
        {
            SetSkill(skill,null);
        }
        private float _skillRefreshAt;
        public virtual void SetSkill(Skills.Skill skill, UITooltip tooltip)
        {
            if (_skill == skill && Time.unscaledTime < _skillRefreshAt)
                return;
            _skillRefreshAt = Time.unscaledTime + .25f;

            ClearData();
            _skill = skill;
            
            EnableObjectBackground(ObjectBackgroundType.Skill);
            EnableDescription(false);

            SetIcon(skill.m_info.m_icon);
            SetTopic(Localization.instance.Localize("$skill_" + skill.m_info.m_skill.ToString().ToLower()));
            SetSubtitle("$skill");

            ClearTextBoxes();
            var textBox = AddTextBox(TwoColumnTextBoxPrefab);
            float bonus = Player.m_localPlayer ? Player.m_localPlayer.GetSkills().GetSkillLevel(skill.m_info.m_skill) - Mathf.Floor(skill.m_level) : 0f;
            string levelText = Mathf.Floor(skill.m_level).ToString("0");
            if (Mathf.Abs(bonus) > .001f) levelText += $" <color=orange>({bonus:+0;-0})</color>";
            textBox.AddLine("$level", levelText);



            var textBox2 = AddTextBox(CenteredTextBoxPrefab);
            textBox2.AddLine(skill.m_info.m_description);

            AddSkillDetails(skill);

            Localization.instance.Localize(transform);
            OnComplexTooltipGeneratedForSkill?.Invoke(this, skill);
        }

        private void AddSkillDetails(Skills.Skill skill)
        {
            var player = Player.m_localPlayer;
            if (!player) return;
            var type = skill.m_info.m_skill;
            var skills = player.GetSkills();

            float factor = skills.GetSkillFactor(type);
            var box = AddTextBox(TwoColumnTextBoxPrefab);
            bool damage = type == Skills.SkillType.Swords || type == Skills.SkillType.Knives
                || type == Skills.SkillType.Clubs || type == Skills.SkillType.Polearms
                || type == Skills.SkillType.Spears || type == Skills.SkillType.Axes
                || type == Skills.SkillType.Bows || type == Skills.SkillType.Crossbows
                || type == Skills.SkillType.Unarmed || type == Skills.SkillType.Pickaxes
                || type == Skills.SkillType.WoodCutting || type == Skills.SkillType.ElementalMagic;
            if (damage)
            {
                skills.GetRandomSkillRange(out float min, out float max, type);
                // Average gain relative to raw weapon damage, respecting the native upper clamp.
                float gain = Mathf.Max(0f, (min + max - .8f) * 50f);
                box.AddLine("$overhaul_skill_damage_" + type.ToString().ToLowerInvariant(), $"+{gain:0.##} %");
                var note = AddTextBox(LeftAlignedTextBoxPrefab);
                note.AddLine("$overhaul_skill_damage_bonus_help");
            }
            if (type == Skills.SkillType.Blocking)
                box.AddLine("$overhaul_skill_block", $"+{50f * factor:0.##} %");
            if (type == Skills.SkillType.Run)
            {
                box.AddLine("$overhaul_skill_speed", $"+{25f * factor:0.##} %");

            }
            if (type == Skills.SkillType.Jump)
                box.AddLine("$overhaul_skill_jump", $"+{40f * factor:0.##} %");
            if (type == Skills.SkillType.Bows)
                box.AddLine("$overhaul_skill_draw", $"−{80f * factor:0.##} %");
            if (type == Skills.SkillType.Crossbows)
                box.AddLine("$overhaul_skill_reload", $"−{50f * factor:0.##} %");
        }

        public static void AddItemStatPreprocessor(Func<ItemDrop.ItemData, string, string, Tuple<string, string>> itemStatPreprocessor)
        {
            _itemStatPreprocessors.Add(itemStatPreprocessor);
        }

        private static Tuple<string, string> ItemStatPreprocess(ItemDrop.ItemData item, string label, string value)
        {
            var result = new Tuple<string, string>(label, value);
            foreach (var itemStatPreprocessor in _itemStatPreprocessors)
            {
                result = itemStatPreprocessor(item, result.Item1, result.Item2);
            }

            return result;
        }

        protected virtual void TextBoxAddPreprocessedLine(TooltipTextBox textBox, ItemDrop.ItemData item, object label, bool localize = true)
        {
            var result = ItemStatPreprocess(item, label.ToString(), null);
            if (result != null)
                textBox.AddLine(result.Item1, localize);
        }

        protected virtual void TextBoxAddPreprocessedLine(TooltipTextBox textBox, ItemDrop.ItemData item, object label, object value, bool localize = true)
        {
            var result = ItemStatPreprocess(item, label.ToString(), value.ToString());
            if (result != null)
                textBox.AddLine(result.Item1, result.Item2, localize);
        }

        protected virtual void TextBoxAddPreprocessedLine(TooltipTextBox textBox, ItemDrop.ItemData item, object label, object value, object parenthetical, bool localize = true)
        {
            var result = ItemStatPreprocess(item, label.ToString(), textBox.GenerateParenthetical(value, parenthetical));
            if (result != null)
                textBox.AddLine(result.Item1, result.Item2, localize);
        }
    }
}
