#if DEBUG
using Kitchen;
using Kitchen.Layouts;
using KitchenMods;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyFloorPlanner
{
    /// <summary>
    /// TEMPORARY test harness (Debug builds only). It proves that layout edits apply, rebuild on every client and
    /// survive a save. It will be replaced by the grid edit mode.
    ///
    /// Host keyboard only, night only. Acts on the first player found:
    ///   F7  paint the tile in front of the player (outdoor tile joins your room / indoor tile becomes outdoors)
    ///   F8  cycle the wall in front of the player: wall, hatch, door, wall
    ///   F6  log the layout grid to Player.log
    /// </summary>
    public class DebugEditHotkeys : GenericSystemBase, IModSystem
    {
        private EntityQuery _players;

        protected override void Initialise()
        {
            base.Initialise();
            _players = GetEntityQuery(typeof(CPlayer), typeof(CPosition));
        }

        protected override void OnUpdate()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }
            bool paint = kb.f7Key.wasPressedThisFrame;
            bool edge = kb.f8Key.wasPressedThisFrame;
            bool dump = kb.f6Key.wasPressedThisFrame;
            if (!paint && !edge && !dump)
            {
                return;
            }
            if (!HasSingleton<SLayout>())
            {
                return;
            }
            Entity layout = GetSingletonEntity<SLayout>();

            if (dump)
            {
                DumpLayout(LayoutWriter.Read(EntityManager, layout));
                return;
            }
            if (!Has<SKitchenMarker>())
            {
                return; // Only the restaurant, never the HQ or other scenes.
            }
            if (!Has<SIsNightTime>())
            {
                ModMain.Log("Layout edits are only allowed at night.");
                return;
            }
            if (!LayoutWriter.IsAvailable)
            {
                return;
            }

            NativeArray<CPosition> positions = _players.ToComponentDataArray<CPosition>(Allocator.Temp);
            if (positions.Length == 0)
            {
                positions.Dispose();
                return;
            }
            CPosition player = positions[0];
            positions.Dispose();

            Vector3 here = player.Position.Rounded();
            Vector3 facing = SnapToCardinal(player.Forward(1f));
            LayoutPosition source = here;
            LayoutPosition target = here + facing;

            LayoutBlueprint bp = LayoutWriter.Read(EntityManager, layout);
            string refused = paint
                ? LayoutEdits.TogglePaint(bp, source, target, OccupancySnapshot.Take(EntityManager, TileManager).BlocksEdit)
                : LayoutEdits.CycleEdge(bp, source, target, out _);

            string what = paint ? "paint" : "edge";
            if (refused != null)
            {
                ModMain.Log($"{what} {LayoutEdits.Fmt(source)} -> {LayoutEdits.Fmt(target)} refused: {refused}");
                return;
            }

            LayoutCommit.Result result = LayoutCommit.Commit(EntityManager, TileManager, layout, bp);
            ModMain.Log($"{what} {LayoutEdits.Fmt(source)} -> {LayoutEdits.Fmt(target)} applied; rebuilt {result.RebuiltViews} floorplan view(s), scenery moved {result.SceneryMoved} / removed {result.SceneryRemoved}.");
        }

        private static Vector3 SnapToCardinal(Vector3 v)
        {
            return Mathf.Abs(v.x) >= Mathf.Abs(v.z)
                ? new Vector3(Mathf.Sign(v.x), 0f, 0f)
                : new Vector3(0f, 0f, Mathf.Sign(v.z));
        }

        /// <summary>
        /// Prints the grid, top row first. Letters are room types, so we can learn how much
        /// outdoor padding vanilla layouts store. That decides how resizing beyond the grid works.
        /// </summary>
        private static void DumpLayout(LayoutBlueprint bp)
        {
            Bounds b = bp.GetBounds();
            int minX = Mathf.RoundToInt(b.min.x), maxX = Mathf.RoundToInt(b.max.x);
            int minY = Mathf.RoundToInt(b.min.y), maxY = Mathf.RoundToInt(b.max.y);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Layout grid x[{minX}..{maxX}] y[{minY}..{maxY}], {bp.Tiles.Count} tiles, {bp.Features.Count} features. Legend: . outdoor, K kitchen, D dining, G garden, ? other, (blank) not stored");
            for (int y = maxY; y >= minY; y--)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!bp.Tiles.TryGetValue(new LayoutPosition(x, y), out Room r))
                    {
                        sb.Append(' ');
                        continue;
                    }
                    sb.Append(r.Type switch
                    {
                        RoomType.NoRoom => '.',
                        RoomType.Kitchen => 'K',
                        RoomType.Dining => 'D',
                        RoomType.Garden => 'G',
                        _ => '?'
                    });
                }
                sb.AppendLine();
            }
            foreach (var f in bp.Features)
            {
                sb.AppendLine($"  {f.Type} {LayoutEdits.Fmt(f.Tile1)}-{LayoutEdits.Fmt(f.Tile2)}");
            }
            ModMain.Log(sb.ToString());
        }
    }
}
#endif
