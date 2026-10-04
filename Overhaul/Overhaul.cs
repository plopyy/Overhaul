using BepInEx;
using HarmonyLib;
using Overhaul.Utility;
using System.Reflection;

namespace Overhaul
{
	[BepInPlugin("plopyy.valheim.Overhaul", "Overhaul", BuildVersion.Value)]
	[BepInDependency("com.jotunn.jotunn")]
        [BepInDependency("Menthus.bepinex.plugins.BetterTrader", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("maximods.valheim.multicraft", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("redseiko.valheim.chatter", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("redseiko.valheim.searscatalog", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.github.abearcodes.valheim.simplerecycling", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.jewelcrafting", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInIncompatibility("randyknapp.mods.auga")]
    [BepInIncompatibility("advize.PlantEasily")]
    [BepInIncompatibility("yay.spikehimself.xportal")]
    [BepInIncompatibility("com.sweetgiorni.anyportal")]
    [BepInIncompatibility("randyknapp.mods.equipmentandquickslots")]
    [BepInIncompatibility("Azumatt.AzuExtendedPlayerInventory")]
    [BepInIncompatibility("aedenthorn.ExtendedPlayerInventory")]
    [BepInIncompatibility("shudnal.ExtraSlots")]
    [BepInIncompatibility("com.bruce.valheim.comfyquickslots")]
    [BepInDependency("moreslots", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("randyknapp.mods.epicloot", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("ishid4.mods.betterarchery", BepInDependency.DependencyFlags.SoftDependency)]
    [Jotunn.Utils.NetworkCompatibility(Jotunn.Utils.CompatibilityLevel.EveryoneMustHaveMod, Jotunn.Utils.VersionStrictness.Patch)]
	public class Overhaul : BaseUnityPlugin
	{
        private Harmony _harmony;
        public const string ConfigFile = "OverhaulConfig";

		public void Awake()
		{
			IntegratedUi.LoadDependencies();
            Initialize();
        }

        // Mono resolves referenced types before executing a method. Keep the
        // embedded UI types out of Awake until their assemblies are loaded.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void Initialize()
        {
            Log.Init(base.Logger);
			Persistence.PlayerPersistenceConfig.Bind(base.Config);
			OverhaulConfig.Bind(base.Config);
            AlterItemStat.Initialize();
            Leveling.LevelingConfig.Initialize();
            Leveling.ClassSkillConfig.Initialize();
            AI.MobBehaviorConfig.Initialize();
            AugaUnity.ComplexTooltip.FoodDuration = Leveling.NutritionDuration.Preview;
            AugaUnity.ComplexTooltip.ElementStatBonus = (player, stat) => Leveling.LevelingEffects.Bonus(player, stat);
            AugaUnity.ComplexTooltip.HasProjectileStat = player =>
            {
                var state=Leveling.LevelingEffects.State(player);
                return state!=null && state.Ready && state.Data.AllocatedStats.TryGetValue("projectile",out int points) && points>0;
            };
            Dungeons.BossDungeonLayout.Initialize();
            Dungeons.BossEncounter.Initialize();
            TarDrain.Initialize();
            DvergerCirclet.Initialize();
            Storage.FeedingTrough.Initialize();
            Storage.CharcoalKilnWoods.Initialize();
            Patches.WoodenArrowRecipe.Initialize();
            Storage.ProductionClock.Initialize();
			StaffShieldVfx.Initialize();
            Persistence.GameStaffGuardRules.Initialize();
            DoPatching();
            Commands.AdminCommands.Initialize();
			Log.LogInfo("Create Config values");
			OverhaulConfig.SyncManager();
            if (!UnityEngine.Application.isBatchMode && UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                IntegratedUi.Start(base.Logger, gameObject);
            EquipmentAndQuickSlots.EquipmentAndQuickSlots.Initialize(base.Logger);
            Advize_PlantEasily.PlantEasily.Initialize(base.Logger);
            XPortal.XPortal.Initialize();
		}

        public void Update() { XPortal.XPortal.Update(); Leveling.ClassWindow.Tick(); EquipmentAndQuickSlots.EquipmentAndQuickSlots.Update(); }

        public void LateUpdate()
        {
            Storage.ProductionClock.Tick();
            Persistence.GamePersistence.Tick();
            Storage.VehicleMarkers.Tick();
            Storage.CircletFog.Tick();
            EquipmentAndQuickSlots.EquipmentAndQuickSlots.LateUpdate();
            Commands.AdminCommands.Tick();
            Dungeons.DungeonRuntime.Tick();
            DynamicCombat.UpdateDodgeAttackSpeed();
            DynamicCombat.UpdateDodgeAura();
            DynamicCombat.UpdateAttackLocomotionPose();
            DynamicCombat.UpdateDodgeAttackBlend();
            DashAnimationPlayback.Update();
        }
		public void OnDestroy()
		{
            Persistence.GamePersistence.Close();
            XPortal.XPortal.Stop();
            Advize_PlantEasily.PlantEasily.Stop();
            Leveling.ClassWindow.Clear();
            Storage.VehicleMarkers.Clear();
            Storage.CircletFog.Clear();
            EquipmentAndQuickSlots.EquipmentAndQuickSlots.Stop();
            IntegratedUi.Stop();
            Commands.AdminCommands.Clear();
            TarDrain.Shutdown();
            DvergerCirclet.Shutdown();
            Storage.FeedingTrough.Shutdown();
            Storage.CharcoalKilnWoods.Shutdown();
            Patches.WoodenArrowRecipe.Shutdown();
            OverhaulConfig.StopSyncManager();
            Storage.ProductionClock.Shutdown();
            Dungeons.BossEncounter.Shutdown();
            Dungeons.BossDungeonLayout.Shutdown();
            Dungeons.BossNativeMaterials.Release();
            Dungeons.MistlandsBossRoom.Release();
            Dungeons.DungeonRuntime.ClearSession();
            DynamicCombat.StopDodgeAura();
			DynamicCombat.ReleaseAttackLocomotion();
            DashAnimationPlayback.Release();
            DashAnimationPose.Unload();
			StaffShieldVfx.Unload();
            Persistence.GameStaffGuardRules.Shutdown();
			_harmony?.UnpatchSelf();
		}

		public void DoPatching()
		{
            this._harmony = new Harmony("plopyy.valheim.Overhaul");
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
                if (type.Namespace != null && (type.Namespace == "Overhaul" || type.Namespace.StartsWith("Overhaul.")))
                    _harmony.CreateClassProcessor(type).Patch();
            //new Harmony(ConfigFile).PatchAll();
            Log.LogInfo(ConfigFile + " Patching complete");
		}
	}
}

