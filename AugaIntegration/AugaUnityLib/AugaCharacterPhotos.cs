using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace AugaUnity
{
    public class AugaCharacterSelectPhotoBooth : MonoBehaviour
    {
        public RenderTexture RenderTexture;
        public PostProcessingProfile Profile;
        // Compatibility with the old full-menu patch; the selected preview is no longer replaced.
        public static bool TakingPhotos => false;
        private static readonly Dictionary<string, Texture2D> Photos = new Dictionary<string, Texture2D>();
        private Camera camera;
        private RenderTexture target;
        private GameObject model;
        private bool busy;
        private static string Key(PlayerProfile profile) => profile.m_fileSource + ":" + profile.GetFilename();
        public static Texture2D GetPhoto(PlayerProfile profile)
        { Photos.TryGetValue(Key(profile), out var photo); return photo; }

        public IEnumerator Start()
        {
            while (FejdStartup.instance && (FejdStartup.instance.m_profiles == null || !FejdStartup.instance.m_mainCamera)) yield return null;
            while (FejdStartup.instance)
            {
                var startup = FejdStartup.instance;
                for (int index = 0; startup.m_profiles != null && index < startup.m_profiles.Count; index++)
                    if (!Photos.ContainsKey(Key(startup.m_profiles[index]))) yield return TakePhoto(index);
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        public IEnumerator TakePhoto(int profileIndex)
        {
            if (busy || !FejdStartup.instance || FejdStartup.instance.m_profiles == null
                || profileIndex < 0 || profileIndex >= FejdStartup.instance.m_profiles.Count) yield break;
            var startup = FejdStartup.instance;
            var profile = startup.m_profiles[profileIndex];
            busy = true;
            try
            {
                if (!camera)
                {
                    camera = new GameObject("Auga character portrait camera").AddComponent<Camera>();
                    camera.CopyFrom(startup.m_mainCamera.GetComponent<Camera>());
                    camera.enabled = false;
                    camera.fieldOfView = 11f;
                    camera.cullingMask = 1 << 31;
                    target = new RenderTexture(RenderTexture ? RenderTexture.width : 120, RenderTexture ? RenderTexture.height : 150, 24);
                    camera.targetTexture = target;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(0.1f, 0.085f, 0.06f, 1f);
                }
                bool previous = ZNetView.m_forceDisableInit;
                try
                {
                    ZNetView.m_forceDisableInit = true;
                    model = Instantiate(startup.m_playerPrefab, startup.m_characterPreviewPoint.position, startup.m_characterPreviewPoint.rotation);
                }
                finally { ZNetView.m_forceDisableInit = previous; }
                var body = model.GetComponent<Rigidbody>();
                if (body) { body.isKinematic = true; body.detectCollisions = false; }
                foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                if (!TryLoadProfile(profile, model.GetComponent<Player>()))
                { Photos[Key(profile)] = null; yield break; }
                foreach (var animator in model.GetComponentsInChildren<Animator>(true))
                { animator.updateMode = AnimatorUpdateMode.Normal; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                for (int frame = 0; frame < 15; frame++)
                {
                    HideModel();
                    yield return null;
                }
                var head = Utils.FindChild(model.transform, "Head");
                if (!head) { Photos[Key(profile)] = null; yield break; }
                camera.transform.position = startup.m_cameraMarkerCharacter.position;
                camera.transform.LookAt(head.position + new Vector3(0, -0.05f, 0));
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                foreach (var node in model.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = 31;
                foreach (var renderer in renderers) renderer.forceRenderingOff = false;
                var previousTarget = UnityEngine.RenderTexture.active;
                try
                {
                    camera.Render();
                    UnityEngine.RenderTexture.active = target;
                    var photo = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    photo.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    photo.Apply();
                    string key = Key(profile);
                    if (Photos.TryGetValue(key, out var old)) Destroy(old);
                    Photos[key] = photo;
                }
                finally
                {
                    UnityEngine.RenderTexture.active = previousTarget;
                    foreach (var renderer in renderers) if (renderer) renderer.forceRenderingOff = true;
                }
            }
            finally
            {
                if (model) { model.SetActive(false); Destroy(model); }
                model = null;
                busy = false;
            }
        }

        private void HideModel()
        {
            if (!model) return;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true)) renderer.forceRenderingOff = true;
            foreach (var audio in model.GetComponentsInChildren<AudioSource>(true)) audio.enabled = false;
        }
        private static bool TryLoadProfile(PlayerProfile profile, Player player)
        {
            try { profile.LoadPlayerData(player); return true; }
            catch (Exception error)
            {
                Debug.LogWarning("[Auga] Unable to render portrait for " + profile.GetFilename() + ": " + error.Message);
                return false;
            }
        }
        public static string GetOutputFilePathForProfile(PlayerProfile profile)
            => Path.Combine(SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local), profile.m_filename + ".png");

        public void OnDestroy()
        {
            StopAllCoroutines();
            if (model) Destroy(model);
            if (camera) Destroy(camera.gameObject);
            if (target) { target.Release(); Destroy(target); }
            foreach (var photo in Photos.Values) if (photo) Destroy(photo);
            Photos.Clear();
        }
    }
}
