using BepInEx.Configuration;
using Jotunn.Utils;

namespace Overhaul.Persistence
{
    internal static class PlayerPersistenceConfig
    {
        internal static ConfigEntry<bool> AllowClientCharacterMigration;
        internal static void Bind(ConfigFile config)
        {
            AllowClientCharacterMigration = config.Bind("PlayerPersistence", "AllowClientCharacterMigration", false,
                new ConfigDescription("Server policy for the player SQLite system under development: permit a one-time client character import only when no server character exists. False starts fresh. Never replaces an existing server character. The login/gameplay integration is not enabled yet.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
        }
    }
}
