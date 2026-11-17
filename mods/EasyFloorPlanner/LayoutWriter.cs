using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Reads and writes the restaurant layout stored on the SLayout entity, and forces the
    /// floorplan geometry to rebuild.
    ///
    /// Why this exists:
    /// - Walls aren't stored. A wall is drawn wherever neighbouring tiles have different room IDs
    ///   (LayoutBlueprint.GetWalls). Hatches and doors are CLayoutFeature entries on a tile pair.
    /// - Vanilla LayoutExtensions.ToEntity only works on a fresh entity: it appends to the buffers
    ///   and calls AddComponentData(CBounds), which throws if CBounds already exists. So we
    ///   rewrite the buffers ourselves.
    /// - LayoutView builds its geometry once (IsInitialised). Destroying the CLayoutView entity
    ///   makes CleanViews send DestroyView to every client, and the copy gets a new view on every
    ///   client. That means no custom networking.
    /// - Vanilla stores only the building's own tiles (no outdoor padding), so growing adds tiles
    ///   and changes CBounds. That in turn means the camera bound markers have to be moved.
    /// </summary>
    internal static class LayoutWriter
    {
        private static readonly Func<LayoutBlueprint, int, int, Reachability> GetReachability = CreateReachabilityDelegate();

        private static Func<LayoutBlueprint, int, int, Reachability> CreateReachabilityDelegate()
        {
            // Private in vanilla. Calling the game's own routine keeps us in step with game updates.
            MethodInfo method = typeof(LayoutExtensions).GetMethod("GetReachability",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(LayoutBlueprint), typeof(int), typeof(int) }, null);
            if (method == null)
            {
                ModMain.LogError("LayoutExtensions.GetReachability not found; the game has changed. Layout edits are disabled.");
                return null;
            }
            return (Func<LayoutBlueprint, int, int, Reachability>)Delegate.CreateDelegate(
                typeof(Func<LayoutBlueprint, int, int, Reachability>), method);
        }

        public static bool IsAvailable => GetReachability != null;

        public static LayoutBlueprint Read(EntityManager em, Entity layout)
        {
            var bp = new LayoutBlueprint();
            bp.FromEntity(em, layout);
            return bp;
        }

        public static void Write(EntityManager em, Entity layout, LayoutBlueprint bp)
        {
            // Compute everything before touching the buffers. No structural changes happen between
            // GetBuffer and the writes, so the buffer handles stay valid.
            var tiles = new List<CLayoutRoomTile>(bp.Tiles.Count);
            foreach (KeyValuePair<LayoutPosition, Room> tile in bp.Tiles)
            {
                tiles.Add(new CLayoutRoomTile
                {
                    Position = tile.Key,
                    Type = tile.Value.Type,
                    RoomID = tile.Value.ID,
                    HasFeature = bp.HasFeature(tile.Key),
                    Reachability = GetReachability(bp, tile.Key.x, tile.Key.y)
                });
            }

            DynamicBuffer<CLayoutRoomTile> tileBuffer = em.GetBuffer<CLayoutRoomTile>(layout);
            tileBuffer.Clear();
            foreach (CLayoutRoomTile t in tiles)
            {
                tileBuffer.Add(t);
            }

            DynamicBuffer<CLayoutFeature> featureBuffer = em.GetBuffer<CLayoutFeature>(layout);
            featureBuffer.Clear();
            foreach (Feature f in bp.Features)
            {
                featureBuffer.Add(new CLayoutFeature { Tile1 = f.Tile1, Tile2 = f.Tile2, Type = f.Type });
            }

            // CBounds is read live by player bounds, letter/parcel spawns and item returns. The
            // camera bound markers are created once from it, so they're moved by hand.
            if (em.HasComponent<CBounds>(layout))
            {
                Bounds before = em.GetComponentData<CBounds>(layout).Bounds;
                Bounds after = bp.GetWorldBounds();
                em.SetComponentData(layout, new CBounds { Bounds = after });
                if (before != after)
                {
                    SyncBoundsDependents(em, before, after);
                }
                // Every write, not only when the bounds change: this also repairs restaurants grown
                // before the street sync existed.
                StreetPieces.Sync(em, bp);
            }
        }

        /// <summary>Result of the last bounds sync, for logging and the self-test.</summary>
        public static (int movedDayPoints, int removedNightPoints) LastBoundsSync { get; private set; }

        /// <summary>Whether the last bounds sync kept a custom camera area (instead of moving the day markers).</summary>
        public static bool LastAreaKept { get; private set; }

        private static readonly Type NightBoundType = typeof(ExpandBoundsAtNight).GetNestedType("CNightBound", BindingFlags.NonPublic);

        /// <summary>
        /// Where vanilla's CreateNewKitchen.AddBoundPoint puts the four day-camera markers for these
        /// bounds, in this order. CameraAreaMarkers finds the markers themselves (by kind).
        /// </summary>
        public static Vector3[] DayBoundPoints(Bounds b)
        {
            b.Expand(1f);
            return new[]
            {
                b.center + new Vector3(b.extents.x + 1f, 0f, b.extents.z + 0.5f),
                b.center + new Vector3(b.extents.x + 1f, 0f, -b.extents.z - 1f),
                b.center + new Vector3(-b.extents.x - 1f, 0f, b.extents.z + 0.5f),
                b.center + new Vector3(-b.extents.x - 1f, 0f, -b.extents.z - 1f),
            };
        }

        /// <summary>The four night-camera markers vanilla's ExpandBoundsAtNight creates from CBounds.</summary>
        public static Vector3[] NightBoundPoints(Bounds b)
        {
            b.Expand(3f);
            return new[]
            {
                new Vector3(b.min.x - 1f, 0f, b.min.z - 2f),
                new Vector3(b.min.x - 1f, 0f, b.max.z),
                new Vector3(b.max.x + 1f, 0f, b.min.z - 2f),
                new Vector3(b.max.x + 1f, 0f, b.max.z),
            };
        }

        public static EntityQuery NightBoundQuery(EntityManager em) =>
            NightBoundType == null ? default : em.CreateEntityQuery(ComponentType.ReadOnly(NightBoundType), ComponentType.ReadOnly<CPosition>());

        private static void SyncBoundsDependents(EntityManager em, Bounds before, Bounds after)
        {
            // Day camera: a whole-restaurant framing follows the new bounds; a custom camera area
            // (chosen in the editor) stays where it is.
            int moved = 0;
            FloorPlanCameraArea area = CameraAreaMarkers.Read(em, before);
            if (area.Custom)
            {
                LastAreaKept = true;
                ModMain.Log($"Kept the custom camera area {area} after the resize.");
            }
            else
            {
                LastAreaKept = false;
                moved = CameraAreaMarkers.Write(em, after, FloorPlanCameraArea.Whole);
            }

            // Night camera: ExpandBoundsAtNight recreates its markers from CBounds when none exist.
            int removed = 0;
            if (NightBoundType == null)
            {
                ModMain.LogWarning("ExpandBoundsAtNight.CNightBound not found; the night camera keeps its old framing.");
            }
            else
            {
                EntityQuery night = NightBoundQuery(em);
                removed = night.CalculateEntityCount();
                em.DestroyEntity(night);
                night.Dispose();
            }

            LastBoundsSync = (moved, removed);
            if (moved != 4 && !area.Custom)
            {
                ModMain.LogWarning($"Moved {moved} of 4 day-camera bound markers; the camera may not frame the new size exactly.");
            }

            LastBlockerSync = SyncMovementBlockers(em, before, after);
        }

        /// <summary>Result of the last movement-blocker sync (removed, added), for logging and the self-test.</summary>
        public static (int removed, int added) LastBlockerSync { get; private set; }

        /// <summary>
        /// Where the restaurant setting's decorator puts its invisible OutdoorMovementBlockers for
        /// these bounds. Countryside, Farm, Halloween, Street and Witch all use the same pattern: a
        /// row 3 tiles in front of the building, one tile past each side, plus two at each front
        /// corner. Together with vanilla's EnforcePlayerBounds they keep players near the restaurant.
        /// </summary>
        public static HashSet<LayoutPosition> MovementBlockerPoints(Bounds b)
        {
            int minX = Mathf.RoundToInt(b.min.x), maxX = Mathf.RoundToInt(b.max.x), minZ = Mathf.RoundToInt(b.min.z);
            var points = new HashSet<LayoutPosition>();
            for (int x = minX - 1; x <= maxX + 1; x++)
            {
                points.Add(new LayoutPosition(x, minZ - 3));
            }
            points.Add(new LayoutPosition(minX - 1, minZ - 1));
            points.Add(new LayoutPosition(minX - 1, minZ - 2));
            points.Add(new LayoutPosition(maxX + 1, minZ - 1));
            points.Add(new LayoutPosition(maxX + 1, minZ - 2));
            return points;
        }

        /// <summary>
        /// The decorator places movement blockers once, from the original bounds. Left alone, they
        /// cut across the front of a restaurant grown sideways, and after shrinking they no longer
        /// stop players walking past the side (where EnforcePlayerBounds then teleports them to the
        /// door). Moves them to the pattern for the new bounds: removes the ones on old-pattern
        /// spots that the new pattern doesn't use, and creates the missing ones. Blockers that
        /// aren't on the old pattern (another mod's) are left alone, and settings without blockers
        /// get none.
        /// </summary>
        private static (int removed, int added) SyncMovementBlockers(EntityManager em, Bounds before, Bounds after)
        {
            int blockerId = KitchenData.AssetReference.OutdoorMovementBlocker;
            var existing = new Dictionary<LayoutPosition, List<Entity>>();
            EntityQuery q = em.CreateEntityQuery(ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>());
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    if (em.GetComponentData<CAppliance>(e).ID != blockerId)
                    {
                        continue;
                    }
                    LayoutPosition p = em.GetComponentData<CPosition>(e).Position.Rounded();
                    if (!existing.TryGetValue(p, out List<Entity> list))
                    {
                        existing[p] = list = new List<Entity>();
                    }
                    list.Add(e);
                }
            }
            q.Dispose();

            HashSet<LayoutPosition> oldPattern = MovementBlockerPoints(before);
            if (!existing.Keys.Any(oldPattern.Contains))
            {
                return (0, 0); // this setting has no blockers (or they're not where vanilla puts them)
            }
            HashSet<LayoutPosition> newPattern = MovementBlockerPoints(after);

            int removed = 0, added = 0;
            foreach (KeyValuePair<LayoutPosition, List<Entity>> kv in existing)
            {
                if (oldPattern.Contains(kv.Key) && !newPattern.Contains(kv.Key))
                {
                    foreach (Entity e in kv.Value)
                    {
                        em.DestroyEntity(e);
                        removed++;
                    }
                }
            }
            foreach (LayoutPosition p in newPattern)
            {
                if (!existing.ContainsKey(p))
                {
                    Entity e = em.CreateEntity();
                    em.AddComponentData(e, new CCreateAppliance { ID = blockerId });
                    em.AddComponentData(e, new CPosition(new Vector3(p.x, 0f, p.y)));
                    added++;
                }
            }
            if (removed + added > 0)
            {
                ModMain.Log($"Moved the outdoor movement blockers to the new size: removed {removed}, added {added}.");
            }
            return (removed, added);
        }

        /// <summary>
        /// Replaces every floorplan view entity with a copy, so each client builds fresh geometry
        /// (walls, floors, colliders, navmesh) from the new layout.
        /// </summary>
        public static int RebuildViews(EntityManager em)
        {
            EntityQuery query = em.CreateEntityQuery(ComponentType.ReadOnly<CLayoutView>(), ComponentType.ReadOnly<CRequiresView>());
            NativeArray<Entity> views = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity old in views)
                {
                    // Instantiate keeps any components other systems added to the view entity.
                    Entity copy = em.Instantiate(old);
                    // CLinkedView is system-state and shouldn't be copied, but make sure: a copied
                    // link would point the new entity at the view that's about to be destroyed.
                    if (em.HasComponent<CLinkedView>(copy))
                    {
                        em.RemoveComponent<CLinkedView>(copy);
                    }
                    em.DestroyEntity(old);
                }
                return views.Length;
            }
            finally
            {
                views.Dispose();
                query.Dispose();
            }
        }
    }
}
