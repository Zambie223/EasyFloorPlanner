using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Kitchen;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Spreads the game's night-time chair path checks over several frames in big restaurants.
    ///
    /// Vanilla (decompiled 2026-11-19): RequestTableUpdateAtNight asks for a table update on every
    /// night frame with EnforcePaths, and MarkUnpathableChairs then runs a full
    /// NavMesh.CalculatePath from the customer entry point to every ghost chair, every frame. The
    /// cost grows with the number of chairs times the size of the navmesh, so a restaurant several
    /// times vanilla's size (which this mod makes possible) lags at night and is smooth by day.
    ///
    /// On ordinary night frames with more than <see cref="ThrottleAbove"/> ghost chairs, this does
    /// the same check (same call, same result) but only for some of the chairs each frame: new or
    /// moved chairs (or a moved entry point) first, then the ones checked longest ago. It works
    /// within a time budget, and always checks at least 1 / <see cref="RefreshFrames"/> of them, so
    /// every chair is rechecked within that many frames. The first night frame, every day frame and
    /// smaller restaurants run vanilla's own code untouched, so whatever the game decides at the
    /// start of the day (which chairs become real) uses a full, fresh check.
    ///
    /// Trade-off: during the night a chair's ✓/✗ (and the seat count the game shows) can trail a
    /// change by up to RefreshFrames frames.
    /// </summary>
    [HarmonyPatch(typeof(MarkUnpathableChairs), "OnUpdate")]
    internal static class NightChairPathing
    {
        /// <summary>Chair count above which checks are spread out (the self-test lowers it).</summary>
        public static int ThrottleAbove = 64;
        public const int RefreshFrames = 30;
        private const double BudgetMs = 1.5;
        private const int ReportAfterFrames = 600;

        private static readonly NavMeshPath Path = new NavMeshPath();
        private static readonly Stopwatch Watch = new Stopwatch();

        private struct Checked
        {
            public Vector3 Position;
            public Vector3 Source;
            public int Frame;
        }

        private static readonly Dictionary<Entity, Checked> Known = new Dictionary<Entity, Checked>();
        private static readonly List<(Entity e, Vector3 p, int order)> Order = new List<(Entity, Vector3, int)>();
        private static readonly HashSet<Entity> Alive = new HashSet<Entity>();

        private static World QueryWorld;
        private static EntityQuery Chairs, Night, Day, NightFirst, DayFirst, Spec;

        // Per-night statistics, logged once.
        private static bool Reported;
        private static int Frames, FullFrames, ChairsSeen, ChecksTotal;
        private static double MsTotal, FullMsTotal, MsMax;

        /// <summary>
        /// The frame number of the latest ordinary night frame with path checks on (not the first
        /// frame of a night or day). Table updates run MarkUnpathableChairs before FindTablePlaces,
        /// so <see cref="NightTablePlacePaths"/> reads this in the same frame.
        /// </summary>
        public static int OrdinaryNightFrame { get; private set; } = -1;

        /// <summary>Self-test: whether the last update was spread out (true) or left to vanilla (false).</summary>
        public static bool LastThrottled { get; private set; }

        /// <summary>Self-test: the chairs checked in the last spread-out update.</summary>
        public static int LastChecked { get; private set; }

        private static void EnsureQueries(EntityManager em, World world)
        {
            if (QueryWorld == world)
            {
                return;
            }
            QueryWorld = world;
            Known.Clear();
            Chairs = em.CreateEntityQuery(ComponentType.ReadWrite<CApplianceGhostChair>(), ComponentType.ReadOnly<CPosition>());
            Night = em.CreateEntityQuery(ComponentType.ReadOnly<SIsNightTime>());
            Day = em.CreateEntityQuery(ComponentType.ReadOnly<SIsDayTime>());
            NightFirst = em.CreateEntityQuery(ComponentType.ReadOnly<SIsNightFirstUpdate>());
            DayFirst = em.CreateEntityQuery(ComponentType.ReadOnly<SIsDayFirstUpdate>());
            Spec = em.CreateEntityQuery(ComponentType.ReadOnly<SPerformTableUpdate>());
        }

        private static bool Prefix(MarkUnpathableChairs __instance)
        {
            Watch.Restart();
            LastThrottled = false;
            try
            {
                EntityManager em = __instance.EntityManager;
                EnsureQueries(em, __instance.World);
                if (!NightFirst.IsEmpty)
                {
                    ResetStats();
                }
                if (Night.IsEmpty || !Day.IsEmpty || !NightFirst.IsEmpty || !DayFirst.IsEmpty || Spec.CalculateEntityCount() != 1)
                {
                    Known.Clear(); // vanilla checks everything now; start fresh next time
                    return true;
                }
                SPerformTableUpdate spec = Spec.GetSingleton<SPerformTableUpdate>();
                if (spec.EnforcePaths)
                {
                    OrdinaryNightFrame = Time.frameCount; // NightTablePlacePaths may spread its checks this frame too
                }
                int count = Chairs.CalculateEntityCount();
                if (!spec.EnforcePaths || count <= ThrottleAbove)
                {
                    Known.Clear();
                    return true;
                }
                Spread(em, spec.PathingSource, count);
                LastThrottled = true;
                return false;
            }
            catch (System.Exception e)
            {
                ModMain.LogWarning($"Night chair path checks: falling back to the game's own ({e.Message}).");
                Known.Clear();
                return true;
            }
        }

        private static void Postfix()
        {
            Watch.Stop();
            if (QueryWorld == null || Night.IsEmpty || !Day.IsEmpty)
            {
                return;
            }
            double ms = Watch.Elapsed.TotalMilliseconds;
            Frames++;
            MsTotal += ms;
            MsMax = System.Math.Max(MsMax, ms);
            if (!LastThrottled)
            {
                FullFrames++;
                FullMsTotal += ms;
            }
            if (!Reported && Frames >= ReportAfterFrames && ChairsSeen > ThrottleAbove)
            {
                Reported = true;
                string full = FullFrames > 0 ? $"{FullMsTotal / FullFrames:0.0} ms" : "not measured";
                ModMain.Log($"Night chair path checks: {ChairsSeen} ghost chairs. Checking all of them took {full} per frame; "
                            + $"spread out: {MsTotal / Frames:0.0} ms per frame on average ({MsMax:0.0} ms at most), "
                            + $"{(double)ChecksTotal / System.Math.Max(Frames - FullFrames, 1):0} chairs per frame, over {Frames} night frames.");
            }
        }

        private static void Spread(EntityManager em, Vector3 source, int count)
        {
            int frame = Time.frameCount;
            Order.Clear();
            Alive.Clear();
            using (NativeArray<Entity> entities = Chairs.ToEntityArray(Allocator.Temp))
            using (NativeArray<CPosition> positions = Chairs.ToComponentDataArray<CPosition>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Vector3 p = positions[i].Position;
                    Alive.Add(e);
                    // New, moved, or a moved entry point: before everything else.
                    int order = Known.TryGetValue(e, out Checked c) && c.Position == p && c.Source == source ? c.Frame : int.MinValue;
                    Order.Add((e, p, order));
                }
            }
            if (Known.Count > Alive.Count + 256)
            {
                var gone = new List<Entity>();
                foreach (Entity e in Known.Keys)
                {
                    if (!Alive.Contains(e))
                    {
                        gone.Add(e);
                    }
                }
                foreach (Entity e in gone)
                {
                    Known.Remove(e);
                }
            }
            Order.Sort((a, b) => a.order.CompareTo(b.order));

            int minimum = Mathf.Max(1, Mathf.CeilToInt(count / (float)RefreshFrames));
            int done = 0;
            foreach ((Entity e, Vector3 p, int order) in Order)
            {
                if (done >= minimum && order != int.MinValue && Watch.Elapsed.TotalMilliseconds >= BudgetMs)
                {
                    break;
                }
                NavMesh.CalculatePath(source, p, -1, Path);
                bool pathable = Path.status == NavMeshPathStatus.PathComplete;
                CApplianceGhostChair ghost = em.GetComponentData<CApplianceGhostChair>(e);
                if (ghost.IsPathable != pathable)
                {
                    ghost.IsPathable = pathable;
                    em.SetComponentData(e, ghost);
                }
                Known[e] = new Checked { Position = p, Source = source, Frame = frame };
                done++;
            }
            LastChecked = done;
            ChairsSeen = count;
            ChecksTotal += done;
        }

        private static void ResetStats()
        {
            Reported = false;
            Frames = FullFrames = ChairsSeen = ChecksTotal = 0;
            MsTotal = FullMsTotal = MsMax = 0;
        }
    }
}
