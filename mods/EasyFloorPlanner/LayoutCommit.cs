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
    ///   4. rebuilds the floorplan view on every client (LayoutWriter.RebuildViews).
    /// </summary>
    internal static class LayoutCommit
    {
        public struct Result
        {
            public int RebuiltViews;
            public int SceneryMoved;
            public int SceneryRemoved;
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
            Last = result;
            return result;
        }
    }
}
