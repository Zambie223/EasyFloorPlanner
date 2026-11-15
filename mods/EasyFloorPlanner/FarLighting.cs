using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Night lighting for floor beyond the 32 x 32 tiles the game's lighting map covers. Local to
    /// each machine (purely visual).
    ///
    /// Measured in-game with the self-test's lighting probe (2026-11-13):
    ///   - map pixel p lights world tile p - 16 (RoomLayoutMapPatch keeps the map there);
    ///   - the shaders sample with the map's own wrap mode: a Repeat map gives tiles 32 apart the same pixel;
    ///   - a renderer's MaterialPropertyBlock can override _RoomLayout for that renderer alone.
    ///
    /// So every renderer reaching past the map gets its own 32 x 32 Repeat map: the window of 32 x
    /// 32 tiles centred on that renderer. Every point of a renderer smaller than 32 tiles then
    /// reads its own tile. Larger renderers get a rule instead: the restaurant's own geometry
    /// (floors and walls; LayoutView children) is lit on any tile column that's indoors somewhere
    /// under it, and anything else (the setting's ground plane) is outdoors, which is only wrong
    /// under floors, where it can't be seen. Renderers are rechecked a few times a second, so
    /// chefs and customers walking into far rooms follow; renderers coming back inside get the
    /// global map again. Restaurants that fit the map cost nothing (no scanning).
    ///
    /// Weather (rain, snow...) is one particle effect that WeatherView hands its own copy of the
    /// global map. It's left alone: measured in the self-test (2026-11-13), it only spawns within a
    /// fixed area around the original restaurant, which the origin-centred global map covers, so
    /// it never falls inside far rooms; far areas simply get no weather. A camera-centred map was
    /// tried and dropped: with the camera far from the origin it mis-masked the area that does get rain.
    /// </summary>
    internal static class FarLighting
    {
        private const int Size = RoomLayoutMapPatch.Size;
        private const int First = RoomLayoutMapPatch.First;
        private const float ScanInterval = 0.25f;

        /// <summary>Renderers up to this size (tiles, each axis) get an exact window.</summary>
        private const float WindowFits = Size - 2f;

        private static readonly int RoomLayoutId = Shader.PropertyToID("_RoomLayout");

        private static LayoutBlueprint Layout;
        private static bool Needed;
        private static GameObject DriverObject;
        private static float NextScan;
        private static readonly Dictionary<long, Texture2D> Windows = new Dictionary<long, Texture2D>();
        private static readonly Dictionary<long, Texture2D> Unions = new Dictionary<long, Texture2D>();
        private static Texture2D AllOutdoor;
        /// <summary>Renderers we've given a map, and whether they already had a property block of their own then.</summary>
        private static readonly Dictionary<Renderer, bool> Touched = new Dictionary<Renderer, bool>();
        private static readonly Dictionary<Renderer, bool> IsLayoutGeometry = new Dictionary<Renderer, bool>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        /// <summary>Renderers given their own map in the last scan (for logging and the self-test).</summary>
        public static int LastOverridden { get; private set; }

        public static int LastOversized { get; private set; }

        /// <summary>Self-test: stop overriding (renderers go back to the global map) until resumed.</summary>
        public static bool Suspended { get; set; }

        /// <summary>Called whenever the lighting map is rebuilt (RoomLayoutMapPatch).</summary>
        public static void SetLayout(LayoutBlueprint bp)
        {
            Layout = new LayoutBlueprint(bp);
            ClearCaches();
            Needed = false;
            foreach (KeyValuePair<LayoutPosition, Room> t in Layout.Tiles)
            {
                if (LayoutHelpers.IsInside(t.Value) && (t.Key.x < First || t.Key.x >= First + Size || t.Key.y < First || t.Key.y >= First + Size))
                {
                    Needed = true;
                    break;
                }
            }
            if ((Needed || Touched.Count > 0) && DriverObject == null)
            {
                DriverObject = new GameObject("EasyFloorPlanner Far Lighting");
                Object.DontDestroyOnLoad(DriverObject);
                DriverObject.AddComponent<Driver>();
            }
            NextScan = 0f;
            if (Needed)
            {
                ModMain.Log("The restaurant reaches past the game's lighting map; lighting far objects individually.");
            }
        }

        /// <summary>Rechecks every renderer now (normally every ScanInterval).</summary>
        public static void Scan()
        {
            Texture global = Shader.GetGlobalTexture(RoomLayoutId);
            bool active = Needed && !Suspended && Layout != null;
            int overridden = 0, oversized = 0;
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                Bounds b = r.bounds;
                bool beyond = active && (b.min.x < First - 0.5f || b.max.x > First + Size - 0.5f || b.min.z < First - 0.5f || b.max.z > First + Size - 0.5f);
                if (beyond)
                {
                    bool big = b.size.x > WindowFits || b.size.z > WindowFits;
                    if (!Touched.ContainsKey(r))
                    {
                        r.GetPropertyBlock(Block);
                        Touched[r] = !Block.isEmpty;
                    }
                    Assign(r, big ? BigRendererMap(r, b) : Window(Mathf.RoundToInt(b.center.x), Mathf.RoundToInt(b.center.z)));
                    overridden++;
                    oversized += big ? 1 : 0;
                }
                else if (Touched.TryGetValue(r, out bool hadOwnBlock))
                {
                    // Back inside the map (or nothing to do any more): the shared map again. The game
                    // never uses property blocks, so a renderer that had none is simply cleared; one
                    // with another mod's block keeps it, with the shared map put back in.
                    if (hadOwnBlock)
                    {
                        Assign(r, global);
                    }
                    else
                    {
                        r.SetPropertyBlock(null);
                    }
                    Touched.Remove(r);
                }
            }
            foreach (Renderer gone in Touched.Keys.Where(r => r == null).ToList())
            {
                Touched.Remove(gone);
            }
            LastOverridden = overridden;
            LastOversized = oversized;
            if (!Needed && Touched.Count == 0 && DriverObject != null)
            {
                Object.Destroy(DriverObject);
                DriverObject = null;
            }
        }

        private static void Assign(Renderer r, Texture map)
        {
            r.GetPropertyBlock(Block);
            if (Block.GetTexture(RoomLayoutId) == map)
            {
                return;
            }
            // Keeps whatever else the game put in this renderer's block.
            Block.SetTexture(RoomLayoutId, map);
            r.SetPropertyBlock(Block);
        }

        private static Texture BigRendererMap(Renderer r, Bounds b)
        {
            if (!IsLayoutGeometry.TryGetValue(r, out bool layout))
            {
                layout = r.GetComponentInParent<LayoutView>() != null;
                IsLayoutGeometry[r] = layout;
            }
            if (!layout)
            {
                return AllOutdoor ?? (AllOutdoor = Build((x, z) => Room.Null));
            }
            // Floors and walls bigger than a window: lit on any tile column that's indoors under them.
            int x0 = Mathf.FloorToInt(b.min.x + 0.5f), x1 = Mathf.CeilToInt(b.max.x - 0.5f);
            int z0 = Mathf.FloorToInt(b.min.z + 0.5f), z1 = Mathf.CeilToInt(b.max.z - 0.5f);
            long key = Key(x0, z0) ^ (Key(x1, z1) * 31);
            if (Unions.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }
            var rooms = new Room?[Size, Size];
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    Room here = RoomAt(x, z);
                    int px = Mod(x - First, Size), py = Mod(z - First, Size);
                    if (LayoutHelpers.IsInside(here) && rooms[px, py] == null)
                    {
                        rooms[px, py] = here;
                    }
                }
            }
            Texture2D t = Build((px, py) => rooms[px, py] ?? Room.Null);
            Unions[key] = t;
            return t;
        }

        /// <summary>The 32 x 32 tiles centred on (cx, cz), arranged so the shaders' wrapped read finds each tile's own pixel.</summary>
        private static Texture2D Window(int cx, int cz)
        {
            long key = Key(cx, cz);
            if (Windows.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }
            if (Windows.Count > 1024)
            {
                ClearWindows();
            }
            int baseX = cx - Size / 2, baseZ = cz - Size / 2;
            Texture2D t = Build((px, py) => RoomAt(baseX + Mod(px + First - baseX, Size), baseZ + Mod(py + First - baseZ, Size)));
            Windows[key] = t;
            return t;
        }

        /// <summary>The room at a tile, as vanilla's map generator reads it (outdoors where there's no tile).</summary>
        private static Room RoomAt(int x, int z) => Layout[new Vector2(x, z)];

        /// <summary>A Repeat map in vanilla's pixel encoding (pixel p stands for tiles p + First + 32k).</summary>
        private static Texture2D Build(System.Func<int, int, Room> roomAt)
        {
            var t = new Texture2D(Size, Size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[Size * Size];
            for (int px = 0; px < Size; px++)
            {
                for (int py = 0; py < Size; py++)
                {
                    pixels[py * Size + px] = RoomLayoutMapPatch.Encode(roomAt(px, py));
                }
            }
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        private static int Mod(int a, int m) => ((a % m) + m) % m;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static void ClearWindows()
        {
            foreach (Texture2D t in Windows.Values)
            {
                Object.Destroy(t);
            }
            Windows.Clear();
        }

        private static void ClearCaches()
        {
            ClearWindows();
            foreach (Texture2D t in Unions.Values)
            {
                Object.Destroy(t);
            }
            Unions.Clear();
            IsLayoutGeometry.Clear();
            // AllOutdoor doesn't depend on the layout. Renderers holding old windows are re-pointed
            // at new ones by the next scan (the textures differ).
        }

        private class Driver : MonoBehaviour
        {
            private void Update()
            {
                if (Time.unscaledTime < NextScan)
                {
                    return;
                }
                NextScan = Time.unscaledTime + ScanInterval;
                try
                {
                    Scan();
                }
                catch (System.Exception e)
                {
                    ModMain.LogWarning($"Far lighting scan failed: {e.Message}");
                    NextScan = Time.unscaledTime + 5f;
                }
            }
        }
    }
}
