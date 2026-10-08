using BepInEx.Configuration;
using Jotunn;
using Jotunn.Managers;
using Jotunn.Utils;

namespace Overhaul.Utility
{
	public static class OverhaulConfig
	{
        public static ConfigEntry<float> ResetIntervalHours { get; private set; }
        public static ConfigEntry<float> RoomMultiplier { get; private set; }
        public static ConfigEntry<string> ResetLocations { get; private set; }
        public static ConfigEntry<float> PickupRange { get; set; }
        public static ConfigEntry<float> RightShoulderOffset { get; set; }

		public static ConfigEntry<float> FireStaffProjectileSpeedMultiplier { get; set; }
		public static ConfigEntry<bool> FireStaffStraightProjectiles { get; set; }
		public static ConfigEntry<float> StaffShieldRegenerationPerSecond { get; set; }

        // --- MoveSpeed ---
        public static ConfigEntry<float> BaseMovementSpeed { get; set; }
		public static ConfigEntry<float> SneakMovementSpeed { get; set; }
		public static ConfigEntry<float> DashSpeed { get; set; }
		public static ConfigEntry<float> DashDuration { get; set; }
		public static ConfigEntry<float> DashStaminaCost { get; set; }

		// --- Movement ---
		public static ConfigEntry<bool> SneakUseStamina { get; set; }
		public static ConfigEntry<float> SneakStaminaDrain { get; set; }
		public static ConfigEntry<bool> JumpUseStamina { get; set; }
		public static ConfigEntry<float> JumpStaminaDrain { get; set; }
		public static ConfigEntry<bool> SwimUseStamina { get; set; }
		public static ConfigEntry<float> SwimStaminaDrain { get; set; }
		public static ConfigEntry<bool> EncumberedUseStamina { get; set; }
		public static ConfigEntry<float> EncumberedStaminaDrain { get; set; }

		// --- Build ---
		public static ConfigEntry<bool> BuildUseStamina { get; set; }
		public static ConfigEntry<bool> TerrainUseStamina { get; set; }
		public static ConfigEntry<bool> FarmUseStamina { get; set; }
		public static ConfigEntry<bool> FishUseStamina { get; set; }

		// --- Combat ---
		public static ConfigEntry<bool> AttackUseStamina { get; set; }
		public static ConfigEntry<float> AttackStaminaDrain { get; set; }
		public static ConfigEntry<float> AttackEitrDrainRate { get; set; }
		public static ConfigEntry<bool> BlockUseStamina { get; set; }
		public static ConfigEntry<float> BlockStaminaDrain { get; set; }
		public static ConfigEntry<bool> BowUseStamina { get; set; }
        public static ConfigEntry<bool> CrouchedBowAiming { get; set; }
        public static ConfigEntry<bool> PlayerReplication { get; private set; }
		public static ConfigEntry<float> BowStaminaDrainRate { get; set; }

		public static void Bind(ConfigFile config)
		{
            Dungeons.BossEncounter.RemoveLegacyStars(config);
			config.SaveOnConfigSet = true;
            RightShoulderOffset = config.Bind("Camera", "RightShoulderOffset", 0.45f,
                new ConfigDescription("Local camera offset to the right in metres. Keeps the crosshair centered and aligns projectile aim with it. 0 restores vanilla camera and aim. Reduced near walls and at very close zoom.",
                    new AcceptableValueRange<float>(0f, 1.5f), new ConfigurationManagerAttributes { IsAdminOnly = false }));
            CrouchedBowAiming = config.Bind("Combat", "CrouchedBowAiming", true,
                new ConfigDescription("Experimental: keep crouching while drawing and releasing a bow, combining native crouched legs with the aiming torso. Set false to restore vanilla bow posture. No prefab or save changes.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            PlayerReplication = config.Bind("Rework", "PlayerReplication", true,
                new ConfigDescription("Rework test: copy the player's profile data (skills for now) into the character's network object, so the server holds it in memory. Read only on the server; saves are unchanged. Set false to disable.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            ResetIntervalHours = config.Bind("Dungeon", "ResetIntervalHours", 120f,
                new ConfigDescription("Real hours after the first player visit since generation/reset. 120 = 5 days; 0 disables automatic resets. Never-visited locations are skipped. One global expiry check every 5 minutes; occupied dungeons are deferred.",
                    new AcceptableValueRange<float>(0f, 87600f), new ConfigurationManagerAttributes { IsAdminOnly = true }));
            RoomMultiplier = config.Bind("Dungeon", "RoomMultiplier", 1.5f,
                new ConfigDescription("Multiplier of original minimum/maximum room parameters. Maximum capped at 96, minimum never above maximum. Excludes Sealed Tower, Infested Citadel and outdoor locations.",
                    new AcceptableValueRange<float>(0.1f, 10f), new ConfigurationManagerAttributes { IsAdminOnly = true }));
            ResetLocations = config.Bind("Dungeon", "ResetLocations", Dungeons.DungeonPolicy.DefaultResetLocations,
                new ConfigDescription("Comma-separated supported location prefab IDs eligible for reset. Remove an ID to exclude it. Infested Citadel and Meadows villages/farm are always excluded. Unknown IDs are ignored.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            OverhaulConfig.PickupRange = config.Bind<float>("PickupRange", "PickupRange", 5f, new ConfigDescription("Area pickup range; Default: 5 | Vanilla: 0 (single target)", new AcceptableValueRange<float>(1f, 50f), new object[]
            {
                new ConfigurationManagerAttributes
                {
                    IsAdminOnly = true
                }
            }));


            WeaponAttackSpeeds.Initialize(config);
			OverhaulConfig.FireStaffProjectileSpeedMultiplier = config.Bind<float>("Staffs", "FireStaffProjectileSpeedMultiplier", 1.2f, new ConfigDescription("Fire staff projectile speed multiplier; Default: 1.2 (+20%) | Vanilla: 1", new AcceptableValueRange<float>(0.1f, 5f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.FireStaffStraightProjectiles = config.Bind<bool>("Staffs", "FireStaffStraightProjectiles", true, new ConfigDescription("Fire staff projectiles fly straight without launch angle or gravity; Default: true | Vanilla: false", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.StaffShieldRegenerationPerSecond = config.Bind<float>("Staffs", "StaffShieldRegenerationPerSecond", 5f, new ConfigDescription("Inactive magic shield regeneration per second; Default: 5 | Vanilla: 0 (no persistent shield regeneration)", new AcceptableValueRange<float>(0f, 500f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));


            OverhaulConfig.BaseMovementSpeed = config.Bind<float>("MoveSpeed", "BaseMovementSpeed", 10f, new ConfigDescription("Base movement speed; Default: 10 | Vanilla: 5", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.SneakMovementSpeed = config.Bind<float>("MoveSpeed", "SneakMovementSpeed", 2f, new ConfigDescription("Sneak movement speed; Default: 2 | Vanilla: 2", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.DashSpeed = config.Bind<float>("Dash", "DashSpeed", 20f, new ConfigDescription("Dash movement speed; Default: 20 | Vanilla: 0 (no dash)", new AcceptableValueRange<float>(1f, 100f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.DashDuration = config.Bind<float>("Dash", "DashDuration", 0.2f, new ConfigDescription("Dash duration in seconds; Default: 0.2 | Vanilla: 0 (no dash)", new AcceptableValueRange<float>(0.05f, 2f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.DashStaminaCost = config.Bind<float>("Dash", "DashStaminaCost", 10f, new ConfigDescription("Dash stamina cost; Default: 10 | Vanilla: not applicable", new AcceptableValueRange<float>(0f, 100f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.SneakUseStamina = config.Bind<bool>("Movement", "SneakUseStamina", false, new ConfigDescription("Sneaking uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.SneakStaminaDrain = config.Bind<float>("Movement", "SneakStaminaDrain", 5f, new ConfigDescription("Sneak stamina drain per second; Default: 5 | Vanilla: 5", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.JumpUseStamina = config.Bind<bool>("Movement", "JumpUseStamina", false, new ConfigDescription("Jumping uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.JumpStaminaDrain = config.Bind<float>("Movement", "JumpStaminaDrain", 10f, new ConfigDescription("Jump stamina cost; Default: 10 | Vanilla: 10", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.SwimUseStamina = config.Bind<bool>("Movement", "SwimUseStamina", false, new ConfigDescription("Swimming uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.SwimStaminaDrain = config.Bind<float>("Movement", "SwimStaminaDrain", 10f, new ConfigDescription("Swim stamina drain per second; Default: 10 | Vanilla: 5 (low skill) to 2 (max skill)", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.EncumberedUseStamina = config.Bind<bool>("Movement", "EncumberedUseStamina", false, new ConfigDescription("Moving while encumbered uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.EncumberedStaminaDrain = config.Bind<float>("Movement", "EncumberedStaminaDrain", 10f, new ConfigDescription("Encumbered stamina drain per second; Default: 10 | Vanilla: 10", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.BuildUseStamina = config.Bind<bool>("Build", "BuildUseStamina", false, new ConfigDescription("Building and destroying use stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.TerrainUseStamina = config.Bind<bool>("Build", "TerrainUseStamina", false, new ConfigDescription("Terrain tools use stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.FarmUseStamina = config.Bind<bool>("Build", "FarmUseStamina", false, new ConfigDescription("Farming uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.FishUseStamina = config.Bind<bool>("Build", "FishUseStamina", false, new ConfigDescription("Fishing uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.AttackUseStamina = config.Bind<bool>("Combat", "AttackUseStamina", false, new ConfigDescription("Attacks use stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.AttackStaminaDrain = config.Bind<float>("Combat", "AttackStaminaDrain", 20f, new ConfigDescription("Attack stamina cost; Default: 20 | Vanilla: depends on the weapon", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.AttackEitrDrainRate = config.Bind<float>("Combat", "AttackEitrDrainRate", 1f, new ConfigDescription("Attack Eitr cost multiplier; Default: 1 | Vanilla: 1", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.BlockUseStamina = config.Bind<bool>("Combat", "BlockUseStamina", false, new ConfigDescription("Blocking hits use stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.BlockStaminaDrain = config.Bind<float>("Combat", "BlockStaminaDrain", 20f, new ConfigDescription("Block stamina cost; Default: 20 | Vanilla: 25 base", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.BowUseStamina = config.Bind<bool>("Combat", "BowUseStamina", false, new ConfigDescription("Drawing a bow uses stamina; Default: false | Vanilla: true", new AcceptableValueList<bool>(new bool[2] { true, false }), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
			OverhaulConfig.BowStaminaDrainRate = config.Bind<float>("Combat", "BowStaminaDrainRate", 1f, new ConfigDescription("Bow draw stamina cost multiplier; Default: 1 | Vanilla: 1", new AcceptableValueRange<float>(0f, 50f), new object[]
			{
				new ConfigurationManagerAttributes
				{
					IsAdminOnly = true
				}
			}));
		}

		// Token: 0x0600004B RID: 75
        private static bool watchingSync;
        public static void SyncManager()
        {
            if (watchingSync) return;
            watchingSync = true;
            SynchronizationManager.OnConfigurationSynchronized += ConfigurationSynchronized;
            SynchronizationManager.OnAdminStatusChanged += AdminStatusChanged;
        }

        internal static void StopSyncManager()
        {
            if (!watchingSync) return;
            watchingSync = false;
            SynchronizationManager.OnConfigurationSynchronized -= ConfigurationSynchronized;
            SynchronizationManager.OnAdminStatusChanged -= AdminStatusChanged;
        }

        private static void ConfigurationSynchronized(object sender, ConfigurationSynchronizationEventArgs args) =>
            Logger.LogMessage(args.InitialSynchronization ? "Initial Config sync event received" : "Config sync event received");

        private static void AdminStatusChanged() => Logger.LogMessage("Admin status sync event received: " +
            (SynchronizationManager.Instance.PlayerIsAdmin ? "You're admin now" : "Downvoted, client"));
    }
}
