using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kitchen;
using KitchenMods;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Opt-in performance diagnostic (added 2026-11-19 for a very large restaurant that still drops
    /// frames). Off unless the file &lt;game&gt;\Mods\EasyFloorPlanner\profile.flag exists when the
    /// game starts; with no flag nothing is patched, so it costs nothing.
    ///
    /// When on, it times every ECS system's Update (SystemBase.Update, the one entry point all game
    /// and mod systems go through; in this Entities version groups are ComponentSystems, so they
    /// aren't timed and nothing is counted twice) for
    /// <see cref="PhaseSeconds"/> of night and the same of day. It also records frame times and
    /// garbage collections. It logs the systems that cost the most per frame, then goes idle (the patch
    /// stays: see Stop).
    /// Frame time beyond the systems' total is everything else: views and other MonoBehaviours,
    /// rendering, physics and the garbage collector.
    /// </summary>
    internal static class SystemProfiler
    {
        public const float PhaseSeconds = 30f;
        private const int TopN = 15;

        public static string FlagPath => Path.Combine(FolderModSource.ModsFolder, "EasyFloorPlanner", "profile.flag");

        public static bool Active { get; private set; }

        private static MethodInfo _update;

        /// <summary>0 = not measuring, 1 = night, 2 = day (set each frame by <see cref="SystemProfilerClock"/>).</summary>
        internal static int Phase;

        internal class Stat
        {
            public double Total, Max;
            public int Calls;
        }

        internal class PhaseData
        {
            public readonly Dictionary<Type, Stat> Systems = new Dictionary<Type, Stat>();
            public double Seconds, WorstMs;
            public int Frames, Over33, Over50, GcStart = -1, GcEnd;
            public bool Done;
        }

        internal static readonly PhaseData[] Phases = { null, new PhaseData(), new PhaseData() };

        public static void TryStart(Harmony harmony)
        {
            if (Active || !File.Exists(FlagPath))
            {
                return;
            }
            _update = AccessTools.Method(typeof(SystemBase), "Update");
            if (_update == null)
            {
                ModMain.LogWarning("Profiler: SystemBase.Update not found; not profiling.");
                return;
            }
            harmony.Patch(_update, prefix: new HarmonyMethod(typeof(SystemProfiler), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(SystemProfiler), nameof(Postfix)));
            Active = true;
            ModMain.Log($"Profiler on (profile.flag found): timing every game system for {PhaseSeconds:0} s of night and {PhaseSeconds:0} s of day in a restaurant.");
        }

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(SystemBase __instance, long __state)
        {
            int phase = Phase;
            if (phase == 0)
            {
                return;
            }
            PhaseData data = Phases[phase];
            if (data.Done)
            {
                return;
            }
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            Type type = __instance.GetType();
            if (!data.Systems.TryGetValue(type, out Stat s))
            {
                s = new Stat();
                data.Systems[type] = s;
            }
            s.Total += ms;
            s.Calls++;
            if (ms > s.Max)
            {
                s.Max = ms;
            }
        }

        /// <summary>Called once per frame by the clock system with the current phase.</summary>
        internal static void Frame(int phase)
        {
            Phase = 0;
            if (!Active || phase == 0)
            {
                return;
            }
            PhaseData data = Phases[phase];
            if (data.Done)
            {
                return;
            }
            Phase = phase;
            float dt = Time.unscaledDeltaTime;
            if (data.GcStart < 0)
            {
                data.GcStart = GC.CollectionCount(0);
                return; // the first frame's delta belongs to whatever came before
            }
            data.Frames++;
            data.Seconds += dt;
            double ms = dt * 1000.0;
            data.WorstMs = Math.Max(data.WorstMs, ms);
            data.Over33 += ms > 33.4 ? 1 : 0;
            data.Over50 += ms > 50 ? 1 : 0;
            if (data.Seconds >= PhaseSeconds)
            {
                data.Done = true;
                data.GcEnd = GC.CollectionCount(0);
                Phase = 0;
                Report(phase == 1 ? "Night" : "Day", data);
                if (Phases[1].Done && Phases[2].Done)
                {
                    Stop();
                }
            }
        }

        private static void Report(string name, PhaseData d)
        {
            double systemsPerFrame = d.Systems.Values.Sum(s => s.Total) / Math.Max(d.Frames, 1);
            double frameMs = d.Seconds * 1000.0 / Math.Max(d.Frames, 1);
            ModMain.Log($"Profiler, {name.ToLowerInvariant()}: {d.Frames} frames in {d.Seconds:0.0} s; frame {frameMs:0.0} ms on average ({1000.0 / frameMs:0} fps), "
                        + $"worst {d.WorstMs:0} ms, {d.Over33} frames over 33 ms, {d.Over50} over 50 ms; "
                        + $"game systems {systemsPerFrame:0.0} ms per frame (the rest is views, rendering and the like); "
                        + $"{d.GcEnd - d.GcStart} garbage collection(s).");
            foreach (KeyValuePair<Type, Stat> s in d.Systems.OrderByDescending(s => s.Value.Total).Take(TopN))
            {
                ModMain.Log($"Profiler, {name.ToLowerInvariant()}:   {s.Value.Total / Math.Max(d.Frames, 1),6:0.00} ms/frame  max {s.Value.Max,6:0.0} ms  {s.Key.FullName}");
            }
        }

        private static void Stop()
        {
            // The patch stays: Stop runs inside a patched SystemBase.Update, and unpatching a method
            // that's on the call stack froze the game (2026-11-19). With Active off, the postfix
            // returns at once; what's left is a timestamp read per system, a few microseconds a frame.
            Active = false;
            ModMain.Log("Profiler finished (it stays idle until the game restarts). Delete profile.flag to keep it off next time.");
        }
    }

    /// <summary>Tells the profiler each frame whether it's night or day in a restaurant. Does nothing unless profiling.</summary>
    public class SystemProfilerClock : GenericSystemBase, IModSystem
    {
        protected override void OnUpdate()
        {
            if (!SystemProfiler.Active)
            {
                return;
            }
            int phase = !Has<SKitchenMarker>() ? 0 : Has<SIsNightTime>() ? 1 : Has<SIsDayTime>() ? 2 : 0;
            SystemProfiler.Frame(phase);
        }
    }
}
