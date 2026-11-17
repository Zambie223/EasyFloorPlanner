using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>What an appliance on a tile means for editing that tile.</summary>
    [Flags]
    public enum OccupantFlags : byte
    {
        None = 0,

        /// <summary>Can't be picked up or belongs outdoors (CImmovable, COutsideOnly): the tile can't switch between indoors and outdoors.</summary>
        Fixed = 1,

        /// <summary>The game won't let it go outdoors (CUnsellableAppliance): its floor can't be erased.</summary>
        KeepIndoors = 2,

        /// <summary>Left outdoors at the start of the day, the game throws it away (MarkOutsideAppliancesForDestruction).</summary>
        Binned = 4,

        /// <summary>Must stand against a solid wall when indoors (CMustHaveWall).</summary>
        WallMounted = 8,
    }

    /// <summary>A tile and what stands on it (sent to the editor).</summary>
    public struct OccupiedTile
    {
        public LayoutPosition Tile;
        public OccupantFlags Flags;
    }

    /// <summary>
    /// The appliances an edit has to respect, by tile. Repainting a tile under an appliance is
    /// allowed (user decision 2026-11-17): moving it to another room is harmless, because every
    /// room-based rule in the game is recalculated live. What the game's own placement rules
    /// (PickUpAndDropAppliance.PerformDrop) would refuse is refused or warned about instead:
    /// - Fixed: no switching the tile between indoors and outdoors.
    /// - KeepIndoors: no erasing its floor (the game never drops it outdoors).
    /// - Binned: erasing its floor is allowed, with a warning; the game bins it at the start of
    ///   the day unless it's moved back inside (or the floor is painted back under it).
    /// - WallMounted: an edit that takes away its last solid wall is refused, and after an edit it
    ///   is turned to face a wall if its own wall went (WallMountedFixup).
    /// Not tracked: scenery and the street's pieces (cleared by SceneryBulldozer and StreetPieces),
    /// chairs (the game regenerates them; see TableFixup) and messes.
    /// </summary>
    internal static class TileOccupants
    {
        public static Dictionary<LayoutPosition, OccupantFlags> Take(EntityManager em)
        {
            var result = new Dictionary<LayoutPosition, OccupantFlags>();
            HashSet<int> street = StreetPieces.ManagedIds(em);
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>() },
                None = new[]
                {
                    ComponentType.ReadOnly<CHeldBy>(), ComponentType.ReadOnly<CDoesNotOccupy>(),
                    ComponentType.ReadOnly<CApplianceChair>(), ComponentType.ReadOnly<CApplianceGhostChair>(),
                    ComponentType.ReadOnly<CMess>(),
                },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            using (NativeArray<CPosition> positions = q.ToComponentDataArray<CPosition>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    if (street.Contains(em.GetComponentData<CAppliance>(e).ID))
                    {
                        continue;
                    }
                    LayoutPosition tile = positions[i].Position.Rounded();
                    if (Math.Abs(tile.x) > LayoutEdits.MaxExtent + 2 || Math.Abs(tile.y) > LayoutEdits.MaxExtent + 2)
                    {
                        continue;
                    }
                    OccupantFlags f = Classify(em, e);
                    result[tile] = result.TryGetValue(tile, out OccupantFlags had) ? had | f : f;
                }
            }
            q.Dispose();
            return result;
        }

        private static OccupantFlags Classify(EntityManager em, Entity e)
        {
            OccupantFlags f = OccupantFlags.None;
            bool immovable = em.HasComponent<CImmovable>(e);
            bool outsideOnly = em.HasComponent<COutsideOnly>(e);
            bool unsellable = em.HasComponent<CUnsellableAppliance>(e);
            if (immovable || outsideOnly)
            {
                f |= OccupantFlags.Fixed;
            }
            if (unsellable)
            {
                f |= OccupantFlags.KeepIndoors;
            }
            // The same exclusions as vanilla's MarkOutsideAppliancesForDestruction query.
            if (!immovable && !outsideOnly && !unsellable && !em.HasComponent<CForSale>(e))
            {
                f |= OccupantFlags.Binned;
            }
            if (em.HasComponent<CMustHaveWall>(e))
            {
                f |= OccupantFlags.WallMounted;
            }
            return f;
        }

        /// <summary>The four sides, as LayoutPositions, with the game's Orientation for each (Reachability's mapping).</summary>
        internal static readonly (Orientation O, LayoutPosition D)[] Sides =
        {
            (Orientation.Right, new LayoutPosition(1, 0)),
            (Orientation.Down, new LayoutPosition(0, -1)),
            (Orientation.Left, new LayoutPosition(-1, 0)),
            (Orientation.Up, new LayoutPosition(0, 1)),
        };

        /// <summary>
        /// Whether the side of indoor tile <paramref name="p"/> toward <paramref name="d"/> is a solid
        /// wall, as the game decides it for wall-mounted appliances: the neighbour is another room or
        /// outdoors, and there's no door or hatch between them (LayoutExtensions.CanDirectReach).
        /// </summary>
        internal static bool IsSolidWall(LayoutBlueprint bp, LayoutPosition p, LayoutPosition d)
        {
            if (!bp.Tiles.TryGetValue(p, out Room room))
            {
                return false;
            }
            LayoutPosition n = p + d;
            if (bp.Tiles.TryGetValue(n, out Room other) && other.ID == room.ID)
            {
                return false;
            }
            return !bp.HasReachingFeature(p, n);
        }

        internal static bool HasSolidWall(LayoutBlueprint bp, LayoutPosition p) =>
            IsIndoor(bp, p) && Sides.Any(s => IsSolidWall(bp, p, s.D));

        internal static bool IsIndoor(LayoutBlueprint bp, LayoutPosition p) =>
            bp.Tiles.TryGetValue(p, out Room r) && LayoutHelpers.IsInside(r);

        /// <summary>
        /// Refusal for changing tile <paramref name="p"/> between indoors and outdoors, given what
        /// stands on it; null if allowed. Changes between indoor rooms are always allowed here.
        /// </summary>
        internal static string Refusal(bool wasIndoor, bool isIndoor, OccupantFlags f)
        {
            if (wasIndoor == isIndoor)
            {
                return null;
            }
            if ((f & OccupantFlags.Fixed) != 0)
            {
                return "an appliance that can't be moved is on that tile";
            }
            if (wasIndoor && (f & OccupantFlags.KeepIndoors) != 0)
            {
                return "the appliance on that tile can't go outdoors, so its floor can't be removed";
            }
            return null;
        }

        /// <summary>
        /// The wall-mounted appliance tiles near <paramref name="changed"/> (itself and its
        /// neighbours, whose walls it can change) that had a solid wall in <paramref name="before"/>
        /// and have none in <paramref name="after"/>. Indoor tiles only: outdoors there's no wall rule.
        /// </summary>
        internal static LayoutPosition? LostWall(LayoutBlueprint before, LayoutBlueprint after, IEnumerable<LayoutPosition> tiles,
            IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants)
        {
            foreach (LayoutPosition p in tiles)
            {
                if (occupants.TryGetValue(p, out OccupantFlags f) && (f & OccupantFlags.WallMounted) != 0
                    && IsIndoor(after, p) && HasSolidWall(before, p) && !HasSolidWall(after, p))
                {
                    return p;
                }
            }
            return null;
        }

        internal static IEnumerable<LayoutPosition> WithNeighbours(LayoutPosition p)
        {
            yield return p;
            foreach (var s in Sides)
            {
                yield return p + s.D;
            }
        }

        // ------------------------------------------------------------------ after a commit (host)

        /// <summary>
        /// Turns each wall-mounted appliance whose wall went to face a remaining solid wall, the way
        /// vanilla's RotateAppliancesOnWalls does (clockwise from its current facing). Returns how
        /// many turned. Uses the committed blueprint, so it doesn't depend on TileManager's cache.
        /// </summary>
        internal static int WallMountedFixup(EntityManager em, LayoutBlueprint bp)
        {
            int turned = 0;
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadWrite<CPosition>(), ComponentType.ReadOnly<CMustHaveWall>() },
                None = new[] { ComponentType.ReadOnly<CHeldBy>(), ComponentType.ReadOnly<CFixedRotation>() },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    CPosition pos = em.GetComponentData<CPosition>(e);
                    LayoutPosition tile = pos.Position.Rounded();
                    if (!IsIndoor(bp, tile))
                    {
                        continue;
                    }
                    Orientation facing = pos.Rotation.ToOrientation();
                    int index = Array.FindIndex(Sides, s => s.O == facing);
                    if (index >= 0 && IsSolidWall(bp, tile, Sides[index].D))
                    {
                        continue;
                    }
                    Orientation o = facing;
                    for (int i = 0; i < 4; i++)
                    {
                        o = o.RotateCW();
                        int k = Array.FindIndex(Sides, s => s.O == o);
                        if (k >= 0 && IsSolidWall(bp, tile, Sides[k].D))
                        {
                            em.SetComponentData(e, new CPosition { Position = pos.Position, Rotation = o.ToRotation() });
                            turned++;
                            break;
                        }
                    }
                }
            }
            q.Dispose();
            return turned;
        }

        /// <summary>
        /// Table groups and chairs only follow walls that the game itself never moves: a group is
        /// only formed when a table is placed, and a chair is only cleared when its table moves.
        /// After an edit, this undoes what the new walls made wrong, and the game's own table update
        /// (every night frame) rebuilds them:
        /// - A table group with a part on or next to a tile whose room changed is broken up the way
        ///   vanilla does when a table is picked up (AssembleTableSets.DestroySet), so it re-forms
        ///   from the tables now side by side in the same room.
        /// - A chair now in a different room from its table is removed (like ClearUnusedChairs),
        ///   and other tables it could serve that are now in another room are dropped from it.
        /// Returns (groups broken up, chairs removed).
        /// </summary>
        internal static (int Groups, int Chairs) TableFixup(EntityManager em, LayoutBlueprint before, LayoutBlueprint after)
        {
            var near = new HashSet<LayoutPosition>();
            foreach (LayoutPosition p in before.Tiles.Keys.Union(after.Tiles.Keys))
            {
                if (RoomAt(before, p) != RoomAt(after, p))
                {
                    foreach (LayoutPosition n in WithNeighbours(p))
                    {
                        near.Add(n);
                    }
                }
            }
            if (near.Count == 0)
            {
                return (0, 0);
            }

            var sets = new HashSet<Entity>();
            EntityQuery tables = em.CreateEntityQuery(ComponentType.ReadOnly<CApplianceTable>(), ComponentType.ReadOnly<CPosition>(), ComponentType.ReadOnly<CPartOfTableSet>());
            using (NativeArray<Entity> entities = tables.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity t in entities)
                {
                    if (near.Contains(em.GetComponentData<CPosition>(t).Position.Rounded()))
                    {
                        sets.Add(em.GetComponentData<CPartOfTableSet>(t).TableSet);
                    }
                }
            }
            tables.Dispose();
            foreach (Entity set in sets)
            {
                if (!em.Exists(set) || !em.HasComponent<CTableSetParts>(set))
                {
                    continue;
                }
                NativeArray<CTableSetParts> parts = em.GetBuffer<CTableSetParts>(set).ToNativeArray(Allocator.Temp);
                foreach (CTableSetParts part in parts)
                {
                    if (em.Exists(part.Entity) && em.HasComponent<CPartOfTableSet>(part.Entity))
                    {
                        em.RemoveComponent<CPartOfTableSet>(part.Entity);
                    }
                }
                parts.Dispose();
                em.DestroyEntity(set);
            }

            int chairsRemoved = 0;
            EntityQuery chairs = em.CreateEntityQuery(ComponentType.ReadOnly<CApplianceGhostChair>(), ComponentType.ReadOnly<CPosition>(), ComponentType.ReadWrite<CGhostChairTableCandidates>());
            using (NativeArray<Entity> entities = chairs.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity chair in entities)
                {
                    LayoutPosition at = em.GetComponentData<CPosition>(chair).Position.Rounded();
                    int room = RoomAt(after, at);
                    bool SameRoom(Entity table) =>
                        em.Exists(table) && em.HasComponent<CPosition>(table)
                        && RoomAt(after, em.GetComponentData<CPosition>(table).Position.Rounded()) == room;

                    if (!SameRoom(em.GetComponentData<CApplianceGhostChair>(chair).Table))
                    {
                        em.DestroyEntity(chair);
                        chairsRemoved++;
                        continue;
                    }
                    DynamicBuffer<CGhostChairTableCandidates> candidates = em.GetBuffer<CGhostChairTableCandidates>(chair);
                    for (int i = candidates.Length - 1; i >= 0; i--)
                    {
                        if (!SameRoom(candidates[i].Table))
                        {
                            candidates = em.GetBuffer<CGhostChairTableCandidates>(chair);
                            candidates.RemoveAt(i);
                        }
                    }
                }
            }
            chairs.Dispose();
            return (sets.Count, chairsRemoved);
        }

        /// <summary>The room ID at a tile (0 outdoors), as TileManager.GetRoom would report it after the rebuild.</summary>
        private static int RoomAt(LayoutBlueprint bp, LayoutPosition p) =>
            bp.Tiles.TryGetValue(p, out Room r) ? r.ID : 0;

        /// <summary>Appliances the edit leaves outdoors that the game will throw away at the start of the day.</summary>
        internal static int CountBinned(LayoutBlueprint before, LayoutBlueprint after, IReadOnlyDictionary<LayoutPosition, OccupantFlags> occupants) =>
            occupants.Count(o => (o.Value & OccupantFlags.Binned) != 0 && IsIndoor(before, o.Key) && !IsIndoor(after, o.Key));
    }
}
