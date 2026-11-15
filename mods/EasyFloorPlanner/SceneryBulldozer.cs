using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Clears outdoor scenery (trees, rocks, bushes, flowers placed by the restaurant setting's
    /// decorator; all CDoesNotOccupy appliances) out of the way of floor that a layout edit adds.
    /// Scenery never blocks growing.
    ///
    /// User decision (2026-11-13): just remove conflicting scenery. The first version pushed each
    /// piece outward to the nearest free tile, but in play pushed trees still stood on (or hung
    /// over) the new floor. A piece conflicts if it stands on a new floor tile or within one tile of
    /// one, so large tree canopies next to the new floor go too.
    ///
    /// Never removed: the decorator's invisible OutdoorMovementBlockers (they keep players near
    /// the restaurant), the nameplate, the street decorator's buildings (StreetPieces refits
    /// those to the new bounds), and anything standing on floor that was already indoors
    /// (vanilla has a few CDoesNotOccupy items inside, and the setting's ground plane sits at (0, 0)).
    /// </summary>
    internal static class SceneryBulldozer
    {
        /// <summary>How far (in tiles, each axis) from a new floor tile a piece still conflicts.</summary>
        public const float Margin = 1.05f;

        public static (int moved, int removed) ClearNewFloor(EntityManager em, TileManager tiles, LayoutBlueprint before, LayoutBlueprint after)
        {
            var newFloor = new HashSet<LayoutPosition>(after.Tiles
                .Where(t => LayoutHelpers.IsInside(t.Value, allow_garden: true)
                            && !(before.Tiles.TryGetValue(t.Key, out Room b) && LayoutHelpers.IsInside(b, allow_garden: true)))
                .Select(t => t.Key));
            if (newFloor.Count == 0)
            {
                return (0, 0);
            }

            int removed = 0;
            foreach (Entity e in Conflicting(em, before, newFloor))
            {
                em.DestroyEntity(e);
                removed++;
            }
            if (removed > 0)
            {
                ModMain.Log($"Removed {removed} piece(s) of outdoor scenery from on or beside the new floor.");
            }
            return (0, removed);
        }

        /// <summary>Scenery entities on, or within Margin of, any of <paramref name="newFloor"/>.</summary>
        public static List<Entity> Conflicting(EntityManager em, LayoutBlueprint before, ICollection<LayoutPosition> newFloor)
        {
            var result = new List<Entity>();
            foreach (KeyValuePair<Entity, Vector3> piece in AllScenery(em))
            {
                Vector3 p = piece.Value;
                LayoutPosition own = p.Rounded();
                if (before.Tiles.TryGetValue(own, out Room r) && LayoutHelpers.IsInside(r, allow_garden: true))
                {
                    continue; // already indoors before this edit: not ours to clear
                }
                int x0 = Mathf.FloorToInt(p.x - Margin), x1 = Mathf.CeilToInt(p.x + Margin);
                int z0 = Mathf.FloorToInt(p.z - Margin), z1 = Mathf.CeilToInt(p.z + Margin);
                bool hit = false;
                for (int x = x0; x <= x1 && !hit; x++)
                {
                    for (int z = z0; z <= z1 && !hit; z++)
                    {
                        hit = Mathf.Abs(p.x - x) <= Margin && Mathf.Abs(p.z - z) <= Margin && newFloor.Contains(new LayoutPosition(x, z));
                    }
                }
                if (hit)
                {
                    result.Add(piece.Key);
                }
            }
            return result;
        }

        /// <summary>Removable scenery pieces (CDoesNotOccupy appliances not being carried, minus the protected ones) and their positions.</summary>
        public static Dictionary<Entity, Vector3> AllScenery(EntityManager em)
        {
            var map = new Dictionary<Entity, Vector3>();
            HashSet<int> street = StreetPieces.ManagedIds(em);
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>(), ComponentType.ReadOnly<CDoesNotOccupy>() },
                None = new[] { ComponentType.ReadOnly<CHeldBy>() },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            using (NativeArray<CPosition> positions = q.ToComponentDataArray<CPosition>(Allocator.Temp))
            using (NativeArray<CAppliance> appliances = q.ToComponentDataArray<CAppliance>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    int id = appliances[i].ID;
                    if (id == AssetReference.OutdoorMovementBlocker || id == AssetReference.Nameplate || street.Contains(id))
                    {
                        continue; // street pieces are refitted by StreetPieces after the write
                    }
                    map[entities[i]] = positions[i].Position;
                }
            }
            q.Dispose();
            return map;
        }

        /// <summary>Removable scenery pieces by tile (for the self-test).</summary>
        public static Dictionary<LayoutPosition, List<Entity>> MapScenery(EntityManager em)
        {
            var map = new Dictionary<LayoutPosition, List<Entity>>();
            foreach (KeyValuePair<Entity, Vector3> piece in AllScenery(em))
            {
                LayoutPosition tile = piece.Value.Rounded();
                if (!map.TryGetValue(tile, out List<Entity> list))
                {
                    map[tile] = list = new List<Entity>();
                }
                list.Add(piece.Key);
            }
            return map;
        }
    }
}
