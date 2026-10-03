using System;
using System.IO;
using BepInEx.Configuration;
using Jotunn;
using Jotunn.Utils;
using System.Linq;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;
using Overhaul.Utility;

namespace Overhaul.Dungeons
{
    internal static class BossEncounter
    {
        internal const string ChestName = "Overhaul_ForestBossChest";
        internal const string SwampChestName = "Overhaul_SwampBossChest";
        internal const string MistlandsChestName = "Overhaul_MistlandsBossChest";
        internal static readonly string[] MistlandsRoster = { "SeekerBrute" };
        internal const string MountainChestName = "Overhaul_MountainBossChest";
        internal static readonly string[] MountainRoster = { "Fenring_Cultist", "StoneGolem", "Ulv" };
        internal static string[] MountainLoot => BossLootData.For("Mountain").Select(e => e.Prefab).ToArray();
        internal static readonly string[] SwampRoster = { "Draugr_Elite", "BlobElite", "Abomination", "Wraith", "Surtling" };
        internal static void RemoveLegacyStars(BepInEx.Configuration.ConfigFile config)
        {
            // Bind temporarily to consume old orphan entries, then remove them.
            // The data file is authoritative; these are no longer BepInEx options.
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                foreach (var name in Roster.Concat(SwampRoster))
                {
                    var entry = config.Bind("DungeonBossStars", name, 0);
                    config.Remove(entry.Definition);
                }
            }
            finally { config.SaveOnConfigSet = save; }
            config.Save();
        }
        internal static int StarsFor(string prefab)=>BossData.StarsFor(prefab);
        internal static bool IsChest(int hash) { return hash == MistlandsChestName.GetStableHashCode() || hash == ChestName.GetStableHashCode() || hash == SwampChestName.GetStableHashCode() || hash == MountainChestName.GetStableHashCode(); }
        internal static readonly int EncounterKey = "overhaul_boss_encounter_v1".GetStableHashCode();
        internal static readonly int DefeatedKey = "overhaul_boss_defeated_v1".GetStableHashCode();
        internal static readonly int BossKey = "overhaul_boss_character_v1".GetStableHashCode();
        private const string DeathRpc = "Overhaul_DungeonBossDeath";
        // Explicit eligible forest captains/elites; no global boss keys or event creatures.
        internal static readonly string[] Roster = { "Greydwarf_Shaman", "Greydwarf_Elite", "Troll", "Ghost" };

        internal static void Initialize() { BossData.Initialize(); BossLootData.Initialize(); PrefabManager.OnVanillaPrefabsAvailable -= RegisterChest; PrefabManager.OnVanillaPrefabsAvailable += RegisterChest; }
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= RegisterChest;
        private static void RegisterChest()
        {
            RegisterChestPrefab(ChestName, "TreasureChest_forestcrypt");
            RegisterChestPrefab(SwampChestName, "TreasureChest_sunkencrypt");
            RegisterChestPrefab(MountainChestName, "TreasureChest_mountaincave");
            RegisterChestPrefab(MistlandsChestName, "TreasureChest_dvergrtown");
        }
        private static void RegisterChestPrefab(string name, string source)
        {
            if (PrefabManager.Instance.GetPrefab(name)) return;
            var prefab = PrefabManager.Instance.CreateClonedPrefab(name, source);
            if (!prefab) throw new InvalidOperationException("Boss chest source missing: " + source);
            foreach (var component in prefab.GetComponentsInChildren<Destructible>(true)) Object.DestroyImmediate(component);
            foreach (var component in prefab.GetComponentsInChildren<WearNTear>(true)) Object.DestroyImmediate(component);
            var chest = prefab.GetComponent<Container>();
            chest.m_name = Leveling.LevelingText.Get("boss_chest");
            chest.m_defaultItems = new DropTable();
            chest.m_autoDestroyEmpty = false;
            chest.m_checkGuardStone = false;
            // Keep the existing width so saved item positions remain valid.
            chest.m_width = 4; chest.m_height = 6;
            PrefabManager.Instance.AddPrefab(prefab);
        }

        internal static void Spawn(Room room, ZoneSystem.SpawnMode mode)
        {
            if (mode == ZoneSystem.SpawnMode.Client) throw new InvalidOperationException("Cannot spawn encounter during planning/load");
            string encounter = Guid.NewGuid().ToString("N");
            var swamp = DungeonPolicy.PrefabName(room.name) == BossDungeonLayout.SwampRoomName;
            var mountain = DungeonPolicy.PrefabName(room.name) == BossDungeonLayout.MountainRoomName;
            var mistlands = DungeonPolicy.PrefabName(room.name) == BossDungeonLayout.MistlandsRoomName;
            var roster = mistlands ? MistlandsRoster : mountain ? MountainRoster : swamp ? SwampRoster : Roster;
            var loot = BossLootData.For(mistlands ? "Mistlands" : mountain ? "Mountain" : swamp ? "Swamp" : "Forest");
            string bossId = roster[UnityEngine.Random.Range(0, roster.Length)];
            var bossPrefab = ZNetScene.instance.GetPrefab(bossId);
            var chestPrefab = ZNetScene.instance.GetPrefab(mistlands ? MistlandsChestName : mountain ? MountainChestName : swamp ? SwampChestName : ChestName);
            if (!bossPrefab || !chestPrefab)
                throw new InvalidOperationException("Dungeon boss assets unavailable");
            ValidateLoot(loot);
            var chestObject = Object.Instantiate(chestPrefab, room.transform.Find("RewardChestSpawn").position, room.transform.rotation);
            var chest = chestObject.GetComponent<Container>();
            ZDO chestData = chest.m_nview.GetZDO();
            chestData.Set(EncounterKey, encounter);
            chestData.Set(DefeatedKey, false);
            FillChest(chest);
            var bossObject = Object.Instantiate(bossPrefab, room.transform.Find("BossSpawn").position + Vector3.up * .15f,
                room.transform.rotation * Quaternion.Euler(0, 180, 0));
            var boss = bossObject.GetComponent<Character>();
            boss.m_nview.GetZDO().Set(EncounterKey, encounter);
            boss.m_nview.GetZDO().Set(BossKey, true);
            AI.MobBehaviorConfig.Rule(boss);
            var entry = BossData.For(bossId);
            BossModifiers.Configure(boss, entry);
            int stars=entry.Stars;
            boss.SetLevel(stars+1); // Native level 1 = zero stars.
            boss.SetHealth(boss.GetMaxHealth());
            BossLoadedGuard.Hold(boss);
            Log.LogInfo("Dungeon : gardien " + bossPrefab.name + " "+stars+" etoiles et coffre verrouille crees (" + encounter + ")");
            if (mode == ZoneSystem.SpawnMode.Ghost) { Object.Destroy(bossObject); Object.Destroy(chestObject); }
        }

        internal static void FillChest(Container chest)
        {
            var swamp = chest.m_nview.GetZDO().GetPrefab() == SwampChestName.GetStableHashCode();
            var mountain = chest.m_nview.GetZDO().GetPrefab() == MountainChestName.GetStableHashCode();
            var mistlands = chest.m_nview.GetZDO().GetPrefab() == MistlandsChestName.GetStableHashCode();
            var loot = BossLootData.For(mistlands ? "Mistlands" : mountain ? "Mountain" : swamp ? "Swamp" : "Forest");
            int slots = ValidateLoot(loot);
            chest.GetInventory().RemoveAll();
            chest.GetInventory().SetHeight(Math.Max(chest.m_height, (slots + chest.m_width - 1) / chest.m_width));
            foreach (var entry in loot)
            {
                if (entry.Chance <= 0 || entry.Maximum == 0 || (entry.Chance < 100 && UnityEngine.Random.value * 100 >= entry.Chance)) continue;
                var prefab = ObjectDB.instance.GetItemPrefab(entry.Prefab);
                var source = prefab.GetComponent<ItemDrop>().m_itemData;
                int remaining = UnityEngine.Random.Range(entry.Minimum, entry.Maximum + 1);
                while (remaining > 0)
                {
                    var item = source.Clone(); item.m_dropPrefab = prefab;
                    int amount = Math.Min(remaining, Math.Max(1, source.m_shared.m_maxStackSize));
                    item.m_stack = amount;
                    if (!chest.GetInventory().AddItem(item)) throw new InvalidOperationException("Boss chest inventory overflow");
                    remaining -= amount;
                }
            }
            chest.Save();
        }

        private static int ValidateLoot(BossLootData.Entry[] loot)
        {
            int slots = 0;
            foreach (var entry in loot.Where(e => e.Chance > 0 && e.Maximum > 0))
            {
                var prefab = ObjectDB.instance.GetItemPrefab(entry.Prefab);
                var item = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!item) throw new InvalidDataException(BossLootData.FileName + " : prefab d'objet introuvable : " + entry.Prefab);
                int stack = Math.Max(1, item.m_itemData.m_shared.m_maxStackSize);
                slots += (entry.Maximum + stack - 1) / stack;
            }
            if (slots > 512) throw new InvalidDataException(BossLootData.FileName + " : plus de 512 piles possibles dans un coffre");
            return slots;
        }

        internal static bool Locked(Container chest)
        {
            if (!chest || !chest.m_nview || !chest.m_nview.IsValid()) return false;
            var zdo = chest.m_nview.GetZDO();
            // The distinct prefab fails closed even before its encounter fields have arrived.
            return IsChest(zdo.GetPrefab()) && !zdo.GetBool(DefeatedKey, false);
        }

        internal static void Died(Character boss)
        {
            if (!boss.m_nview || !boss.m_nview.IsValid() || !boss.m_nview.IsOwner()) return;
            var zdo = boss.m_nview.GetZDO();
            if (!zdo.GetBool(BossKey, false)) return;
            string encounter = zdo.GetString(EncounterKey, "");
            if (ZNet.instance.IsServer()) Unlock(ZNet.GetUID(), encounter);
            else ZNet.instance.GetServerPeer()?.m_rpc.Invoke(DeathRpc, encounter);
        }

        private static void Unlock(long sender, string encounter)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || encounter == null || encounter.Length != 32) return;
            var objects = ZDOMan.instance.m_objectsByID.Values.Where(z => z.GetString(EncounterKey, "") == encounter).ToArray();
            // Death is reported by the simulation owner, never by a player-supplied character ID.
            var boss = objects.FirstOrDefault(z => z.GetBool(BossKey, false));
            if (boss == null || boss.GetOwner() != sender || boss.GetInt(DungeonRuntime.PendingKey, 0) != 0) return;
            foreach (var chest in objects.Where(z => IsChest(z.GetPrefab())))
            {
                if (chest.GetBool(DefeatedKey, false)) continue;
                chest.SetOwner(ZNet.GetUID());
                chest.Set(DefeatedKey, true);
                ZDOMan.instance.ForceSendZDO(chest.m_uid);
                Log.LogInfo("Dungeon : boss vaincu, coffre deverrouille (" + encounter + ")");
            }
        }

        internal static void Register(ZNetPeer peer)
        {
            peer.m_rpc.Register<string>(DeathRpc, (rpc, encounter) =>
            {
                var sender = ZNet.instance.GetPeer(rpc);
                if (sender != null && sender.IsReady()) Unlock(sender.m_uid, encounter);
            });
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class BossConnectionPatch { private static void Postfix(ZNetPeer peer) { BossEncounter.Register(peer); } }
    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class BossDeathPatch { private static void Prefix(Character __instance) { BossEncounter.Died(__instance); } }
    [HarmonyPatch(typeof(Container), "CheckAccess")]
    internal static class BossChestAccessPatch
    {
        private static bool Prefix(Container __instance, ref bool __result)
        { if (!BossEncounter.Locked(__instance)) return true; __result = false; return false; }
    }
    [HarmonyPatch(typeof(Container), "GetHoverText")]
    internal static class BossChestHoverPatch
    {
        private static bool Prefix(Container __instance, ref string __result)
        {
            if (BossEncounter.IsChest(DungeonPolicy.PrefabName(__instance.name).GetStableHashCode())) __instance.m_name = Leveling.LevelingText.Get("boss_chest");
            if (!BossEncounter.Locked(__instance)) return true;
            __result = Leveling.LevelingText.Get("boss_chest") + "\n" + Leveling.LevelingText.Get("boss_chest_locked"); return false;
        }
        private static void Postfix(ref string __result)
        { if (__result != null) __result = __result.Replace("$overhaul_boss_chest", Leveling.LevelingText.Get("boss_chest")); }
    }
    [HarmonyPatch(typeof(Container), "GetHoverName")]
    internal static class BossChestNamePatch
    {
        private static void Postfix(Container __instance, ref string __result)
        { if (BossEncounter.IsChest(DungeonPolicy.PrefabName(__instance.name).GetStableHashCode())) __result = Leveling.LevelingText.Get("boss_chest"); }
    }
    [HarmonyPatch(typeof(LevelEffects), "SetupLevelVisualization")]
    internal static class BossLevelVisualPatch
    {
        internal static bool IsGuardian(Character c)
        { return c && c.GetLevel()==4 && c.m_nview && c.m_nview.IsValid() && c.m_nview.GetZDO().GetBool(BossEncounter.BossKey,false); }
        private static void Prefix(LevelEffects __instance,ref int level)
        {
            var c=__instance.GetComponentInParent<Character>(true);
            if(!c || !c.m_nview || !c.m_nview.IsValid() || !c.m_nview.GetZDO().GetBool(BossEncounter.BossKey,false))return;
            // Visual argument only: combat level and loot remain unchanged.
            if(DungeonPolicy.PrefabName(c.name)=="Abomination"){level=1;return;}
            if(level!=4 || __instance.m_levelSetups.Count!=2)return;
            var previous=__instance.m_levelSetups[1];
            var ai=c.GetComponent<MonsterAI>();
            bool large=DungeonPolicy.PrefabName(c.name)=="Troll" || (ai && ai.m_pathAgentType==Pathfinding.AgentType.TrollSize);
            __instance.m_levelSetups=new System.Collections.Generic.List<LevelEffects.LevelSetup>(__instance.m_levelSetups);
            __instance.m_levelSetups.Add(new LevelEffects.LevelSetup {
                m_scale=previous.m_scale*(large?1f:1.15f), m_hue=previous.m_hue,
                m_saturation=previous.m_saturation,m_value=previous.m_value,
                m_setEmissiveColor=previous.m_setEmissiveColor,m_emissiveColor=previous.m_emissiveColor,
                m_enableObject=previous.m_enableObject });
        }
    }
    [HarmonyPatch(typeof(CharacterDrop),"GenerateDropList")]
    internal static class BossDropMultiplierPatch
    {
        private static float Multiplier(float native,CharacterDrop drop)
        { return BossLevelVisualPatch.IsGuardian(drop.GetComponent<Character>())?6f:native; }
        private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            foreach(var code in instructions)
            {
                yield return code;
                if(code.Calls(AccessTools.Method(typeof(Mathf),nameof(Mathf.Pow))))
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,AccessTools.Method(typeof(BossDropMultiplierPatch),nameof(Multiplier)));
                }
            }
        }
    }
    [HarmonyPatch(typeof(EnemyHud),"UpdateHuds")]
    internal static class BossStarsPatch
    {
        private static void Postfix(EnemyHud __instance)
        {
            foreach(var hud in __instance.m_huds.Values)
            {
                if(!hud.m_gui || !hud.m_level2)continue;
                var root=hud.m_level2.parent.Find("OverhaulThreeStars");
                bool show=BossLevelVisualPatch.IsGuardian(hud.m_character);
                if(!root && show)
                {
                    root=new GameObject("OverhaulThreeStars",typeof(RectTransform)).transform;
                    root.SetParent(hud.m_level2.parent,false);
                    var rect=(RectTransform)root;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=Vector2.zero;rect.offsetMax=Vector2.zero;
                    var icon=hud.m_level2.GetComponentInChildren<UnityEngine.UI.Image>(true);
                    float spacing=icon?icon.rectTransform.rect.width*Mathf.Abs(icon.transform.localScale.x)+2:16;
                    for(int i=0;i<3;i++)
                    {
                        var star=Object.Instantiate(hud.m_level2,root,false);
                        star.anchoredPosition=hud.m_level2.anchoredPosition+new Vector2((i-1)*spacing,0);
                        star.gameObject.SetActive(true);
                    }
                }
                if(root)root.gameObject.SetActive(show);
            }
        }
    }
}
