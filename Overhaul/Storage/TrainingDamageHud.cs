using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Storage
{
    internal sealed class TrainingDamageHud : MonoBehaviour
    {
        private static TrainingDamageHud instance;
        private GameObject panel;
        private RectTransform reset;
        private TMP_Text dps, peak;
        private Player player;
        private float nextScan;
        private bool nearby;
        private void Awake() => instance = this;
        internal static bool BlocksPointer(Vector2 position, bool locked)
        {
            if (locked || !instance || !instance.reset || !instance.reset.gameObject.activeInHierarchy) return false;
            var canvas = instance.reset.GetComponentInParent<Canvas>();
            return RectTransformUtility.RectangleContainsScreenPoint(instance.reset, position,
                canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null);
        }
        internal static bool InRange(Vector3 playerPosition, Vector3 dummyPosition) => (playerPosition - dummyPosition).sqrMagnitude <= 400f;
        private void Update()
        {
            var local = Player.m_localPlayer;
            if (player != local)
            {
                player = local; nearby = false; nextScan = 0;
                TrainingDamageMeter.Session.Reset(double.NegativeInfinity);
                TrainingDamageMeter.Session.Cutoff = double.NegativeInfinity;
            }
            TrainingDamageMeter.Session.Tick(TrainingDamageMeter.Now);
            if (!player) { if (panel) panel.SetActive(false); return; }
            if (!panel)
            {
                var prefab = Auga.Auga.Assets.TrainingMeter;
                if (!prefab) return;
                panel = Instantiate(prefab, transform.Find("hudroot"), false);
                var canvas = panel.GetComponentInParent<Canvas>();
                if (canvas && !canvas.GetComponent<GraphicRaycaster>()) canvas.gameObject.AddComponent<GraphicRaycaster>();
                Localization.instance.Localize(panel.transform);
                dps = panel.transform.Find("DpsValue").GetComponent<TMP_Text>();
                peak = panel.transform.Find("PeakValue").GetComponent<TMP_Text>();
                reset = (RectTransform)panel.transform.Find("Reset");
                panel.transform.Find("Reset").GetComponent<Button>().onClick.AddListener(() => {
                    TrainingDamageMeter.Session.Reset(TrainingDamageMeter.Now); Refresh();
                });
            }
            if (Time.unscaledTime >= nextScan)
            {
                bool wasNearby=nearby;
                nextScan = Time.unscaledTime + .25f; nearby = false;
                foreach (var character in Character.GetAllCharacters())
                    if (TrainingDummyProtection.IsDummy(character) && InRange(player.transform.position, character.transform.position))
                    { nearby = true; break; }
                if(wasNearby && !nearby)TrainingDamageMeter.Session.Reset(TrainingDamageMeter.Now);
            }
            panel.SetActive(nearby && !player.IsDead());
            if (panel.activeSelf) Refresh();
        }
        private void Refresh()
        {
            var session = TrainingDamageMeter.Session;
            dps.text = session.Duration > 0 ? session.Dps.ToString("0.0") : "—";
            peak.text = session.Count > 0 ? session.Peak.ToString("0.0") : "—";
        }
        private void OnDestroy()
        {
            if (instance == this) instance = null;
            TrainingDamageMeter.Session.Reset(double.NegativeInfinity);
            TrainingDamageMeter.Session.Cutoff = double.NegativeInfinity;
        }
    }
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class TrainingResetInput
    {
        private static bool Prefix(ref bool __result)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !TrainingDamageHud.BlocksPointer(mouse.position.ReadValue(), Cursor.lockState == CursorLockMode.Locked)) return true;
            __result = false;
            return false;
        }
    }
    [HarmonyPatch(typeof(Hud), "Awake")]
    internal static class TrainingDamageHudInstall
    {
        private static void Postfix(Hud __instance)
        {
            if (!__instance.GetComponent<TrainingDamageHud>()) __instance.gameObject.AddComponent<TrainingDamageHud>();
        }
    }
}
