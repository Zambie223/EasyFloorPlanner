using System;
using System.Collections.Generic;
using System.Reflection;
using Kitchen;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The part of the restaurant the day camera frames, in tile coordinates (inclusive). Not
    /// Custom means the whole restaurant (vanilla's framing, which follows every resize).
    /// </summary>
    public struct FloorPlanCameraArea
    {
        public bool Custom;
        public int MinX, MinY, MaxX, MaxY;

        public static FloorPlanCameraArea Whole => default;

        public static FloorPlanCameraArea Box(int x0, int y0, int x1, int y1) => new FloorPlanCameraArea
        {
            Custom = true,
            MinX = Math.Min(x0, x1),
            MinY = Math.Min(y0, y1),
            MaxX = Math.Max(x0, x1),
            MaxY = Math.Max(y0, y1),
        };

        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;

        public bool SameAs(FloorPlanCameraArea o) =>
            Custom == o.Custom && (!Custom || (MinX == o.MinX && MinY == o.MinY && MaxX == o.MaxX && MaxY == o.MaxY));

        public override string ToString() => Custom ? $"({MinX}, {MinY})..({MaxX}, {MaxY})" : "whole restaurant";
    }

    /// <summary>
    /// Reads and sets the camera area through the four day-camera markers vanilla's CreateNewKitchen
    /// places at the restaurant's bounds (marker views with CMaintainInView; the camera frames them).
    /// Storing the area as those markers' positions needs no data of our own: the game saves them,
    /// and a run still loads without the mod (keeping the chosen framing).
    ///
    /// The markers are recognised by kind (a Marker view with CMaintainInView, without
    /// ExpandBoundsAtNight's night tag), not by position, since a custom area moves them. At night
    /// the night markers (bounds + 3) are framed too, so the area shows during the day.
    /// </summary>
    internal static class CameraAreaMarkers
    {
        /// <summary>Smallest area, in tiles per side.</summary>
        public const int MinSide = 3;

        private static readonly Type NightBoundType =
            typeof(ExpandBoundsAtNight).GetNestedType("CNightBound", BindingFlags.NonPublic);

        /// <summary>The day markers (vanilla makes exactly four).</summary>
        public static List<Entity> DayMarkers(EntityManager em)
        {
            var result = new List<Entity>();
            EntityQuery q = em.CreateEntityQuery(
                ComponentType.ReadOnly<CRequiresView>(), ComponentType.ReadOnly<CMaintainInView>(), ComponentType.ReadOnly<CPosition>());
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    if (em.GetComponentData<CRequiresView>(e).Type != ViewType.Marker)
                    {
                        continue;
                    }
                    if (NightBoundType != null && em.HasComponent(e, ComponentType.ReadOnly(NightBoundType)))
                    {
                        continue;
                    }
                    result.Add(e);
                }
            }
            q.Dispose();
            return result;
        }

        /// <summary>World bounds of a tile rectangle, the way LayoutBlueprint.GetWorldBounds builds CBounds (tile centres).</summary>
        public static Bounds TileBounds(int minX, int minY, int maxX, int maxY) =>
            new Bounds(new Vector3((minX + maxX) / 2f, 0f, (minY + maxY) / 2f), new Vector3(maxX - minX, 10f, maxY - minY));

        public static (int minX, int minY, int maxX, int maxY) Rect(Bounds b) =>
            (Mathf.RoundToInt(b.min.x), Mathf.RoundToInt(b.min.z), Mathf.RoundToInt(b.max.x), Mathf.RoundToInt(b.max.z));

        /// <summary>
        /// The current area: the markers' rectangle, inverted from LayoutWriter.DayBoundPoints
        /// (x: min - 1.5 / max + 1.5; z: min - 1.5 / max + 1). Whole if it equals
        /// <paramref name="bounds"/>, or if the markers aren't the expected four.
        /// </summary>
        public static FloorPlanCameraArea Read(EntityManager em, Bounds bounds)
        {
            List<Entity> markers = DayMarkers(em);
            if (markers.Count != 4)
            {
                return FloorPlanCameraArea.Whole;
            }
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (Entity e in markers)
            {
                Vector3 p = em.GetComponentData<CPosition>(e).Position;
                x0 = Mathf.Min(x0, p.x);
                x1 = Mathf.Max(x1, p.x);
                z0 = Mathf.Min(z0, p.z);
                z1 = Mathf.Max(z1, p.z);
            }
            FloorPlanCameraArea area = FloorPlanCameraArea.Box(
                Mathf.RoundToInt(x0 + 1.5f), Mathf.RoundToInt(z0 + 1.5f), Mathf.RoundToInt(x1 - 1.5f), Mathf.RoundToInt(z1 - 1f));
            return Normalise(area, bounds);
        }

        /// <summary>An area equal to the restaurant's bounds is the whole restaurant.</summary>
        public static FloorPlanCameraArea Normalise(FloorPlanCameraArea area, Bounds bounds)
        {
            if (!area.Custom)
            {
                return area;
            }
            (int minX, int minY, int maxX, int maxY) = Rect(bounds);
            return area.MinX == minX && area.MinY == minY && area.MaxX == maxX && area.MaxY == maxY
                ? FloorPlanCameraArea.Whole
                : area;
        }

        /// <summary>Why an area can't be used, or null.</summary>
        public static string Problem(FloorPlanCameraArea area)
        {
            if (!area.Custom)
            {
                return null;
            }
            if (area.Width < MinSide || area.Height < MinSide)
            {
                return $"the camera area must be at least {MinSide} x {MinSide} tiles";
            }
            int limit = LayoutEdits.MaxExtent + 2;
            if (area.MinX < -limit || area.MinY < -limit || area.MaxX > limit || area.MaxY > limit)
            {
                return "the camera area is outside the editable map";
            }
            return null;
        }

        /// <summary>
        /// Moves the four markers to frame <paramref name="area"/> (or the whole of
        /// <paramref name="bounds"/>). Each keeps its corner. Returns how many were placed (4, or 0
        /// if the markers aren't the expected four).
        /// </summary>
        public static int Write(EntityManager em, Bounds bounds, FloorPlanCameraArea area)
        {
            List<Entity> markers = DayMarkers(em);
            if (markers.Count != 4)
            {
                ModMain.LogWarning($"Found {markers.Count} day-camera markers instead of 4; the camera area wasn't changed.");
                return 0;
            }
            Bounds target = area.Custom ? TileBounds(area.MinX, area.MinY, area.MaxX, area.MaxY) : bounds;
            Vector3[] points = LayoutWriter.DayBoundPoints(target); // (+x,+z), (+x,-z), (-x,+z), (-x,-z)

            Vector3 centre = Vector3.zero;
            foreach (Entity e in markers)
            {
                centre += em.GetComponentData<CPosition>(e).Position / 4f;
            }
            var used = new bool[4];
            int placed = 0;
            foreach (Entity e in markers)
            {
                Vector3 p = em.GetComponentData<CPosition>(e).Position;
                int i = (p.x >= centre.x ? 0 : 2) + (p.z >= centre.z ? 0 : 1);
                if (used[i])
                {
                    i = Array.IndexOf(used, false); // degenerate (all markers on one spot): any free corner
                }
                used[i] = true;
                em.SetComponentData(e, new CPosition(points[i]));
                placed++;
            }
            return placed;
        }
    }
}
