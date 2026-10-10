using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace Overhaul.Utility
{
    // Diagnostic: ShutdownTrace.log, next to the plugin, timestamps every step of a server or game shutdown, written
    // and closed at once (the BepInEx log may not be flushed when the process hangs). Once the quit starts, a watchdog
    // writes every 5 seconds what the main thread is doing, when the runtime can tell.
    internal static class ShutdownTrace
    {
        private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ShutdownTrace).Assembly.Location), "ShutdownTrace.log");
        private static readonly Stopwatch Clock = new Stopwatch();
        private static Thread main, watchdog;
        private static volatile string step = "";

        internal static void Initialize()
        {
            main = Thread.CurrentThread;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Write("ProcessExit");
            AppDomain.CurrentDomain.DomainUnload += (s, e) => Write("DomainUnload");
        }

        internal static void Write(string text)
        {
            try { File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff") + (Clock.IsRunning ? " (+" + Clock.Elapsed.TotalSeconds.ToString("0.0") + " s)" : "") + "  " + text + Environment.NewLine); }
            catch { }
        }

        private static void Start()
        {
            if (Clock.IsRunning) return;
            Clock.Start();
            Write("=== Shutdown started ===");
            watchdog = new Thread(Watch) { Name = "Overhaul shutdown watchdog", IsBackground = true };
            watchdog.Start();
        }

        private static void Watch()
        {
            while (true)
            {
                Thread.Sleep(5000);
                string sample = "";
#pragma warning disable 618
                try { main.Suspend(); try { sample = new StackTrace(main, false).ToString(); } finally { main.Resume(); } }
                catch (Exception e) { sample = "(stack not available: " + e.GetType().Name + ")"; }
#pragma warning restore 618
                Write("still running, last step: " + step + Environment.NewLine + sample);
            }
        }

        // Every step logged before and after, with its duration.
        [HarmonyPatch]
        private static class Steps
        {
            private static IEnumerable<MethodBase> TargetMethods() => Candidates().Where(m => m != null);

            private static IEnumerable<MethodBase> Candidates()
            {
                yield return AccessTools.DeclaredMethod(typeof(Game), "OnApplicationQuit");
                yield return AccessTools.DeclaredMethod(typeof(ZNet), "Shutdown");
                yield return AccessTools.DeclaredMethod(typeof(ZNet), "StopAll");
                yield return AccessTools.DeclaredMethod(typeof(ZNet), "SaveWorld");
                yield return AccessTools.DeclaredMethod(typeof(ZNet), "OnDestroy");
                yield return AccessTools.DeclaredMethod(typeof(ZNetScene), "OnDestroy");
                yield return AccessTools.DeclaredMethod(typeof(ZoneSystem), "OnDestroy");
                yield return AccessTools.DeclaredMethod(typeof(Game), "OnDestroy");
                yield return AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "Dispose");
            }

            private static void Prefix(MethodBase __originalMethod, out Stopwatch __state)
            {
                if (__originalMethod.Name == "OnApplicationQuit") Start();
                __state = Clock.IsRunning ? Stopwatch.StartNew() : null;
                if (__state == null) return;
                step = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
                Write("begin " + step);
            }

            private static void Finalizer(MethodBase __originalMethod, Stopwatch __state)
            {
                if (__state != null) Write("end   " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " (" + __state.Elapsed.TotalSeconds.ToString("0.00") + " s)");
            }
        }
    }
}
