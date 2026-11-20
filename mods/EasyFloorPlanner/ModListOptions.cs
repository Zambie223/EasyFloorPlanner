using System;
using System.Collections;
using System.Collections.Generic;
using Kitchen;
using Platforms;

namespace EasyFloorPlanner
{
    /// <summary>
    /// EFP's page in Mod List's options (Pause, Mods, the gear on EFP's row; options API version 1,
    /// Mod List 0.2.0+, see its docs/MOD_OPTIONS.md). Mod List finds this class by its name, so
    /// there's no reference to Mod List and nothing happens without it. The values live in
    /// <see cref="Settings"/>.
    /// </summary>
    public static class ModListOptions
    {
        private static bool IsClient => Session.NetworkedPlayState == NetworkedPlayState.Client;

        public static IEnumerable GetOptions()
        {
            return new List<Dictionary<string, object>>
            {
                Heading("Camera"),
                new Dictionary<string, object>
                {
                    ["id"] = "follow",
                    ["type"] = "bool",
                    ["label"] = "Follow your chef",
                    ["description"] = "While zoomed in, keeps your chef on screen. Also F, or RB + right stick click.",
                    ["get"] = (Func<bool>)(() => PlayCamera.Follow),
                    ["set"] = (Action<bool>)(v => PlayCamera.SetFollow(v, toast: false)),
                },
                Speed("zoomSpeed", "Zoom speed", "Mouse wheel and controller zooming.", () => Settings.ZoomSpeed, Settings.SetZoomSpeed),
                Speed("panSpeed", "Pan speed", "Controller panning, and moving the editor's view.",
                    () => Settings.PanSpeed, Settings.SetPanSpeed),
                new Dictionary<string, object>
                {
                    ["id"] = "zoomOut",
                    ["type"] = "choice",
                    ["label"] = "Furthest zoom-out",
                    ["description"] = "Further out widens the view, so the edges stretch a little.",
                    ["choices"] = new[] { nameof(Settings.ZoomOutLimit.Normal), nameof(Settings.ZoomOutLimit.Further), nameof(Settings.ZoomOutLimit.Furthest) },
                    ["get"] = (Func<string>)(() => Settings.ZoomOut.ToString()),
                    ["set"] = (Action<string>)(v => Settings.SetZoomOut((Settings.ZoomOutLimit)Enum.Parse(typeof(Settings.ZoomOutLimit), v))),
                },

                Heading("Floor plan editor"),
                new Dictionary<string, object>
                {
                    ["id"] = "palette",
                    ["type"] = "choice",
                    ["label"] = "Editor colours",
                    ["description"] = "Colourblind-friendly: colours that stay distinct with colour blindness.",
                    ["choices"] = new[] { nameof(Settings.Palette.Standard), nameof(Settings.Palette.Colourblind) },
                    ["labels"] = new[] { "Standard", "Colourblind-friendly" },
                    ["get"] = (Func<string>)(() => Settings.EditorPalette.ToString()),
                    ["set"] = (Action<string>)(v => Settings.SetPalette((Settings.Palette)Enum.Parse(typeof(Settings.Palette), v))),
                },
                new Dictionary<string, object>
                {
                    ["id"] = "rememberTools",
                    ["type"] = "bool",
                    ["label"] = "Remember mode and brush",
                    ["description"] = "Open the editor with the mode and brush you last used.",
                    ["get"] = (Func<bool>)(() => Settings.RememberTools),
                    ["set"] = (Action<bool>)Settings.SetRememberTools,
                },
                new Dictionary<string, object>
                {
                    ["id"] = "applyKeepsOpen",
                    ["type"] = "bool",
                    ["label"] = "Apply keeps the editor open",
                    ["description"] = "Apply doesn't close the editor. Esc or Back closes it.",
                    ["get"] = (Func<bool>)(() => Settings.ApplyKeepsOpen),
                    ["set"] = (Action<bool>)Settings.SetApplyKeepsOpen,
                },
                new Dictionary<string, object>
                {
                    ["id"] = "hideControls",
                    ["type"] = "bool",
                    ["label"] = "Hide controls legend",
                    ["description"] = "Hides the editor's two lines of control hints.",
                    ["get"] = (Func<bool>)(() => Settings.HideControls),
                    ["set"] = (Action<bool>)Settings.SetHideControls,
                },
                new Dictionary<string, object>
                {
                    ["id"] = "whoCanEdit",
                    ["type"] = "choice",
                    ["label"] = "Who can edit",
                    ["description"] = "Host's setting applies. Everyone can still watch.",
                    ["choices"] = new[] { nameof(Settings.WhoCanEdit.Everyone), nameof(Settings.WhoCanEdit.HostOnly) },
                    ["labels"] = new[] { "Everyone", "Host only" },
                    ["enabled"] = (Func<bool>)(() => !IsClient),
                    ["get"] = (Func<string>)(() => Settings.Editors.ToString()),
                    ["set"] = (Action<string>)(v => Settings.SetEditors((Settings.WhoCanEdit)Enum.Parse(typeof(Settings.WhoCanEdit), v))),
                },

                Heading("Display"),
                new Dictionary<string, object>
                {
                    ["id"] = "weatherEverywhere",
                    ["type"] = "bool",
                    ["label"] = "Weather everywhere",
                    ["description"] = "Rain and snow over all of a big restaurant. Off: near the middle only.",
                    ["get"] = (Func<bool>)(() => Settings.WeatherEverywhere),
                    ["set"] = (Action<bool>)Settings.SetWeatherEverywhere,
                },

                Heading("Diagnostics"),
                new Dictionary<string, object>
                {
                    ["id"] = "profile",
                    ["type"] = "button",
                    ["label"] = SystemProfiler.Active ? "Profiling (see Player.log)" : "Profile next night and day",
                    ["description"] = $"Times every game system for {SystemProfiler.PhaseSeconds:0} s of night and of day, into Player.log.",
                    ["enabled"] = (Func<bool>)(() => !SystemProfiler.Active && !SystemProfiler.Requested),
                    ["action"] = (Action)SystemProfiler.Request,
                },
            };
        }

        public static void OnOptionsClosed() => Settings.Save();

        private static Dictionary<string, object> Heading(string label) => new Dictionary<string, object> { ["type"] = "heading", ["label"] = label };

        private static Dictionary<string, object> Speed(string id, string label, string description, Func<int> get, Action<int> set) => new Dictionary<string, object>
        {
            ["id"] = id,
            ["type"] = "int",
            ["label"] = label,
            ["description"] = description,
            ["min"] = Settings.MinSpeed,
            ["max"] = Settings.MaxSpeed,
            ["step"] = Settings.SpeedStep,
            ["format"] = "{0}%",
            ["slider"] = true,
            ["get"] = get,
            ["set"] = set,
        };
    }
}
