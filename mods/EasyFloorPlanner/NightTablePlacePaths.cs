using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kitchen;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Caches the game's night-time seat path checks in big restaurants.
    ///
    /// Vanilla (decompiled 2026-11-19): FindTablePlaces runs on every night frame (table updates are
    /// requested every night frame; see <see cref="NightChairPathing"/>). For every seat at every
    /// table it runs a full NavMesh.CalculatePath from the customer entry point. It also rebuilds each
    /// table's list of places and the seat numbers shown over the tables, so the system itself
    /// can't be skipped. The profiler measured it at 27.7 ms per frame in a 972-tile restaurant,
    /// about 70% of all game-system time at night.
    ///
    /// A transpiler swaps just those two calls in its lambda body: NavMesh.CalculatePath and
    /// NavMeshPath.status. On ordinary night frames (<see cref="NightChairPathing.OrdinaryNightFrame"/>)
    /// with more than <see cref="NightChairPathing.ThrottleAbove"/> seats known, a seat's result is
    /// reused until it's due, and recomputed every <see cref="RefreshFrames"/> frames. Due times are
    /// staggered, so about 1 / RefreshFrames of the seats are recomputed each frame instead of all
    /// at once. New seats, and seats whose position or entry point changed, are computed at once.
    /// Every other frame (day, the first night frame, small restaurants) makes the real call, so
    /// the start of the day uses a full, fresh check.
    ///
    /// Trade-off: during the night, the seat count over a table can trail a change by up to
    /// RefreshFrames frames.
    /// </summary>
    [HarmonyPatch]
    internal static class NightTablePlacePaths
    {
        public const int RefreshFrames = 30;
        private const int PruneEvery = 600;

        private struct Key : IEquatable<Key>
        {
            public Vector3 Source, Target;
            public int Mask;

            public bool Equals(Key o) => Source == o.Source && Target == o.Target && Mask == o.Mask;

            public override bool Equals(object obj) => obj is Key k && Equals(k);

            public override int GetHashCode() => Source.GetHashCode() * 31 ^ Target.GetHashCode() * 17 ^ Mask;
        }

        private struct Entry
        {
            public bool Ok;
            public NavMeshPathStatus Status;
            public int Due;
        }

        private static readonly Dictionary<Key, Entry> Cache = new Dictionary<Key, Entry>();
        private static NavMeshPathStatus Last;
        private static int Stagger, LastPrune;

        /// <summary>Self-test / stats: real path calculations and cached answers since the counters were reset.</summary>
        public static int Computed, Reused;

        public static int CacheSize => Cache.Count;

        private static MethodBase Target()
        {
            foreach (Type t in typeof(FindTablePlaces).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo m = AccessTools.Method(t, "OriginalLambdaBody");
                if (m != null)
                {
                    return m;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            if (Target() == null)
            {
                ModMain.LogWarning("Night seat path checks: FindTablePlaces' lambda body wasn't found (game update?); leaving it as it is.");
                return false;
            }
            return true;
        }

        private static MethodBase TargetMethod() => Target();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo calc = AccessTools.Method(typeof(NavMesh), nameof(NavMesh.CalculatePath),
                new[] { typeof(Vector3), typeof(Vector3), typeof(int), typeof(NavMeshPath) });
            MethodInfo status = AccessTools.PropertyGetter(typeof(NavMeshPath), nameof(NavMeshPath.status));
            var code = new List<CodeInstruction>(instructions);
            var original = new List<CodeInstruction>(code.Count);
            original.AddRange(code.ConvertAll(c => c.Clone()));
            int calcs = 0, statuses = 0;
            foreach (CodeInstruction c in code)
            {
                if (c.Calls(calc))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(NightTablePlacePaths), nameof(CalculatePath));
                    calcs++;
                }
                else if (c.Calls(status))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(NightTablePlacePaths), nameof(Status));
                    statuses++;
                }
            }
            if (calcs != 1 || statuses != 1)
            {
                ModMain.LogWarning($"Night seat path checks: FindTablePlaces didn't look as expected ({calcs} path call(s), {statuses} status read(s)); leaving it as it is.");
                return original;
            }
            ModMain.Log("Night seat path checks: FindTablePlaces patched (results cached on night frames in big restaurants).");
            return code;
        }

        /// <summary>Stands in for NavMesh.CalculatePath(source, target, areaMask, path) in FindTablePlaces.</summary>
        public static bool CalculatePath(Vector3 source, Vector3 target, int areaMask, NavMeshPath path)
        {
            int frame = Time.frameCount;
            if (NightChairPathing.OrdinaryNightFrame != frame)
            {
                if (Cache.Count > 0)
                {
                    Cache.Clear(); // a full, real check this frame; start fresh next night frame
                }
                return Real(source, target, areaMask, path);
            }
            var key = new Key { Source = source, Target = target, Mask = areaMask };
            bool spread = Cache.Count > NightChairPathing.ThrottleAbove;
            if (spread && Cache.TryGetValue(key, out Entry e) && frame < e.Due)
            {
                Last = e.Status;
                Reused++;
                return e.Ok;
            }
            bool ok = Real(source, target, areaMask, path);
            // A seat seen for the first time gets a staggered due time, so the seats don't all come
            // due on the same frame; after that each keeps its own place in the cycle.
            int due = Cache.ContainsKey(key) ? frame + RefreshFrames : frame + 1 + Stagger++ % RefreshFrames;
            Cache[key] = new Entry { Ok = ok, Status = Last, Due = due };
            if (frame - LastPrune > PruneEvery)
            {
                LastPrune = frame;
                Prune(frame);
            }
            return ok;
        }

        /// <summary>Stands in for path.status right after the call above.</summary>
        public static NavMeshPathStatus Status(NavMeshPath path) => Last;

        private static bool Real(Vector3 source, Vector3 target, int areaMask, NavMeshPath path)
        {
            bool ok = NavMesh.CalculatePath(source, target, areaMask, path);
            Last = path.status;
            Computed++;
            return ok;
        }

        private static void Prune(int frame)
        {
            var stale = new List<Key>();
            foreach (KeyValuePair<Key, Entry> kv in Cache)
            {
                if (kv.Value.Due < frame - RefreshFrames)
                {
                    stale.Add(kv.Key); // no longer asked for (the seat or table went away)
                }
            }
            foreach (Key k in stale)
            {
                Cache.Remove(k);
            }
        }
    }
}
