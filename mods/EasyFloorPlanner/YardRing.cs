using System.Linq;
using HarmonyLib;
using Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// A small yard around the building for chefs, while it has a door on a side or back outer wall
    /// (user decision 2026-11-19: "small yard ring").
    ///
    /// Vanilla keeps chefs inside the building's bounds plus the strip in front of the front door:
    /// EnforcePlayerBounds snaps anyone outside CBounds (encapsulating the door - 2 z, expanded by
    /// 0.5) back to the front door, and the decorator's invisible OutdoorMovementBlockers fence off
    /// the front strip (a row 3 tiles in front and two posts at each front corner). With an outside
    /// door, chefs could only step out to be snapped back. So while one exists:
    ///   - EnforcePlayerBounds sees the bounds grown by <see cref="Width"/> tiles each way (a postfix
    ///     on GenericSystemBase.Bounds for that system only; every other system sees vanilla's);
    ///   - the movement blockers form a ring <see cref="Width"/> + 1 tiles out instead
    ///     (LayoutWriter.MovementBlockerPoints);
    ///   - street settings move their side and back buildings out of the way (StreetPieces);
    ///   - scenery is cleared from the tile outside each new outside door (LayoutCommit).
    /// Appliances still can't be put down outside (vanilla's drop rules use the real bounds).
    /// </summary>
    internal static class YardRing
    {
        /// <summary>Tiles of yard around the building.</summary>
        public const int Width = 2;

        /// <summary>Whether the current restaurant has the ring (host side; set on every layout write and load).</summary>
        public static bool Active { get; private set; }

        /// <summary>A plain door on a side or back outer wall (the front door doesn't count).</summary>
        public static bool Wanted(LayoutBlueprint bp)
        {
            Feature front = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            int frontRow = front == null ? int.MinValue : (LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1.y : front.Tile2.y);
            return bp.Features.Any(f => LayoutEdits.IsLegalOuterDoor(bp, f, frontRow));
        }

        public static void Refresh(LayoutBlueprint bp)
        {
            bool now = Wanted(bp);
            if (now != Active)
            {
                ModMain.Log(now
                    ? $"The restaurant has a side or back door: chefs may walk a {Width}-tile yard round the building."
                    : "No side or back doors: the yard round the building is closed again.");
            }
            Active = now;
        }
    }

    /// <summary>EnforcePlayerBounds reads GenericSystemBase.Bounds: grow it for that system while the yard ring is open.</summary>
    [HarmonyPatch(typeof(GenericSystemBase), "Bounds", MethodType.Getter)]
    internal static class YardRingBoundsPatch
    {
        private static void Postfix(GenericSystemBase __instance, ref Bounds __result)
        {
            if (YardRing.Active && __instance is EnforcePlayerBounds && __result.size != Vector3.zero)
            {
                __result.Expand(new Vector3(YardRing.Width * 2f, 0f, YardRing.Width * 2f));
            }
        }
    }
}
