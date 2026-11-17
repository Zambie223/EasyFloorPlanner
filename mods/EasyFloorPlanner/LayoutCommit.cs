using Kitchen;
using Kitchen.Layouts;
using Unity.Entities;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The single entry point for applying a layout edit. It:
    ///   1. removes outdoor scenery on or beside any new floor (SceneryBulldozer),
    ///   2. writes the tile/feature buffers, bounds and camera markers (LayoutWriter.Write),
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
        }

        /// <summary>The last commit's result, for logging and the self-test.</summary>
        public static Result Last { get; private set; }

        public static Result Commit(EntityManager em, TileManager tiles, Entity layout, LayoutBlueprint after)
        {
            LayoutBlueprint before = LayoutWriter.Read(em, layout);
            var result = new Result();
            (result.SceneryMoved, result.SceneryRemoved) = SceneryBulldozer.ClearNewFloor(em, tiles, before, after);
            LayoutWriter.Write(em, layout, after);
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
    }
}
