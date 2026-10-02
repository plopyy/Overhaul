using System;
using Newtonsoft.Json;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Leveling
{
    public sealed class OverhaulCharacter : MonoBehaviour
    {
        internal const string SaveKey = "Overhaul.Character";
        public OverhaulCharacterData Data = new OverhaulCharacterData();
        public long TotalExperience => Data.TotalExperience;
        public long CurrentExperience => Data.CurrentExperience;
        public int Level => Data.Level;
        public bool Ready;
        internal Player Player;
        internal float CombatUntil;
        internal bool InvalidSave;
        private float nextHello, nextHeal;
        public bool InCombat => Time.time < CombatUntil;

        public static OverhaulCharacter Get(Player player)
        {
            if (!player) return null;
            var state = player.GetComponent<OverhaulCharacter>();
            if (!state) { state=player.gameObject.AddComponent<OverhaulCharacter>(); state.Player=player; }
            return state;
        }

        internal void Load()
        {
            Ready=false; InvalidSave=false;
            try
            {
                Data=Player.m_customData.TryGetValue(SaveKey,out string json)?JsonConvert.DeserializeObject<OverhaulCharacterData>(json):new OverhaulCharacterData();
                if(Data==null || Data.Version!=1 || Data.TotalExperience<0)throw new InvalidOperationException("Invalid progression version/data");
                if(ZNet.instance && ZNet.instance.IsServer()) { Reconcile(); Ready=true; }
            }
            catch(Exception error) { InvalidSave=true; Log.LogError("Progression preserved without overwriting unreadable save: "+error); }
        }

        internal void Reconcile()
        {
            int spent=Data.AllocatedStats==null?0:System.Linq.Enumerable.Sum(Data.AllocatedStats.Values);
            bool changed=LevelingSystem.Reconcile(Data);
            if((changed || (spent>0 && Data.AllocatedStats.Count==0)) && Player==global::Player.m_localPlayer)
                Player.Message(MessageHud.MessageType.Center,LevelingText.Get("reconciled"));
        }

        internal void Store()
        {
            if (Ready) { NutritionDuration.Sync(Player); NutritionDuration.RefreshTooltip(Player); }
            if(!InvalidSave && Ready)
            {
                Player.m_customData[SaveKey]=JsonConvert.SerializeObject(Data);
                if(Player.m_nview && Player.m_nview.IsValid() && Player.m_nview.IsOwner())Player.m_nview.GetZDO().Set("overhaul_loot_chance",(float)Math.Min(1,Bonus("bonus_loot")));
            }
        }

        public double Bonus(string id)
        {
            if(!Ready || !Data.AllocatedStats.TryGetValue(id,out int rank) || !LevelingConfig.Current.Stats.TryGetValue(id,out StatRule rule))return 0;
            return rank*rule.PerPoint;
        }
        public bool HasPassive(string id)=>Ready && Data.Passive==id && Level>=LevelingConfig.Current.PassiveLevel;

        private void Update()
        {
            if(!Player || Player!=global::Player.m_localPlayer || InvalidSave || !ZNet.instance)return;
            if(!Ready && Time.unscaledTime>=nextHello)
            {
                nextHello=Time.unscaledTime+5;
                if(ZNet.instance.IsServer()){Reconcile();Ready=true;Store();}
                else LevelingNetwork.Hello();
            }
            if(Ready && HasPassive("vitality") && !Player.IsDead() && Time.time>=nextHeal)
            { nextHeal=Time.time+1;Player.Heal((float)LevelingConfig.Current.VitalityRegen); }
        }
    }

    [HarmonyPatch(typeof(Player),nameof(Player.Load))]
    internal static class LevelingLoadPatch { private static void Postfix(Player __instance)=>OverhaulCharacter.Get(__instance).Load(); }
    [HarmonyPatch(typeof(Player),nameof(Player.Save))]
    internal static class LevelingSavePatch { private static void Prefix(Player __instance)=>OverhaulCharacter.Get(__instance).Store(); }
    [HarmonyPatch(typeof(Player),nameof(Player.SetLocalPlayer))]
    internal static class LevelingLocalPatch { private static void Postfix(Player __instance)=>OverhaulCharacter.Get(__instance); }
}
