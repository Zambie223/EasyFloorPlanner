#if DEBUG
using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Kitchen;
using KitchenMods;
using Platforms;

namespace EasyFloorPlanner
{
    /// <summary>
    /// DEBUG BUILDS ONLY. This is an automated in-game test, used for development, that never ships.
    ///
    /// Triggered by a flag file, not a launch argument. A steam:// launch that passes arguments
    /// shows a confirmation prompt, and starting the exe directly can relaunch through Steam and
    /// lose the arguments.
    ///   &lt;game&gt;\Mods\EasyFloorPlanner\selftest.flag
    ///     line 1: expiry time in UTC unix seconds (a stale flag is ignored)
    ///     line 2: folder for screenshots
    ///     line 3: "quit" to close the game when the test finishes (optional)
    /// The file is deleted as soon as it's read (single use), so a leftover flag can't hijack a
    /// normal play session.
    ///
    /// While active, every save write and delete is blocked (see SaveGuard), so the test run
    /// never touches the player's saves or Steam Cloud. If the guard can't be installed, the test
    /// doesn't run. The only writes allowed are to the memory-only saves (SelfTestMemorySaves) that
    /// the reload test swaps in.
    /// </summary>
    internal static class SelfTest
    {
        public static bool Active { get; private set; }
        public static string ShotDir { get; private set; }
        public static bool QuitWhenDone { get; private set; }

        /// <summary>Restaurant setting to start the run in (matched against its name), or null for the HQ's current one.</summary>
        public static string ForceSetting { get; private set; }

        /// <summary>Survey mode: only record the setting's ground (GroundSurvey), then quit.</summary>
        public static bool Survey { get; private set; }

        /// <summary>
        /// Removes other mods' Harmony patches on LayoutView for this test session only (in memory),
        /// so results reflect this mod alone. Example: Kitchen Designer Plus prefixes
        /// UpdateNavmesh and sets the navmesh ground box to 300x300, which would hide whether
        /// NavmeshAreaPatch works. Called every frame while the test runs (cheap: three patch-info
        /// lookups), because other mods may patch late or re-patch; it only logs when it removes something.
        /// </summary>
        public static void IsolateFromOtherMods()
        {
            // Declared on LayoutView itself. UpdateData has a base-class overload, so name its exact signature.
            var methods = new (string name, System.Reflection.MethodBase method)[]
            {
                ("Initialise", AccessTools.DeclaredMethod(typeof(LayoutView), "Initialise")),
                ("UpdateData", AccessTools.DeclaredMethod(typeof(LayoutView), "UpdateData", new[] { typeof(LayoutView.InitialViewData) })),
                ("UpdateNavmesh", AccessTools.DeclaredMethod(typeof(LayoutView), "UpdateNavmesh")),
            };
            foreach ((string name, System.Reflection.MethodBase method) in methods)
            {
                var info = method == null ? null : Harmony.GetPatchInfo(method);
                if (info == null)
                {
                    continue;
                }
                foreach (string owner in info.Owners)
                {
                    if (owner == ModMain.Id || owner == ModMain.Id + ".selftest")
                    {
                        continue;
                    }
                    new Harmony(owner).Unpatch(method, HarmonyPatchType.All, owner);
                    ModMain.Log($"[SELFTEST] isolation: removed '{owner}' patches on LayoutView.{name} for this session.");
                }
            }
        }

        public static string FlagPath => Path.Combine(FolderModSource.ModsFolder, "EasyFloorPlanner", "selftest.flag");

        public static void TryActivate()
        {
            string[] lines;
            try
            {
                if (!File.Exists(FlagPath))
                {
                    return;
                }
                lines = File.ReadAllLines(FlagPath);
                File.Delete(FlagPath);
            }
            catch (Exception e)
            {
                ModMain.LogError($"[SELFTEST] could not read/delete flag: {e.Message}");
                return;
            }

            if (lines.Length < 2 || !long.TryParse(lines[0].Trim(), out long expiry))
            {
                ModMain.LogError("[SELFTEST] malformed flag file; ignoring.");
                return;
            }
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
            {
                ModMain.LogWarning("[SELFTEST] flag file expired; ignoring.");
                return;
            }

            if (!SaveGuard.Install())
            {
                ModMain.LogError("[SELFTEST] save guard failed to install; refusing to run the test.");
                return;
            }

            ShotDir = lines[1].Trim();
            // Options after the first two lines, in any order: "quit"; "setting=<name>" starts the run
            // in that restaurant setting (e.g. Turbo); "survey" only surveys the ground (GroundSurvey).
            QuitWhenDone = false;
            ForceSetting = null;
            Survey = false;
            foreach (string raw in lines.Skip(2))
            {
                string line = raw.Trim();
                if (line.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    QuitWhenDone = true;
                }
                else if (line.StartsWith("setting=", StringComparison.OrdinalIgnoreCase))
                {
                    ForceSetting = line.Substring("setting=".Length).Trim();
                }
                else if (line.Equals("survey", StringComparison.OrdinalIgnoreCase))
                {
                    Survey = true;
                }
            }
            try
            {
                Directory.CreateDirectory(ShotDir);
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"[SELFTEST] screenshot folder unusable ({e.Message}); continuing without screenshots.");
                ShotDir = null;
            }
            Active = true;
            ModMain.Log($"[SELFTEST] ACTIVE. Saves are blocked. Screenshots: {ShotDir ?? "(none)"}. Quit when done: {QuitWhenDone}. Setting: {ForceSetting ?? "(HQ's current)"}.{(Survey ? " Survey only." : "")}");
        }
    }

    /// <summary>
    /// Blocks all save-file writes and deletes while the self-test is active. Every save type
    /// (profile, progress, the five full-world slots, preferences file) goes through
    /// SaveStorage&lt;T&gt;.Set(string, SaveInfo&lt;T&gt;) and Clear(). Nothing is a class and
    /// ProgressInfo is a struct, so their closed types have separate code and both are patched.
    /// </summary>
    internal static class SaveGuard
    {
        private static Harmony _harmony;

        public static bool Install()
        {
            try
            {
                _harmony = new Harmony(ModMain.Id + ".selftest");
                var block = new HarmonyMethod(typeof(SaveGuard), nameof(Block));
                foreach (Type t in new[] { typeof(SaveStorage<Nothing>), typeof(SaveStorage<ProgressInfo>) })
                {
                    Type info = typeof(SaveInfo<>).MakeGenericType(t.GetGenericArguments()[0]);
                    var set = AccessTools.Method(t, "Set", new[] { typeof(string), info });
                    var clear = AccessTools.Method(t, "Clear");
                    if (set == null || clear == null)
                    {
                        return false;
                    }
                    _harmony.Patch(set, prefix: block);
                    _harmony.Patch(clear, prefix: block);
                }
                return true;
            }
            catch (Exception e)
            {
                ModMain.LogError($"[SELFTEST] save guard error: {e}");
                return false;
            }
        }

        private static bool Block(object __instance)
        {
            if (!SelfTest.Active || SelfTestMemorySaves.Owns(__instance))
            {
                return true;
            }
            ModMain.Log("[SELFTEST] blocked a save write/delete.");
            return false;
        }
    }
}
#endif
