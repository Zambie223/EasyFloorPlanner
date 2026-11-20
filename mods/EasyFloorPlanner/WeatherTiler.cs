using System.Collections.Generic;
using HarmonyLib;
using Kitchen;
using Kitchen.Layouts;
using UnityEngine;
using UnityEngine.VFX;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Brings the weather (rain, snow, wind, fireflies...) to restaurants built beyond the area the
    /// game's weather effect covers. Local to each machine (visual only). Added 2026-11-19 (user
    /// report: rain only fell round the original restaurant).
    ///
    /// Vanilla (decompiled): WeatherView has one VisualEffect per weather mode at the origin. On each
    /// update it turns on the one for the current mode, sets its "Active" bool and hands it the
    /// _RoomLayout map (indoor tiles stay dry); every frame it shows or hides it (accessibility option)
    /// and passes it _NightFade.
    ///
    /// Measured in the self-test (StepRainMeasure, 2026-11-13): the effect spawns over a fixed area
    /// of local tiles x -10..10, z -16..5 (21 x 22) round its own position, and it reads the map in its
    /// local space: local tile t is pixel t + 16 (moved 8 tiles east, it rained 8 tiles further east,
    /// masked by the same pixels).
    ///
    /// So each effect gets copies on a 21 x 22 grid (the spawn area, so they tile without gaps or
    /// double density) wherever the restaurant plus <see cref="Margin"/> tiles needs weather, each
    /// with its own 32 x 32 map of its window (pixel p = world tile p - 16 + offset). Every frame the
    /// copies mirror the original (shown, "Active", _NightFade), and only copies whose area the
    /// camera can see are shown, so a huge restaurant costs a handful of effects, not dozens.
    /// </summary>
    internal static class WeatherTiler
    {
        public const float Margin = 12f;
        private const int StepX = 21, StepZ = 22;

        // The measured spawn area (local tiles); the self-test re-measures it every run.
        internal const int AreaMinX = -10, AreaMaxX = 10, AreaMinZ = -16, AreaMaxZ = 5;

        private static readonly AccessTools.FieldRef<WeatherView, List<WeatherView.WeatherLookup>> Lookups =
            AccessTools.FieldRefAccess<WeatherView, List<WeatherView.WeatherLookup>>("WeatherLookups");
        private static readonly int NightFade = Shader.PropertyToID("_NightFade");

        private static LayoutBlueprint Layout;
        private static int LayoutVersion;

        /// <summary>Self-test: show every copy, not only those in the camera's view.</summary>
        internal static bool TestShowAll { get; set; }

        /// <summary>Copies currently shown (self-test).</summary>
        public static int VisibleCopies { get; internal set; }

        /// <summary>Copies made for the current layout (self-test).</summary>
        public static int TotalCopies { get; internal set; }

        /// <summary>Called whenever the restaurant's lighting map is rebuilt (RoomLayoutMapPatch).</summary>
        public static void SetLayout(LayoutBlueprint bp)
        {
            Layout = new LayoutBlueprint(bp);
            LayoutVersion++;
        }

        /// <summary>The grid offsets (other than the origin's) whose spawn area meets the restaurant plus Margin.</summary>
        private static List<Vector2Int> Offsets()
        {
            var result = new List<Vector2Int>();
            if (Layout == null || GameInfo.CurrentScene != SceneType.Kitchen)
            {
                return result;
            }
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (KeyValuePair<LayoutPosition, Room> t in Layout.Tiles)
            {
                if (LayoutHelpers.IsInside(t.Value))
                {
                    x0 = Mathf.Min(x0, t.Key.x);
                    x1 = Mathf.Max(x1, t.Key.x);
                    z0 = Mathf.Min(z0, t.Key.y);
                    z1 = Mathf.Max(z1, t.Key.y);
                }
            }
            if (x0 > x1)
            {
                return result;
            }
            if (x0 >= AreaMinX && x1 <= AreaMaxX && z0 >= AreaMinZ && z1 <= AreaMaxZ)
            {
                return result; // the game's own effect covers the whole building: leave it as vanilla
            }
            x0 -= Margin;
            x1 += Margin;
            z0 -= Margin;
            z1 += Margin;
            int i0 = Mathf.FloorToInt((x0 - AreaMaxX) / StepX), i1 = Mathf.CeilToInt((x1 - AreaMinX) / StepX);
            int j0 = Mathf.FloorToInt((z0 - AreaMaxZ) / StepZ), j1 = Mathf.CeilToInt((z1 - AreaMinZ) / StepZ);
            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    if (i == 0 && j == 0)
                    {
                        continue; // the original's own area
                    }
                    int ox = i * StepX, oz = j * StepZ;
                    bool meets = ox + AreaMaxX >= x0 && ox + AreaMinX <= x1 && oz + AreaMaxZ >= z0 && oz + AreaMinZ <= z1;
                    if (meets)
                    {
                        result.Add(new Vector2Int(ox, oz));
                    }
                }
            }
            return result;
        }

        /// <summary>The map a copy at this offset reads: pixel p is world tile p - 16 + offset (Clamp, like vanilla's).</summary>
        private static Texture2D MapFor(Vector2Int offset)
        {
            int size = RoomLayoutMapPatch.Size, first = RoomLayoutMapPatch.First;
            var t = new Texture2D(size, size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            for (int px = 0; px < size; px++)
            {
                for (int py = 0; py < size; py++)
                {
                    pixels[py * size + px] = RoomLayoutMapPatch.Encode(Layout[new Vector2(px + first + offset.x, py + first + offset.y)]);
                }
            }
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        /// <summary>Lives on each WeatherView: keeps its copies made, mapped and in step with the originals.</summary>
        internal sealed class Copies : MonoBehaviour
        {
            private WeatherView View;
            private int BuiltFor = -1;
            private readonly List<(VisualEffect original, VisualEffect copy, Vector2Int offset, Texture2D map)> Made = new List<(VisualEffect, VisualEffect, Vector2Int, Texture2D)>();
            private string LastLogged;
            private readonly List<Texture2D> Maps = new List<Texture2D>();

            public void Init(WeatherView view) => View = view;

            private void LateUpdate()
            {
                if (View == null)
                {
                    return;
                }
                if (BuiltFor != LayoutVersion)
                {
                    Rebuild();
                }
                Rect seen = TestShowAll ? new Rect(-1e6f, -1e6f, 2e6f, 2e6f) : CameraGroundRect();
                int visible = 0;
                foreach ((VisualEffect original, VisualEffect copy, Vector2Int o, Texture2D map) in Made)
                {
                    if (copy == null || original == null)
                    {
                        continue;
                    }
                    var area = Rect.MinMaxRect(o.x + AreaMinX - 0.5f, o.y + AreaMinZ - 0.5f, o.x + AreaMaxX + 0.5f, o.y + AreaMaxZ + 0.5f);
                    bool on = Settings.WeatherEverywhere && original.gameObject.activeSelf && seen.Overlaps(area);
                    if (copy.gameObject.activeSelf != on)
                    {
                        copy.gameObject.SetActive(on);
                        if (on && copy.HasTexture("_RoomLayout"))
                        {
                            // After switching it on, as WeatherView does: a value set while the effect
                            // was inactive didn't stick (2026-11-19 self-test: indoor tiles got rain).
                            copy.SetTexture("_RoomLayout", map);
                        }
                    }
                    if (!on)
                    {
                        continue;
                    }
                    visible++;
                    if (original.HasBool("Active") && copy.GetBool("Active") != original.GetBool("Active"))
                    {
                        copy.SetBool("Active", original.GetBool("Active"));
                    }
                    if (copy.HasFloat(NightFade))
                    {
                        copy.SetFloat(NightFade, Shader.GetGlobalFloat(NightFade));
                    }
                }
                VisibleCopies = visible;
            }

            private void Rebuild()
            {
                BuiltFor = LayoutVersion;
                Clear();
                List<Vector2Int> offsets = Offsets();
                if (offsets.Count == 0)
                {
                    TotalCopies = 0;
                    return;
                }
                var maps = new Dictionary<Vector2Int, Texture2D>();
                foreach (Vector2Int o in offsets)
                {
                    Texture2D map = MapFor(o);
                    maps[o] = map;
                    Maps.Add(map);
                }
                foreach (WeatherView.WeatherLookup lookup in Lookups(View) ?? new List<WeatherView.WeatherLookup>())
                {
                    VisualEffect original = lookup.Effect;
                    if (original == null)
                    {
                        continue;
                    }
                    foreach (Vector2Int o in offsets)
                    {
                        GameObject go = Object.Instantiate(original.gameObject, original.transform.parent);
                        go.name = original.gameObject.name + $" (EFP {o.x},{o.y})";
                        go.transform.localPosition = original.transform.localPosition + new Vector3(o.x, 0f, o.y);
                        go.transform.localRotation = original.transform.localRotation;
                        go.transform.localScale = original.transform.localScale;
                        go.SetActive(false);
                        VisualEffect copy = go.GetComponent<VisualEffect>();
                        if (copy == null)
                        {
                            Object.Destroy(go);
                            continue;
                        }
                        Made.Add((original, copy, o, maps[o]));
                    }
                }
                TotalCopies = Made.Count;
                string message = $"Weather extended over the restaurant: {offsets.Count} extra area(s) per weather effect ({Made.Count} copies, shown only where the camera looks).";
                if (message != LastLogged)
                {
                    LastLogged = message;
                    ModMain.Log(message);
                }
            }

            private void Clear()
            {
                foreach ((VisualEffect _, VisualEffect copy, Vector2Int _, Texture2D _) in Made)
                {
                    if (copy != null)
                    {
                        Object.Destroy(copy.gameObject);
                    }
                }
                Made.Clear();
                foreach (Texture2D t in Maps)
                {
                    Object.Destroy(t);
                }
                Maps.Clear();
            }

            private void OnDestroy() => Clear();

            /// <summary>The ground area the main camera can see (its view's corners on y = 0), a little enlarged.</summary>
            private static Rect CameraGroundRect()
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    return new Rect(-1e6f, -1e6f, 2e6f, 2e6f);
                }
                var ground = new Plane(Vector3.up, Vector3.zero);
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (Vector2 v in new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) })
                {
                    Ray ray = cam.ViewportPointToRay(new Vector3(v.x, v.y, 0f));
                    Vector3 p = ground.Raycast(ray, out float d) && d > 0f ? ray.GetPoint(d) : ray.GetPoint(cam.farClipPlane);
                    x0 = Mathf.Min(x0, p.x);
                    x1 = Mathf.Max(x1, p.x);
                    z0 = Mathf.Min(z0, p.z);
                    z1 = Mathf.Max(z1, p.z);
                }
                // Rain falls from above: let areas just outside the view show too.
                return Rect.MinMaxRect(x0 - 4f, z0 - 4f, x1 + 4f, z1 + 8f);
            }
        }
    }

    /// <summary>Gives each WeatherView its copy manager when the game first updates it.</summary>
    [HarmonyPatch(typeof(WeatherView), "UpdateData", typeof(WeatherView.ViewData))] // exact signature: the base class has an overload
    internal static class WeatherViewPatch
    {
        private static void Postfix(WeatherView __instance)
        {
            if (__instance != null && __instance.GetComponent<WeatherTiler.Copies>() == null)
            {
                __instance.gameObject.AddComponent<WeatherTiler.Copies>().Init(__instance);
            }
        }
    }
}
