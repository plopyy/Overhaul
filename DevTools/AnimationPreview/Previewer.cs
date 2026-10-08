using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace OverhaulAnimationPreview
{
    // Plays the selected FBX on the local player after the game's Animator, every frame.
    [DefaultExecutionOrder(32000)]
    public sealed class Previewer : MonoBehaviour
    {
        public static Previewer Instance { get; private set; }
        public List<string> Files { get; } = new List<string>();
        public string Selected { get; private set; }
        public FbxClip Clip { get; private set; }
        public string Status { get; private set; } = "";
        public float Time { get; private set; }
        public bool Active { get; private set; }   // the clip drives the character
        public bool Playing { get; private set; }
        public bool Loop = true;
        public bool RootMotion;
        public float Speed = 1f;
        public float MoveSpeed = 1f;
        public event Action Changed;

        private PoseMapper mapper;
        private Player mapperPlayer;
        private DateTime loadedStamp;
        private float nextScan;
        private Vector3 lastRoot;
        private PreviewWindow window;

        private void Awake() => Instance = this;

        // Clip travel in metres: exact once matched to the player, else from the player's hips height (0.96 m).
        public float TravelMeters => Clip == null ? 0 : mapper != null ? Clip.Travel * mapper.Scale : Clip.Travel * 0.96f / Mathf.Max(1e-4f, Clip.HipsHeight);

        private void Update()
        {
            if (UnityEngine.Time.unscaledTime >= nextScan) { nextScan = UnityEngine.Time.unscaledTime + 1f; Scan(); }
            if (Hud.instance && !window) window = PreviewWindow.Create(Hud.instance, this);
            if (window && Plugin.ToggleKey.Value.IsDown() && Player.m_localPlayer && !Console.IsVisible() && !(Chat.instance && Chat.instance.HasFocus()))
                window.Visible = !window.Visible;
        }

        // Lists the .fbx files and reloads the selected one when it is exported again.
        private void Scan()
        {
            var files = Directory.Exists(Plugin.Folder)
                ? Directory.GetFiles(Plugin.Folder, "*.fbx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            bool changed = !files.SequenceEqual(Files);
            if (changed) { Files.Clear(); Files.AddRange(files); }
            if (Selected != null && File.Exists(Selected) && File.GetLastWriteTimeUtc(Selected) != loadedStamp) { Load(Selected, keepTime: true); changed = true; }
            if (changed) Changed?.Invoke();
        }

        public void Select(string file) { Load(file, keepTime: false); Changed?.Invoke(); }

        private void Load(string file, bool keepTime)
        {
            Selected = file; mapper = null;
            try
            {
                loadedStamp = File.GetLastWriteTimeUtc(file);
                Clip = FbxClip.Load(file);
                Status = "";
                if (!keepTime)
                {
                    Time = 0; Playing = false; Active = false;
                    // Start in the clip's own mode; either can then be switched.
                    RootMotion = Clip.HasRootMotion;
                    MoveSpeed = Clip.HasRootMotion ? 1f : 4f;
                }
                Time = Mathf.Min(Time, Clip.Duration);
            }
            catch (Exception error)
            {
                Clip = null; Active = Playing = false;
                Status = Localization.instance.Localize("$animpreview_error") + " : " + error.Message;
            }
        }

        public void TogglePlay()
        {
            if (Clip == null) return;
            if (!Playing && Time >= Clip.Duration) Time = 0;
            Playing = !Playing; Active = true; lastRoot = Vector3.zero; mapper = null;
            Changed?.Invoke();
        }

        public void Stop() { Playing = false; Active = false; Time = 0; Changed?.Invoke(); }

        public void Seek(float seconds)
        {
            if (Clip == null) return;
            Time = Mathf.Clamp(seconds, 0, Clip.Duration); Active = true; mapper = null;
        }

        private void LateUpdate()
        {
            var player = Player.m_localPlayer;
            if (!Active || Clip == null || !player || player.IsDead() || !player.m_animator) return;
            if (mapper == null || mapperPlayer != player)
            {
                mapper = CreateMapper(player);
                mapperPlayer = player;
                if (mapper == null) { Active = false; return; }
                lastRoot = mapper.Apply(Time);
            }
            bool wrapped = false;
            if (Playing)
            {
                Time += UnityEngine.Time.deltaTime * Speed;
                if (Time >= Clip.Duration)
                {
                    if (Loop && Clip.Duration > 0) { Time %= Clip.Duration; wrapped = true; }
                    else { Time = Clip.Duration; Playing = false; Changed?.Invoke(); }
                }
            }
            var root = mapper.Apply(Time);
            if (wrapped) lastRoot = root;
            if (RootMotion && Playing)
            {
                // Clip travel scaled by MoveSpeed; for an in-place clip, MoveSpeed is a forward speed in m/s.
                var step = Clip.HasRootMotion ? (root - lastRoot) * MoveSpeed : Vector3.forward * (MoveSpeed * UnityEngine.Time.deltaTime);
                step.y = 0;
                // The character moves itself: Valheim turns accumulated root motion into velocity.
                player.m_rootMotion += player.transform.TransformVector(step);
            }
            lastRoot = root;
        }

        // Matches the clip to the player's skeleton, against the bones' rest pose in the prefab.
        private PoseMapper CreateMapper(Player player)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab("Player") : null;
            var prefabRoot = prefab ? prefab.GetComponentInChildren<Animator>(true)?.transform : null;
            if (!prefabRoot) { Status = Localization.instance.Localize("$animpreview_error"); return null; }
            var rest = prefabRoot.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => (g.First().localRotation, g.First().localPosition));
            var result = new PoseMapper(Clip, player.m_animator.transform, rest);
            var missing = result.Missing.ToList();
            Status = missing.Count == 0 ? "" : Localization.instance.Localize("$animpreview_missing") + " : " + string.Join(", ", missing.Take(6));
            Changed?.Invoke();
            return result;
        }
    }
}
