using System;
using System.Collections.Generic;
using Kitchen;
using Kitchen.Layouts;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Whether anything stands on a tile, for placing the Floor Plan terminal, the debug hotkeys'
    /// direct edits and the self-test. (The editor's rules for appliances on edited floor are in
    /// TileOccupants.) A tile is occupied if either is true:
    /// - TileManager has an occupant there in any layer (Default, Wall, Floor, Ceiling), or
    /// - any appliance that isn't being carried stands on it.
    ///
    /// The second check matters because TileManager leaves out appliances with CDoesNotOccupy or
    /// CAllowPlacingOver, and outdoor scenery (trees, bushes, rocks placed by the restaurant
    /// setting's decorator) is CDoesNotOccupy. In the in-game self-test, the TileManager-only
    /// check grew floor over trees, and their obstacles cut the navmesh in a new corridor.
    ///
    /// Take one snapshot per edit (cheap), not one per tile query.
    /// </summary>
    internal sealed class OccupancySnapshot
    {
        private static readonly OccupancyLayer[] Layers = (OccupancyLayer[])Enum.GetValues(typeof(OccupancyLayer));

        private readonly TileManager _tiles;
        private readonly HashSet<LayoutPosition> _solid = new HashSet<LayoutPosition>();
        private readonly HashSet<LayoutPosition> _scenery = new HashSet<LayoutPosition>();
        private readonly HashSet<Entity> _clearable = new HashSet<Entity>();

        private OccupancySnapshot(TileManager tiles)
        {
            _tiles = tiles;
        }

        public static OccupancySnapshot Take(EntityManager em, TileManager tiles)
        {
            var snapshot = new OccupancySnapshot(tiles);
            HashSet<int> street = StreetPieces.ManagedIds(em);
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>() },
                None = new[] { ComponentType.ReadOnly<CHeldBy>() },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            using (NativeArray<CPosition> positions = q.ToComponentDataArray<CPosition>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    LayoutPosition tile = positions[i].Position.Rounded();
                    // Outdoor scenery (trees, rocks, bushes) is CDoesNotOccupy; tracked apart so a
                    // caller can choose to treat it as clearable. The street decorator's buildings
                    // are clearable too, whatever their components: StreetPieces refits them to the
                    // new bounds after every write, so they never end up on floor.
                    bool clearable = em.HasComponent<CDoesNotOccupy>(entities[i])
                                     || street.Contains(em.GetComponentData<CAppliance>(entities[i]).ID);
                    (clearable ? snapshot._scenery : snapshot._solid).Add(tile);
                    if (clearable)
                    {
                        snapshot._clearable.Add(entities[i]);
                    }
                }
            }
            q.Dispose();
            return snapshot;
        }

        public bool IsOccupied(LayoutPosition p) => IsOccupied(p, ignoreScenery: false);

        /// <summary>
        /// Whether something on the tile blocks a layout edit. Outdoor scenery doesn't: the
        /// SceneryBulldozer removes it from new floor when the edit is committed (and StreetPieces
        /// moves the street's buildings).
        /// </summary>
        public bool BlocksEdit(LayoutPosition p) => IsOccupied(p, ignoreScenery: true);

        public bool IsOccupied(LayoutPosition p, bool ignoreScenery)
        {
            if (_solid.Contains(p) || (!ignoreScenery && _scenery.Contains(p)))
            {
                return true;
            }
            Vector3 pos = p;
            foreach (OccupancyLayer layer in Layers)
            {
                Entity occupant = _tiles.GetOccupant(pos, layer);
                if (occupant != default(Entity) && !(ignoreScenery && _clearable.Contains(occupant)))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
