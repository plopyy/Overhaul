using System.Collections;
using System.Collections.Generic;
using System.IO;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.PostProcessing;
using UnityEngine.UI;
using TMPro;

namespace AugaUnity
{
    public class AugaCharacterSelect : MonoBehaviour
    {
        // Bound in the original SelectCharacter prefab by the authoring migration.
        public Button BackButton, StartButton, RemoveButton, NewButton, NewBigButton, ManageSavesButton;
        public Button RemoveYesButton, RemoveNoButton, LeftButton, RightButton;
        public GameObject RemoveDialog;
        public TMP_Text RemoveName, NativeName, NativeSourceInfo, NativeFileSource;
        public CharacterSelectPortrait CharacterPortraitPrefab;
        public Scrollbar ScrollBar;
        public RectTransform CharacterList;
        public RenderTexture RenderTexture;
        public GameObject SourceInfoPanel;
        public Text SourceInfoContent;

        private readonly List<CharacterSelectPortrait> _portraits = new List<CharacterSelectPortrait>();
        private bool _onFirstUpdate;

        [UsedImplicitly]
        public void OnEnable()
        {
            _onFirstUpdate = false;
            UpdateCharacterList();
        }

        public void UpdateCharacterList()
        {
            if (!FejdStartup.instance || FejdStartup.instance.m_profiles == null) return;
            foreach (var portrait in _portraits)
            {
                Destroy(portrait.gameObject);
            }
            _portraits.Clear();

            for (var index = 0; index < FejdStartup.instance.m_profiles.Count; index++)
            {
                var profile = FejdStartup.instance.m_profiles[index];
                var portrait = Instantiate(CharacterPortraitPrefab, CharacterList, false);
                portrait.Setup(profile, index, RenderTexture);
                _portraits.Add(portrait);
            }

            var showSourceInfoPanel = !FileHelpers.CloudStorageSupportedAndEnabled;
            SourceInfoContent.text = "";
            if (FejdStartup.instance.m_profileIndex >= 0 && FejdStartup.instance.m_profileIndex < FejdStartup.instance.m_profiles.Count)
            {
                var selectedProfile = FejdStartup.instance.m_profiles[FejdStartup.instance.m_profileIndex];
                if (selectedProfile != null && selectedProfile.m_fileSource == FileHelpers.FileSource.Legacy)
                {
                    showSourceInfoPanel = true;
                    SourceInfoContent.text = Localization.instance.Localize("$menu_legacynotice \n\n");
                }
            }

            if (!FileHelpers.CloudStorageSupportedAndEnabled)
            {
                SourceInfoContent.text += Localization.instance.Localize("$menu_cloudsavesdisabled");
            }

            SourceInfoPanel.gameObject.SetActive(showSourceInfoPanel);
        }

        public void LateUpdate()
        {
            if (!FejdStartup.instance || FejdStartup.instance.m_profiles == null) return;
            if (!_onFirstUpdate)
            {
                _onFirstUpdate = true;

                var currentIndex = FejdStartup.instance.m_profileIndex;
                if (currentIndex >= 0 && currentIndex < _portraits.Count && _portraits.Count > 1)
                {
                    ScrollBar.value = 1.0f - (currentIndex / (_portraits.Count - 1.0f));
                }
            }
        }
    }

    public class CharacterSelectPortrait : MonoBehaviour
    {
        public RawImage Image;
        public Text CharacterName;
        public Button Button;
        public GameObject Selected;
        public Text StatsText;
        public Image LocalSave;
        public Image LegacySave;
        public Image CloudSave;

        public Color NameColorSelected;
        public Color StatsTextColorSelected;

        private PlayerProfile _profile;
        private int _index;
        private Color _originalNameColor;
        private Color _originalStatsTextColor;

        public void Awake()
        {
            _originalNameColor = CharacterName.color;
            _originalStatsTextColor = StatsText.color;
            Update();
        }

        public void Setup(PlayerProfile profile, int index, RenderTexture renderTexture)
        {
            if (Localization.instance != null) Localization.instance.Localize(transform);
            _profile = profile;
            _index = index;
            CharacterName.text = profile.m_playerName;
            Button.onClick.AddListener(() => FejdStartup.instance.SetSelectedProfile(_profile.m_filename));
            StatsText.text = $"{profile.GetStat(PlayerStatType.Deaths)}\n{profile.GetStat(PlayerStatType.Builds)}\n{profile.GetStat(PlayerStatType.Crafts)}";

            Image.texture = AugaCharacterSelectPhotoBooth.GetPhoto(profile);

            LocalSave?.gameObject.SetActive(profile.m_fileSource == FileHelpers.FileSource.Local);
            LegacySave?.gameObject.SetActive(profile.m_fileSource == FileHelpers.FileSource.Legacy);
            CloudSave?.gameObject.SetActive(profile.m_fileSource == FileHelpers.FileSource.Cloud || profile.m_fileSource == FileHelpers.FileSource.Auto);

            Update();
        }

        public void Update()
        {
            if (!FejdStartup.instance) return;
            if (_profile != null) Image.texture = AugaCharacterSelectPhotoBooth.GetPhoto(_profile);
            var selected = _index == FejdStartup.instance.m_profileIndex;
            Selected.SetActive(selected);
            CharacterName.color = selected ? NameColorSelected : _originalNameColor;
            StatsText.color = selected ? StatsTextColorSelected : _originalStatsTextColor;
        }

    }
}
