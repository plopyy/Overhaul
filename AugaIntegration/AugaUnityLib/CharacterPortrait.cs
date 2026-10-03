using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.PostProcessing;
using UnityEngine.UI;
using UnityStandardAssets.ImageEffects;

namespace AugaUnity
{
    public enum PortraitMode
    {
        Hair,
        Beard
    }

    public class CharacterPortraitsController : MonoBehaviour
    {
        public CharacterPortrait PortraitPrefab;
        public RectTransform PortraitList;
        public RenderTexture RenderTexture;
        public PortraitMode Mode;
        public PostProcessingProfile Profile;

        private Camera _camera;
        private bool _ownsTarget;
        private Transform _lookTarget;
        private PortraitMode _currentMode;
        private const float FOV = 11;
        private readonly Vector3 _offset = new Vector3(0, -0.05f, 0);
        private PlayerCustomizaton _playerCustomizaton;
        private readonly List<CharacterPortrait> _characterPortraits = new List<CharacterPortrait>();
        private readonly Renderer[] _noRenderers = Array.Empty<Renderer>();

        [UsedImplicitly]
        public void Awake()
        {
            _playerCustomizaton = FejdStartup.instance.m_newCharacterPanel.GetComponent<PlayerCustomizaton>();

            RenderTexture = new RenderTexture(RenderTexture.width, RenderTexture.height, 24);
            _ownsTarget = true;
            _camera = GetCamera(RenderTexture, Profile);
            _camera.name = "AugaCamera NewCharPortraits";

            _currentMode = Mode;
        }

        public void SwitchToHairMode()
        {
            Mode = PortraitMode.Hair;
        }

        public void SwitchToBeardMode()
        {
            Mode = PortraitMode.Beard;
        }

        public static Camera GetCamera(RenderTexture renderTexture, PostProcessingProfile profile)
        {
            var camera = new GameObject("Auga customization camera").AddComponent<Camera>();
            camera.CopyFrom(FejdStartup.instance.m_mainCamera.GetComponent<Camera>());
            camera.fieldOfView = FOV;
            camera.targetTexture = renderTexture;
            camera.enabled = false;

            camera.transform.position = FejdStartup.instance.m_cameraMarkerCharacter.position;
            camera.transform.rotation = FejdStartup.instance.m_cameraMarkerCharacter.rotation;


            return camera;
        }

        public void InitializeChraracterPortraits()
        {
            foreach (var characterPortrait in _characterPortraits)
            {
                Destroy(characterPortrait.gameObject);
            }
            _characterPortraits.Clear();

            var count = Mode == PortraitMode.Hair ? _playerCustomizaton.m_hairs.Count : _playerCustomizaton.m_beards.Count;
            for (var i = 0; i < count; i++)
            {
                var item = Mode == PortraitMode.Hair ? _playerCustomizaton.m_hairs[i] : _playerCustomizaton.m_beards[i];
                if (!item || item.m_itemData.m_shared.m_toolTier > _playerCustomizaton.m_hairToolTier) continue;
                var characterPortrait = Instantiate(PortraitPrefab, PortraitList);
                characterPortrait.ItemIndex = i;
                var index = i;
                characterPortrait.Button.onClick.AddListener(() => OnPortraitClick(index));
                characterPortrait.Setup(_playerCustomizaton, Mode, index);
                _characterPortraits.Add(characterPortrait);
            }
        }

        public void OnPortraitClick(int index)
        {
            switch (Mode)
            {
                case PortraitMode.Hair:
                    _playerCustomizaton.SetHair(index);
                    break;

                default:
                    if (_playerCustomizaton.GetPlayer().GetPlayerModel() == 0) _playerCustomizaton.SetBeard(index);
                    break;
            }
        }

        public void LateUpdate()
        {
            if (!_playerCustomizaton || !_playerCustomizaton.LoadHair() || !FejdStartup.instance || !FejdStartup.instance.m_playerInstance) return;
            var newLookTarget = Utils.FindChild(FejdStartup.instance.m_playerInstance.transform, "Head");
            if (!newLookTarget) return;
            if (_characterPortraits.Count == 0 || _lookTarget != newLookTarget || _currentMode != Mode)
            {
                InitializeChraracterPortraits();
            }

            _currentMode = Mode;
            _lookTarget = newLookTarget;
            _camera.transform.LookAt(_lookTarget.position + _offset);

            var player = _playerCustomizaton.GetPlayer();
            var visEquip = player.m_visEquipment;
            var itemInstance = Mode == PortraitMode.Hair ? visEquip.m_hairItemInstance : visEquip.m_beardItemInstance;
            var renderers = itemInstance?.GetComponentsInChildren<Renderer>() ?? _noRenderers;
            var previousVisibility = renderers.Select(r => r.forceRenderingOff).ToArray();
            try
            {
            foreach (var renderer in renderers)
            {
                renderer.forceRenderingOff = true;
            }

            var currentIndex = Mode == PortraitMode.Hair ? _playerCustomizaton.GetHairIndex() : _playerCustomizaton.GetBeardIndex();
            for (var index = 0; index < _characterPortraits.Count; index++)
            {
                var characterPortrait = _characterPortraits[index];
                characterPortrait.Selected.SetActive(characterPortrait.ItemIndex == currentIndex);
                characterPortrait.DoRender(visEquip, _camera);
            }

            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++) if (renderers[i]) renderers[i].forceRenderingOff = previousVisibility[i];
            }
        }
        public void OnDisable()
        {
            foreach (var portrait in _characterPortraits) if (portrait) Destroy(portrait.gameObject);
            _characterPortraits.Clear();
        }
        public void OnDestroy()
        {
            if (_camera) Destroy(_camera.gameObject);
            if (_ownsTarget && RenderTexture) { RenderTexture.Release(); Destroy(RenderTexture); }
        }
    }

    public class CharacterPortrait : MonoBehaviour
    {
        public int ItemIndex;
        public RawImage Image;
        public Button Button;
        public GameObject Selected;

        private Texture _texture;
        private GameObject _attachedItem;
        private List<Renderer> _renderers;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private static readonly int SkinColor = Shader.PropertyToID("_SkinColor");

        public void Setup(PlayerCustomizaton playerCustomizaton, PortraitMode mode, int index)
        {
            var player = playerCustomizaton.GetPlayer();
            var visEquip = player.m_visEquipment;
            var items = mode == PortraitMode.Hair ? playerCustomizaton.m_hairs : playerCustomizaton.m_beards;
            var itemName = items[index].gameObject.name;
            var itemHash = itemName.GetStableHashCode();

            _attachedItem = visEquip.AttachItem(itemHash, 0, visEquip.m_helmet);
            _renderers = _attachedItem != null ? _attachedItem.GetComponentsInChildren<Renderer>().ToList() : new List<Renderer>();
            foreach (var renderer in _renderers) renderer.forceRenderingOff = true;
            var tooltip = GetComponent<UITooltip>();
            if (tooltip) tooltip.m_text = items[index].m_itemData.m_shared.m_name;
        }

        public void DoRender(VisEquipment visEquip, Camera camera)
        {
            try
            {
            var hairColor = Utils.Vec3ToColor(visEquip.m_nview.GetZDO()?.GetVec3("HairColor", Vector3.one) ?? visEquip.m_hairColor);
            foreach (var renderer in _renderers)
            {
                renderer.forceRenderingOff = false;
                renderer.GetPropertyBlock(_properties);
                _properties.SetColor(SkinColor, hairColor);
                renderer.SetPropertyBlock(_properties);
            }

            camera.Render();
            SetTexture(camera.targetTexture);

            }
            finally
            {
                foreach (var renderer in _renderers) if (renderer) renderer.forceRenderingOff = true;
            }
        }

        public void SetTexture(RenderTexture renderTexture)
        {
            if (_texture == null)
            {
                _texture = new Texture2D(renderTexture.width, renderTexture.height, renderTexture.graphicsFormat, renderTexture.mipmapCount, TextureCreationFlags.None);
                Image.texture = _texture;
            }

            Graphics.ConvertTexture(renderTexture, _texture);
        }

        [UsedImplicitly]
        public void OnDestroy()
        {
            _renderers?.Clear();
            Destroy(_attachedItem);
            Destroy(_texture);
        }
    }
}
