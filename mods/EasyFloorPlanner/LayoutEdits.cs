using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Pure edit rules on a LayoutBlueprint. Each method either changes the blueprint and returns
    /// null, or leaves it untouched and returns the reason it refused.
    /// </summary>
    internal static class LayoutEdits
    {
        // Maximum restaurant extent, in tiles from the origin. Vanilla's fixed navmesh area is lifted
        // by NavmeshAreaPatch and TileManager has no fixed grid, so this is a sanity limit. The
        // navmesh is rebuilt once per Apply (logged by NavmeshRebuildTiming), and its cost grows with
        // area. Raised from 30 to 63 on 2026-11-19 (user's choice): 127 x 127 tiles, which keeps the
        // editor's network tile cap (FloorPlanSerializer.MaxTiles, derived from this) bounded.
        public const int MaxExtent = 63;

        /// <summary>
        /// Paints <paramref name="target"/>. An outdoor tile (not stored, or stored as NoRoom) joins
        /// the room of <paramref name="source"/> (grow). An indoor tile is removed from the layout
        /// (shrink). Vanilla stores only the building's own tiles, so removing the tile is the
        /// vanilla-shaped way to make it outdoors.
        /// </summary>
        public static string TogglePaint(LayoutBlueprint bp, LayoutPosition source, LayoutPosition target, Func<LayoutPosition, bool> isOccupied)
        {
            // Checked before the occupancy test, so a door tile reports the door, not what's on it.
            // GetFeatures also finds features whose outer tile isn't stored, like the front door's step.
            if (bp.GetFeatures(target).Any(f => f.Type.IsDoor()))
            {
                return "tile has a door on it";
            }
            if (isOccupied(target))
            {
                return "an appliance or scenery object is on that tile";
            }

            bool stored = bp.Tiles.TryGetValue(target, out Room targetRoom);
            if (!stored || targetRoom.Type == RoomType.NoRoom)
            {
                return Grow(bp, source, target);
            }

            if (!LayoutHelpers.IsInside(targetRoom))
            {
                return $"{targetRoom.Type} tiles can't be edited yet";
            }
            if (bp.Tiles.Count(t => t.Value.ID == targetRoom.ID) <= 1)
            {
                return "that would remove the room entirely";
            }

            List<Feature> removedFeatures = bp.GetFeatures(target);
            bp.Tiles.Remove(target);
            foreach (Feature f in removedFeatures)
            {
                bp.Features.Remove(f);
            }

            string unreachable = CheckConnectivity(bp);
            if (unreachable != null)
            {
                bp[target] = targetRoom;
                bp.Features.AddRange(removedFeatures);
                return unreachable;
            }
            return null;
        }

        private static string Grow(LayoutBlueprint bp, LayoutPosition source, LayoutPosition target)
        {
            if (Math.Abs(source.x - target.x) + Math.Abs(source.y - target.y) != 1)
            {
                return "can only grow onto a tile right next to the room";
            }
            Room sourceRoom = bp[source];
            if (!LayoutHelpers.IsInside(sourceRoom) || sourceRoom.Type == RoomType.Garden)
            {
                return "stand inside the room you want to extend";
            }
            if (Math.Abs(target.x) > MaxExtent || Math.Abs(target.y) > MaxExtent)
            {
                return "that's past the maximum restaurant size";
            }
            int frontRow = FrontRow(bp);
            if (target.y < frontRow)
            {
                // Customers queue and walk along the street side, and front decorations sit there.
                return "the restaurant can't grow toward the street";
            }
            bp[target] = sourceRoom;
            return null;
        }

        /// <summary>The y of the front door's indoor tile, or int.MinValue if there's no front door.</summary>
        private static int FrontRow(LayoutBlueprint bp)
        {
            Feature front = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (front == null)
            {
                return int.MinValue;
            }
            return LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1.y : front.Tile2.y;
        }

        /// <summary>
        /// Cycles the wall between two neighbouring indoor tiles of different rooms:
        /// wall, then hatch, then door, then back to wall. Only plain doors and hatches are cycled.
        /// Vanilla's special doors (front door, garage, greenhouse and so on) are protected.
        /// Removing a door is refused if it would cut any tile off from the front door.
        /// <paramref name="now"/> is the edge's new state (null for a plain wall).
        /// </summary>
        public static string CycleEdge(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, out FeatureType? now)
        {
            now = null;
            if (Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) != 1)
            {
                return "those tiles don't share a wall";
            }
            if (!bp.Tiles.ContainsKey(a) || !bp.Tiles.ContainsKey(b))
            {
                return "that wall is on the outside of the building";
            }
            if (!LayoutHelpers.IsInside(bp[a]) || !LayoutHelpers.IsInside(bp[b]))
            {
                return "hatches and doors only go between two indoor rooms";
            }
            if (bp[a].ID == bp[b].ID)
            {
                return "there's no wall here (both tiles are the same room)";
            }

            Feature existing = bp.Features.FirstOrDefault(f => (f.Tile1 == a && f.Tile2 == b) || (f.Tile1 == b && f.Tile2 == a));
            if (existing == null)
            {
                bp.Features.Add(new Feature(a, b, FeatureType.Hatch));
                now = FeatureType.Hatch;
                return null;
            }
            if (existing.Type == FeatureType.Hatch)
            {
                existing.Type = FeatureType.Door;
                now = FeatureType.Door;
                return null;
            }
            if (existing.Type == FeatureType.Door)
            {
                bp.Features.Remove(existing);
                string unreachable = CheckConnectivity(bp);
                if (unreachable != null)
                {
                    bp.Features.Add(existing);
                    now = FeatureType.Door;
                    return $"this door is needed: {unreachable}";
                }
                return null;
            }
            now = existing.Type;
            return $"the {existing.Type} can't be edited";
        }

        // ------------------------------------------------------------------ edit mode (drafts)

        /// <summary>
        /// Edit-mode wall painting: sets the edge between two neighbouring indoor tiles of different
        /// rooms to a plain wall (<paramref name="type"/> null), a hatch, or a door. Special doors
        /// are protected. Unlike CycleEdge, removing a door is allowed even if it cuts floor off:
        /// this is a draft, which shows cut-off floor and refuses Apply until it's fixed.
        /// Returns null on success or when nothing changes.
        /// </summary>
        public static string SetEdge(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, FeatureType? type, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            // A hatch or door isn't a wall a wall-mounted appliance can hang on.
            bool wallCheck = new[] { a, b }.Any(p => occupants.TryGetValue(p, out OccupantFlags f) && (f & OccupantFlags.WallMounted) != 0);
            LayoutBlueprint before = wallCheck ? new LayoutBlueprint(bp) : null;
            string refused = SetEdgeUnchecked(bp, a, b, type, occupants);
            if (refused == null && wallCheck && TileOccupants.LostWall(before, bp, new[] { a, b }, occupants).HasValue)
            {
                Restore(bp, before);
                return "that would take away the only wall a wall-mounted appliance hangs on";
            }
            return refused;
        }

        private static string SetEdgeUnchecked(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, FeatureType? type,
                                               IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            if (type == FeatureType.FrontDoor)
            {
                return MoveFrontDoor(bp, a, b, occupants);
            }
            if (type.HasValue && !IsPlain(type.Value))
            {
                return "only walls, hatches and doors can be placed";
            }
            (LayoutPosition inside, LayoutPosition outside)? outer = OuterEdge(bp, a, b);
            if (outer.HasValue)
            {
                return SetOuterEdge(bp, outer.Value.inside, outer.Value.outside, type);
            }
            string notAWall = EdgeProblem(bp, a, b);
            if (notAWall != null)
            {
                return notAWall;
            }
            Feature existing = bp.Features.FirstOrDefault(f => (f.Tile1 == a && f.Tile2 == b) || (f.Tile1 == b && f.Tile2 == a));
            if (existing != null && !IsPlain(existing.Type))
            {
                return $"the {existing.Type} can't be changed";
            }
            if (type == null)
            {
                if (existing != null)
                {
                    bp.Features.Remove(existing);
                }
            }
            else if (existing != null)
            {
                existing.Type = type.Value;
            }
            else
            {
                bp.Features.Add(new Feature(a, b, type.Value));
            }
            return null;
        }

        /// <summary>
        /// What the edge between two tiles currently is: null for a plain wall, or its feature type.
        /// Returns a refusal (and no type) if it isn't an editable wall between two rooms.
        /// </summary>
        public static string ReadEdge(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, out FeatureType? type)
        {
            type = null;
            (LayoutPosition inside, LayoutPosition outside)? outer = OuterEdge(bp, a, b);
            if (outer.HasValue)
            {
                type = FeatureAt(bp, outer.Value.inside, outer.Value.outside)?.Type;
                return type.HasValue && !IsPlain(type.Value) && type != FeatureType.FrontDoor ? $"that's the {type}, which can't be changed" : null;
            }
            string notAWall = EdgeProblem(bp, a, b);
            if (notAWall != null)
            {
                return notAWall;
            }
            Feature existing = bp.Features.FirstOrDefault(f => (f.Tile1 == a && f.Tile2 == b) || (f.Tile1 == b && f.Tile2 == a));
            if (existing != null)
            {
                type = existing.Type;
                if (!IsPlain(existing.Type))
                {
                    return $"that's the {existing.Type}, which can't be changed";
                }
            }
            return null;
        }

        private static string EdgeProblem(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b)
        {
            if (Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) != 1)
            {
                return "those tiles don't share a wall";
            }
            if (!bp.Tiles.TryGetValue(a, out Room ra) || !bp.Tiles.TryGetValue(b, out Room rb)
                || !LayoutHelpers.IsInside(ra) || !LayoutHelpers.IsInside(rb))
            {
                return "there's no wall here";
            }
            if (ra.ID == rb.ID)
            {
                return "there's no wall here (both tiles are the same room)";
            }
            return null;
        }

        /// <summary>
        /// Edit-mode painting: sets <paramref name="target"/> to <paramref name="brush"/>. A brush
        /// with RoomType.NoRoom erases the tile to outdoors. Only local rules are checked here.
        /// Reachability is checked for the whole draft in ValidateDraft, because a new room's first
        /// tile can't have a door yet. Returns null on success or when nothing changes.
        /// </summary>
        public static string PaintTile(LayoutBlueprint bp, LayoutPosition target, Room brush, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            // A wall-mounted appliance on the tile or beside it can lose its wall: keep what's
            // needed to undo the paint (only then, so box painting stays cheap).
            bool wallCheck = TileOccupants.WithNeighbours(target).Any(p => occupants.TryGetValue(p, out OccupantFlags f) && (f & OccupantFlags.WallMounted) != 0);
            LayoutBlueprint before = wallCheck ? new LayoutBlueprint(bp) : null;
            string refused = PaintTileUnchecked(bp, target, brush, occupants);
            if (refused == null && wallCheck)
            {
                LayoutPosition? lost = TileOccupants.LostWall(before, bp, TileOccupants.WithNeighbours(target), occupants);
                if (lost.HasValue)
                {
                    Restore(bp, before);
                    return "that would take away the only wall a wall-mounted appliance hangs on";
                }
            }
            return refused;
        }

        private static void Restore(LayoutBlueprint bp, LayoutBlueprint from)
        {
            bp.Tiles.Clear();
            foreach (KeyValuePair<LayoutPosition, Room> t in from.Tiles)
            {
                bp.Tiles[t.Key] = t.Value;
            }
            bp.Features.Clear();
            bp.Features.AddRange(from.Features);
        }

        private static string PaintTileUnchecked(LayoutBlueprint bp, LayoutPosition target, Room brush, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            bool stored = bp.Tiles.TryGetValue(target, out Room current);
            bool indoor = stored && LayoutHelpers.IsInside(current);
            if (stored && current.Type == RoomType.Garden)
            {
                return "garden tiles can't be edited";
            }
            bool erase = brush.Type == RoomType.NoRoom;
            if ((erase && !indoor) || (!erase && indoor && current.ID == brush.ID))
            {
                return null; // nothing to change
            }
            // Only special doors (front door, garage...) pin a tile. A plain door or hatch on the
            // tile is cleared below if it no longer sits between two rooms. Refusing those too
            // deadlocked new rooms: their door couldn't go (it's the only way in) and their last
            // tile couldn't go (it has a door). User report, 2026-11-13.
            if (bp.GetFeatures(target).Any(f => !IsPlain(f.Type)))
            {
                return "that tile has a special door (the front door can be moved with the Front door brush in wall mode)";
            }
            occupants.TryGetValue(target, out OccupantFlags here);
            string occupied = TileOccupants.Refusal(indoor, !erase, here);
            if (occupied != null)
            {
                return occupied;
            }

            if (erase)
            {
                bp.Tiles.Remove(target);
                bp.Features.RemoveAll(f => f.Tile1 == target || f.Tile2 == target);
                return null;
            }

            if (!indoor)
            {
                if (Math.Abs(target.x) > MaxExtent || Math.Abs(target.y) > MaxExtent)
                {
                    return "that's past the maximum restaurant size";
                }
                if (target.y < FrontRow(bp))
                {
                    return "the restaurant can't grow toward the street";
                }
            }
            bool brushHasTiles = bp.Tiles.Values.Any(r => r.ID == brush.ID);
            bool adjacent = LayoutHelpers.Directions.Any(d =>
                bp.Tiles.TryGetValue(target + d, out Room n) && (brushHasTiles ? n.ID == brush.ID : LayoutHelpers.IsInside(n)));
            if (!adjacent)
            {
                return brushHasTiles ? "paint next to the room you're extending" : "start a new room next to the building";
            }

            bp[target] = brush;
            // Plain hatches and doors only belong between two different indoor rooms, or (doors)
            // on a side or back outer wall. Growing onto a door's outside tile can make it neither.
            int row = FrontRow(bp);
            bp.Features.RemoveAll(f => (f.Tile1 == target || f.Tile2 == target) && IsPlain(f.Type) && !IsLegalPlain(bp, f, row));
            return null;
        }

        /// <summary>A new room of the given type, with an ID no tile in <paramref name="bp"/> uses.</summary>
        public static Room NewRoom(LayoutBlueprint bp, RoomType type)
        {
            var used = new HashSet<int>(bp.Tiles.Values.Select(r => r.ID)) { Room.Null.ID };
            Room room;
            do
            {
                room = new Room(type);
            }
            while (used.Contains(room.ID));
            return room;
        }

        /// <summary>
        /// The host's check of a whole edit-mode draft before committing it (never trust the client).
        /// Returns null if <paramref name="after"/> is a legal result of editing <paramref name="before"/>.
        /// </summary>
        public static string ValidateDraft(LayoutBlueprint before, LayoutBlueprint after, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            // Special features (garage, greenhouse... anything but plain doors and hatches) can't be
            // added, removed or moved. The front door may move along the front wall (2026-11-19).
            var specialBefore = new HashSet<string>(before.Features.Where(f => !IsPlain(f.Type) && f.Type != FeatureType.FrontDoor).Select(EdgeKey));
            var specialAfter = new HashSet<string>(after.Features.Where(f => !IsPlain(f.Type) && f.Type != FeatureType.FrontDoor).Select(EdgeKey));
            if (!specialBefore.SetEquals(specialAfter))
            {
                return "special doors (like a garage door) can't be changed";
            }
            int frontRow = FrontRow(before);
            Feature frontBefore = before.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            List<Feature> frontsAfter = after.Features.Where(f => f.Type == FeatureType.FrontDoor).ToList();
            bool frontMoved = false;
            if (frontBefore != null)
            {
                if (frontsAfter.Count != 1)
                {
                    return "the restaurant needs exactly one front door";
                }
                Feature frontAfter = frontsAfter[0];
                frontMoved = EdgeKey(frontAfter) != EdgeKey(frontBefore);
                if (frontMoved)
                {
                    (LayoutPosition inside, LayoutPosition outside)? edge = OuterEdge(after, frontAfter.Tile1, frontAfter.Tile2);
                    if (!edge.HasValue || !IsFrontWall(frontRow, edge.Value.inside, edge.Value.outside) || after[edge.Value.inside].Type == RoomType.Garden)
                    {
                        return "the front door must be on the front wall (the street side)";
                    }
                    if (occupants.TryGetValue(edge.Value.inside, out OccupantFlags f) && f != OccupantFlags.None)
                    {
                        return $"nothing can stand just inside the front door {Fmt(edge.Value.inside)}";
                    }
                }
            }
            else if (frontsAfter.Count != 0)
            {
                return "a front door can't be added";
            }
            foreach (Feature f in after.Features.Where(f => IsPlain(f.Type)))
            {
                if (!IsLegalPlain(after, f, frontRow))
                {
                    return f.Type == FeatureType.Door
                        ? $"the door at {Fmt(f.Tile1)} must be between two rooms, or on a side or back outer wall"
                        : $"the hatch at {Fmt(f.Tile1)} isn't between two rooms";
                }
            }

            // Tiles under special doors can't change. The front door's old spot can if it moved.
            var protectedTiles = new HashSet<LayoutPosition>(before.Features
                .Where(f => !IsPlain(f.Type) && !(frontMoved && f.Type == FeatureType.FrontDoor))
                .SelectMany(f => new[] { f.Tile1, f.Tile2 }));
            foreach (LayoutPosition p in before.Tiles.Keys.Union(after.Tiles.Keys))
            {
                bool hadB = before.Tiles.TryGetValue(p, out Room b);
                bool hasA = after.Tiles.TryGetValue(p, out Room a);
                bool inB = hadB && LayoutHelpers.IsInside(b);
                bool inA = hasA && LayoutHelpers.IsInside(a);
                bool changed = inB != inA || (inB && inA && b.ID != a.ID) || (hadB && hasA && b.Type == RoomType.Garden && a.Type != RoomType.Garden);
                if (!changed)
                {
                    continue;
                }
                if ((hadB && b.Type == RoomType.Garden) || (hasA && a.Type == RoomType.Garden))
                {
                    return "garden tiles can't be edited";
                }
                if (protectedTiles.Contains(p))
                {
                    return $"tile {Fmt(p)} has a door that can't be moved";
                }
                occupants.TryGetValue(p, out OccupantFlags here);
                string occupied = TileOccupants.Refusal(inB, inA, here);
                if (occupied != null)
                {
                    return $"{occupied} {Fmt(p)}";
                }
                if (inA && !inB)
                {
                    if (Math.Abs(p.x) > MaxExtent || Math.Abs(p.y) > MaxExtent)
                    {
                        return "that's past the maximum restaurant size";
                    }
                    if (p.y < frontRow)
                    {
                        return "the restaurant can't grow toward the street";
                    }
                }
            }

            var typesBefore = new Dictionary<int, RoomType>();
            foreach (Room r in before.Tiles.Values.Where(r => LayoutHelpers.IsInside(r)))
            {
                typesBefore[r.ID] = r.Type;
            }
            foreach (Room r in after.Tiles.Values.Where(r => LayoutHelpers.IsInside(r)))
            {
                if (typesBefore.TryGetValue(r.ID, out RoomType t) ? t != r.Type : (r.Type != RoomType.Dining && r.Type != RoomType.Kitchen) || r.ID == Room.Null.ID)
                {
                    return "a room's type can't be changed, and new rooms must be a kitchen or dining room";
                }
            }

            LayoutPosition? lost = TileOccupants.LostWall(before, after, occupants.Keys, occupants);
            if (lost.HasValue)
            {
                return $"the wall-mounted appliance at {Fmt(lost.Value)} would have no wall left";
            }

            return CheckConnectivity(after);
        }

        private static bool IsPlain(FeatureType t) => t == FeatureType.Hatch || t == FeatureType.Door;

        // ------------------------------------------------------------------ outside doors and the front door

        /// <summary>
        /// The inside and outside tiles of an edge on the building's outer wall: one side an indoor
        /// tile, the other open ground (not stored, or stored as NoRoom). Null for any other edge.
        /// </summary>
        public static (LayoutPosition inside, LayoutPosition outside)? OuterEdge(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b)
        {
            if (Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) != 1)
            {
                return null;
            }
            bool ia = bp.Tiles.TryGetValue(a, out Room ra) && LayoutHelpers.IsInside(ra);
            bool ib = bp.Tiles.TryGetValue(b, out Room rb) && LayoutHelpers.IsInside(rb);
            if (ia == ib)
            {
                return null;
            }
            LayoutPosition inside = ia ? a : b, outside = ia ? b : a;
            if (bp.Tiles.TryGetValue(outside, out Room ro) && ro.Type != RoomType.NoRoom)
            {
                return null; // a garden or other non-room tile, not open ground
            }
            return (inside, outside);
        }

        /// <summary>An edge of the front wall: the outside tile is right in front of (south of) a front-row tile.</summary>
        public static bool IsFrontWall(int frontRow, LayoutPosition inside, LayoutPosition outside) =>
            inside.y == frontRow && outside.x == inside.x && outside.y == inside.y - 1;

        /// <summary>
        /// A plain door on a side or back outer wall (added 2026-11-19). Not on the front wall: that
        /// side has the front door, the queue and the setting's front decorations.
        /// </summary>
        public static bool IsLegalOuterDoor(LayoutBlueprint bp, Feature f, int frontRow)
        {
            if (f.Type != FeatureType.Door)
            {
                return false;
            }
            (LayoutPosition inside, LayoutPosition outside)? edge = OuterEdge(bp, f.Tile1, f.Tile2);
            return edge.HasValue && bp[edge.Value.inside].Type != RoomType.Garden && !IsFrontWall(frontRow, edge.Value.inside, edge.Value.outside);
        }

        private static bool IsLegalPlain(LayoutBlueprint bp, Feature f, int frontRow) =>
            IsInternalWall(bp, f.Tile1, f.Tile2) || (f.Type == FeatureType.Door && IsLegalOuterDoor(bp, f, frontRow));

        private static Feature FeatureAt(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b) =>
            bp.Features.FirstOrDefault(f => (f.Tile1 == a && f.Tile2 == b) || (f.Tile1 == b && f.Tile2 == a));

        /// <summary>Wall or door on an outer wall. Hatches don't go outside; the front door has its own brush.</summary>
        private static string SetOuterEdge(LayoutBlueprint bp, LayoutPosition inside, LayoutPosition outside, FeatureType? type)
        {
            Feature existing = FeatureAt(bp, inside, outside);
            if (existing != null && existing.Type == FeatureType.FrontDoor)
            {
                return "that's the front door: to move it, use the Front door brush on another spot of the front wall";
            }
            if (existing != null && !IsPlain(existing.Type))
            {
                return $"the {existing.Type} can't be changed";
            }
            if (type == null)
            {
                if (existing != null)
                {
                    bp.Features.Remove(existing);
                }
                return null;
            }
            if (type == FeatureType.Hatch)
            {
                return "hatches only go between two indoor rooms";
            }
            if (bp[inside].Type == RoomType.Garden)
            {
                return "garden tiles can't have doors";
            }
            if (IsFrontWall(FrontRow(bp), inside, outside))
            {
                return "the front wall only has the front door (use the Front door brush to move it)";
            }
            if (existing == null)
            {
                bp.Features.Add(new Feature(inside, outside, FeatureType.Door));
            }
            return null;
        }

        /// <summary>
        /// The Front door brush: moves the front door to this edge of the front wall. The old spot
        /// becomes wall. Nothing may stand just inside the new spot (vanilla reserves that tile).
        /// </summary>
        private static string MoveFrontDoor(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            Feature current = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (current == null)
            {
                return "this restaurant has no front door to move";
            }
            (LayoutPosition inside, LayoutPosition outside)? edge = OuterEdge(bp, a, b);
            if (!edge.HasValue || !IsFrontWall(FrontRow(bp), edge.Value.inside, edge.Value.outside))
            {
                return "the front door goes on the front wall (the street side)";
            }
            if (FeatureAt(bp, edge.Value.inside, edge.Value.outside) == current)
            {
                return null; // already there
            }
            if (bp[edge.Value.inside].Type == RoomType.Garden)
            {
                return "garden tiles can't have doors";
            }
            if (occupants.TryGetValue(edge.Value.inside, out OccupantFlags f) && f != OccupantFlags.None)
            {
                return "move the appliance off that tile first: nothing can stand just inside the front door";
            }
            bp.Features.Remove(current);
            bp.Features.Add(new Feature(edge.Value.inside, edge.Value.outside, FeatureType.FrontDoor));
            return null;
        }

        private static bool IsInternalWall(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b) =>
            Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) == 1
            && bp.Tiles.TryGetValue(a, out Room ra) && bp.Tiles.TryGetValue(b, out Room rb)
            && LayoutHelpers.IsInside(ra) && LayoutHelpers.IsInside(rb) && ra.ID != rb.ID;

        private static string EdgeKey(Feature f) =>
            $"{f.Type}:{Math.Min(f.Tile1.x, f.Tile2.x)},{Math.Min(f.Tile1.y, f.Tile2.y)},{Math.Max(f.Tile1.x, f.Tile2.x)},{Math.Max(f.Tile1.y, f.Tile2.y)}";

        /// <summary>
        /// Every indoor tile must stay reachable from the front door through same-room
        /// neighbours or doors. Hatches don't count, because people can't walk through them.
        /// </summary>
        public static string CheckConnectivity(LayoutBlueprint bp)
        {
            HashSet<LayoutPosition> cut = Unreachable(bp);
            if (cut.Count == 0)
            {
                return null;
            }
            LayoutPosition first = cut.OrderBy(p => p.x).ThenBy(p => p.y).First();
            return $"tile {Fmt(first)} would be cut off from the front door";
        }

        /// <summary>
        /// Indoor tiles that can't be reached from the front door by walking (same-room neighbours
        /// or doors). Empty if the layout has no front door, since there's nothing to anchor on.
        /// </summary>
        public static HashSet<LayoutPosition> Unreachable(LayoutBlueprint bp)
        {
            var cut = new HashSet<LayoutPosition>();
            Feature front = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (front == null)
            {
                return cut;
            }
            LayoutPosition start = LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1 : front.Tile2;

            var doors = new HashSet<(LayoutPosition, LayoutPosition)>();
            foreach (Feature f in bp.Features.Where(f => f.Type.IsDoor()))
            {
                doors.Add((f.Tile1, f.Tile2));
                doors.Add((f.Tile2, f.Tile1));
            }
            // Tiles with a door to the outside (the front door among them) are connected through the
            // outside: people walk round the building (the yard ring lets chefs, 2026-11-19).
            var outsideDoorTiles = new HashSet<LayoutPosition>(bp.Features
                .Where(f => f.Type.IsDoor())
                .Select(f => OuterEdge(bp, f.Tile1, f.Tile2))
                .Where(e => e.HasValue)
                .Select(e => e.Value.inside));
            bool outsideReached = false;
            var seen = new HashSet<LayoutPosition> { start };
            var queue = new Queue<LayoutPosition>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                LayoutPosition p = queue.Dequeue();
                if (!outsideReached && outsideDoorTiles.Contains(p))
                {
                    outsideReached = true;
                    foreach (LayoutPosition o in outsideDoorTiles.Where(o => seen.Add(o)))
                    {
                        queue.Enqueue(o);
                    }
                }
                foreach (LayoutPosition d in LayoutHelpers.Directions)
                {
                    LayoutPosition n = p + d;
                    if (seen.Contains(n) || !bp.Tiles.TryGetValue(n, out Room room) || !LayoutHelpers.IsInside(room))
                    {
                        continue;
                    }
                    if (room.ID == bp[p].ID || doors.Contains((p, n)))
                    {
                        seen.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }

            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
            {
                if (LayoutHelpers.IsInside(t.Value) && !seen.Contains(t.Key))
                {
                    cut.Add(t.Key);
                }
            }
            return cut;
        }

        internal static string Fmt(LayoutPosition p) => $"({p.x}, {p.y})";
    }
}
