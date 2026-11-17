using System;
using System.Reflection;
using HarmonyLib;
using KitchenMods;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Entry point. Easy Floor Planner lets players resize the restaurant and edit its walls, hatches
    /// and doors during the night, through a "Floor Plan" terminal by the front door. Systems are
    /// discovered automatically (IModSystem). Harmony patches (navmesh area, network message
    /// routing, view prefab, terminal label) are applied here.
    /// </summary>
    public class ModMain : IModInitializer
    {
        public const string Id = "zambie.easyfloorplanner";
        public const string Name = "Easy Floor Planner";
        public const string Version = "0.2.0";

        private static Harmony _harmony;
        private static bool _resolverRegistered;

        public void PostActivate(KitchenMods.Mod mod)
        {
            Log($"v{Version} loaded");
            RegisterAssemblyResolver();
            if (_harmony == null)
            {
                _harmony = new Harmony(Id);
                _harmony.PatchAll(typeof(ModMain).Assembly);
            }
            PlayCamera.Install();
#if DEBUG
            SelfTest.TryActivate();
#endif
        }

        public void PreInject() { }

        public void PostInject() { }

        /// <summary>
        /// Client-to-host responses carry the response's System.Type, serialized by name. Mod
        /// assemblies are loaded from bytes, so Type.GetType(name) on the receiving side can't
        /// find them by itself. Point the runtime at this (already loaded) assembly.
        /// </summary>
        private static void RegisterAssemblyResolver()
        {
            if (_resolverRegistered)
            {
                return;
            }
            _resolverRegistered = true;
            Assembly self = typeof(ModMain).Assembly;
            string selfName = self.GetName().Name;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    if (new AssemblyName(args.Name).Name == selfName)
                    {
                        return self;
                    }
                }
                catch (Exception)
                {
                    // Malformed names are not ours.
                }
                return null;
            };
        }

        // Everything goes to Player.log:
        // %USERPROFILE%\AppData\LocalLow\It's Happening\PlateUp\Player.log
        internal static void Log(string message) => Debug.Log($"[{Name}] {message}");
        internal static void LogWarning(string message) => Debug.LogWarning($"[{Name}] {message}");
        internal static void LogError(string message) => Debug.LogError($"[{Name}] {message}");
    }
}
