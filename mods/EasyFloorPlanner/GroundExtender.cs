using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Extends the setting's ground under very large restaurants. Local to each machine (visual only).
    ///
    /// Surveyed in-game 2026-11-19 (self-test survey mode):
    ///   - Countryside-type settings (Country, Autumn, Alpine, ...) place one "Ground" scenery appliance:
    ///     a unit cube scaled 100 x 0.1 x 100 at (0, 0), so the ground ends 50 tiles out. Witch Hut's
    ///     ground is two such boxes either side of a river (x -112..93, z -56..44).
    ///   - Their material uses the "Simple Flat" shader with a texture overlay projected in world space
    ///     (_TextureByUV = 0): a close-up across the edge of a ground box with a copy beside it shows no
    ///     seam. So more boxes with the same material continue the ground exactly.
    ///   - Street settings (City, Turbo) have no ground: their look is the street and the backs of
    ///     neighbouring buildings (StreetPieces fits both) on the camera's dark background. Left alone.
    ///
    /// Past the edge there's only the camera's background colour, which is what shows as "black
    /// areas" when a restaurant (or another mod's range) reaches beyond the ground.
    ///
    /// The ground boxes are found at runtime (big, flat, at ground level, not the restaurant), so
    /// modded settings work too. The area that needs ground is the restaurant's tiles plus
    /// <see cref="Margin"/> tiles all round (the customer spawn and the widest camera view fit in it).
    /// What of that area isn't already ground is covered by up to four strips (west, east, south,
    /// north of the ground boxes' combined extent), each a box of the nearest ground box's mesh and
    /// material, at the same height. Strips never overlap the original pieces, so things set into
    /// the ground (Witch Hut's river) aren't covered. They're parented to the ground's own view, so
    /// they go when it does. Restaurants whose area fits the ground (every vanilla one) get none.
    /// </summary>
    internal static class GroundExtender
    {
        /// <summary>Tiles of ground kept around the restaurant.</summary>
        public const float Margin = 40f;

        private const string StripName = "EasyFloorPlanner Ground Extension";
        private static readonly float[] CheckAfter = { 1f, 3f, 10f };

        private static Rect? Needed;
        private static readonly List<GameObject> Strips = new List<GameObject>();
        private static readonly List<Renderer> Grounds = new List<Renderer>();
        private static GameObject DriverObject;
        private static float LayoutAt;
        private static int ChecksDone;
        private static string LastLogged;

        /// <summary>Strips currently in place (self-test).</summary>
        public static int StripCount => Strips.Count(s => s != null);

        /// <summary>Called whenever the restaurant's lighting map is rebuilt (RoomLayoutMapPatch).</summary>
        public static void SetLayout(LayoutBlueprint bp)
        {
            Needed = null;
            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
            {
                if (!LayoutHelpers.IsInside(t.Value))
                {
                    continue;
                }
                var tile = new Rect(t.Key.x - 0.5f, t.Key.y - 0.5f, 1f, 1f);
                Needed = Needed.HasValue ? Union(Needed.Value, tile) : tile;
            }
            if (Needed.HasValue)
            {
                Rect n = Needed.Value;
                Needed = Rect.MinMaxRect(n.xMin - Margin, n.yMin - Margin, n.xMax + Margin, n.yMax + Margin);
            }
            if (DriverObject == null)
            {
                DriverObject = new GameObject("EasyFloorPlanner Ground Extender");
                Object.DontDestroyOnLoad(DriverObject);
                DriverObject.AddComponent<Driver>();
            }
            // The setting's scenery views can appear a little after the layout, so look again a few times.
            LayoutAt = Time.unscaledTime;
            ChecksDone = 0;
            Refresh();
        }

        /// <summary>Finds the ground boxes and puts the strips where they're needed.</summary>
        public static void Refresh()
        {
            Clear();
            Grounds.Clear();
            if (!Needed.HasValue || GameInfo.CurrentScene != SceneType.Kitchen)
            {
                return; // only restaurants (HQ has its own, fixed ground)
            }
            Grounds.AddRange(FindGrounds());
            if (Grounds.Count == 0)
            {
                return; // a setting without ground (street settings): its backdrop is the intended look
            }
            Rect have = Grounds.Select(Footprint).Aggregate(Union);
            Rect need = Needed.Value;
            if (have.xMin <= need.xMin && have.yMin <= need.yMin && have.xMax >= need.xMax && have.yMax >= need.yMax)
            {
                return;
            }
            // West and east strips run the full height; south and north strips fill between them.
            var rects = new List<Rect>();
            if (need.xMin < have.xMin)
            {
                rects.Add(Rect.MinMaxRect(need.xMin, Mathf.Min(need.yMin, have.yMin), have.xMin, Mathf.Max(need.yMax, have.yMax)));
            }
            if (need.xMax > have.xMax)
            {
                rects.Add(Rect.MinMaxRect(have.xMax, Mathf.Min(need.yMin, have.yMin), need.xMax, Mathf.Max(need.yMax, have.yMax)));
            }
            if (need.yMin < have.yMin)
            {
                rects.Add(Rect.MinMaxRect(have.xMin, need.yMin, have.xMax, have.yMin));
            }
            if (need.yMax > have.yMax)
            {
                rects.Add(Rect.MinMaxRect(have.xMin, have.yMax, have.xMax, need.yMax));
            }
            foreach (Rect r in rects.Where(r => r.width > 0.01f && r.height > 0.01f))
            {
                Renderer source = Grounds.OrderBy(g => Distance(Footprint(g), r)).First();
                GameObject strip = MakeStrip(source, r);
                if (strip != null)
                {
                    Strips.Add(strip);
                }
            }
            if (Strips.Count > 0)
            {
                FarLighting.RequestRescan(); // give the strips the outdoor night lighting straight away
                string message = $"Extended the ground under a big restaurant: {Strips.Count} strip(s) around the setting's ground "
                                 + $"({have.xMin:0}..{have.xMax:0} x {have.yMin:0}..{have.yMax:0}) to cover {need.xMin:0}..{need.xMax:0} x {need.yMin:0}..{need.yMax:0}.";
                if (message != LastLogged)
                {
                    LastLogged = message; // the re-checks after a layout change usually rebuild the same strips
                    ModMain.Log(message);
                }
            }
        }

        /// <summary>Big, flat, ground-level, axis-aligned boxes that aren't the restaurant or ours.</summary>
        private static IEnumerable<Renderer> FindGrounds() => Object.FindObjectsOfType<MeshRenderer>()
            .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.name != StripName)
            .Where(r => r.bounds.size.x >= 50f && r.bounds.size.z >= 50f && r.bounds.size.x < 5000f && r.bounds.size.z < 5000f)
            .Where(r => r.bounds.size.y < 0.5f && Mathf.Abs(r.bounds.center.y) < 0.5f)
            .Where(r => Quaternion.Angle(r.transform.rotation, Quaternion.identity) < 0.5f)
            .Where(r => r.GetComponent<MeshFilter>()?.sharedMesh != null)
            .Where(r => r.GetComponentInParent<LayoutView>() == null);

        private static GameObject MakeStrip(Renderer source, Rect r)
        {
            Mesh mesh = source.GetComponent<MeshFilter>().sharedMesh;
            Bounds local = mesh.bounds;
            if (local.size.x < 1e-4f || local.size.z < 1e-4f)
            {
                return null;
            }
            var go = new GameObject(StripName) { layer = source.gameObject.layer };
            go.transform.SetParent(source.transform.parent, false);
            Vector3 scale = source.transform.lossyScale;
            scale.x = r.width / local.size.x;
            scale.z = r.height / local.size.z;
            go.transform.rotation = Quaternion.identity;
            SetLossyScale(go.transform, scale);
            // Same height as the original; centre the mesh's local bounds on the strip.
            Vector3 centre = new Vector3(r.center.x - local.center.x * scale.x, source.transform.position.y, r.center.y - local.center.z * scale.z);
            go.transform.position = centre;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = source.sharedMaterials;
            mr.shadowCastingMode = source.shadowCastingMode;
            mr.receiveShadows = source.receiveShadows;
            mr.lightProbeUsage = source.lightProbeUsage;
            mr.reflectionProbeUsage = source.reflectionProbeUsage;
            return go;
        }

        private static void SetLossyScale(Transform t, Vector3 lossy)
        {
            Vector3 parent = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(lossy.x / Safe(parent.x), lossy.y / Safe(parent.y), lossy.z / Safe(parent.z));
        }

        private static float Safe(float v) => Mathf.Abs(v) < 1e-6f ? 1f : v;

        private static Rect Footprint(Renderer r) => Rect.MinMaxRect(r.bounds.min.x, r.bounds.min.z, r.bounds.max.x, r.bounds.max.z);

        private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        private static float Distance(Rect a, Rect b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.xMin - b.xMax, b.xMin - a.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(a.yMin - b.yMax, b.yMin - a.yMax));
            return dx * dx + dz * dz;
        }

        private static void Clear()
        {
            foreach (GameObject s in Strips)
            {
                if (s != null)
                {
                    Object.Destroy(s);
                }
            }
            Strips.Clear();
        }

        private class Driver : MonoBehaviour
        {
            private void Update()
            {
                if (ChecksDone < CheckAfter.Length && Time.unscaledTime - LayoutAt >= CheckAfter[ChecksDone])
                {
                    ChecksDone++;
                    Refresh();
                    return;
                }
                // A ground view that went away (scene change, reload) takes its strips with it; one
                // that's replaced needs new ones.
                if (Grounds.Count > 0 && Grounds.Any(g => g == null))
                {
                    LayoutAt = Time.unscaledTime;
                    ChecksDone = 0;
                    Grounds.Clear();
                }
            }
        }
    }
}
