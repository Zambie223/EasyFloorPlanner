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
    /// So every renderer reaching past the map gets its own 32 x 32 Repeat map: a window of 32 x 32
    /// tiles that contains the renderer, so every point of it reads its own tile. Renderers with
    /// only plain outdoor ground under them all share one all-outdoor map, and the others share
    /// windows centred on an 8-tile grid (falling back to one centred on the renderer if it
    /// doesn't fit), so a big restaurant needs a few dozen maps rather than one per tile.
    /// (Before 2026-11-19 each renderer got a window centred on its own tile; in a big restaurant
    /// that could overflow the cache, which was then cleared and rebuilt.) Larger renderers get a rule instead: the restaurant's own geometry
    /// (floors and walls; LayoutView children) is lit on any tile column that's indoors somewhere
    /// under it, and anything else (the setting's ground plane) is outdoors, which is only wrong
    /// under floors, where it can't be seen. Renderers are rechecked a few times a second, so
    /// chefs and customers walking into far rooms follow; renderers coming back inside get the
    /// global map again. Restaurants that fit the map cost nothing (no scanning).
    ///
    /// Cost (2026-11-19, measured in a 972-tile restaurant with 7,045 renderers): one pass over
    /// every renderer took 10 ms (up to 19 ms), four times a second, which showed as a hitch. Now
    /// the renderer list is fetched once a second, a slice of it is processed each frame (the whole
    /// list every <see cref="SliceFrames"/> frames), and a renderer whose map hasn't changed isn't
    /// touched again (re-applied every <see cref="ReassertCycles"/> passes, in case something
    /// replaced its property block).
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
        /// <summary>Seconds between fetching the list of renderers (finds new ones).</summary>
        private const float DiscoverInterval = 1f;

        /// <summary>Frames per pass over the list (each frame does 1 / SliceFrames of it).</summary>
        private const int SliceFrames = 15;

        /// <summary>Passes after which an unchanged renderer's map is applied again anyway.</summary>
        private const int ReassertCycles = 20;

        /// <summary>Window centres snap to this grid, so nearby renderers share a map.</summary>
        private const int WindowGrid = 8;

        /// <summary>Seconds after a layout change at which the scan timings are logged once.</summary>
        private const float ReportAfter = 30f;

        /// <summary>Renderers up to this size (tiles, each axis) get an exact window.</summary>
        private const float WindowFits = Size - 2f;

        private static readonly int RoomLayoutId = Shader.PropertyToID("_RoomLayout");

        private static LayoutBlueprint Layout;
        private static bool Needed;
        private static GameObject DriverObject;
        private static float NextDiscover;
        private static Renderer[] List = new Renderer[0];
        private static int Cursor, Cycle, CycleOverridden, CycleOversized;
        private static readonly Dictionary<long, Texture2D> Windows = new Dictionary<long, Texture2D>();
        private static readonly Dictionary<long, Texture2D> Unions = new Dictionary<long, Texture2D>();
        private static Texture2D AllOutdoor;
        private class State
        {
            /// <summary>Whether the renderer already had a property block of its own when we first gave it a map.</summary>
            public bool HadOwnBlock;

            /// <summary>The map last applied, and the pass it was applied in.</summary>
            public Texture Map;
            public int Cycle;
        }

        /// <summary>Renderers we've given a map.</summary>
        private static readonly Dictionary<Renderer, State> Touched = new Dictionary<Renderer, State>();
        private static readonly Dictionary<Renderer, bool> IsLayoutGeometry = new Dictionary<Renderer, bool>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        /// <summary>
        /// Prefix sums over the layout's stored tiles that don't read as plain outdoor ground, so
        /// "is anything but plain ground under this renderer" is O(1).
        /// </summary>
        private static int[] Marked;
        private static int MarkX0, MarkZ0, MarkW, MarkH;

        // Timings since the last layout change, logged once (ReportAfter).
        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();
        private static float LayoutSetAt;
        private static bool Reported;
        private static int Frames, Discovers, MapsBuilt, CacheClears;
        private static double FrameMsTotal, FrameMsMax, DiscoverMsTotal, DiscoverMsMax;

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
            BuildMarked();
            Frames = Discovers = MapsBuilt = CacheClears = 0;
            FrameMsTotal = FrameMsMax = DiscoverMsTotal = DiscoverMsMax = 0;
            Reported = false;
            LayoutSetAt = Time.unscaledTime;
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
            NextDiscover = 0f;
            if (Needed)
            {
                ModMain.Log("The restaurant reaches past the game's lighting map; lighting far objects individually.");
            }
        }

        /// <summary>Rechecks every renderer now, in one go (the self-test; normally spread over frames by the driver).</summary>
        public static void Scan()
        {
            Discover();
            Cursor = 0;
            Process(0, List.Length, Cycle);
            EndCycle();
        }

        /// <summary>One frame's share: the renderer list once a second, and a slice of it every frame.</summary>
        private static void Tick()
        {
            Watch.Restart();
            bool discovered = false;
            if (Time.unscaledTime >= NextDiscover)
            {
                NextDiscover = Time.unscaledTime + DiscoverInterval;
                Discover();
                discovered = true;
            }
            double discoverMs = Watch.Elapsed.TotalMilliseconds;
            int n = List.Length;
            if (n > 0)
            {
                Cursor = System.Math.Min(Cursor, n);
                int end = System.Math.Min(Cursor + (n + SliceFrames - 1) / SliceFrames, n);
                Process(Cursor, end, Cycle);
                Cursor = end;
                if (Cursor >= n)
                {
                    Cursor = 0;
                    EndCycle();
                }
            }
            Watch.Stop();
            if (Needed && !Suspended)
            {
                double ms = Watch.Elapsed.TotalMilliseconds;
                Frames++;
                FrameMsTotal += ms;
                FrameMsMax = System.Math.Max(FrameMsMax, ms);
                if (discovered)
                {
                    Discovers++;
                    DiscoverMsTotal += discoverMs;
                    DiscoverMsMax = System.Math.Max(DiscoverMsMax, discoverMs);
                }
                if (!Reported && Time.unscaledTime - LayoutSetAt >= ReportAfter && Discovers > 0)
                {
                    Reported = true;
                    ModMain.Log($"Far lighting: {List.Length} renderers, {LastOverridden} given their own map; "
                                + $"{Windows.Count + Unions.Count} maps in use, {MapsBuilt} built, cache cleared {CacheClears} time(s); "
                                + $"{FrameMsTotal / Frames:0.00} ms per frame on average, {FrameMsMax:0.0} ms at most "
                                + $"(of which fetching the renderer list, once a second: {DiscoverMsTotal / Discovers:0.0} ms on average, {DiscoverMsMax:0.0} at most); "
                                + $"every renderer rechecked every {SliceFrames} frames ({Frames} frames).");
                }
            }
        }

        private static void Discover()
        {
            List = Object.FindObjectsOfType<Renderer>();
            foreach (Renderer gone in Touched.Keys.Where(r => r == null).ToList())
            {
                Touched.Remove(gone);
            }
        }

        /// <summary>Gives renderers List[from..to) the map they need. <paramref name="cycle"/> is the pass number used to decide when to re-apply.</summary>
        private static void Process(int from, int to, int cycle)
        {
            Texture global = Shader.GetGlobalTexture(RoomLayoutId);
            bool active = Needed && !Suspended && Layout != null;
            for (int i = from; i < to; i++)
            {
                Renderer r = List[i];
                if (r == null)
                {
                    continue; // destroyed since the list was fetched
                }
                Bounds b = r.bounds;
                bool beyond = active && (b.min.x < First - 0.5f || b.max.x > First + Size - 0.5f || b.min.z < First - 0.5f || b.max.z > First + Size - 0.5f);
                if (beyond)
                {
                    bool big = b.size.x > WindowFits || b.size.z > WindowFits;
                    if (!Touched.TryGetValue(r, out State state))
                    {
                        r.GetPropertyBlock(Block);
                        state = new State { HadOwnBlock = !Block.isEmpty, Cycle = int.MinValue / 2 };
                        Touched[r] = state;
                    }
                    Texture map = big ? BigRendererMap(r, b) : SmallRendererMap(b);
                    if (!ReferenceEquals(state.Map, map) || cycle - state.Cycle >= ReassertCycles)
                    {
                        Assign(r, map);
                        state.Map = map;
                        state.Cycle = cycle;
                    }
                    CycleOverridden++;
                    CycleOversized += big ? 1 : 0;
                }
                else if (Touched.TryGetValue(r, out State state))
                {
                    // Back inside the map (or nothing to do any more): the shared map again. The game
                    // never uses property blocks, so a renderer that had none is simply cleared; one
                    // with another mod's block keeps it, with the shared map put back in.
                    if (state.HadOwnBlock)
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
        }

        private static void EndCycle()
        {
            LastOverridden = CycleOverridden;
            LastOversized = CycleOversized;
            CycleOverridden = CycleOversized = 0;
            Cycle++;
            if (!Needed && Touched.Count == 0 && DriverObject != null)
            {
                Object.Destroy(DriverObject);
                DriverObject = null;
                List = new Renderer[0];
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

        /// <summary>
        /// A map that's exact for every tile under a renderer smaller than a window: the shared
        /// all-outdoor map if only plain ground is under it, else the window on the 8-tile grid
        /// nearest its centre if that contains it, else the window centred on it.
        /// </summary>
        private static Texture SmallRendererMap(Bounds b)
        {
            int x0 = Mathf.FloorToInt(b.min.x + 0.5f), x1 = Mathf.CeilToInt(b.max.x - 0.5f);
            int z0 = Mathf.FloorToInt(b.min.z + 0.5f), z1 = Mathf.CeilToInt(b.max.z - 0.5f);
            if (!AnyMarked(x0, z0, x1, z1))
            {
                return AllOutdoor ?? (AllOutdoor = Build((x, z) => Room.Null));
            }
            int cx = Mathf.RoundToInt(b.center.x / WindowGrid) * WindowGrid;
            int cz = Mathf.RoundToInt(b.center.z / WindowGrid) * WindowGrid;
            if (!Covers(cx, x0, x1) || !Covers(cz, z0, z1))
            {
                cx = Mathf.RoundToInt(b.center.x);
                cz = Mathf.RoundToInt(b.center.z);
            }
            return Window(cx, cz);
        }

        /// <summary>Whether the window centred on c (tiles c - 16 .. c + 15) covers tiles lo .. hi.</summary>
        private static bool Covers(int c, int lo, int hi) => lo >= c - Size / 2 && hi <= c + Size / 2 - 1;

        private static void BuildMarked()
        {
            Marked = null;
            if (Layout.Tiles.Count == 0)
            {
                return;
            }
            Color plain = RoomLayoutMapPatch.Encode(Room.Null);
            MarkX0 = Layout.Tiles.Keys.Min(t => t.x);
            MarkZ0 = Layout.Tiles.Keys.Min(t => t.y);
            MarkW = Layout.Tiles.Keys.Max(t => t.x) - MarkX0 + 1;
            MarkH = Layout.Tiles.Keys.Max(t => t.y) - MarkZ0 + 1;
            var grid = new int[MarkW, MarkH];
            foreach (KeyValuePair<LayoutPosition, Room> t in Layout.Tiles)
            {
                if (RoomLayoutMapPatch.Encode(t.Value) != plain)
                {
                    grid[t.Key.x - MarkX0, t.Key.y - MarkZ0] = 1;
                }
            }
            int h = MarkH + 1;
            Marked = new int[(MarkW + 1) * h];
            for (int x = 0; x < MarkW; x++)
            {
                for (int z = 0; z < MarkH; z++)
                {
                    Marked[(x + 1) * h + z + 1] = grid[x, z] + Marked[x * h + z + 1] + Marked[(x + 1) * h + z] - Marked[x * h + z];
                }
            }
        }

        /// <summary>Whether any tile in x0..x1, z0..z1 reads as something other than plain outdoor ground.</summary>
        private static bool AnyMarked(int x0, int z0, int x1, int z1)
        {
            if (Marked == null)
            {
                return false;
            }
            int a = System.Math.Max(x0 - MarkX0, 0), b = System.Math.Min(x1 - MarkX0, MarkW - 1);
            int c = System.Math.Max(z0 - MarkZ0, 0), d = System.Math.Min(z1 - MarkZ0, MarkH - 1);
            if (a > b || c > d)
            {
                return false;
            }
            int h = MarkH + 1;
            return Marked[(b + 1) * h + d + 1] - Marked[a * h + d + 1] - Marked[(b + 1) * h + c] + Marked[a * h + c] > 0;
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
                CacheClears++;
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
            MapsBuilt++;
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
                if (Time.unscaledTime < NextDiscover - DiscoverInterval)
                {
                    return; // backing off after an error
                }
                try
                {
                    Tick();
                }
                catch (System.Exception e)
                {
                    ModMain.LogWarning($"Far lighting failed: {e.Message}");
                    NextDiscover = Time.unscaledTime + DiscoverInterval + 5f;
                }
            }
        }
    }
}
