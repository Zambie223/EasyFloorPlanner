using System;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// This machine's settings (added 2026-11-19 at the user's request). They're stored in
    /// PlayerPrefs under "EasyFloorPlanner.", and players change them in Mod List's options page
    /// (<see cref="ModListOptions"/>). Without Mod List they keep their defaults (or whatever was
    /// saved before). Nothing here is networked: <see cref="Editors"/> only counts on the host,
    /// which tells the clients through the terminal's label (FloorPlanTerminalLabelSystem).
    /// Camera follow keeps its own key (PlayCamera, saved since 0.2).
    /// </summary>
    internal static class Settings
    {
        public enum ZoomOutLimit { Normal, Further, Furthest }
        public enum Palette { Standard, Colourblind }
        public enum WhoCanEdit { Everyone, HostOnly }

        private const string Prefix = "EasyFloorPlanner.";
        public const int MinSpeed = 25, MaxSpeed = 300, SpeedStep = 25;

        /// <summary>Camera zoom speed, in percent of the default (wheel steps and the controller's zoom).</summary>
        public static int ZoomSpeed { get; private set; } = 100;

        /// <summary>Camera pan speed, in percent of the default (controller stick, and WASD / stick in the editor).</summary>
        public static int PanSpeed { get; private set; } = 100;

        public static ZoomOutLimit ZoomOut { get; private set; } = ZoomOutLimit.Normal;

        /// <summary>EFP's copies of the rain / snow effect over the parts of a big restaurant the game's own effect doesn't reach.</summary>
        public static bool WeatherEverywhere { get; private set; } = true;

        public static Palette EditorPalette { get; private set; } = Palette.Standard;

        /// <summary>The editor opens with the mode and brush it was closed with (this game session only).</summary>
        public static bool RememberTools { get; private set; }

        /// <summary>Apply commits the draft and the editor stays open (Esc / Back closes it).</summary>
        public static bool ApplyKeepsOpen { get; private set; }

        /// <summary>Host's setting: who may open the editor. Anyone can still watch.</summary>
        public static WhoCanEdit Editors { get; private set; } = WhoCanEdit.Everyone;

        // The camera's limits for each zoom-out choice. The furthest view is bounded by the field of
        // view: the game's camera already sits at its 40-unit maximum distance with a far clip of
        // 60 (logged 2026-11-19), so going further out means widening the view instead.
        public static float MinZoom => ZoomOut == ZoomOutLimit.Furthest ? 0.4f : ZoomOut == ZoomOutLimit.Further ? 0.5f : 0.6f;
        public static float MaxFov => ZoomOut == ZoomOutLimit.Furthest ? 70f : 60f;

        public static void Load()
        {
            ZoomSpeed = ClampSpeed(GetInt("ZoomSpeed", 100));
            PanSpeed = ClampSpeed(GetInt("PanSpeed", 100));
            ZoomOut = GetEnum("ZoomOut", ZoomOutLimit.Normal);
            WeatherEverywhere = GetInt("WeatherEverywhere", 1) == 1;
            EditorPalette = GetEnum("Palette", Palette.Standard);
            RememberTools = GetInt("RememberTools", 0) == 1;
            ApplyKeepsOpen = GetInt("ApplyKeepsOpen", 0) == 1;
            Editors = GetEnum("WhoCanEdit", WhoCanEdit.Everyone);
            ModMain.Log($"Settings: {Describe()}.");
        }

        public static string Describe() =>
            $"zoom speed {ZoomSpeed}%, pan speed {PanSpeed}%, zoom-out {ZoomOut}, weather everywhere {WeatherEverywhere}, palette {EditorPalette}, "
            + $"remember tools {RememberTools}, apply keeps the editor open {ApplyKeepsOpen}, who can edit {Editors}, camera follow {PlayCamera.Follow}";

        public static void SetZoomSpeed(int v) => SetInt("ZoomSpeed", ZoomSpeed = ClampSpeed(v));
        public static void SetPanSpeed(int v) => SetInt("PanSpeed", PanSpeed = ClampSpeed(v));
        public static void SetZoomOut(ZoomOutLimit v) => SetInt("ZoomOut", (int)(ZoomOut = v));
        public static void SetWeatherEverywhere(bool v) => SetInt("WeatherEverywhere", (WeatherEverywhere = v) ? 1 : 0);
        public static void SetPalette(Palette v) => SetInt("Palette", (int)(EditorPalette = v));
        public static void SetRememberTools(bool v) => SetInt("RememberTools", (RememberTools = v) ? 1 : 0);
        public static void SetApplyKeepsOpen(bool v) => SetInt("ApplyKeepsOpen", (ApplyKeepsOpen = v) ? 1 : 0);
        public static void SetEditors(WhoCanEdit v) => SetInt("WhoCanEdit", (int)(Editors = v));

        /// <summary>Writes the settings to disk (PlayerPrefs only flushes on Save or on quit).</summary>
        public static void Save()
        {
            try
            {
                PlayerPrefs.Save();
                ModMain.Log($"Settings saved: {Describe()}.");
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't save the settings: {e.Message}");
            }
        }

        private static int ClampSpeed(int v) => Mathf.Clamp(Mathf.RoundToInt(v / (float)SpeedStep) * SpeedStep, MinSpeed, MaxSpeed);

        private static int GetInt(string key, int fallback)
        {
            try
            {
                return PlayerPrefs.GetInt(Prefix + key, fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static T GetEnum<T>(string key, T fallback) where T : struct, Enum
        {
            int v = GetInt(key, Convert.ToInt32(fallback));
            return Enum.IsDefined(typeof(T), v) ? (T)Enum.ToObject(typeof(T), v) : fallback;
        }

        private static void SetInt(string key, int v)
        {
            try
            {
                PlayerPrefs.SetInt(Prefix + key, v);
            }
            catch (Exception)
            {
                // Not stored; the value still applies until the game closes.
            }
        }

#if DEBUG
        /// <summary>Self-test: every setting's current value, to put back afterwards.</summary>
        internal static Action TestSnapshot()
        {
            int zoom = ZoomSpeed, pan = PanSpeed;
            ZoomOutLimit zoomOut = ZoomOut;
            bool weather = WeatherEverywhere, remember = RememberTools, keep = ApplyKeepsOpen;
            Palette palette = EditorPalette;
            WhoCanEdit editors = Editors;
            return () =>
            {
                SetZoomSpeed(zoom);
                SetPanSpeed(pan);
                SetZoomOut(zoomOut);
                SetWeatherEverywhere(weather);
                SetPalette(palette);
                SetRememberTools(remember);
                SetApplyKeepsOpen(keep);
                SetEditors(editors);
            };
        }
#endif
    }
}
