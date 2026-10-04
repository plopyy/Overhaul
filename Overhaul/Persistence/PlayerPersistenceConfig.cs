using BepInEx.Configuration;
using Jotunn.Utils;

namespace Overhaul.Persistence
{
    internal static class PlayerPersistenceConfig
    {
        internal static ConfigEntry<bool> AllowClientCharacterMigration;
        internal static ConfigEntry<bool> Enabled;
        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("PlayerPersistence", "Enabled", false,
                new ConfigDescription("Experimental server-authoritative gameplay and per-world player SQLite saves. Disabled by default pending real multiplayer validation. Enable only on a test world with matching Overhaul clients.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            AllowClientCharacterMigration = config.Bind("PlayerPersistence", "AllowClientCharacterMigration", false,
                new ConfigDescription("When PlayerPersistence.Enabled is true, permit a one-time client character import only when no server character exists for this account in this world. False starts fresh. Never replaces an existing server character. Enable only for trusted initial imports.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
        }
    }
}
