using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using Unity.Entities;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The single entry point for applying a layout edit. It:
    ///   1. removes outdoor scenery on or beside any new floor (SceneryBulldozer), and from in front of
    ///      any new door to the outside,
    ///   2. writes the tile/feature buffers, bounds and camera markers (LayoutWriter.Write; this also
    ///      opens or closes the yard ring and refits the movement blockers and front decorations),
    ///      then makes the game follow a moved front door (FrontDoorMove),
    ///   3. refreshes TileManager's tile cache, so same-frame checks see the new layout,
    ///   4. rebuilds the floorplan view on every client (LayoutWriter.RebuildViews),
    ///   5. fixes up appliances that follow walls: turns wall-mounted ones whose wall went, breaks
    ///      up table groups and chairs that a new wall split (TileOccupants).
    /// </summary>
    internal static class LayoutCommit
    {
        public struct Result
        {
            public int RebuiltViews;
            public int SceneryMoved;
            public int SceneryRemoved;
            public int WallMountedTurned;
            public int TableGroupsReformed;
            public int ChairsCleared;
            public bool FrontDoorMoved;
            public int DoorwaySceneryRemoved;
        }

        /// <summary>The last commit's result, for logging and the self-test.</summary>
        public static Result Last { get; private set; }

        public static Result Commit(EntityManager em, TileManager tiles, Entity layout, LayoutBlueprint after)
        {
            LayoutBlueprint before = LayoutWriter.Read(em, layout);
            var result = new Result();
            (result.SceneryMoved, result.SceneryRemoved) = SceneryBulldozer.ClearNewFloor(em, tiles, before, after);
            result.DoorwaySceneryRemoved = ClearNewDoorways(em, before, after);
            LayoutWriter.Write(em, layout, after);
            result.FrontDoorMoved = FrontDoorMove.Apply(em, layout, before, after);
            tiles.InvalidateTileCache();
            result.RebuiltViews = LayoutWriter.RebuildViews(em);
            result.WallMountedTurned = TileOccupants.WallMountedFixup(em, after);
            // Only at night: the game re-forms groups and chairs in its night table update, and
            // breaking up a group by day could strand seated customers. (Edits are night-only.)
            EntityQuery night = em.CreateEntityQuery(ComponentType.ReadOnly<SIsNightTime>());
            if (!night.IsEmptyIgnoreFilter)
            {
                (result.TableGroupsReformed, result.ChairsCleared) = TileOccupants.TableFixup(em, before, after);
            }
            night.Dispose();
            Last = result;
            return result;
        }

        /// <summary>
        /// Removes outdoor scenery (trees, rocks...) from the tile just outside each door to the outside
        /// that's new in this edit (a side or back door, or the front door's new spot), so nothing
        /// blocks the doorway. Protected pieces (movement blockers, nameplate, managed front
        /// decorations) are left to their own refits.
        /// </summary>
        private static int ClearNewDoorways(EntityManager em, LayoutBlueprint before, LayoutBlueprint after)
        {
            HashSet<LayoutPosition> Outsides(LayoutBlueprint bp) => new HashSet<LayoutPosition>(bp.Features
                .Where(f => f.Type.IsDoor())
                .Select(f => LayoutEdits.OuterEdge(bp, f.Tile1, f.Tile2))
                .Where(e => e.HasValue)
                .Select(e => e.Value.outside));
            HashSet<LayoutPosition> doorways = Outsides(after);
            doorways.ExceptWith(Outsides(before));
            if (doorways.Count == 0)
            {
                return 0;
            }
            int removed = 0;
            foreach (Entity e in SceneryBulldozer.Conflicting(em, before, doorways))
            {
                em.DestroyEntity(e);
                removed++;
            }
            if (removed > 0)
            {
                ModMain.Log($"Removed {removed} piece(s) of outdoor scenery from in front of new doors.");
            }
            return removed;
        }
    }
}
