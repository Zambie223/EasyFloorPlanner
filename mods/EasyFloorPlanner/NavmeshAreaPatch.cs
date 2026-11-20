using System.Collections.Generic;
using HarmonyLib;
using Kitchen;
using Kitchen.Layouts;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Lifts vanilla's fixed navmesh area so customers can walk on floor beyond it.
    ///
    /// LayoutView.Initialise adds a thin BoxCollider (centre (-10, 0, 0), size (40, 0.01, 20)) on the
    /// "NavMesh Level Geometry" layer. It's the ground the floorplan navmesh is baked from, so it
    /// covers the building and the yard around it. Outside the box there's no navmesh. This prefix
    /// runs right before the view builds its geometry and navmesh (UpdateData, first call), and
    /// grows the box to cover the layout plus a margin. It never shrinks below vanilla's box, so
    /// vanilla layouts are unaffected.
    ///
    /// It runs on every machine that builds the view (host and clients), because the view is built
    /// locally from the blueprint the host sends.
    /// </summary>
    [HarmonyPatch(typeof(LayoutView), "UpdateData", typeof(LayoutView.InitialViewData))] // exact signature: a base-class overload exists
    internal static class NavmeshAreaPatch
    {
        private static readonly Vector3 VanillaCentre = new Vector3(-10f, 0f, 0f);
        private static readonly Vector3 VanillaSize = new Vector3(40f, 0.01f, 20f);

        /// <summary>Walkable yard kept around the building, in tiles. Vanilla leaves at least this much.</summary>
        private const float Margin = 4f;

        /// <summary>How far west of the building customers can spawn (10 + 3 random + 1 spare).</summary>
        public const float SpawnReach = 14f;

        /// <summary>The z vanilla spawns customers around (CreateCustomerGroup: -6 + Random.Range(-1, 1)).</summary>
        public const float CustomerSpawnZ = -6f;

        private static bool _warned;

        private static void Prefix(LayoutView __instance, LayoutView.InitialViewData view_data)
        {
            if (__instance.IsInitialised || view_data.Floorplan?.Tiles == null || view_data.Floorplan.Tiles.Count == 0)
            {
                return;
            }

            BoxCollider box = null;
            foreach (BoxCollider candidate in __instance.GetComponents<BoxCollider>())
            {
                if ((candidate.center - VanillaCentre).sqrMagnitude < 0.001f && (candidate.size - VanillaSize).sqrMagnitude < 0.001f)
                {
                    box = candidate;
                    break;
                }
            }
            if (box == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    ModMain.LogWarning("Navmesh ground box not found on LayoutView (game changed?). Floor beyond vanilla's area may be unwalkable.");
                }
                return;
            }

            var area = new Bounds(VanillaCentre, VanillaSize);
            int minX = int.MaxValue;
            foreach (KeyValuePair<LayoutPosition, Room> tile in view_data.Floorplan.Tiles)
            {
                area.Encapsulate(new Vector3(tile.Key.x - 0.5f - Margin, 0f, tile.Key.y - 0.5f - Margin));
                area.Encapsulate(new Vector3(tile.Key.x + 0.5f + Margin, 0f, tile.Key.y + 0.5f + Margin));
                minX = Mathf.Min(minX, tile.Key.x);
            }
            // Customers spawn at x = Bounds.min.x - 10 +/- 3, z around -6 (CreateCustomerGroup), then
            // walk to the door. Vanilla buildings never get far enough west for that to leave the
            // fixed box; a building grown west can. Keep the whole spawn spread walkable.
            area.Encapsulate(new Vector3(minX - SpawnReach, 0f, CustomerSpawnZ));
            box.center = new Vector3(area.center.x, VanillaCentre.y, area.center.z);
            box.size = new Vector3(area.size.x, VanillaSize.y, area.size.z);
            PathfindingBudget.Fit(area.size.x * area.size.z > VanillaSize.x * VanillaSize.z + 0.5f);
        }
    }

    /// <summary>
    /// Unity computes NavMeshAgent paths a few A* iterations at a time:
    /// NavMesh.pathfindingIterationsPerFrame, default 100, shared by every agent. The game never
    /// changes it. In a restaurant bigger than vanilla's navmesh area with many customers, paths
    /// queue up, customers stand still, and vanilla's CustomerView.CheckForStuck re-requests the
    /// path and, after 10 s, warps them to their target (what players see as teleporting).
    ///
    /// Measured 2026-11-19 in a 972-tile restaurant, 30 s windows of a day (CustomerMovementProbe):
    /// with 100, 141 customers, 39.6 waiting for a path on average, 86 stuck warps, 44.1 ms
    /// frames; with 1000, 104 customers, 0 waiting, 0 warps, 42.1 ms frames. The budget is only
    /// spent while paths are pending, so it costs nothing without a backlog.
    ///
    /// So while the restaurant's navmesh is bigger than vanilla's it's raised to
    /// <see cref="BigRestaurant"/> (never lowered if something else set it higher), and put back
    /// when the restaurant is vanilla-sized again (unless something else changed it since).
    /// Set wherever the restaurant is built (host and clients), since customers walk on each
    /// machine's own navmesh.
    /// </summary>
    internal static class PathfindingBudget
    {
        public const int BigRestaurant = 1000;

        private static int _original = -1;
        private static bool _ours;

        public static void Fit(bool big)
        {
            int now = NavMesh.pathfindingIterationsPerFrame;
            if (big && !_ours && now < BigRestaurant)
            {
                _original = now;
                _ours = true;
                NavMesh.pathfindingIterationsPerFrame = BigRestaurant;
                ModMain.Log($"Big restaurant: customer path budget raised from {now} to {BigRestaurant} per frame, so customers don't wait for paths (and get warped).");
            }
            else if (!big && _ours)
            {
                _ours = false;
                if (now == BigRestaurant && _original > 0)
                {
                    NavMesh.pathfindingIterationsPerFrame = _original;
                    ModMain.Log($"Restaurant back within vanilla's area: customer path budget back to {_original} per frame.");
                }
            }
        }
    }

    /// <summary>
    /// Logs how long each navmesh rebuild takes (it runs once per layout change or load and grows with
    /// the restaurant's area), so the pause on Apply is measured rather than guessed.
    /// </summary>
    [HarmonyPatch(typeof(LayoutView), nameof(LayoutView.UpdateNavmesh))]
    internal static class NavmeshRebuildTiming
    {
        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

        /// <summary>The last rebuild's duration in milliseconds (the self-test reads it).</summary>
        public static double LastMs { get; private set; }

        private static void Prefix() => Watch.Restart();

        private static void Postfix(LayoutView __instance)
        {
            Watch.Stop();
            LastMs = Watch.Elapsed.TotalMilliseconds;
            Vector3 size = Vector3.zero;
            foreach (BoxCollider box in __instance.GetComponents<BoxCollider>())
            {
                size = Vector3.Max(size, box.size);
            }
            ModMain.Log($"Navmesh rebuilt in {LastMs:0} ms (walkable area {size.x:0} x {size.z:0}).");
        }
    }
}
