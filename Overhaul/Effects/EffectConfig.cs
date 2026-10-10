using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Overhaul.Rarity;

namespace Overhaul.Effects
{
    // One effect of Effects.cfg: every source (enchantments today) adds to its total, which is kept within [Min/Max].
    internal sealed class EffectDef
    {
        internal string Id, NameKey, DescriptionKey;
        internal float Min = float.MinValue, Max = float.MaxValue;
        internal bool IsPercent;
        internal string Name => RarityConfig.Localize(NameKey);
        internal string Description => RarityConfig.Localize(DescriptionKey);
        // "+10", "-5 %"
        internal string Format(float value) => (value >= 0 ? "+" : "") + value.ToString("0.#") + (IsPercent ? " %" : "");
    }

    internal sealed class EffectData
    {
        internal readonly Dictionary<string, EffectDef> Effects = new Dictionary<string, EffectDef>(StringComparer.OrdinalIgnoreCase);
        internal string Text = "";
    }

    // Effects.cfg: the effects used by the whole mod. The server's file is the reference, sent to every client.
    internal static class EffectConfig
    {
        internal const string FileName = "Effects.cfg";
        private const string RequestRpc = "Overhaul_EffectsRequest", ConfigRpc = "Overhaul_EffectsConfig";
        internal static EffectData Local, Current = new EffectData();

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(EffectConfig).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("Effects.cfg unreadable: " + e.Message); }
        }

        private static void Use(EffectData data) { Current = data; Effects.Invalidate(); }

        internal static EffectData Parse(string text)
        {
            var data = new EffectData { Text = text };
            foreach (var section in RarityConfig.Sections(text))
            {
                if (!section.Key.StartsWith("Effect_", StringComparison.OrdinalIgnoreCase)) { Utility.Log.LogWarning("Effects.cfg: unknown section [" + section.Key + "]"); continue; }
                string id = section.Key.Substring("Effect_".Length);
                var v = section.Value;
                var effect = new EffectDef
                {
                    Id = id, NameKey = RarityConfig.Get(v, "Name", id), DescriptionKey = RarityConfig.Get(v, "Description", ""),
                    IsPercent = RarityConfig.Get(v, "IsPercent", "false").Equals("true", StringComparison.OrdinalIgnoreCase)
                };
                // Value = [min/max]
                string[] range = RarityConfig.Get(v, "Value", "").Trim('[', ']').Split('/');
                if (range.Length == 2) { effect.Min = RarityConfig.ParseFloat(range[0], float.MinValue); effect.Max = RarityConfig.ParseFloat(range[1], float.MaxValue); }
                if (!Effects.Known(id)) Utility.Log.LogWarning("Effects.cfg: effect " + id + " has no code yet, it does nothing");
                data.Effects[id] = effect;
            }
            return data;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register(RequestRpc, rpc => rpc.Invoke(ConfigRpc, Current.Text));
                else peer.m_rpc.Register<string>(ConfigRpc, (rpc, text) =>
                {
                    try { Use(Parse(text)); Utility.Log.LogInfo("Effects received from the server: " + Current.Effects.Count + " effects"); }
                    catch (Exception e) { Utility.Log.LogWarning("Effects from the server rejected: " + e.Message); }
                });
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Spawned
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !ZNet.instance) return;
                if (ZNet.instance.IsServer()) { Use(Local ?? Current); return; }
                ZNet.instance.GetServerRPC()?.Invoke(RequestRpc);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class LocalRules { private static void Prefix() { if (Local != null) Use(Local); } }
    }
}
