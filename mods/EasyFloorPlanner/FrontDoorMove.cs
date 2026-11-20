using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Makes the game follow a front door moved along the front wall (the editor's Front door brush,
    /// added 2026-11-19).
    ///
    /// Vanilla (decompiled): when the kitchen is created, LayoutExtensions stores the blueprint's
    /// front door in CFrontDoorMarker on the SLayout entity (and CRoadMarker = door - 2 z), and every
    /// system reads GenericSystemBase.GetFrontDoor from that marker: the queue, queue patience,
    /// reachability of the outside tile, reserved tiles, player bounds and so on. So the markers are
    /// updated, and those systems follow on their own. What was placed once, relative to the door,
    /// is moved by hand, using vanilla's own formulas for the old and the new door:
    ///   - the reroll and practice terminals (GetRerollTile / GetPracticeTile: the outside tile
    ///     ±3 / ±4 in x, toward the middle);
    ///   - the nameplate (Decorator.GetNameplateTile) and the outside bin (door.x, door.z - 3; the
    ///     Halloween decorator puts a front tile there too).
    /// The setting's door-gapped front decorations and the cobble path follow through StreetPieces.
    /// EFP's own Floor Plan terminal is removed and respawns beside the new door.
    /// </summary>
    internal static class FrontDoorMove
    {
        /// <summary>The pieces moved by the last door move (self-test).</summary>
        public static int LastMoved { get; private set; }

        /// <summary>Returns whether the door moved.</summary>
        public static bool Apply(EntityManager em, Entity layout, LayoutBlueprint before, LayoutBlueprint after)
        {
            LastMoved = 0;
            if (!HasFrontDoor(before) || !HasFrontDoor(after))
            {
                return false;
            }
            Vector3 from = before.GetFrontDoor(), to = after.GetFrontDoor();
            if ((from - to).sqrMagnitude < 0.01f)
            {
                return false;
            }
            if (em.HasComponent<CFrontDoorMarker>(layout))
            {
                em.SetComponentData(layout, new CFrontDoorMarker { Location = to });
            }
            if (em.HasComponent<CRoadMarker>(layout))
            {
                em.SetComponentData(layout, new CRoadMarker { Location = to - new Vector3(0f, 0f, 2f) });
            }

            Vector3[] oldSpots = Spots(from), newSpots = Spots(to);
            int blocker = KitchenData.AssetReference.OutdoorMovementBlocker;
            HashSet<int> managed = StreetPieces.ManagedIds(em);
            var moves = new List<(Entity e, Vector3 to)>();
            var terminals = new List<Entity>();
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>() },
                None = new[] { ComponentType.ReadOnly<CHeldBy>() },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    if (em.HasComponent<CFloorPlanTerminal>(e))
                    {
                        terminals.Add(e);
                        continue;
                    }
                    int id = em.GetComponentData<CAppliance>(e).ID;
                    if (id == blocker || managed.Contains(id))
                    {
                        continue; // the movement blockers and the managed front decorations are refitted elsewhere
                    }
                    Vector3 p = em.GetComponentData<CPosition>(e).Position;
                    int spot = System.Array.FindIndex(oldSpots, s => Mathf.Abs(s.x - p.x) < 0.05f && Mathf.Abs(s.z - p.z) < 0.05f);
                    if (spot >= 0)
                    {
                        moves.Add((e, newSpots[spot]));
                    }
                }
            }
            q.Dispose();
            foreach ((Entity e, Vector3 target) in moves)
            {
                CPosition pos = em.GetComponentData<CPosition>(e);
                pos.Position = new Vector3(target.x, pos.Position.y, target.z);
                pos.ForceSnap = true;
                em.SetComponentData(e, pos);
            }
            foreach (Entity t in terminals)
            {
                em.DestroyEntity(t); // FloorPlanService respawns it beside the new door
            }
            LastMoved = moves.Count;
            ModMain.Log($"Moved the front door from x = {from.x:0} to x = {to.x:0}; moved {moves.Count} piece(s) that belong beside it.");
            return true;
        }

        private static bool HasFrontDoor(LayoutBlueprint bp) => bp.Features.Any(f => f.Type == FeatureType.FrontDoor);

        /// <summary>Vanilla's door-relative spots: reroll, practice, nameplate, outside bin.</summary>
        private static Vector3[] Spots(Vector3 door)
        {
            Vector3 outside = door - new Vector3(0f, 0f, 1f);
            int toward = outside.x > 0f ? -1 : 1;
            Vector3 nameplate = Decorator.GetNameplateTile(door);
            return new[]
            {
                outside + new Vector3(toward * 3, 0f, 0f),
                outside + new Vector3(toward * 4, 0f, 0f),
                new Vector3(nameplate.x, 0f, nameplate.z),
                new Vector3(door.x, 0f, door.z - 3f),
            };
        }
    }
}
