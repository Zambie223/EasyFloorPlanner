using System;
using HarmonyLib;
using Kitchen;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Opt-in diagnostic, part of the profiler (only with profile.flag). Added 2026-11-19 because
    /// customers in a very large restaurant teleported instead of walking.
    ///
    /// Vanilla CustomerView (decompiled) moves customers with a NavMeshAgent and teleports them in
    /// two places:
    ///   - CheckForStuck: every 2 s, if a customer moved under ~1.4 units and isn't at its target, it
    ///     resets the path, asks again and disables the agent for a frame; after 10 s of that it warps
    ///     the customer to its target ("stuck warps");
    ///   - SetPosition: when the simulation's position differs from the view's by more than 0.5 (or
    ///     is forced), it warps the agent there ("position snaps").
    /// Suspect: Unity computes agent paths a few A* iterations at a time (NavMesh.
    /// pathfindingIterationsPerFrame, default 100, shared by all agents; the game never changes it).
    /// With hundreds of customers on a big navmesh, paths stay pending, customers stand still, and
    /// the stuck check re-queues them and finally warps them.
    ///
    /// During the day, with customers present, it measures three 30 s windows: Unity's default (100,
    /// what vanilla uses), then 1000 iterations per frame, then 100 again (A-B-A, so the day's
    /// progress doesn't bias the comparison). Since PathfindingBudget raises the budget in big
    /// restaurants, the default is set explicitly rather than taken from the current value. It logs per window: customers, how many are waiting
    /// for a path, stuck warps, position snaps and the frame time. Then it restores the setting.
    /// </summary>
    internal static class CustomerMovementProbe
    {
        private const float WindowSeconds = 30f;
        private const int RaisedIterations = 1000;
        private const int UnityDefaultIterations = 100;

        private static readonly AccessTools.FieldRef<CustomerView, NavMeshAgent> AgentOf =
            AccessTools.FieldRefAccess<CustomerView, NavMeshAgent>("Agent");

        public static bool Active { get; private set; }

        private static int _window = -1, _original;
        private static float _windowTime, _nextSample;
        private static int _samples, _frames, _stuckWarps, _snaps;
        private static double _customers, _pending, _offMesh, _still, _frameMs;
        private static float _snapMax;

        public static void TryStart(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(CustomerView), "CheckForStuck"),
                    prefix: new HarmonyMethod(typeof(CustomerMovementProbe), nameof(StuckPrefix)),
                    postfix: new HarmonyMethod(typeof(CustomerMovementProbe), nameof(StuckPostfix)));
                harmony.Patch(AccessTools.Method(typeof(CustomerView), nameof(CustomerView.SetPosition)),
                    prefix: new HarmonyMethod(typeof(CustomerMovementProbe), nameof(SetPositionPrefix)));
                Active = true;
                ModMain.Log($"Customer movement probe on: three {WindowSeconds:0} s windows during the day "
                            + $"(path budget {UnityDefaultIterations}, then {RaisedIterations}, then {UnityDefaultIterations} again).");
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Customer movement probe not started: {e.Message}");
            }
        }

        private static void StuckPrefix(CustomerView __instance, out Vector3 __state) => __state = __instance.transform.position;

        private static void StuckPostfix(CustomerView __instance, Vector3 __state)
        {
            if (_window >= 0 && (__instance.transform.position - __state).sqrMagnitude > 0.75f * 0.75f)
            {
                _stuckWarps++;
            }
        }

        private static void SetPositionPrefix(CustomerView __instance, UpdateViewPositionData pos)
        {
            if (_window < 0 || __instance == null)
            {
                return;
            }
            Vector3 d = __instance.transform.localPosition - pos.Position;
            float cheb = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            if (pos.Force || cheb > 0.5f)
            {
                _snaps++;
                _snapMax = Mathf.Max(_snapMax, cheb);
            }
        }

        /// <summary>Called every frame by the profiler clock; <paramref name="day"/> is true in a restaurant by day.</summary>
        public static void Frame(bool day)
        {
            if (!Active || !day)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (now >= _nextSample)
            {
                _nextSample = now + 1f;
                Sample();
            }
            if (_window < 0)
            {
                return;
            }
            _frames++;
            _frameMs += Time.unscaledDeltaTime * 1000.0;
            _windowTime += Time.unscaledDeltaTime;
            if (_windowTime >= WindowSeconds)
            {
                Report();
                _window++;
                if (_window > 2)
                {
                    NavMesh.pathfindingIterationsPerFrame = _original;
                    Active = false;
                    _window = -1;
                    ModMain.Log($"Customer movement probe finished; path budget restored to {_original}.");
                    return;
                }
                StartWindow();
            }
        }

        private static void Sample()
        {
            CustomerView[] views = UnityEngine.Object.FindObjectsOfType<CustomerView>();
            if (_window < 0)
            {
                if (views.Length == 0)
                {
                    return; // wait for customers
                }
                _original = NavMesh.pathfindingIterationsPerFrame;
                _window = 0;
                StartWindow();
            }
            int pending = 0, offMesh = 0, still = 0;
            foreach (CustomerView v in views)
            {
                NavMeshAgent a = AgentOf(v);
                if (a == null || !a.enabled)
                {
                    continue;
                }
                pending += a.pathPending ? 1 : 0;
                offMesh += a.isOnNavMesh ? 0 : 1;
                still += a.velocity.sqrMagnitude < 0.01f && a.isOnNavMesh && a.remainingDistance > a.stoppingDistance + 0.3f ? 1 : 0;
            }
            _samples++;
            _customers += views.Length;
            _pending += pending;
            _offMesh += offMesh;
            _still += still;
        }

        private static void StartWindow()
        {
            NavMesh.pathfindingIterationsPerFrame = _window == 1 ? RaisedIterations : UnityDefaultIterations;
            _windowTime = 0f;
            _samples = _frames = _stuckWarps = _snaps = 0;
            _customers = _pending = _offMesh = _still = _frameMs = 0;
            _snapMax = 0f;
        }

        private static void Report()
        {
            int n = Math.Max(_samples, 1);
            ModMain.Log($"Customer movement, window {_window + 1} of 3 (path budget {NavMesh.pathfindingIterationsPerFrame} per frame): "
                        + $"{_customers / n:0} customers on average; {_pending / n:0.0} waiting for a path, {_still / n:0.0} standing still short of their target, "
                        + $"{_offMesh / n:0.0} off the navmesh; {_stuckWarps} stuck warp(s), {_snaps} position snap(s) (largest {_snapMax:0.0} tiles); "
                        + $"frame {_frameMs / Math.Max(_frames, 1):0.0} ms on average.");
        }
    }
}
