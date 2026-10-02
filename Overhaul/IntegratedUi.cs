using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Overhaul
{
    internal static class IntegratedUi
    {
        private static Auga.Auga ui;

        // The prefab scripts retain their serialized Unity.Auga identity, but are
        // shipped inside Overhaul.dll. No second BepInEx plugin is registered.
        internal static void LoadDependencies()
        {
            foreach (string name in new[] { "fastJSON", "Unity.Auga", "APIManager" })
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name)) continue;
                using (var stream = typeof(IntegratedUi).Assembly.GetManifestResourceStream("Overhaul." + name + ".dll"))
                {
                    if (stream == null) throw new InvalidOperationException("Missing embedded dependency: " + name);
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        Assembly.Load(buffer.ToArray());
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Start(ManualLogSource logger, GameObject host)
        {
            if (ui != null) return;
            var config = new ConfigFile(Path.Combine(Path.GetDirectoryName(typeof(IntegratedUi).Assembly.Location), "Interface.cfg"), true);
            ui = new Auga.Auga();
            ui.Initialize(config, logger, host);
        }

        internal static void Stop()
        {
            ui?.OnDestroy();
            ui = null;
        }
    }
}
