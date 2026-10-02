using BepInEx.Logging;

namespace Overhaul.Utility
{
	internal static class Log
	{
		internal static void Init(ManualLogSource logSource)
		{
			Log._logSource = logSource;
		}

		internal static void LogDebug(object data)
		{
			Log._logSource.LogDebug(data);
		}

		internal static void LogError(object data)
		{
			Log._logSource.LogError(data);
		}

		internal static void LogFatal(object data)
		{
			Log._logSource.LogFatal(data);
		}

		internal static void LogInfo(object data)
		{
			Log._logSource.LogInfo(data);
		}

		internal static void LogMessage(object data)
		{
			Log._logSource.LogMessage(data);
		}

		internal static void LogWarning(object data)
		{
			Log._logSource.LogWarning(data);
		}

		internal static ManualLogSource _logSource;
	}
}
