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
        // by NavmeshAreaPatch, so this is only a sanity limit. The navmesh is rebuilt on every edit,
        // and its cost grows with area. Raise it once batching (rebuild when leaving edit mode) exists.
        public const int MaxExtent = 30;

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
            string refused = SetEdgeUnchecked(bp, a, b, type);
            if (refused == null && wallCheck && TileOccupants.LostWall(before, bp, new[] { a, b }, occupants).HasValue)
            {
                Restore(bp, before);
                return "that would take away the only wall a wall-mounted appliance hangs on";
            }
            return refused;
        }

        private static string SetEdgeUnchecked(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b, FeatureType? type)
        {
            if (type.HasValue && !IsPlain(type.Value))
            {
                return "only walls, hatches and doors can be placed";
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
                return "hatches and doors only go between two indoor rooms (not to the outside yet)";
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
                return "that tile has a special door (like the front door) that can't be moved";
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
            // Plain hatches and doors only belong between two different indoor rooms.
            bp.Features.RemoveAll(f => (f.Tile1 == target || f.Tile2 == target)
                                       && (f.Type == FeatureType.Hatch || f.Type == FeatureType.Door)
                                       && !IsInternalWall(bp, f.Tile1, f.Tile2));
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
            // Special features (front door, garage, greenhouse... anything but plain doors and
            // hatches) can't be added, removed or moved.
            var specialBefore = new HashSet<string>(before.Features.Where(f => !IsPlain(f.Type)).Select(EdgeKey));
            var specialAfter = new HashSet<string>(after.Features.Where(f => !IsPlain(f.Type)).Select(EdgeKey));
            if (!specialBefore.SetEquals(specialAfter))
            {
                return "special doors (like the front door) can't be changed";
            }
            foreach (Feature f in after.Features.Where(f => IsPlain(f.Type)))
            {
                if (!IsInternalWall(after, f.Tile1, f.Tile2))
                {
                    return $"the {f.Type.ToString().ToLowerInvariant()} at {Fmt(f.Tile1)} isn't between two rooms";
                }
            }

            var protectedTiles = new HashSet<LayoutPosition>(before.Features.Where(f => !IsPlain(f.Type)).SelectMany(f => new[] { f.Tile1, f.Tile2 }));
            int frontRow = FrontRow(before);
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
            var seen = new HashSet<LayoutPosition> { start };
            var queue = new Queue<LayoutPosition>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                LayoutPosition p = queue.Dequeue();
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
