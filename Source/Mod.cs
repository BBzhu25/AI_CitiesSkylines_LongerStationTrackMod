using ColossalFramework;
using ColossalFramework.Globalization;
using System.Reflection;
using HarmonyLib;
using ICities;

namespace LongerStationTrackMod
{
    /// <summary>
    /// Mod entry point. Instantiated by the game when the mod is enabled
    /// in the Content Manager.
    /// The Name/Description shown in the Content Manager switch automatically
    /// between Chinese and English based on the game's selected language.
    /// </summary>
    public class Mod : IUserMod
    {
        /// <summary>
        /// True when the game language is Chinese (Simplified or Traditional).
        /// LocaleManager lives in ColossalFramework.Globalization and derives from
        /// Singleton&lt;T&gt;, so use the static "exists" guard before touching the
        /// instance; the try/catch makes this safe at any point during startup.
        /// </summary>
        private static bool IsChinese
        {
            get
            {
                try
                {
                    if (!LocaleManager.exists)
                        return false;
                    string lang = LocaleManager.instance.language;
                    return !string.IsNullOrEmpty(lang) && lang.ToLower().StartsWith("zh");
                }
                catch
                {
                    return false;
                }
            }
        }

        public string Name => IsChinese ? "更长车站轨道" : "Longer Station Track Mod";

        public string Description => IsChinese
            ? "突破火车站台轨道长度限制，支持在超长站台(215m+)之间正常建立列车线路。需要 Harmony。"
            : "Removes train station platform track length limits and lets you create train lines between extra-long platforms (215m+). Requires Harmony.";

        // Harmony instance ID, must be a globally unique string
        internal const string HarmonyId = "com.example.longerstationtrackmod";

        private static Harmony _harmony;

        /// <summary>
        /// Called when the mod is enabled: applies all Harmony patches.
        /// The 0Harmony runtime is provided by Harmony (this mod does
        /// not ship its own 0Harmony.dll).
        /// </summary>
        public void OnEnabled()
        {
            ApplyPatches();
        }

        /// <summary>
        /// Applies patches class by class: a single failing patch class does
        /// not prevent the others (PatchAll aborts entirely on first failure).
        /// </summary>
        internal static void ApplyPatches()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                int ok = 0, fail = 0;
                foreach (System.Type t in typeof(Mod).Assembly.GetTypes())
                {
                    object[] attrs = t.GetCustomAttributes(typeof(HarmonyPatch), false);
                    if (attrs == null || attrs.Length == 0)
                        continue;
                    try
                    {
                        _harmony.CreateClassProcessor(t).Patch();
                        ok++;
                    }
                    catch (System.Exception ex)
                    {
                        fail++;
                        UnityEngine.Debug.LogError("[LongerStationTrack] Failed to apply patch class " + t.Name + ": " + ex.Message);
                    }
                }
                UnityEngine.Debug.Log("[LongerStationTrack] Patches applied: " + ok + " succeeded, " + fail + " failed");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError("[LongerStationTrack] Failed to apply Harmony patches: " + ex);
            }
        }

        /// <summary>
        /// Called when the mod is disabled: removes all patches applied by this mod.
        /// </summary>
        public void OnDisabled()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError("[LongerStationTrack] OnDisabled failed: " + ex);
            }
        }
    }
}
