#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;
using KitchenMods;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// DEBUG BUILDS ONLY. Drives the self-test (see SelfTest):
    ///   1. In HQ, start a random restaurant with vanilla's own BeginRestaurant.InitialiseRandomRestaurant.
    ///   2. Once in the kitchen at night, run scripted layout edits. After each one, check the data,
    ///      the rebuilt view and the navmesh, and save a screenshot.
    ///   3. Log a PASS/FAIL summary, then optionally quit.
    /// All output lines start with "[Easy Floor Planner] [SELFTEST]" in Player.log.
    /// State is static so it survives this system being recreated across scene changes.
    /// </summary>
    [AlwaysUpdateSystem]
    public class SelfTestDriver : GenericSystemBase, IModSystem
    {
        private enum Stage { WaitHq, WaitKitchen, Settle, Steps, WaitReload, Done }

        private static Stage _stage = Stage.WaitHq;
        private static float _waitUntil = -1f;
        private static int _step;
        private static int _pass, _fail;
        private static GameObject _runner;

        // Step timing: a step can ask to be rerun (polling for something) with Retry().
        private static float? _retryAfter;
        private static float _stepStartedAt = -1f;
        private static float _reloadEarliest;
        private static int _loggedStep = -1;
        private static float StepElapsed => UnityEngine.Time.realtimeSinceStartup - _stepStartedAt;
        private static void Retry(float seconds) => _retryAfter = seconds;

        // Shared between steps.
        private static LayoutBlueprint _baseline;
        // Grown strip: (source tile inside the room, new tile outside), applied as one edit.
        private static List<(LayoutPosition src, LayoutPosition dst)> _grown = new List<(LayoutPosition, LayoutPosition)>();
        private static int _growRoomId;
        private static Bounds _baselineBounds;
        private static LayoutPosition _edgeA, _edgeB;
        private static float _pathBeforeDoor;
        private static bool? _vanillaDoorsWalkable;
        private static bool _haveGrow, _haveEdge, _haveFar;
        // Far strip beyond vanilla's old navmesh edge, ordered from the building outward.
        private static List<(LayoutPosition src, LayoutPosition dst)> _far = new List<(LayoutPosition, LayoutPosition)>();

        private const float StepGap = 3f;

        protected override void OnUpdate()
        {
            if (!SelfTest.Active || _stage == Stage.Done)
            {
                return;
            }
            // Other mods may patch LayoutView at any point (and again after scene changes);
            // keep them off for the whole session so results reflect this mod alone.
            try
            {
                SelfTest.IsolateFromOtherMods();
            }
            catch (Exception e)
            {
                Fail($"isolation from other mods threw: {e.Message}");
            }
            float now = UnityEngine.Time.realtimeSinceStartup;

            switch (_stage)
            {
                case Stage.WaitHq:
                    if (GameInfo.CurrentScene == SceneType.Kitchen)
                    {
                        Log("already in a kitchen; skipping restaurant start.");
                        _stage = Stage.WaitKitchen;
                        return;
                    }
                    if (GameInfo.CurrentScene != SceneType.Franchise || BeginRestaurant.Instance == null)
                    {
                        return;
                    }
                    if (_waitUntil < 0f)
                    {
                        _waitUntil = now + 5f; // Let HQ finish spawning its layout and dish items.
                        Log("in HQ; starting a random restaurant in 5s.");
                        return;
                    }
                    if (now < _waitUntil)
                    {
                        return;
                    }
                    if (SelfTest.ForceSetting != null && !_settingForced)
                    {
                        _settingForced = true;
                        if (ForceSetting(SelfTest.ForceSetting))
                        {
                            _waitUntil = now + 5f; // let HandleLayoutRequests regenerate the maps
                            return;
                        }
                    }
                    _runner = new GameObject("EasyFloorPlanner SelfTest Runner");
                    UnityEngine.Object.DontDestroyOnLoad(_runner);
                    _runner.AddComponent<CoroutineRunner>().StartCoroutine(BeginRestaurant.Instance.InitialiseRandomRestaurant());
                    Log("InitialiseRandomRestaurant started.");
                    _stage = Stage.WaitKitchen;
                    _waitUntil = now + 120f;
                    return;

                case Stage.WaitKitchen:
                    if (GameInfo.CurrentScene == SceneType.Kitchen && Has<SKitchenMarker>() && HasSingleton<SLayout>() && Has<SIsNightTime>())
                    {
                        Log("in kitchen at night; settling for 8s.");
                        _stage = Stage.Settle;
                        _waitUntil = now + 8f;
                    }
                    else if (now > _waitUntil)
                    {
                        Fail("timed out waiting for kitchen at night");
                        Finish();
                    }
                    return;

                case Stage.Settle:
                    if (now >= _waitUntil)
                    {
                        _stage = Stage.Steps;
                        _waitUntil = now;
                    }
                    return;

                case Stage.Steps:
                    if (now < _waitUntil)
                    {
                        return;
                    }
                    _retryAfter = null;
                    if (_stepStartedAt < 0f)
                    {
                        _stepStartedAt = now;
                    }
                    RunStep(_step);
                    if (_stage == Stage.Steps && _retryAfter.HasValue)
                    {
                        _waitUntil = now + _retryAfter.Value;
                        return;
                    }
                    _step++;
                    _stepStartedAt = -1f;
                    if (_stage == Stage.Steps)
                    {
                        // A step that switched stage (the reload) has set its own deadline.
                        _waitUntil = now + StepGap;
                    }
                    return;

                case Stage.WaitReload:
                    // CurrentScene stays Kitchen through a reload, so also wait for the transition to
                    // finish and a freshly built floorplan view.
                    if (now > _reloadEarliest && GameInfo.CurrentScene == SceneType.Kitchen && !HasSingleton<SPerformSceneTransition>()
                        && HasSingleton<SLayout>() && Has<SIsNightTime>() && LayoutViews().Any(v => v.IsInitialised))
                    {
                        Log("reload finished; settling for 5s.");
                        _stage = Stage.Settle;
                        _waitUntil = now + 5f;
                    }
                    else if (now > _waitUntil)
                    {
                        Fail("timed out waiting for the reload");
                        Finish();
                    }
                    return;
            }
        }

        private bool _settingForced;

        /// <summary>
        /// Selects a restaurant setting in HQ the way vanilla's SelectSetting does (without saving it
        /// as the player's last choice), and asks HQ to regenerate its layout maps, since a map's
        /// decorations are baked in for the setting it was generated with.
        /// </summary>
        private bool ForceSetting(string name)
        {
            KitchenData.RestaurantSetting match = KitchenData.GameData.Main.Get<KitchenData.RestaurantSetting>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase))
                ?? KitchenData.GameData.Main.Get<KitchenData.RestaurantSetting>()
                .FirstOrDefault(s => (s.name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null)
            {
                Fail($"setting '{name}' not found; settings: {string.Join(", ", KitchenData.GameData.Main.Get<KitchenData.RestaurantSetting>().Select(s => $"{s.name} ({s.Name})"))}");
                return false;
            }
            EntityQuery selectors = GetEntityQuery(typeof(CSettingSelector));
            if (selectors.IsEmpty)
            {
                Fail("no setting selector in HQ; can't force the setting");
                return false;
            }
            Entity selector = selectors.First();
            EntityManager.SetComponentData(selector, new CSettingSelector { SettingID = match.ID });
            Set(default(HandleLayoutRequests.SLayoutRequest));
            Log($"forced setting {match.name} ({match.Name}, id {match.ID}); regenerating HQ layout maps.");
            return true;
        }

        private void RunStep(int index)
        {
            Action[] steps =
            {
                StepBaseline,
                StepGrow, StepVerifyGrow,
                StepEdgePick, StepVerifyEdge,
                StepEdgeCycle, StepVerifyEdge,
                StepEdgeCycle, StepVerifyEdge,
                StepShrinkBack, StepVerifyShrink,
                StepGrowFar, StepVerifyGrowFar,
                StepShrinkFar, StepVerifyShrinkFar,
                StepGrowWest, StepVerifyGrowWest, StepShrinkWest, StepVerifyShrinkWest,
                StepBulldozeScenery, StepVerifyBulldoze, StepShrinkBulldoze,
                StepRefusals,
                StepRoundTrip,
                StepEditTerminal, StepEditTerminalRelocated, StepEditOpen, StepEditVerifyOpen,
                StepEditPan, StepEditVerifyPan, StepEditBox, StepEditRemoveNewRoom, StepEditPaint,
                StepEditRejectTampered, StepEditVerifyRejected,
                StepSpectateOpen, StepSpectateVerify,
                StepSpectateFollow, StepSpectateVerifyFollow, StepSpectateVerifyUndo,
                StepSpectateTamper, StepSpectateVerifyIgnored,
                StepSpectateStop, StepSpectateVerifyStopped,
                StepSpectateOpen, StepSpectateVerifyLateJoin,
                StepEditApply, StepEditVerifyApplied,
                StepLightingProbe,
                StepGrowFarWest, StepVerifyFarLighting,
                StepWeatherForce, StepRainMeasure, StepWeatherShot, StepWeatherOff,
                StepShrinkFarWest,
                StepAreaKeptAfterResize,
                StepAreaReopen, StepAreaVerifyReopened, StepAreaReset, StepAreaVerifyReset,
                StepOccupiedSetup, StepOccupiedGrouped, StepOccupiedEdit, StepOccupiedVerify, StepOccupiedRestore, StepOccupiedVerifyRestored,
                StepWallMountedSetup, StepWallMountedSpawned, StepWallMountedEdit, StepWallMountedVerify, StepWallMountedRestore, StepWallMountedVerifyRestored,
                StepPlayCamera, StepPlayCameraZoom, StepPlayCameraPan, StepPlayCameraFollow, StepPlayCameraReset,
                StepCameraOtherUiOpen, StepCameraOtherUiClosed,
                StepCameraControlsPause, StepCameraControlsOpen, StepCameraControlsPages,
                StepPrepareDayLayout, StepVerifyDayLayout,
                StepSaveToMemory, StepVerifySaved,
                StepReload, StepVerifyReload,
                StepStartDay, StepWaitForDay,
                StepWatchCustomers,
                StepWatchIndicator,
            };
            if (index >= steps.Length)
            {
                Finish();
                return;
            }
            _occupancy = null;
            try
            {
                if (_loggedStep != index)
                {
                    _loggedStep = index;
                    Log($"--- step {index}: {steps[index].Method.Name}");
                }
                steps[index]();
            }
            catch (Exception e)
            {
                Fail($"step {steps[index].Method.Name} threw: {e}");
            }
        }

        // ---------------- steps ----------------

        private void StepBaseline()
        {
            _baseline = ReadLayout();
            Log($"baseline: {_baseline.Tiles.Count} tiles, {_baseline.Features.Count} features, {_baseline.Rooms().Count} rooms.");
            Log(DumpGrid(_baseline));
            Check(LayoutWriter.IsAvailable, "GetReachability reflection resolved");
            Check(LayoutEdits.CheckConnectivity(_baseline) == null, "vanilla layout passes our connectivity rule");
            Check(LayoutViews().Length == 1, $"exactly one LayoutView before edits (found {LayoutViews().Length})");
            // Validates our copy of the decorators' formulas against what vanilla placed: on a fresh
            // run the load check must have found nothing to change.
            (int removed, int added)? load = StreetPiecesLoadCheck.LastResult;
            Check(load.HasValue && load.Value.removed == 0 && load.Value.added == 0,
                $"street-front load check ran and changed nothing on a fresh run ({(load.HasValue ? $"removed {load.Value.removed}, added {load.Value.added}" : "didn't run")}; setting manages: {StreetPieces.TestDescribe(EntityManager)})");
            CheckStreetPieces(CurrentBounds(), "before any edit");

            // Are vanilla's own internal doors walkable on the navmesh? This is the reference for
            // judging doors we add.
            foreach (Feature f in _baseline.Features.Where(f => f.Type == FeatureType.Door))
            {
                float len = PathLength(f.Tile1, f.Tile2);
                Log($"vanilla door {Fmt(f.Tile1)}|{Fmt(f.Tile2)}: navmesh path length {len:0.0}");
                bool walkable = len > 0f && len < 2.5f;
                _vanillaDoorsWalkable = (_vanillaDoorsWalkable ?? true) && walkable;
            }
            Shot("00-baseline");
        }

        /// <summary>
        /// Grows a strip of up to 3 new tiles beyond the stored grid (vanilla stores no outdoor
        /// tiles), so the bounds and camera markers have to move. The back of the restaurant is
        /// tried first, then the sides. The street side is refused by design.
        /// </summary>
        private void StepGrow()
        {
            LayoutBlueprint bp = ReadLayout();
            _baselineBounds = CurrentBounds();
            LayoutPosition[] preference = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            foreach (LayoutPosition d in preference)
            {
                LayoutPosition perp = new LayoutPosition(d.y, d.x);
                IEnumerable<LayoutPosition> edge = bp.Tiles
                    .Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden && !bp.Tiles.ContainsKey(t.Key + d))
                    .Select(t => t.Key)
                    .OrderBy(p => p.x).ThenBy(p => p.y);
                foreach (LayoutPosition s in edge)
                {
                    LayoutBlueprint trial = new LayoutBlueprint(bp);
                    var strip = new List<(LayoutPosition, LayoutPosition)>();
                    foreach (LayoutPosition src in new[] { s, s + perp, s - perp })
                    {
                        if (bp[src].ID != bp[s].ID || bp.Tiles.ContainsKey(src + d))
                        {
                            continue;
                        }
                        if (LayoutEdits.TogglePaint(trial, src, src + d, BlocksEdit) == null)
                        {
                            strip.Add((src, src + d));
                        }
                    }
                    if (strip.Count == 0)
                    {
                        continue;
                    }
                    _grown = strip;
                    _growRoomId = bp[s].ID;
                    Log($"growing {strip.Count} tile(s) in direction {Fmt(d)}: {string.Join(" ", strip.Select(x => Fmt(x.Item2)))}");
                    Apply(trial);
                    _haveGrow = true;
                    foreach ((LayoutPosition _, LayoutPosition dst) in _grown)
                    {
                        CLayoutRoomTile tile = TileManager.GetTile((Vector3)dst);
                        Check(tile.RoomID == _growRoomId && LayoutHelpers.IsInside(tile.Type), $"buffer: new tile {Fmt(dst)} belongs to the source room");
                    }
                    Check(ReadLayout().Tiles.Count == _baseline.Tiles.Count + _grown.Count, "buffer: tile count grew by the strip size");
                    Bounds now = CurrentBounds();
                    Check(now != _baselineBounds, $"CBounds changed ({_baselineBounds.center}/{_baselineBounds.size} -> {now.center}/{now.size})");
                    Check(LayoutWriter.LastBoundsSync.movedDayPoints == 4, $"moved 4 day-camera markers (moved {LayoutWriter.LastBoundsSync.movedDayPoints})");
                    Check(LayoutWriter.LastBoundsSync.removedNightPoints == 4, $"removed 4 night-camera markers for vanilla to recreate (removed {LayoutWriter.LastBoundsSync.removedNightPoints})");
                    return;
                }
            }
            Fail("no edge accepted a grow in any allowed direction");
        }

        private void StepVerifyGrow()
        {
            if (!_haveGrow)
            {
                return;
            }
            LayoutView[] views = LayoutViews();
            Check(views.Length == 1, $"exactly one LayoutView after rebuild (found {views.Length})");
            LayoutView v = views.FirstOrDefault();
            Check(v != null && v.IsInitialised && v.Builder != null, "rebuilt LayoutView is initialised");
            foreach ((LayoutPosition src, LayoutPosition dst) in _grown)
            {
                if (v?.Builder != null)
                {
                    Check(v.Builder.Blueprint[dst].ID == _growRoomId, $"rebuilt view geometry includes {Fmt(dst)}");
                }
                Check(NavMesh.SamplePosition((Vector3)dst, out NavMeshHit hit, 0.3f, NavMesh.AllAreas), $"navmesh covers {Fmt(dst)}");
                Check(TileManager.CanReach((Vector3)src, (Vector3)dst), $"TileManager.CanReach {Fmt(src)} -> {Fmt(dst)}");
            }
            CheckCameraMarkers(CurrentBounds(), "after grow");
            Shot("01-grown");
        }

        private void CheckCameraMarkers(Bounds b, string when)
        {
            EntityQuery day = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CMaintainInView>(), ComponentType.ReadOnly<CPosition>(), ComponentType.ReadOnly<CRequiresView>());
            int dayHits = CountAt(day, LayoutWriter.DayBoundPoints(b));
            day.Dispose();
            Check(dayHits == 4, $"{when}: 4 day-camera markers frame the bounds (found {dayHits})");

            EntityQuery night = LayoutWriter.NightBoundQuery(EntityManager);
            int nightTotal = night.CalculateEntityCount();
            int nightHits = CountAt(night, LayoutWriter.NightBoundPoints(b));
            night.Dispose();
            Check(nightTotal == 4 && nightHits == 4, $"{when}: vanilla recreated 4 night-camera markers at the new bounds (total {nightTotal}, matching {nightHits})");
            CheckMovementBlockers(b, when);
            CheckStreetPieces(b, when);
            CheckLightingMap(when);
        }

        /// <summary>
        /// The night-lighting map (shader global _RoomLayout) marks exactly the indoor tiles, at the
        /// fixed position the shaders read it from (tiles -16..15, see RoomLayoutMapPatch).
        /// </summary>
        private void CheckLightingMap(string when)
        {
            var map = Shader.GetGlobalTexture("_RoomLayout") as Texture2D;
            if (map == null || map.width != RoomLayoutMapPatch.Size)
            {
                Fail($"{when}: no {RoomLayoutMapPatch.Size}-pixel night-lighting map set ({(map == null ? "none" : map.width + " px")})");
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            int first = RoomLayoutMapPatch.First, wrong = 0, checkedTiles = 0;
            string example = "";
            for (int x = first; x < first + RoomLayoutMapPatch.Size; x++)
            {
                for (int y = first; y < first + RoomLayoutMapPatch.Size; y++)
                {
                    bool indoor = bp.Tiles.TryGetValue(new LayoutPosition(x, y), out Room r) && LayoutHelpers.IsInside(r);
                    bool lit = map.GetPixel(x - first, y - first).g < 0.5f;
                    checkedTiles++;
                    if (indoor != lit)
                    {
                        wrong++;
                        example = $" e.g. {Fmt(new LayoutPosition(x, y))} indoor={indoor}";
                    }
                }
            }
            Check(wrong == 0, $"{when}: the night-lighting map marks exactly the indoor tiles ({wrong} of {checkedTiles} wrong{example})");
        }

        /// <summary>The outdoor movement blockers sit exactly on the pattern for bounds <paramref name="b"/> (if this setting has any).</summary>
        /// <summary>The street decorator's neighbouring buildings sit exactly where vanilla would put them for these bounds.</summary>
        private void CheckStreetPieces(Bounds b, string when)
        {
            var appliances = new List<(int id, Vector3 p)>();
            foreach (Entity e in AllWith<CPosition>())
            {
                // Spawned ones are pending (CCreateAppliance) until the game creates the appliance.
                int id = HasComponent<CAppliance>(e) ? EntityManager.GetComponentData<CAppliance>(e).ID
                       : HasComponent<CCreateAppliance>(e) ? EntityManager.GetComponentData<CCreateAppliance>(e).ID : 0;
                if (id != 0 && !HasComponent<CHeldBy>(e))
                {
                    appliances.Add((id, EntityManager.GetComponentData<CPosition>(e).Position));
                }
            }
            (int pieces, int off, int missing, string roles) = StreetPieces.TestCheck(EntityManager, appliances, ReadLayout());
            if (pieces == 0)
            {
                Log($"INFO {when}: no street-front decorations in this run (setting manages: {StreetPieces.TestDescribe(EntityManager)}).");
                return;
            }
            Check(off == 0 && missing == 0,
                $"{when}: street-front decorations match the layout ({pieces} found: {roles}; {off} off the pattern, {missing} pattern spots empty)");
        }

        private void CheckMovementBlockers(Bounds b, string when)
        {
            int id = KitchenData.AssetReference.OutdoorMovementBlocker;
            var at = new List<LayoutPosition>();
            foreach (Entity e in AllWith<CPosition>())
            {
                // Spawned ones are pending (CCreateAppliance) until the game creates the appliance.
                bool blocker = (HasComponent<CAppliance>(e) && EntityManager.GetComponentData<CAppliance>(e).ID == id)
                               || (HasComponent<CCreateAppliance>(e) && EntityManager.GetComponentData<CCreateAppliance>(e).ID == id);
                if (blocker)
                {
                    at.Add(EntityManager.GetComponentData<CPosition>(e).Position.Rounded());
                }
            }
            if (at.Count == 0)
            {
                Log($"INFO {when}: this setting has no outdoor movement blockers.");
                return;
            }
            HashSet<LayoutPosition> expected = LayoutWriter.MovementBlockerPoints(b);
            var missing = expected.Where(p => !at.Contains(p)).ToList();
            int dupes = at.Count - at.Distinct().Count();
            var stray = at.Distinct().Where(p => !expected.Contains(p)).ToList();
            Check(missing.Count == 0 && dupes == 0 && stray.Count == 0,
                $"{when}: outdoor movement blockers match the bounds ({at.Count} found, {expected.Count} expected; missing {string.Join(" ", missing.Select(Fmt))}; stray {string.Join(" ", stray.Select(Fmt))}; duplicates {dupes})");
        }

        private int CountAt(EntityQuery q, Vector3[] points)
        {
            int hits = 0;
            using (Unity.Collections.NativeArray<CPosition> positions = q.ToComponentDataArray<CPosition>(Unity.Collections.Allocator.Temp))
            {
                foreach (Vector3 p in points)
                {
                    if (positions.Any(c => (c.Position - p).sqrMagnitude < 0.0001f))
                    {
                        hits++;
                    }
                }
            }
            return hits;
        }

        private Bounds CurrentBounds() => GetComponent<CBounds>(GetSingletonEntity<SLayout>()).Bounds;

        // ---- edge cycle: wall -> hatch -> door -> wall (starting from whatever the edge is) ----

        private static FeatureType? _edgeStart, _edgeState;
        private static bool _edgeBothFree;

        /// <summary>
        /// Picks an internal edge between two different indoor rooms and cycles it once. The
        /// preference order is: a plain wall with both sides free, a plain wall with an appliance
        /// beside it, then a vanilla hatch (some layouts have no plain internal wall at all). Three
        /// cycles bring the edge back to where it started, which the round-trip check confirms.
        /// </summary>
        private void StepEdgePick()
        {
            LayoutBlueprint bp = ReadLayout();
            var candidates = new List<(LayoutPosition a, LayoutPosition b, FeatureType? state, bool free)>();
            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles.OrderBy(t => t.Key.x).ThenBy(t => t.Key.y))
            {
                if (!LayoutHelpers.IsInside(t.Value) || t.Value.Type == RoomType.Garden)
                {
                    continue;
                }
                foreach (LayoutPosition d in new[] { new LayoutPosition(1, 0), new LayoutPosition(0, 1) })
                {
                    LayoutPosition n = t.Key + d;
                    if (!bp.Tiles.TryGetValue(n, out Room r) || !LayoutHelpers.IsInside(r) || r.Type == RoomType.Garden || r.ID == t.Value.ID)
                    {
                        continue;
                    }
                    Feature f = bp.Features.FirstOrDefault(x => SameEdge(x, t.Key, n));
                    if (f != null && f.Type != FeatureType.Hatch)
                    {
                        continue; // doors are protected or may be needed; start from a wall or hatch
                    }
                    candidates.Add((t.Key, n, f?.Type, !Occupied(t.Key) && !Occupied(n)));
                }
            }
            var pick = candidates.OrderBy(c => c.state == null ? 0 : 1).ThenBy(c => c.free ? 0 : 1).Cast<(LayoutPosition a, LayoutPosition b, FeatureType? state, bool free)?>().FirstOrDefault();
            if (pick == null)
            {
                Fail("no internal wall or hatch found to cycle");
                return;
            }
            (_edgeA, _edgeB, _edgeStart, _edgeBothFree) = pick.Value;
            _edgeState = _edgeStart;
            _pathBeforeDoor = PathLength(_edgeA, _edgeB);
            Log($"edge {Fmt(_edgeA)}|{Fmt(_edgeB)} starts as {Label(_edgeStart)} (both sides free: {_edgeBothFree}); path length {_pathBeforeDoor:0.0}");
            StepEdgeCycle();
        }

        private void StepEdgeCycle()
        {
            if (_edgeA.Equals(_edgeB))
            {
                return;
            }
            FeatureType? expected = _edgeState == null ? FeatureType.Hatch : _edgeState == FeatureType.Hatch ? FeatureType.Door : (FeatureType?)null;
            LayoutBlueprint bp = ReadLayout();
            string refused = LayoutEdits.CycleEdge(bp, _edgeA, _edgeB, out FeatureType? now);
            Check(refused == null && now == expected, $"cycle {Fmt(_edgeA)}|{Fmt(_edgeB)} {Label(_edgeState)} -> {Label(expected)}{(refused == null ? "" : ": " + refused)}");
            if (refused == null)
            {
                Apply(bp);
                _edgeState = now;
                _haveEdge = true;
            }
        }

        private void StepVerifyEdge()
        {
            if (!_haveEdge)
            {
                return;
            }
            LayoutView v = LayoutViews().FirstOrDefault();
            Feature inView = v?.Builder?.Blueprint.Features.FirstOrDefault(f => SameEdge(f, _edgeA, _edgeB));
            Check(v?.Builder != null && inView?.Type == _edgeState, $"rebuilt view shows the edge as {Label(_edgeState)} (view has {Label(inView?.Type)})");
            bool reach = TileManager.CanReach((Vector3)_edgeA, (Vector3)_edgeB);
            Check(reach == (_edgeState != null), $"reachability across a {Label(_edgeState)} is {(_edgeState != null)}");

            float path = PathLength(_edgeA, _edgeB);
            if (_edgeState == FeatureType.Door)
            {
                Log($"navmesh path length through the door = {path:0.0}");
                if (_vanillaDoorsWalkable == true && _edgeBothFree)
                {
                    Check(path > 0f && path < 2.5f, "navmesh: customers can walk straight through the new door");
                }
                else
                {
                    Log($"INFO not asserting door walkability (vanilla doors walkable: {_vanillaDoorsWalkable?.ToString() ?? "unknown"}, both sides free: {_edgeBothFree}).");
                }
            }
            else if (_edgeState == _edgeStart && _edgeBothFree)
            {
                Check(Mathf.Abs(path - _pathBeforeDoor) < 0.5f, $"navmesh path length back to its starting value ({path:0.0} vs {_pathBeforeDoor:0.0})");
            }
            Shot($"02-edge-{Label(_edgeState).ToLowerInvariant()}");
        }

        private static string Label(FeatureType? t) => t?.ToString() ?? "Wall";

        /// <summary>Navmesh path length between two tile centres, or -1 if there's no complete path.</summary>
        private static float PathLength(LayoutPosition from, LayoutPosition to)
        {
            if (!NavMesh.SamplePosition((Vector3)from, out NavMeshHit a, 0.5f, NavMesh.AllAreas)
                || !NavMesh.SamplePosition((Vector3)to, out NavMeshHit b, 0.5f, NavMesh.AllAreas))
            {
                return -1f;
            }
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                return -1f;
            }
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }
            return length;
        }

        private void StepShrinkBack()
        {
            if (!_haveGrow)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            bool allAccepted = true;
            foreach ((LayoutPosition src, LayoutPosition dst) in _grown)
            {
                string refused = LayoutEdits.TogglePaint(bp, src, dst, BlocksEdit);
                Check(refused == null, $"shrink {Fmt(dst)} accepted{(refused == null ? "" : ": " + refused)}");
                allAccepted &= refused == null;
            }
            if (allAccepted)
            {
                Apply(bp);
                Check(CurrentBounds() == _baselineBounds, "CBounds back to the baseline");
            }
        }

        private void StepVerifyShrink()
        {
            if (!_haveGrow)
            {
                return;
            }
            LayoutView v = LayoutViews().FirstOrDefault();
            foreach ((LayoutPosition _, LayoutPosition dst) in _grown)
            {
                Check(TileManager.GetTile((Vector3)dst).Type == RoomType.NoRoom, $"buffer: {Fmt(dst)} is outdoors again");
                Check(v?.Builder != null && !v.Builder.Blueprint.Tiles.ContainsKey(dst), $"rebuilt view geometry dropped {Fmt(dst)}");
            }
            CheckCameraMarkers(CurrentBounds(), "after shrink");
            Shot("04-shrunk");
        }

        // ---- far grow: past vanilla's old navmesh edge (x = 10 / z = 10) ----

        private const int FarTarget = 12;

        private void StepGrowFar()
        {
            LayoutBlueprint bp = ReadLayout();
            var inside = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden).Select(t => t.Key).ToList();

            // East along each row, then north along each column. Rows and columns nearest the middle go first.
            var attempts = new List<(LayoutPosition edge, LayoutPosition dir)>();
            foreach (int y in inside.Select(p => p.y).Distinct().OrderBy(y => Math.Abs(y)))
            {
                attempts.Add((new LayoutPosition(inside.Where(p => p.y == y).Max(p => p.x), y), new LayoutPosition(1, 0)));
            }
            foreach (int x in inside.Select(p => p.x).Distinct().OrderBy(x => Math.Abs(x)))
            {
                attempts.Add((new LayoutPosition(x, inside.Where(p => p.x == x).Max(p => p.y)), new LayoutPosition(0, 1)));
            }

            foreach ((LayoutPosition edge, LayoutPosition dir) in attempts)
            {
                if (!inside.Contains(edge))
                {
                    continue;
                }
                LayoutBlueprint trial = new LayoutBlueprint(bp);
                var strip = new List<(LayoutPosition, LayoutPosition)>();
                LayoutPosition src = edge;
                bool ok = true;
                while ((dir.x != 0 ? src.x : src.y) < FarTarget)
                {
                    LayoutPosition dst = src + dir;
                    if (LayoutEdits.TogglePaint(trial, src, dst, BlocksEdit) != null)
                    {
                        ok = false;
                        break;
                    }
                    strip.Add((src, dst));
                    src = dst;
                }
                if (!ok || strip.Count < 2)
                {
                    continue;
                }
                _far = strip;
                Log($"far grow: {strip.Count} tiles from {Fmt(edge)} toward {Fmt(dir)} to {Fmt(strip.Last().Item2)}");
                Apply(trial);
                _haveFar = true;
                return;
            }
            Fail($"no row or column could grow out to {FarTarget} (scenery in the way?)");
        }

        private void StepVerifyGrowFar()
        {
            if (!_haveFar)
            {
                return;
            }
            LayoutView v = LayoutViews().FirstOrDefault();
            foreach ((LayoutPosition _, LayoutPosition dst) in _far)
            {
                Check(v?.Builder != null && v.Builder.Blueprint.Tiles.ContainsKey(dst), $"rebuilt view includes far tile {Fmt(dst)}");
                Check(NavMesh.SamplePosition((Vector3)dst, out NavMeshHit _, 0.3f, NavMesh.AllAreas), $"navmesh covers far tile {Fmt(dst)}");
            }

            LayoutPosition last = _far.Last().dst;
            BoxCollider ground = v?.GetComponents<BoxCollider>().FirstOrDefault(b => Mathf.Abs(b.size.y - 0.01f) < 0.001f);
            Check(ground != null && ground.bounds.Contains(new Vector3(last.x, 0f, last.y)), $"navmesh ground box was expanded to cover {Fmt(last)} ({(ground == null ? "not found" : ground.bounds.ToString())})");
            // Other mods' LayoutView patches are removed for the test (SelfTest.IsolateFromOtherMods),
            // so the box must be the size NavmeshAreaPatch computed, not e.g. a 300x300 override.
            Check(ground != null && ground.size.x < 100f && ground.size.z < 100f, $"the ground box is our patch's fitted size, not another mod's override (size {ground?.size})");

            // Walk the new strip end to end, from the building tile it grew out of. The route from
            // the front door to that tile can be walled off by kitchen appliances, which isn't
            // something this mod controls; it's logged for information only.
            // From the first new tile: the building tile it grew from may hold an appliance (no navmesh there).
            LayoutPosition first = _far[0].dst;
            // Diagnostics: agent size the floorplan navmesh is built for, and where each hop breaks.
            NavMeshSurface surface = v?.GetComponent<NavMeshSurface>();
            if (surface != null)
            {
                NavMeshBuildSettings agent = NavMesh.GetSettingsByID(surface.agentTypeID);
                Log($"INFO navmesh agent type {surface.agentTypeID}: radius {agent.agentRadius:0.00}, height {agent.agentHeight:0.00}, ground box {ground?.bounds}");
            }
            var hops = new List<string>();
            LayoutPosition prev = _far[0].src;
            foreach ((LayoutPosition _, LayoutPosition dst) in _far)
            {
                string snap = NavMesh.SamplePosition((Vector3)dst, out NavMeshHit h, 0.3f, NavMesh.AllAreas) ? $"{h.position.x:0.00},{h.position.z:0.00}" : "none";
                hops.Add($"{Fmt(prev)}->{Fmt(dst)}={PathLength(prev, dst):0.0} (snap {snap})");
                prev = dst;
            }
            Log("INFO strip hops: " + string.Join("; ", hops));
            float along = PathLength(first, last);
            Check(along > 0f, $"navmesh path along the whole strip {Fmt(first)} -> {Fmt(last)} (path {along:0.0})");
            LayoutPosition? door = FrontInside(ReadLayout());
            if (door.HasValue)
            {
                Log($"INFO path front door {Fmt(door.Value)} -> far tile {Fmt(last)}: {PathLength(door.Value, last):0.0}");
            }
            CheckCameraMarkers(CurrentBounds(), "after far grow");
            Shot("06-grown-far");
        }

        private void StepShrinkFar()
        {
            if (!_haveFar)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();

            // Removing the innermost far tile would strand the rest; the connectivity rule must refuse it.
            string strand = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), _far[0].src, _far[0].dst, BlocksEdit);
            Check(strand != null && strand.Contains("cut off"), $"refuses removing a tile that strands the rest of the strip ({strand})");

            for (int i = _far.Count - 1; i >= 0; i--)
            {
                string refused = LayoutEdits.TogglePaint(bp, _far[i].src, _far[i].dst, BlocksEdit);
                if (refused != null)
                {
                    Fail($"shrink far tile {Fmt(_far[i].dst)} refused: {refused}");
                    return;
                }
            }
            Apply(bp);
            Check(CurrentBounds() == _baselineBounds, "CBounds back to the baseline after far shrink");
        }

        private void StepVerifyShrinkFar()
        {
            if (!_haveFar)
            {
                return;
            }
            LayoutView v = LayoutViews().FirstOrDefault();
            Check(v?.Builder != null && _far.All(f => !v.Builder.Blueprint.Tiles.ContainsKey(f.dst)), "rebuilt view dropped the far strip");
            CheckCameraMarkers(CurrentBounds(), "after far shrink");
            Shot("07-shrunk-far");
        }

        // ---- far west: past vanilla's chair pathing point (-15, 0, 0) and toward the customer spawn ----

        private const int WestTarget = -18;
        private static List<(LayoutPosition src, LayoutPosition dst)> _west = new List<(LayoutPosition, LayoutPosition)>();
        private static bool _haveWest;

        private void StepGrowWest()
        {
            LayoutBlueprint bp = ReadLayout();
            var inside = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden).Select(t => t.Key).ToList();
            // Prefer the row through y = 0 (vanilla's pathing point), then rows nearest it.
            foreach (int y in inside.Select(p => p.y).Distinct().OrderBy(y => Math.Abs(y)))
            {
                var edge = new LayoutPosition(inside.Where(p => p.y == y).Min(p => p.x), y);
                LayoutBlueprint trial = new LayoutBlueprint(bp);
                var strip = new List<(LayoutPosition, LayoutPosition)>();
                LayoutPosition src = edge;
                bool ok = true;
                while (src.x > WestTarget)
                {
                    LayoutPosition dst = src + new LayoutPosition(-1, 0);
                    if (LayoutEdits.TogglePaint(trial, src, dst, BlocksEdit) != null)
                    {
                        ok = false;
                        break;
                    }
                    strip.Add((src, dst));
                    src = dst;
                }
                if (!ok || strip.Count == 0)
                {
                    continue;
                }
                _west = strip;
                Log($"west grow: {strip.Count} tiles from {Fmt(edge)} to {Fmt(strip.Last().Item2)}");
                Apply(trial);
                _haveWest = true;
                return;
            }
            Fail($"no row could grow west to x = {WestTarget}");
        }

        private void StepVerifyGrowWest()
        {
            if (!_haveWest)
            {
                return;
            }
            Vector3 source = SPerformTableUpdate.DefaultPathingSource;
            Check((source - new Vector3(-15f, 0f, 0f)).sqrMagnitude > 0.01f, $"chair pathing source moved off vanilla's point now that the building reaches it ({source})");
            Check(NavMesh.SamplePosition(source, out NavMeshHit _, 0.5f, NavMesh.AllAreas), "the chair pathing source is on the navmesh");

            Bounds b = CurrentBounds();
            foreach (Vector3 spawn in new[] { new Vector3(b.min.x - 13f, 0f, -7f), new Vector3(b.min.x - 7f, 0f, -6f), new Vector3(b.min.x - 10f, 0f, -6f) })
            {
                Check(NavMesh.SamplePosition(spawn, out NavMeshHit _, 0.5f, NavMesh.AllAreas), $"customer spawn area {spawn} is on the navmesh");
            }

            // The route customers take: from the pathing source (their spawn area) into the restaurant.
            LayoutPosition? door = FrontInside(ReadLayout());
            if (door.HasValue && NavMesh.SamplePosition(source, out NavMeshHit s, 0.5f, NavMesh.AllAreas)
                && NavMesh.SamplePosition((Vector3)door.Value, out NavMeshHit d, 0.5f, NavMesh.AllAreas))
            {
                var path = new NavMeshPath();
                bool ok = NavMesh.CalculatePath(s.position, d.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
                Check(ok, "navmesh path from the chair pathing source to the front door's indoor tile");
            }
            Shot("06b-grown-west");
        }

        private void StepShrinkWest()
        {
            if (!_haveWest)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            for (int i = _west.Count - 1; i >= 0; i--)
            {
                string refused = LayoutEdits.TogglePaint(bp, _west[i].src, _west[i].dst, BlocksEdit);
                if (refused != null)
                {
                    Fail($"shrink west tile {Fmt(_west[i].dst)} refused: {refused}");
                    return;
                }
            }
            Apply(bp);
        }

        private void StepVerifyShrinkWest()
        {
            if (_haveWest)
            {
                Vector3 source = SPerformTableUpdate.DefaultPathingSource;
                Check((source - new Vector3(-15f, 0f, 0f)).sqrMagnitude < 0.01f, $"chair pathing source back to vanilla's (-15, 0, 0) after shrinking ({source})");
            }
        }

        // ---- edit mode: terminal, session, draft painting, apply through the real network path ----

        private static LayoutBlueprint _editExpected;
        private static LayoutPosition _editGrowA, _editGrowB, _editNewA, _editNewB;
        private static int _editDiningId;
        private static LayoutPosition _editDoorFacing;

        private void StepEditTerminal()
        {
            List<Entity> terminals = AllWith<CFloorPlanTerminal>();
            Check(terminals.Count == 1, $"exactly one Floor Plan terminal at night ({terminals.Count})");
            if (terminals.Count == 0)
            {
                return;
            }
            Entity t = terminals[0];
            Check(!HasComponent<CRerollShopAfterDuration>(t) && !HasComponent<CTakesDuration>(t), "terminal has no reroll behaviour");
            string[] names = EntityManager.GetComponentTypes(t).Select(c => c.GetManagedType()?.Name ?? "").ToArray();
            Check(!names.Contains("CShopTerminal"), "Blueprint Shop didn't convert the Floor Plan terminal into a shop");
            Vector3 door = GetFrontDoor(get_external_tile: true);
            float rowsOut = door.z - Pos(t).z;
            Check((Mathf.Abs(rowsOut) < 0.01f || Mathf.Abs(rowsOut - 1f) < 0.01f) && !ReadLayout().Tiles.ContainsKey((LayoutPosition)Pos(t)),
                $"terminal stands in front of the building, outside ({Pos(t)}, door {door})");
            var title = HarmonyLib.AccessTools.Field(typeof(RerollBlueprintView), "Title");
            bool labelled = UnityEngine.Object.FindObjectsOfType<RerollBlueprintView>()
                .Any(v => title?.GetValue(v) is TMPro.TextMeshPro tmp && tmp.text == "Floor Plan");
            Check(labelled, "a terminal label reads \"Floor Plan\"");
            Bounds reach = FloorPlanService.PlayerReach(Bounds, GetFrontDoor());
            Check(reach.Contains(Pos(t)), $"the terminal is within the players' reach ({Pos(t)}; reach x {reach.min.x:0.##}..{reach.max.x:0.##}, z from {reach.min.z:0.##})");
            Shot("12-terminal");

            // Push it out of reach (as a shrinking edit can): the service must respawn it in reach.
            Vector3 away = new Vector3(Mathf.Ceil(reach.max.x) + 3f, 0f, door.z);
            EntityManager.SetComponentData(t, new CPosition(away));
            Log($"moved the terminal out of reach to {away}.");
        }

        private void StepEditTerminalRelocated()
        {
            List<Entity> terminals = AllWith<CFloorPlanTerminal>();
            Bounds reach = FloorPlanService.PlayerReach(Bounds, GetFrontDoor());
            bool ok = terminals.Count == 1 && reach.Contains(Pos(terminals[0]));
            if (!ok && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(ok, $"an out-of-reach terminal is respawned within reach ({terminals.Count} terminal(s){(terminals.Count > 0 ? " at " + Pos(terminals[0]) : "")})");
        }

        private void StepEditOpen()
        {
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null)
            {
                Fail("no player or no FloorPlanService to open the editor with");
                return;
            }
            _pitchBefore = Camera.main != null ? Camera.main.transform.rotation.eulerAngles.x : -1f;
            service.RequestOpen(players[0]);
            Log($"requested the editor for player entity {players[0].Index} (camera pitch {_pitchBefore:0.#}).");
        }

        private void StepEditVerifyOpen()
        {
            if (FloorPlanView.TestInstance == null || !FloorPlanView.TestInstance.TestReady)
            {
                if (StepElapsed < 10f)
                {
                    Retry(0.5f);
                    return;
                }
                Fail("the editor view didn't open on this (host) machine within 10s");
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 1, "exactly one edit session");
            var title = HarmonyLib.AccessTools.Field(typeof(RerollBlueprintView), "Title");
            bool watch = UnityEngine.Object.FindObjectsOfType<RerollBlueprintView>()
                .Any(v => title?.GetValue(v) is TMPro.TextMeshPro tmp && tmp.text == "Watch");
            Check(watch, "terminal label switched to \"Watch\" while editing");
            Check(SameLayout(ReadLayout(), FloorPlanView.TestInstance.TestDraft, out _), "the draft starts as a copy of the live layout");
            Log("HUD " + FloorPlanView.TestInstance.TestHudReport());
            (int title, int help, int status) hud = FloorPlanView.TestInstance.TestHudCharacters();
            Check(hud.title > 0 && hud.help > 0 && hud.status > 0, $"every HUD line renders text (title {hud.title}, help {hud.help}, status {hud.status} characters)");
            Shot("13-editor-open");
        }

        // ---- camera panning while editing ----

        private static Vector3 _camBefore;
        private static float _pitchBefore;
        private static readonly Vector3 PanBy = new Vector3(8f, 0f, 7f);

        private void StepEditPan()
        {
            Check(FloorPlanCamera.TestOursApplied, "the editor took over the camera's target group");
            _camBefore = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            FloorPlanCamera.Move(PanBy);
            Log($"panned the view by {PanBy} (camera at {_camBefore}).");
        }

        private void StepEditVerifyPan()
        {
            Vector3 now = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            Vector3 moved = now - _camBefore;
            // Cinemachine damps the move; wait until most of it has happened.
            bool done = moved.x > PanBy.x * 0.75f && moved.z > PanBy.z * 0.75f;
            if (!done && StepElapsed < 6f)
            {
                Retry(0.5f);
                return;
            }
            Check(done, $"the camera followed the pan ({moved.x:0.0}, {moved.z:0.0} of {PanBy.x}, {PanBy.z})");
            Check((FloorPlanCamera.Offset - PanBy).sqrMagnitude < 0.01f, $"pan offset recorded ({FloorPlanCamera.Offset})");
            float pitch = Camera.main != null ? Camera.main.transform.rotation.eulerAngles.x : -1f;
            Check(pitch > 85f && pitch < 95f, $"the camera looks straight down while editing (pitch {pitch:0.#}, was {_pitchBefore:0.#})");
            Shot("13b-editor-panned");
            FloorPlanCamera.Move(-PanBy);
        }

        /// <summary>Shift + drag box: paints a 3 x 2 block beyond a room edge in one go, then undoes it.</summary>
        private void StepEditBox()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            LayoutBlueprint live = ReadLayout();
            LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            foreach (LayoutPosition d in dirs)
            {
                var perp = new LayoutPosition(d.y == 0 ? 0 : 1, d.x == 0 ? 0 : 1);
                foreach (LayoutPosition s in live.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden && !live.Tiles.ContainsKey(t.Key + d))
                             .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y)))
                {
                    var block = new List<LayoutPosition>();
                    for (int i = 1; i <= 2; i++)
                    {
                        for (int j = -1; j <= 1; j++)
                        {
                            block.Add(new LayoutPosition(s.x + d.x * i + perp.x * j, s.y + d.y * i + perp.y * j));
                        }
                    }
                    if (block.Any(p => BlocksEdit(p) || live.Tiles.ContainsKey(p)))
                    {
                        continue;
                    }
                    int room = live[s].ID;
                    Check(view.TestSelectRoom(room), "box: brush for the room available");
                    LayoutPosition a = new LayoutPosition(s.x + d.x - perp.x, s.y + d.y - perp.y);
                    LayoutPosition b = new LayoutPosition(s.x + d.x * 2 + perp.x, s.y + d.y * 2 + perp.y);
                    view.TestPaintBox(a, b);
                    bool all = block.All(p => view.TestDraft.Tiles.TryGetValue(p, out Room r) && r.ID == room);
                    Check(all, $"Shift+drag box {Fmt(a)}..{Fmt(b)} painted all 6 tiles into room {room} ({view.TestMessage})");
                    view.TestUndo();
                    Check(SameLayout(live, view.TestDraft, out string diff), $"one undo removes the whole box{(diff == null ? "" : ": " + diff)}");
                    return;
                }
            }
            Log("INFO no free 3 x 2 block beyond a room edge; box painting not exercised.");
        }

        /// <summary>
        /// User report: a new room with a door couldn't be removed. Its door couldn't go (the only
        /// way in) and its last tile couldn't go (it had a door). Both must work now: removing the
        /// door just shows the room as unreachable, and the tile can be erased, door and all.
        /// </summary>
        private void StepEditRemoveNewRoom()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            LayoutBlueprint live = ReadLayout();
            LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            foreach (LayoutPosition d in dirs)
            {
                foreach (LayoutPosition s in live.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden && !live.Tiles.ContainsKey(t.Key + d))
                             .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y)))
                {
                    LayoutPosition t = s + d;
                    if (BlocksEdit(t))
                    {
                        continue;
                    }
                    var back = new LayoutPosition(-d.x, -d.y);
                    Check(view.TestSelectBrush("New dining room"), "deadlock: \"New dining room\" brush available");
                    view.TestPaint(t);
                    Check(view.TestDraft.Tiles.TryGetValue(t, out Room nr) && nr.Type == RoomType.Dining && nr.ID != live[s].ID, $"deadlock: a one-tile new room at {Fmt(t)}");
                    view.TestSetEdge(t, back, FeatureType.Door);
                    Check(view.TestUnreachableCount == 0 && view.TestDraft.Features.Any(f => f.Type == FeatureType.Door && SameEdge(f, t, s)), "deadlock: its door makes it reachable");

                    view.TestSetEdge(t, back, null);
                    Check(!view.TestDraft.Features.Any(f => SameEdge(f, t, s)) && view.TestUnreachableCount > 0,
                        $"removing the room's only door is allowed in the draft (the room shows unreachable: {view.TestUnreachableCount}; {view.TestMessage})");
                    view.TestUndo();

                    Check(view.TestSelectBrush("Outdoors (remove floor)"), "deadlock: erase brush available");
                    view.TestPaint(t);
                    Check(!view.TestDraft.Tiles.ContainsKey(t) && !view.TestDraft.Features.Any(f => f.Tile1 == t || f.Tile2 == t),
                        $"erasing a tile with a door removes the tile and its door ({view.TestMessage})");
                    Check(view.TestUnreachableCount == 0, "after erasing the new room, everything is reachable again");

                    view.TestUndo(); // erase
                    view.TestUndo(); // door
                    view.TestUndo(); // new room
                    Check(SameLayout(live, view.TestDraft, out string diff), $"undo returns the draft to the live layout{(string.IsNullOrEmpty(diff) ? "" : ": " + diff)}");
                    return;
                }
            }
            Log("INFO no free tile beyond a room edge; new-room removal not exercised.");
        }

        private void StepEditPaint()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("editor not open");
                return;
            }
            LayoutBlueprint live = ReadLayout();
            // Any indoor room edge (back or sides) with a free 2x2 outdoor block beyond it: column A
            // grows the room, column B becomes a new dining room beside it.
            LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            foreach (LayoutPosition d in dirs)
            {
                var perp = new LayoutPosition(d.y == 0 ? 0 : 1, d.x == 0 ? 0 : 1); // a side direction
                foreach (LayoutPosition s in live.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden && !live.Tiles.ContainsKey(t.Key + d))
                             .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y)))
                {
                    LayoutPosition a = s + d, b = a + d, side = a + perp, side2 = side + d;
                    var block = new[] { a, b, side, side2 };
                    if (block.Any(p => BlocksEdit(p) || live.Tiles.ContainsKey(p)) || LayoutEdits.PaintTile(new LayoutBlueprint(live), a, live[s], TileOccupants.Take(EntityManager)) != null)
                    {
                        continue;
                    }
                    _editDiningId = live[s].ID;
                    _editGrowA = a;
                    _editGrowB = b;
                    _editNewA = side;
                    _editNewB = side2;
                    _editDoorFacing = new LayoutPosition(-perp.x, -perp.y);
                    break;
                }
                if (_editDiningId != 0)
                {
                    break;
                }
            }
            if (_editDiningId == 0)
            {
                Fail("no room edge with a free 2x2 block for the edit-mode test");
                return;
            }
            Log($"edit test: grow room {_editDiningId} onto {Fmt(_editGrowA)},{Fmt(_editGrowB)}; new dining room on {Fmt(_editNewA)},{Fmt(_editNewB)}");

            // 1. Grow the existing room by two tiles (brush = that room).
            Check(view.TestSelectRoom(_editDiningId), "brush for the existing room available");
            view.TestPaint(_editGrowA);
            view.TestPaint(_editGrowB);
            Check(view.TestDraft[_editGrowA].ID == _editDiningId && view.TestDraft[_editGrowB].ID == _editDiningId, $"draft: the room grew onto {Fmt(_editGrowA)} and {Fmt(_editGrowB)}");
            Check(ReadLayout().Tiles.Count == live.Tiles.Count, "the live layout is untouched while editing (draft only)");

            // 2. A new dining room beside it: walled off, so it must show as unreachable.
            Check(view.TestSelectBrush("New dining room"), "\"New dining room\" brush available");
            view.TestPaint(_editNewA);
            view.TestPaint(_editNewB);
            bool newRoom = view.TestDraft.Tiles.TryGetValue(_editNewA, out Room nr) && nr.ID != _editDiningId && nr.Type == RoomType.Dining
                           && view.TestDraft[_editNewB].ID == nr.ID;
            Check(newRoom, $"draft: a new dining room on {Fmt(_editNewA)} and {Fmt(_editNewB)}");
            Check(view.TestUnreachableCount > 0, $"the walled-off new room shows as unreachable ({view.TestUnreachableCount} tiles)");
            view.TestApply();
            Check(!view.TestAwaiting && view.TestMessage.Contains("reached"), $"apply is refused locally while floor is unreachable ({view.TestMessage})");

            // 3. Appliances no longer block painting (see StepOccupied*), except ones that can't be
            //    moved or can't go outdoors, when their floor would switch between in and out.
            LayoutPosition? stuck = view.TestOccupants.Where(o => (o.Value & (OccupantFlags.Fixed | OccupantFlags.KeepIndoors)) != 0 && TileOccupants.IsIndoor(live, o.Key))
                .Select(o => (LayoutPosition?)o.Key).FirstOrDefault();
            if (stuck.HasValue)
            {
                view.TestSelectBrush("Outdoors (remove floor)");
                view.TestPaint(stuck.Value);
                Check(view.TestMessage.Contains("appliance") && TileOccupants.IsIndoor(view.TestDraft, stuck.Value),
                    $"removing the floor under an appliance that can't go outdoors is refused ({view.TestMessage})");
            }
            else
            {
                Log("INFO no indoor appliance that can't be moved or sold; that refusal isn't exercised.");
            }

            // 4. A door between the new room and the grown dining tile, via a hatch (wall brushes).
            view.TestSetEdge(_editNewA, _editDoorFacing, FeatureType.Hatch);
            Check(view.TestDraft.Features.Any(f => f.Type == FeatureType.Hatch && SameEdge(f, _editNewA, _editGrowA)), "wall brush Hatch: the wall becomes a hatch");
            Check(view.TestPickEdge(_editNewA, _editDoorFacing) == "Hatch (serving window)", "right-clicking a hatch picks the Hatch wall brush");
            view.TestSetEdge(_editNewA, _editDoorFacing, FeatureType.Door);
            bool door = view.TestDraft.Features.Any(f => f.Type == FeatureType.Door && SameEdge(f, _editNewA, _editGrowA));
            Check(door, "draft: a door between the new room and the grown room");
            Check(view.TestPickEdge(_editNewA, _editDoorFacing) == "Door", "right-clicking a door picks the Door wall brush");
            Check(view.TestHologramMarks > 0, $"the drafted door gets a hologram ({view.TestHologramMarks} marks, layer at height {view.TestHoloHeight:0.00})");
            Check(view.TestUnreachableCount == 0, $"with the door, every tile is reachable ({view.TestUnreachableCount})");
            Check(view.TestPooledMarks > 0, $"the overlay draws the draft ({view.TestPooledMarks} marks)");
            _editExpected = new LayoutBlueprint(view.TestDraft);
        }

        private void StepEditRejectTampered()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady || _editExpected == null)
            {
                return;
            }
            // Move the front door by one tile in a copy of the draft: the host must refuse it.
            LayoutBlueprint bad = new LayoutBlueprint(_editExpected);
            Feature front = bad.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (front == null)
            {
                Log("INFO no front door; tamper test skipped.");
                return;
            }
            Shot("14-editor-draft"); // a step later than the edits, so the HUD's Update has refreshed
            bad.Features.Remove(front);
            bad.Features.Add(new Feature(front.Tile1 + new LayoutPosition(1, 0), front.Tile2 + new LayoutPosition(1, 0), FeatureType.FrontDoor));
            view.TestSendRawDraft(bad);
            Log("sent a tampered draft (front door moved) straight to the host.");
        }

        private void StepEditVerifyRejected()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || _editExpected == null)
            {
                return;
            }
            if (view.TestAwaiting && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(!view.TestAwaiting && view.TestMessage.Contains("special doors"), $"the host refused the tampered draft ({view.TestMessage})");
            Check(AllWith<CFloorPlanSession>().Count == 1, "the session stays open after a refusal");
            Check(!SameLayout(ReadLayout(), _editExpected, out _) && ReadLayout().Features.Any(f => f.Type == FeatureType.FrontDoor), "the live layout is unchanged by the refused draft");
        }

        // ---- spectators: a second player watches the editor's draft (read-only) ----
        // The test has one player, who is also the editor, so the spectator is added on the same
        // machine through a Debug-only hook (real players on the editor's machine are skipped:
        // they already share its screen). The editor's own overlay/HUD is hidden meanwhile so the
        // screenshot shows the spectator's.

        private void StepSpectateOpen()
        {
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null || AllWith<CFloorPlanSession>().Count != 1)
            {
                Fail("no player, service or open session to spectate");
                return;
            }
            FloorPlanView.TestInstance?.TestSetVisible(false);
            service.TestAddSpectator(players[0]);
            Log("added a spectator for player entity " + players[0].Index + ".");
        }

        /// <summary>Waits until the spectator view is up and shows the editor's draft; false = retrying or failed.</summary>
        private bool SpectatorShowsEditor(string what, out FloorPlanView spectator, out FloorPlanView editor)
        {
            spectator = FloorPlanView.TestSpectatorInstance;
            editor = FloorPlanView.TestInstance;
            bool ready = spectator != null && spectator.TestReady && editor != null && editor.TestReady;
            bool same = ready && SameLayout(editor.TestDraft, spectator.TestDraft, out _);
            if (!same && StepElapsed < 8f)
            {
                Retry(0.5f);
                return false;
            }
            if (!ready)
            {
                Fail($"{what}: the spectator view (or the editor) isn't open");
                return false;
            }
            bool exact = SameLayout(editor.TestDraft, spectator.TestDraft, out string diff);
            Check(exact, $"{what}: the spectator shows the editor's draft{(exact ? "" : ": " + diff)}");
            return exact;
        }

        private void StepSpectateVerify()
        {
            FloorPlanView spectator = FloorPlanView.TestSpectatorInstance, editor = FloorPlanView.TestInstance;
            bool waiting = spectator == null || editor == null || editor.TestSpectatorCount != 1
                           || !spectator.TestCursorShown || !spectator.TestCursor.SameAs(editor.TestCursor);
            if (waiting && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            if (!SpectatorShowsEditor("watch", out spectator, out editor))
            {
                return;
            }
            Check(AllWith<CFloorPlanSpectator>().Count == 1 && AllWith<CFloorPlanSession>().Count == 1, "one spectator and still one edit session");
            Check(spectator.TestHasPreview, "the spectator received the editor's preview (not just the committed layout)");
            Check(editor.TestSpectatorCount == 1 && spectator.TestSpectatorCount == 1, $"both views count one spectator (editor {editor.TestSpectatorCount}, spectator {spectator.TestSpectatorCount})");
            Check(spectator.TestCursorShown && spectator.TestCursor.SameAs(editor.TestCursor),
                $"the spectator shows the editor's cursor ({Fmt(spectator.TestCursor.Tile)} mode {spectator.TestCursor.Mode} vs {Fmt(editor.TestCursor.Tile)} mode {editor.TestCursor.Mode})");
            Check(spectator.TestUnreachableCount == editor.TestUnreachableCount, "the spectator's reachability matches the editor's");
            Check(spectator.TestPooledMarks > 0, $"the spectator's overlay draws the draft ({spectator.TestPooledMarks} marks)");
            Log("spectator HUD " + spectator.TestHudReport());
            (int title, int help, int status) hud = spectator.TestHudCharacters();
            Check(hud.title > 0 && hud.help > 0 && hud.status > 0, $"every spectator HUD line renders text (title {hud.title}, help {hud.help}, status {hud.status})");
            Check(!SameLayout(ReadLayout(), _editExpected, out _), "watching doesn't change the live layout");
            Shot("14b-spectator");
        }

        private LayoutPosition _followTile;

        private void StepSpectateFollow()
        {
            FloorPlanView editor = FloorPlanView.TestInstance;
            if (editor == null || !editor.TestReady)
            {
                Fail("editor not open");
                return;
            }
            // Erase the second grown tile, so the spectator has a change to follow (undone next).
            _followTile = _editGrowB;
            editor.TestSelectBrush("Outdoors (remove floor)");
            editor.TestPaint(_followTile);
            Check(!SameLayout(editor.TestDraft, _editExpected, out _), $"the editor's draft changed (erased {Fmt(_followTile)}: {editor.TestMessage})");
        }

        private void StepSpectateVerifyFollow()
        {
            FloorPlanView spectator = FloorPlanView.TestSpectatorInstance, editor = FloorPlanView.TestInstance;
            bool waiting = spectator == null || editor == null || !spectator.TestCursor.SameAs(editor.TestCursor);
            if (waiting && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            if (!SpectatorShowsEditor("follow", out spectator, out editor))
            {
                return;
            }
            Check(!spectator.TestDraft.Tiles.TryGetValue(_followTile, out Room r) || !LayoutHelpers.IsInside(r), "the spectator sees the erased tile as outdoors");
            // Not the painted tile: a keyboard/mouse editor's cursor follows the real mouse every
            // frame, so after the hook paints it's back wherever the mouse is. Check the relay instead.
            FloorPlanCursor c = spectator.TestCursor;
            Check(c.SameAs(editor.TestCursor) && c.Mode == 0 && c.BrushLabel == "Outdoors (remove floor)",
                $"the spectator follows the editor's cursor, mode and brush ({Fmt(c.Tile)}, mode {c.Mode}, '{c.BrushLabel}'; editor at {Fmt(editor.TestCursor.Tile)})");
        }

        private bool _followUndone;

        private void StepSpectateVerifyUndo()
        {
            // Undo first, whatever the follow check found, so the later Apply applies the expected draft.
            if (!_followUndone)
            {
                _followUndone = true;
                FloorPlanView.TestInstance?.TestUndo();
                Retry(0.5f);
                return;
            }
            if (!SpectatorShowsEditor("undo", out FloorPlanView spectator, out FloorPlanView editor))
            {
                return;
            }
            Check(SameLayout(editor.TestDraft, _editExpected, out _) && SameLayout(spectator.TestDraft, _editExpected, out _), "after the editor's undo, both show the original draft again");
        }

        private int _ignoredBefore;

        private void StepSpectateTamper()
        {
            FloorPlanView spectator = FloorPlanView.TestSpectatorInstance;
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (spectator == null || service == null)
            {
                Fail("no spectator view to tamper with");
                return;
            }
            _ignoredBefore = service.TestIgnoredSpectatorActions;
            // A spectator's view sends a perfectly valid draft as an Apply: the host must ignore it.
            spectator.TestSendRaw(FloorPlanAction.Apply, new LayoutBlueprint(_editExpected));
            Log("sent an Apply from the spectator's view.");
        }

        private void StepSpectateVerifyIgnored()
        {
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (service.TestIgnoredSpectatorActions == _ignoredBefore && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(service.TestIgnoredSpectatorActions == _ignoredBefore + 1, "the host ignored the spectator's Apply");
            Check(!SameLayout(ReadLayout(), _editExpected, out _), "the live layout is unchanged by a spectator's Apply");
            Check(AllWith<CFloorPlanSession>().Count == 1 && AllWith<CFloorPlanSpectator>().Count == 1, "the session and the spectator both stay open");
        }

        private void StepSpectateStop()
        {
            FloorPlanView spectator = FloorPlanView.TestSpectatorInstance;
            if (spectator == null)
            {
                Fail("no spectator view to close");
                return;
            }
            spectator.TestStopWatching();
        }

        private void StepSpectateVerifyStopped()
        {
            FloorPlanView editor = FloorPlanView.TestInstance;
            bool waiting = AllWith<CFloorPlanSpectator>().Count > 0 || editor == null || editor.TestSpectatorCount != 0;
            if (waiting && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(AllWith<CFloorPlanSpectator>().Count == 0, "stopping watching removes the spectator");
            Check(editor != null && editor.TestSpectatorCount == 0, "the editor's spectator count is back to 0");
            Check(AllWith<CFloorPlanSession>().Count == 1 && editor != null && editor.TestReady, "the editor's session stays open");
            Check(FloorPlanCamera.TestOursApplied, "the editor keeps camera panning after the spectator on the same screen leaves");
        }

        private void StepSpectateVerifyLateJoin()
        {
            // A spectator who joins after the edits must get the stored draft straight away.
            if (!SpectatorShowsEditor("late join", out FloorPlanView spectator, out _))
            {
                return;
            }
            Check(spectator.TestHasPreview && SameLayout(spectator.TestDraft, _editExpected, out _), "a late spectator sees the draft made before they joined");
        }

        private void StepEditApply()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                return;
            }
            view.TestSetVisible(true);

            // Also draft a camera area (a 5 x 4 corner of the restaurant), applied with the layout.
            (int minX, int minY, int _, int _) = CameraAreaMarkers.Rect(CurrentBounds());
            _areaExpected = FloorPlanCameraArea.Box(minX, minY, minX + 4, minY + 3);
            view.TestSetCameraArea(new LayoutPosition(minX, minY), new LayoutPosition(minX + 4, minY + 3));
            Check(view.TestDraftArea.SameAs(_areaExpected), $"the editor drafts the camera area {view.TestDraftArea} (expected {_areaExpected})");
            Shot("14b-camera-area-drafted");

            view.TestApply();
            Log($"applied the draft ({view.TestMessage}).");
        }

        private static FloorPlanCameraArea _areaExpected;

        /// <summary>Whether the four day markers frame <paramref name="area"/> (Whole: the bounds).</summary>
        private bool MarkersFrame(FloorPlanCameraArea area, out string detail)
        {
            Bounds b = CurrentBounds();
            Bounds target = area.Custom ? CameraAreaMarkers.TileBounds(area.MinX, area.MinY, area.MaxX, area.MaxY) : b;
            Vector3[] expected = LayoutWriter.DayBoundPoints(target);
            List<Entity> markers = CameraAreaMarkers.DayMarkers(EntityManager);
            int hits = expected.Count(p => markers.Any(m => (EntityManager.GetComponentData<CPosition>(m).Position - p).sqrMagnitude < 0.0001f));
            detail = $"{markers.Count} markers, {hits} of 4 where expected; reads as {CameraAreaMarkers.Read(EntityManager, b)}";
            return markers.Count == 4 && hits == 4;
        }

        private void StepEditVerifyApplied()
        {
            if (_editExpected == null)
            {
                return;
            }
            if (AllWith<CFloorPlanSession>().Count > 0 && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            if (!FloorPlanCamera.TestTiltDone && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 0, "the session closed after a successful apply");
            float pitch = Camera.main != null ? Camera.main.transform.rotation.eulerAngles.x : -1f;
            Check(FloorPlanCamera.TestTiltDone && Mathf.Abs(pitch - _pitchBefore) < 0.5f, $"the camera tilted back to its usual angle (pitch {pitch:0.#}, was {_pitchBefore:0.#})");
            Check(AllWith<CFloorPlanSpectator>().Count == 0 && FloorPlanView.TestSpectatorInstance == null, "the spectator closed with the session");
            var group = FloorPlanCamera.TestGroup;
            bool restored = !FloorPlanCamera.Active && group != null && group.m_Targets != null && group.m_Targets.Length > 0
                            && group.m_Targets.All(t => t.target == null || t.target.parent == null || t.target.parent.name != "EasyFloorPlanner Camera Anchors");
            Check(restored, $"closing the editor gives the camera back to the game ({group?.m_Targets?.Length ?? 0} target(s))");
            bool same = SameLayout(_editExpected, ReadLayout(), out string diff);
            Check(same, $"the live layout is now the applied draft{(same ? "" : ": " + diff)}");
            LayoutView v = LayoutViews().FirstOrDefault();
            Check(v?.Builder != null && v.Builder.Blueprint.Features.Any(f => f.Type == FeatureType.Door && SameEdge(f, _editNewA, _editGrowA)), "the rebuilt restaurant has the new door");
            Check(PathLength(_editNewB, _editGrowB) > 0f, "navmesh path from the new room into the grown room");
            Check(MarkersFrame(_areaExpected, out string markers), $"the day camera markers frame the applied camera area {_areaExpected} ({markers})");
            Shot("15-editor-applied");
        }

        // ---- camera area (chosen in the editor) and the play camera ----

        private void StepAreaKeptAfterResize()
        {
            // The far-west strip grew and shrank the restaurant with a custom area in effect.
            Check(LayoutWriter.LastAreaKept && LayoutWriter.LastBoundsSync.movedDayPoints == 0,
                $"a resize keeps a custom camera area (kept {LayoutWriter.LastAreaKept}, markers moved {LayoutWriter.LastBoundsSync.movedDayPoints})");
            Check(MarkersFrame(_areaExpected, out string markers), $"the camera area is unchanged after the far-west strip came and went ({markers})");
        }

        private int _areaLayoutHash;

        private void StepAreaReopen()
        {
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null)
            {
                Fail("no player or no FloorPlanService to reopen the editor with");
                return;
            }
            _areaLayoutHash = FloorPlanSerializer.Hash(ReadLayout());
            service.RequestOpen(players[0]);
        }

        private void StepAreaVerifyReopened()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if ((view == null || !view.TestReady || !view.TestCommittedArea.Custom) && StepElapsed < 10f)
            {
                Retry(0.5f);
                return;
            }
            if (view == null || !view.TestReady)
            {
                Fail("the editor didn't reopen within 10s");
                return;
            }
            Check(view.TestCommittedArea.SameAs(_areaExpected) && view.TestDraftArea.SameAs(_areaExpected),
                $"a reopened editor starts from the camera area in effect ({view.TestCommittedArea})");
            var framing = PlayCamera.TestFraming;
            Check(framing != null && Mathf.Abs(framing.m_ScreenY - PlayCamera.TestVanillaScreenY) < 0.001f && Mathf.Abs(PlayCamera.TestEditorZoom - 1f) < 0.001f,
                $"in the editor the play camera's HUD margin and pan are off (screen y {framing?.m_ScreenY:0.000}, vanilla {PlayCamera.TestVanillaScreenY:0.000})");
            view.TestSetCameraArea(new LayoutPosition(_areaExpected.MinX, _areaExpected.MinY), new LayoutPosition(_areaExpected.MaxX, _areaExpected.MaxY)); // same area: just shows camera mode
            Shot("16-camera-area-mode");
        }

        private void StepAreaReset()
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if (view == null || !view.TestReady)
            {
                Fail("no editor to reset the camera area in");
                return;
            }
            view.TestSetCameraArea(null, default);
            Check(!view.TestDraftArea.Custom, "right click (Grab) in camera mode drafts the whole restaurant");
            view.TestApply(); // the layout is unchanged: a camera-only apply
        }

        private void StepAreaVerifyReset()
        {
            if (AllWith<CFloorPlanSession>().Count > 0 && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            Check(AllWith<CFloorPlanSession>().Count == 0, "a camera-only apply closes the editor");
            Check(FloorPlanSerializer.Hash(ReadLayout()) == _areaLayoutHash, "a camera-only apply leaves the layout as it was");
            Check(MarkersFrame(FloorPlanCameraArea.Whole, out string markers), $"the day camera frames the whole restaurant again ({markers})");
        }

        // ---- appliances on edited floor (user decision 2026-11-17) ----
        // Repainting a tile under an appliance is allowed; erasing it warns that the game will
        // bin the appliance at the start of the day; a wall-mounted appliance keeps its last wall.
        // Two tables are joined side by side, then a new wall is drafted between them: the editor
        // must allow it, and after Apply they must no longer be one table group, and no chair may
        // stay across a wall from its table. Then everything is painted back.

        private Entity _occT1, _occT2, _occE;
        private LayoutPosition _occA, _occB, _occETile;
        private int _occR1, _occR2, _occERoom, _occLayoutHash;
        private bool _occReady;

        private void StepOccupiedSetup()
        {
            // The wall-mounted appliances in this game version (their flag lives in the game data).
            var wallMounted = KitchenData.GameData.Main.Get<KitchenData.Appliance>()
                .Where(a => a.Properties != null && a.Properties.Any(p => p is CMustHaveWall)).Select(a => a.Name).ToList();
            Log($"INFO wall-mounted appliances (CMustHaveWall): {wallMounted.Count}{(wallMounted.Count > 0 ? ": " + string.Join(", ", wallMounted) : "")}");

            // Rule check on a small made-up layout: a 3 x 3 room with a second room above its top
            // middle tile, so a wall-mounted appliance there has walls only up (room 2) and... none
            // other than that one, since its other sides are the same room or the room's own edge.
            var r1 = new Room(RoomType.Dining);
            var r2 = new Room(RoomType.Kitchen);
            var bp = new LayoutBlueprint();
            for (int x = -1; x <= 3; x++)
            {
                for (int y = -1; y <= 3; y++)
                {
                    bp.Tiles[new LayoutPosition(x, y)] = r1; // 5 x 5, so the tile below isn't on an edge
                }
            }
            var top = new LayoutPosition(1, 3);
            var above = new LayoutPosition(1, 4);
            bp.Tiles[above] = r2;
            // (1, 3) is on the 5 x 5 room's top row: up is room 2 (a wall), left/right/down are room 1.
            var occ = new Dictionary<LayoutPosition, OccupantFlags> { [top] = OccupantFlags.WallMounted | OccupantFlags.Binned };
            Check(TileOccupants.HasSolidWall(bp, top) && TileOccupants.Sides.Count(s => TileOccupants.IsSolidWall(bp, top, s.D)) == 1,
                "(setup) the made-up wall-mounted spot has exactly one wall");
            string merged = LayoutEdits.PaintTile(bp, above, r1, occ);
            Check(merged != null && merged.Contains("wall-mounted") && bp[above].ID == r2.ID,
                $"painting away the only wall of a wall-mounted appliance is refused, and the draft is left as it was ({merged ?? "allowed"})");
            string door = LayoutEdits.SetEdge(bp, top, above, FeatureType.Door, occ);
            Check(door != null && !bp.Features.Any(), $"a door in the only wall of a wall-mounted appliance is refused ({door ?? "allowed"})");
            // Give it a second wall (room 2 grows round to its right), then the first wall may go.
            var two = new LayoutBlueprint(bp);
            string grow = LayoutEdits.PaintTile(two, new LayoutPosition(2, 4), r2, occ) ?? LayoutEdits.PaintTile(two, new LayoutPosition(2, 3), r2, occ);
            string oneOfTwo = grow ?? LayoutEdits.PaintTile(two, above, r1, occ);
            Check(oneOfTwo == null && two[above].ID == r1.ID && TileOccupants.HasSolidWall(two, top),
                $"a wall-mounted appliance with another wall doesn't block removing one ({oneOfTwo ?? "allowed"})");
            Check(TileOccupants.Refusal(true, false, OccupantFlags.Fixed) != null && TileOccupants.Refusal(true, false, OccupantFlags.KeepIndoors) != null
                  && TileOccupants.Refusal(true, false, OccupantFlags.Binned) == null && TileOccupants.Refusal(true, true, OccupantFlags.Fixed) == null,
                "rules: unmovable or can't-go-outdoors appliances keep their floor; other appliances may lose it; any may change rooms");

            // In the restaurant: two tables of the same kind, side by side in one room, where the
            // second tile borders another room it can join without cutting anything off.
            LayoutBlueprint live = ReadLayout();
            Dictionary<LayoutPosition, OccupantFlags> occupants = TileOccupants.Take(EntityManager);
            var tables = AllWith<CApplianceTable>().Where(t => !HasComponent<CHeldBy>(t) && HasComponent<CAppliance>(t)
                                                               && !EntityManager.GetComponentData<CApplianceTable>(t).IsIndividualTable).ToList();
            (Entity t, Entity u) pair = default;
            foreach (Entity t in tables)
            {
                Entity u = tables.FirstOrDefault(x => x != t
                    && EntityManager.GetComponentData<CAppliance>(x).ID == EntityManager.GetComponentData<CAppliance>(t).ID
                    && EntityManager.GetComponentData<CApplianceTable>(x).IsWaitingTable == EntityManager.GetComponentData<CApplianceTable>(t).IsWaitingTable);
                if (u != default)
                {
                    pair = (t, u);
                    break;
                }
            }
            if (pair.t == default)
            {
                Log($"INFO no two tables of the same kind ({tables.Count} tables); the table part of this test is skipped.");
            }
            bool Free(LayoutPosition p) => !occupants.ContainsKey(p) && TileManager.GetOccupant((Vector3)p) == default(Entity)
                                           && !live.GetFeatures(p).Any();
            bool Indoor(LayoutPosition p, out Room r) => live.Tiles.TryGetValue(p, out r) && LayoutHelpers.IsInside(r) && r.Type != RoomType.Garden;
            foreach (KeyValuePair<LayoutPosition, Room> t in live.Tiles.OrderBy(t => Math.Abs(t.Key.x) + Math.Abs(t.Key.y)))
            {
                if (pair.t == default || _occR1 != 0)
                {
                    break;
                }
                if (!Indoor(t.Key, out Room ra) || !Free(t.Key))
                {
                    continue;
                }
                foreach (var s in TileOccupants.Sides)
                {
                    LayoutPosition b = t.Key + s.D;
                    if (!Indoor(b, out Room rb) || rb.ID != ra.ID || !Free(b))
                    {
                        continue;
                    }
                    foreach (var s2 in TileOccupants.Sides)
                    {
                        LayoutPosition c = b + s2.D;
                        if (c == t.Key || !Indoor(c, out Room rc) || rc.ID == ra.ID)
                        {
                            continue;
                        }
                        var trial = new LayoutBlueprint(live);
                        if (LayoutEdits.PaintTile(trial, b, rc, occupants) != null || LayoutEdits.CheckConnectivity(trial) != null)
                        {
                            continue;
                        }
                        _occA = t.Key;
                        _occB = b;
                        _occR1 = ra.ID;
                        _occR2 = rc.ID;
                        break;
                    }
                    if (_occR1 != 0)
                    {
                        break;
                    }
                }
            }
            if (pair.t != default && _occR1 == 0)
            {
                Log("INFO no free pair of tiles on a room border for the table part of this test; skipped.");
            }
            if (_occR1 != 0)
            {
                _occT1 = pair.t;
                _occT2 = pair.u;
                DissolveTableSet(_occT1);
                DissolveTableSet(_occT2);
                MoveBy(_occT1, (Vector3)_occA - Pos(_occT1).Rounded());
                MoveBy(_occT2, (Vector3)_occB - Pos(_occT2).Rounded());
                Log($"tables moved side by side onto {Fmt(_occA)} and {Fmt(_occB)} (room {_occR1}); {Fmt(_occB)} borders room {_occR2}.");
            }

            // An appliance on an outer edge tile whose floor can be removed (it would be binned).
            occupants = TileOccupants.Take(EntityManager);
            foreach (KeyValuePair<LayoutPosition, OccupantFlags> o in occupants.OrderBy(o => o.Key.x).ThenBy(o => o.Key.y))
            {
                LayoutPosition p = o.Key;
                if (o.Value != OccupantFlags.Binned || !Indoor(p, out Room room) || live.GetFeatures(p).Any()
                    || TileOccupants.WithNeighbours(p).Any(n => n == _occA || n == _occB)
                    || !TileOccupants.Sides.Any(s => !live.Tiles.ContainsKey(p + s.D)))
                {
                    continue;
                }
                var trial = new LayoutBlueprint(live);
                if (LayoutEdits.PaintTile(trial, p, Room.Null, occupants) != null || LayoutEdits.CheckConnectivity(trial) != null)
                {
                    continue;
                }
                Entity e = AllWith<CAppliance>().FirstOrDefault(a => HasComponent<CPosition>(a) && !HasComponent<CHeldBy>(a)
                                                                     && ((LayoutPosition)Pos(a).Rounded()) == p && !HasComponent<CDoesNotOccupy>(a));
                if (e == default)
                {
                    continue;
                }
                _occE = e;
                _occETile = p;
                _occERoom = room.ID;
                Log($"appliance {EntityManager.GetComponentData<CAppliance>(e).ID} on edge tile {Fmt(p)} (room {room.ID}) will have its floor removed.");
                break;
            }
            if (_occE == default)
            {
                Log("INFO no appliance on an edge tile whose floor could be removed; the warning part of this test is skipped.");
            }
            _occLayoutHash = FloorPlanSerializer.Hash(live);
            _occReady = _occR1 != 0 || _occE != default;
        }

        private bool SameTableSet(Entity a, Entity b) =>
            HasComponent<CPartOfTableSet>(a) && HasComponent<CPartOfTableSet>(b)
            && EntityManager.GetComponentData<CPartOfTableSet>(a).TableSet == EntityManager.GetComponentData<CPartOfTableSet>(b).TableSet;

        private void StepOccupiedGrouped()
        {
            if (!_occReady)
            {
                return;
            }
            if (_occR1 != 0)
            {
                if (!SameTableSet(_occT1, _occT2) && StepElapsed < 5f)
                {
                    Retry(0.25f);
                    return;
                }
                Check(SameTableSet(_occT1, _occT2), "the two tables side by side form one table group");
            }
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null)
            {
                Fail("no player or FloorPlanService to open the editor with");
                _occReady = false;
                return;
            }
            service.RequestOpen(players[0]);
        }

        private void StepOccupiedEdit()
        {
            if (!_occReady)
            {
                return;
            }
            FloorPlanView view = FloorPlanView.TestInstance;
            if ((view == null || !view.TestReady) && StepElapsed < 10f)
            {
                Retry(0.5f);
                return;
            }
            if (view == null || !view.TestReady)
            {
                Fail("the editor didn't open for the appliance test");
                _occReady = false;
                return;
            }
            view.TestSetVisible(true);
            if (_occR1 != 0)
            {
                Check(view.TestOccupants.ContainsKey(_occB), $"the editor knows a table stands on {Fmt(_occB)}");
                view.TestSelectRoom(_occR2);
                view.TestPaint(_occB);
                Check(view.TestDraft[_occB].ID == _occR2, $"repainting the floor under a table into another room is allowed ({view.TestMessage})");
            }
            if (_occE != default)
            {
                view.TestSelectBrush("Outdoors (remove floor)");
                view.TestPaint(_occETile);
                bool erased = !TileOccupants.IsIndoor(view.TestDraft, _occETile);
                Check(erased && view.TestBinnedCount == 1 && view.TestMessage.Contains("thrown away"),
                    $"removing the floor under an appliance is allowed, with a warning ({view.TestBinnedCount} flagged: {view.TestMessage})");
            }
            Shot("23-appliance-floor-edited");
            view.TestApply();
        }

        private void StepOccupiedVerify()
        {
            if (!_occReady)
            {
                return;
            }
            bool closed = AllWith<CFloorPlanSession>().Count == 0;
            bool split = _occR1 == 0 || (HasComponent<CPartOfTableSet>(_occT1) && HasComponent<CPartOfTableSet>(_occT2) && !SameTableSet(_occT1, _occT2));
            bool marked = _occE == default || (EntityManager.Exists(_occE) && HasComponent<CDestroyApplianceAtDay>(_occE));
            if ((!closed || !split || !marked) && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(closed, "the edit with appliances on changed floor was applied");
            LayoutBlueprint live = ReadLayout();
            if (_occR1 != 0)
            {
                Check(live[_occB].ID == _occR2, $"{Fmt(_occB)} is now part of room {_occR2}");
                Check(split, "a new wall between two joined tables splits their table group");
                int across = 0;
                foreach (Entity chair in AllWith<CApplianceGhostChair>())
                {
                    Entity table = EntityManager.GetComponentData<CApplianceGhostChair>(chair).Table;
                    if (EntityManager.Exists(table) && HasComponent<CPosition>(table)
                        && live[(LayoutPosition)Pos(chair).Rounded()].ID != live[(LayoutPosition)Pos(table).Rounded()].ID)
                    {
                        across++;
                    }
                }
                Check(across == 0, $"no chair is left across a wall from its table ({across})");
            }
            if (_occE != default)
            {
                Check(marked, "the appliance left outdoors is marked to be thrown away at the start of the day (the game's bin)");
            }
            Shot("24-appliance-floor-applied");
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count > 0 && service != null)
            {
                service.RequestOpen(players[0]);
            }
        }

        private void StepOccupiedRestore()
        {
            if (!_occReady)
            {
                return;
            }
            FloorPlanView view = FloorPlanView.TestInstance;
            if ((view == null || !view.TestReady) && StepElapsed < 10f)
            {
                Retry(0.5f);
                return;
            }
            if (view == null || !view.TestReady)
            {
                Fail("the editor didn't reopen to put the floor back");
                _occReady = false;
                return;
            }
            if (_occR1 != 0)
            {
                view.TestSelectRoom(_occR1);
                view.TestPaint(_occB);
            }
            if (_occE != default)
            {
                view.TestSelectRoom(_occERoom);
                view.TestPaint(_occETile);
                Check(TileOccupants.IsIndoor(view.TestDraft, _occETile) && view.TestBinnedCount == 0,
                    $"painting the floor back under the outdoor appliance is allowed ({view.TestMessage})");
            }
            view.TestApply();
        }

        private void StepOccupiedVerifyRestored()
        {
            if (!_occReady)
            {
                return;
            }
            bool closed = AllWith<CFloorPlanSession>().Count == 0;
            bool joined = _occR1 == 0 || SameTableSet(_occT1, _occT2);
            bool unmarked = _occE == default || (EntityManager.Exists(_occE) && !HasComponent<CDestroyApplianceAtDay>(_occE));
            if ((!closed || !joined || !unmarked) && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(closed && FloorPlanSerializer.Hash(ReadLayout()) == _occLayoutHash, "the floor is back as it was");
            if (_occR1 != 0)
            {
                Check(joined, "with the wall gone again, the two tables form one group again");
            }
            if (_occE != default)
            {
                Check(unmarked, "the appliance back on indoor floor is no longer marked to be thrown away");
            }
        }

        // ---- a real wall-mounted appliance whose wall is edited away ----
        // A painting is hung on a tile with two walls, facing one of them. The editor then merges
        // the room behind that wall into the painting's room; after Apply the painting must still
        // exist and face its other wall (LayoutCommit's WallMountedFixup; vanilla only turns these
        // when a player interacts with them). Then the floor is put back and the painting removed.

        private Entity _wallE;
        private int _wallId, _wallRoomP, _wallRoomN, _wallHash;
        private LayoutPosition _wallP, _wallN;
        private Orientation _wallFacing;
        private bool _wallReady;

        private void StepWallMountedSetup()
        {
            var all = KitchenData.GameData.Main.Get<KitchenData.Appliance>()
                .Where(a => a.Properties != null && a.Properties.Any(p => p is CMustHaveWall) && !a.Properties.Any(p => p is CFixedRotation)).ToList();
            KitchenData.Appliance pick = new[] { "Painting", "Calm Painting", "Stock Picture" }
                .Select(n => all.FirstOrDefault(a => a.Name == n)).FirstOrDefault(a => a != null) ?? all.FirstOrDefault();
            if (pick == null)
            {
                Log("INFO no wall-mounted appliance that can turn in this game version; the real wall-mounted test is skipped.");
                return;
            }
            LayoutBlueprint live = ReadLayout();
            Dictionary<LayoutPosition, OccupantFlags> occupants = TileOccupants.Take(EntityManager);
            bool Free(LayoutPosition p) => !occupants.ContainsKey(p) && TileManager.GetOccupant((Vector3)p) == default(Entity) && !live.GetFeatures(p).Any();
            bool Indoor(LayoutPosition p, out Room r) => live.Tiles.TryGetValue(p, out r) && LayoutHelpers.IsInside(r) && r.Type != RoomType.Garden;
            foreach (KeyValuePair<LayoutPosition, Room> t in live.Tiles.OrderBy(t => Math.Abs(t.Key.x) + Math.Abs(t.Key.y)))
            {
                LayoutPosition p = t.Key;
                if (!Indoor(p, out Room rp) || !Free(p) || TileOccupants.WithNeighbours(p).Any(n => occupants.ContainsKey(n)))
                {
                    continue;
                }
                foreach (var s in TileOccupants.Sides)
                {
                    LayoutPosition n = p + s.D;
                    if (!TileOccupants.IsSolidWall(live, p, s.D) || !Indoor(n, out Room rn) || rn.ID == rp.ID || !Free(n))
                    {
                        continue;
                    }
                    var occ = new Dictionary<LayoutPosition, OccupantFlags>(occupants) { [p] = OccupantFlags.WallMounted | OccupantFlags.Binned };
                    var trial = new LayoutBlueprint(live);
                    if (LayoutEdits.PaintTile(trial, n, rp, occ) != null || LayoutEdits.CheckConnectivity(trial) != null
                        || TileOccupants.IsSolidWall(trial, p, s.D) || !TileOccupants.HasSolidWall(trial, p))
                    {
                        continue;
                    }
                    _wallP = p;
                    _wallN = n;
                    _wallFacing = s.O;
                    _wallRoomP = rp.ID;
                    _wallRoomN = rn.ID;
                    break;
                }
                if (_wallRoomP != 0)
                {
                    break;
                }
            }
            if (_wallRoomP == 0)
            {
                Log("INFO no free tile with two walls next to a room it can merge with; the real wall-mounted test is skipped.");
                return;
            }
            _wallId = pick.ID;
            _wallHash = FloorPlanSerializer.Hash(live);
            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new CCreateAppliance { ID = pick.ID });
            EntityManager.AddComponentData(e, new CPosition((Vector3)_wallP, _wallFacing.ToRotation()));
            Log($"hung '{pick.Name}' on {Fmt(_wallP)} facing {_wallFacing} (the wall to {Fmt(_wallN)}, room {_wallRoomN}).");
            _wallReady = true;
        }

        private void StepWallMountedSpawned()
        {
            if (!_wallReady)
            {
                return;
            }
            _wallE = AllWith<CAppliance>().FirstOrDefault(a => EntityManager.GetComponentData<CAppliance>(a).ID == _wallId
                                                               && HasComponent<CPosition>(a) && HasComponent<CMustHaveWall>(a)
                                                               && (LayoutPosition)Pos(a).Rounded() == _wallP);
            if (_wallE == default && StepElapsed < 5f)
            {
                Retry(0.25f);
                return;
            }
            if (_wallE == default)
            {
                Fail("the wall-mounted appliance wasn't created");
                _wallReady = false;
                return;
            }
            Check(EntityManager.GetComponentData<CPosition>(_wallE).Rotation.ToOrientation() == _wallFacing,
                $"(setup) the wall-mounted appliance faces {_wallFacing}");
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count == 0 || service == null)
            {
                Fail("no player or FloorPlanService to open the editor with");
                _wallReady = false;
                return;
            }
            service.RequestOpen(players[0]);
        }

        private bool WallEditorReady(string what)
        {
            FloorPlanView view = FloorPlanView.TestInstance;
            if ((view == null || !view.TestReady) && StepElapsed < 10f)
            {
                Retry(0.5f);
                return false;
            }
            if (view == null || !view.TestReady)
            {
                Fail($"the editor didn't open {what}");
                _wallReady = false;
                return false;
            }
            view.TestSetVisible(true);
            return true;
        }

        private void StepWallMountedEdit()
        {
            if (!_wallReady || !WallEditorReady("for the wall-mounted test"))
            {
                return;
            }
            FloorPlanView view = FloorPlanView.TestInstance;
            Check(view.TestOccupants.TryGetValue(_wallP, out OccupantFlags f) && (f & OccupantFlags.WallMounted) != 0,
                $"the editor knows a wall-mounted appliance hangs on {Fmt(_wallP)}");
            view.TestSelectRoom(_wallRoomP);
            view.TestPaint(_wallN);
            Check(view.TestDraft[_wallN].ID == _wallRoomP,
                $"removing the wall a wall-mounted appliance faces is allowed while it has another wall ({view.TestMessage})");
            view.TestApply();
        }

        private void StepWallMountedVerify()
        {
            if (!_wallReady)
            {
                return;
            }
            bool closed = AllWith<CFloorPlanSession>().Count == 0;
            if (!closed && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(closed, "the wall-mounted edit was applied");
            LayoutBlueprint live = ReadLayout();
            bool alive = EntityManager.Exists(_wallE) && HasComponent<CPosition>(_wallE);
            Orientation now = alive ? EntityManager.GetComponentData<CPosition>(_wallE).Rotation.ToOrientation() : Orientation.Null;
            int side = Array.FindIndex(TileOccupants.Sides, s => s.O == now);
            Check(alive && !HasComponent<CDestroyApplianceAtDay>(_wallE), "the wall-mounted appliance is still there and not marked for the bin");
            Check(alive && side >= 0 && now != _wallFacing && TileOccupants.IsSolidWall(live, _wallP, TileOccupants.Sides[side].D),
                $"the wall-mounted appliance turned to face a remaining wall (was {_wallFacing}, now {now})");
            Shot("25-wall-mounted-turned");
            List<Entity> players = AllWith<CPlayer>();
            FloorPlanService service = World.GetExistingSystem<FloorPlanService>();
            if (players.Count > 0 && service != null)
            {
                service.RequestOpen(players[0]);
            }
        }

        private void StepWallMountedRestore()
        {
            if (!_wallReady || !WallEditorReady("to put the wall back"))
            {
                return;
            }
            FloorPlanView view = FloorPlanView.TestInstance;
            view.TestSelectRoom(_wallRoomN);
            view.TestPaint(_wallN);
            view.TestApply();
        }

        private void StepWallMountedVerifyRestored()
        {
            if (!_wallReady)
            {
                return;
            }
            bool closed = AllWith<CFloorPlanSession>().Count == 0;
            if (!closed && StepElapsed < 8f)
            {
                Retry(0.25f);
                return;
            }
            Check(closed && FloorPlanSerializer.Hash(ReadLayout()) == _wallHash, "the wall is back as it was");
            if (EntityManager.Exists(_wallE))
            {
                EntityManager.DestroyEntity(_wallE); // as vanilla's DestroyAppliancesAtDay does
            }
        }

        private float _fovBefore;
        private float _centreYBefore;

        private Vector3 RestaurantCentre()
        {
            Bounds b = CurrentBounds();
            return new Vector3(b.center.x, 0f, b.center.z);
        }

        private void StepPlayCamera()
        {
            var framing = PlayCamera.TestFraming;
            if ((framing == null || !PlayCamera.TestApplied || FloorPlanCamera.Active || !FloorPlanCamera.TestTiltDone) && StepElapsed < 8f)
            {
                Retry(0.5f);
                return;
            }
            Check(framing != null && PlayCamera.TestApplied, $"the play camera is active in the restaurant ({PlayCamera.TestDiagnostics})");
            if (framing == null)
            {
                return;
            }
            float expectY = PlayCamera.TestVanillaScreenY - PlayCamera.HudFraction / 2f;
            float expectSize = PlayCamera.TestVanillaSize * (1f - PlayCamera.HudFraction);
            Check(Mathf.Abs(framing.m_ScreenY - expectY) < 0.001f && Mathf.Abs(framing.m_GroupFramingSize - expectSize) < 0.001f,
                $"the HUD margin is applied (screen y {framing.m_ScreenY:0.000}, framing {framing.m_GroupFramingSize:0.000}; expected {expectY:0.000}, {expectSize:0.000})");
            Camera cam = Camera.main;
            Bounds b = CurrentBounds();
            Vector3 front = new Vector3(b.center.x, 0f, b.min.z - 0.5f);
            float frontY = cam != null ? cam.WorldToViewportPoint(front).y : -1f;
            Check(frontY > PlayCamera.HudFraction, $"the front of the restaurant is above the player cards (viewport y {frontY:0.000}, cards below {PlayCamera.HudFraction})");
            _fovBefore = cam != null ? cam.fieldOfView : 0f;
            Log($"play camera: FOV {_fovBefore:0.0}, front edge at viewport y {frontY:0.000}.");
            Shot("17-play-camera-hud-margin");
        }

        private void StepPlayCameraZoom()
        {
            if (StepElapsed < 0.1f)
            {
                PlayCamera.TestSet(2f, Vector2.zero);
            }
            float fov = Camera.main != null ? Camera.main.fieldOfView : 0f;
            if (fov > _fovBefore * 0.7f && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(fov < _fovBefore * 0.7f, $"zooming in narrows the view (FOV {fov:0.0}, was {_fovBefore:0.0})");
            _centreYBefore = Camera.main != null ? Camera.main.WorldToViewportPoint(RestaurantCentre()).y : 0f;
            Shot("18-play-camera-zoomed");
        }

        private void StepPlayCameraPan()
        {
            if (StepElapsed < 0.1f)
            {
                PlayCamera.TestSet(2f, new Vector2(0f, -0.3f)); // look further down (towards the street)
            }
            float y = Camera.main != null ? Camera.main.WorldToViewportPoint(RestaurantCentre()).y : 0f;
            if (y < _centreYBefore + 0.2f && StepElapsed < 5f)
            {
                Retry(0.5f);
                return;
            }
            Check(y > _centreYBefore + 0.2f, $"panning down moves the restaurant up the screen (centre at viewport y {y:0.000}, was {_centreYBefore:0.000})");
            Shot("19-play-camera-panned");
        }

        private void StepPlayCameraFollow()
        {
            if (StepElapsed < 0.1f)
            {
                PlayCamera.TestSetFollowSilently(true);
                PlayCamera.TestSet(2.5f, new Vector2(0.9f, 0.9f)); // far away from the chef
            }
            Camera cam = Camera.main;
            List<Entity> players = AllWith<CPlayer>();
            Vector3 chef = players.Count > 0 ? GetComponent<CPosition>(players[0]).Position : Vector3.zero;
            Vector3 v = cam != null ? cam.WorldToViewportPoint(chef) : new Vector3(-1f, -1f, 0f);
            bool inView = v.x > 0.1f && v.x < 0.9f && v.y > 0.1f && v.y < 0.9f;
            if (!inView && StepElapsed < 6f)
            {
                Retry(0.5f);
                return;
            }
            Check(PlayCamera.TestLocalChefCount >= 1, $"follow finds this machine's chef ({PlayCamera.TestLocalChefCount})");
            Check(inView, $"follow brings the chef back into view when zoomed in (viewport {v.x:0.00}, {v.y:0.00}; pan {PlayCamera.Pan})");
            Shot("20-play-camera-follow");
            PlayCamera.TestSetFollowSilently(false);
        }

        // ---- the camera steps back while another UI (another mod's window) has the player's input ----

        /// <summary>Stands in for another mod's window: takes every player's input, as an open shop window does.</summary>
        private sealed class ClaimsInput : Controllers.IInputConsumer
        {
            public Controllers.InputConsumerState TakeInput(int player_id, Controllers.InputState state) => Controllers.InputConsumerState.Consumed;
        }

        private static ClaimsInput _otherUi;

        private void StepCameraOtherUiOpen()
        {
            if (StepElapsed < 0.1f)
            {
                Check(PlayCamera.TestMouseFree, "with no other UI open, the camera may use the mouse");
                _otherUi = new ClaimsInput();
                Controllers.LocalInputSourceConsumers.Register(_otherUi);
                Retry(0.5f);
                return;
            }
            Check(PlayCamera.TestProbeLast, "the camera's input probe stays last in the consumer queue");
            Check(!PlayCamera.TestMouseFree, "while another UI takes the player's input, the camera leaves the mouse wheel and buttons alone");
        }

        private void StepCameraOtherUiClosed()
        {
            if (_otherUi != null)
            {
                Controllers.LocalInputSourceConsumers.Remove(_otherUi);
                _otherUi = null;
                Retry(0.5f);
                return;
            }
            Check(PlayCamera.TestMouseFree, "once the other UI closes, the camera uses the mouse again");
        }

        // ---- "Camera Controls" in the pause menu ----

        private static PlayerPauseView _pause;
        private static int _shotPage = -1;

        private static void CallPause(string method, params object[] args)
        {
            MethodInfo m = HarmonyLib.AccessTools.Method(typeof(PlayerPauseView), method);
            if (m == null)
            {
                throw new MissingMethodException(nameof(PlayerPauseView), method);
            }
            m.Invoke(_pause, args);
        }

        private void StepCameraControlsPause()
        {
            _pause = UnityEngine.Object.FindObjectOfType<PlayerPauseView>();
            List<Entity> players = AllWith<CPlayer>();
            if (_pause == null || players.Count == 0)
            {
                Fail("no pause view (or no player) to open the pause menu with");
                return;
            }
            if (StepElapsed < 0.1f)
            {
                // What pressing the menu button does (PlayerPauseView.TakeInput).
                int id = GetComponent<CPlayer>(players[0]).ID;
                CallPause("CreateForPlayer", id);
                CallPause("SetPlayer", id);
                Retry(1f);
                return;
            }
            bool button = _pause.GetComponentsInChildren<TMPro.TextMeshPro>(true).Any(t => t.text == CameraControlsButtonPatch.Label);
            Check(button, $"the pause menu has a \"{CameraControlsButtonPatch.Label}\" button");
            Shot("21-pause-menu");
        }

        private void StepCameraControlsOpen()
        {
            if (_pause == null)
            {
                return;
            }
            CallPause("SetMenu", typeof(CameraControlsMenu), false, false);
        }

        private void StepCameraControlsPages()
        {
            CameraControlsElement panel = UnityEngine.Object.FindObjectOfType<CameraControlsElement>();
            if (panel == null && StepElapsed < 3f)
            {
                Retry(0.5f);
                return;
            }
            Check(panel != null, "the Camera Controls panel opens from the pause menu");
            if (panel == null)
            {
                _pause?.Hide();
                return;
            }
            // Page through like the Recipes panel. A screenshot is captured at the end of the frame,
            // so each page is shot on one call and turned on the next.
            int page = panel.TestPage;
            if (_shotPage != page)
            {
                _shotPage = page;
                Shot($"22-camera-controls-page{page + 1}");
                Retry(0.6f);
                return;
            }
            if (page < CameraControlsElement.PageCount - 1)
            {
                panel.HandleInteraction(new Controllers.InputState { MenuRight = Controllers.ButtonState.Pressed });
                Retry(0.6f);
                return;
            }
            Check(panel.TestPage == CameraControlsElement.PageCount - 1, $"the panel pages through to the last page ({panel.TestPage + 1} of {CameraControlsElement.PageCount})");
            for (int i = 0; i < CameraControlsElement.PageCount; i++)
            {
                (string title, string body) = CameraControlsElement.Content(i);
                Log($"camera controls page {i + 1}: {title}: {body.Replace("\n", " / ")}");
            }
            _pause.Hide();
            Check(!_pause.GetComponentsInChildren<CameraControlsElement>(false).Any(), "closing the pause menu hides the panel");
        }

        private void StepPlayCameraReset()
        {
            if (StepElapsed < 0.1f)
            {
                PlayCamera.TestSet(1f, Vector2.zero);
            }
            float fov = Camera.main != null ? Camera.main.fieldOfView : 0f;
            if (Mathf.Abs(fov - _fovBefore) > _fovBefore * 0.02f && StepElapsed < 6f)
            {
                Retry(0.5f);
                return;
            }
            Check(Mathf.Abs(fov - _fovBefore) <= _fovBefore * 0.02f, $"resetting the view restores the framing (FOV {fov:0.0}, was {_fovBefore:0.0})");
        }

        // ---- scenery bulldozer: grow into outdoor scenery ----

        private static List<(LayoutPosition src, LayoutPosition dst)> _dozed = new List<(LayoutPosition, LayoutPosition)>();
        private static int _sceneryBefore, _blockersBefore;
        private static HashSet<LayoutPosition> _floorSceneryBefore = new HashSet<LayoutPosition>();
        private static List<Entity> _expectGone = new List<Entity>();
        private static bool _haveDoze;

        private int CountBlockers() => AllWith<CAppliance>().Count(e => EntityManager.GetComponentData<CAppliance>(e).ID == KitchenData.AssetReference.OutdoorMovementBlocker);

        /// <summary>
        /// Grows a strip from the building into the nearest outdoor scenery (within 6 tiles), so
        /// the commit's SceneryBulldozer has to remove what's on or beside the new floor.
        /// </summary>
        private void StepBulldozeScenery()
        {
            LayoutBlueprint bp = ReadLayout();
            Dictionary<LayoutPosition, List<Entity>> scenery = SceneryBulldozer.MapScenery(EntityManager);
            _sceneryBefore = scenery.Values.Sum(l => l.Count);
            // Vanilla has some CDoesNotOccupy items indoors already; only new arrivals on floor count.
            _floorSceneryBefore = new HashSet<LayoutPosition>(scenery.Keys.Where(t => bp.Tiles.TryGetValue(t, out Room r) && r.Type != RoomType.NoRoom));
            foreach (LayoutPosition t in _floorSceneryBefore)
            {
                Log($"INFO vanilla CDoesNotOccupy item already indoors at {Fmt(t)}: appliance {string.Join(",", scenery[t].Select(e => EntityManager.GetComponentData<CAppliance>(e).ID))}");
            }
            _blockersBefore = CountBlockers();
            Log($"scenery before: {_sceneryBefore} piece(s) on {scenery.Count} tile(s); {_blockersBefore} outdoor movement blocker(s).");
            if (_sceneryBefore == 0)
            {
                Log("INFO this restaurant setting has no outdoor scenery; bulldozer not exercised.");
                return;
            }

            LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            foreach (int reach in Enumerable.Range(1, 6))
            {
                foreach (LayoutPosition d in dirs)
                {
                    IEnumerable<LayoutPosition> edges = bp.Tiles
                        .Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden && !bp.Tiles.ContainsKey(t.Key + d))
                        .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y));
                    foreach (LayoutPosition s in edges)
                    {
                        var hit = new LayoutPosition(s.x + d.x * reach, s.y + d.y * reach);
                        if (!scenery.ContainsKey(hit))
                        {
                            continue;
                        }
                        LayoutBlueprint trial = new LayoutBlueprint(bp);
                        var strip = new List<(LayoutPosition, LayoutPosition)>();
                        LayoutPosition src = s;
                        bool ok = true;
                        for (int k = 0; k < reach; k++)
                        {
                            LayoutPosition dst = src + d;
                            if (LayoutEdits.TogglePaint(trial, src, dst, BlocksEdit) != null)
                            {
                                ok = false;
                                break;
                            }
                            strip.Add((src, dst));
                            src = dst;
                        }
                        if (!ok)
                        {
                            continue;
                        }
                        _dozed = strip;
                        _expectGone = SceneryBulldozer.Conflicting(EntityManager, bp, strip.Select(x => x.Item2).ToList());
                        Log($"growing {strip.Count} tile(s) from {Fmt(s)} toward {Fmt(d)} into scenery at {Fmt(hit)} ({scenery[hit].Count} piece(s)); {_expectGone.Count} piece(s) on or beside the strip");
                        Shot("07b-before-bulldoze");
                        Apply(trial);
                        _haveDoze = true;
                        Check(LayoutCommit.Last.SceneryRemoved == _expectGone.Count && _expectGone.Count >= 1,
                            $"the commit removed the scenery on and beside the new floor ({LayoutCommit.Last.SceneryRemoved} of {_expectGone.Count}; moved {LayoutCommit.Last.SceneryMoved})");
                        return;
                    }
                }
            }
            Log("INFO no scenery within 6 tiles of a growable edge; bulldozer not exercised.");
        }

        private void StepVerifyBulldoze()
        {
            if (!_haveDoze)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            Dictionary<LayoutPosition, List<Entity>> scenery = SceneryBulldozer.MapScenery(EntityManager);
            var onFloor = scenery.Keys.Where(t => bp.Tiles.TryGetValue(t, out Room r) && r.Type != RoomType.NoRoom && !_floorSceneryBefore.Contains(t)).ToList();
            Check(onFloor.Count == 0, $"no scenery was left on floor ({onFloor.Count}: {string.Join(" ", onFloor.Select(Fmt))})");
            var stillBeside = SceneryBulldozer.Conflicting(EntityManager, bp, _dozed.Select(x => x.Item2).ToList());
            Check(stillBeside.Count == 0, $"no scenery left within a tile of the new floor ({stillBeside.Count})");
            Check(_expectGone.All(e => !EntityManager.HasComponent<CAppliance>(e)), "every conflicting piece's entity is gone");
            int now = scenery.Values.Sum(l => l.Count);
            Check(now == _sceneryBefore - LayoutCommit.Last.SceneryRemoved, $"scenery count accounts for every piece ({_sceneryBefore} before, {now} now, {LayoutCommit.Last.SceneryRemoved} removed)");
            Check(CountBlockers() == _blockersBefore, $"the outdoor movement blockers are untouched ({_blockersBefore} before, {CountBlockers()} now)");
            foreach ((LayoutPosition _, LayoutPosition dst) in _dozed)
            {
                Check(NavMesh.SamplePosition((Vector3)dst, out NavMeshHit _, 0.3f, NavMesh.AllAreas), $"navmesh covers bulldozed tile {Fmt(dst)}");
            }
            if (_dozed.Count >= 2)
            {
                float along = PathLength(_dozed[0].dst, _dozed.Last().dst);
                Check(along > 0f, $"navmesh path along the bulldozed strip ({along:0.0})");
            }
            Shot("07c-bulldozed");
        }

        private void StepShrinkBulldoze()
        {
            if (!_haveDoze)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            for (int i = _dozed.Count - 1; i >= 0; i--)
            {
                string refused = LayoutEdits.TogglePaint(bp, _dozed[i].src, _dozed[i].dst, BlocksEdit);
                if (refused != null)
                {
                    Fail($"shrink bulldozed tile {Fmt(_dozed[i].dst)} refused: {refused}");
                    return;
                }
            }
            Apply(bp);
            Check(CurrentBounds() == _baselineBounds, "CBounds back to the baseline after the bulldozer strip");
        }


        private static LayoutPosition? FrontInside(LayoutBlueprint bp)
        {
            Feature front = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (front == null)
            {
                return null;
            }
            return LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1 : front.Tile2;
        }

        private void StepRefusals()
        {
            LayoutBlueprint bp = ReadLayout();

            // Occupied indoor tile.
            // (Not a tile with a door: those are refused for the door first, which isn't what this checks.)
            LayoutPosition? occupied = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && BlocksEdit(t.Key) && !bp.GetFeatures(t.Key).Any())
                .Select(t => (LayoutPosition?)t.Key).FirstOrDefault();
            if (occupied.HasValue)
            {
                string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), occupied.Value, occupied.Value, BlocksEdit);
                Check(r != null && r.Contains("appliance"), $"refuses painting an occupied tile ({r})");
            }
            else
            {
                Log("INFO no occupied indoor tile to test refusal with.");
            }

            // Front door tile.
            Feature front = bp.Features.FirstOrDefault(f => f.Type == FeatureType.FrontDoor);
            if (front != null)
            {
                LayoutPosition inside = LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1 : front.Tile2;
                string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), inside, inside, _ => false);
                Check(r != null && r.Contains("door"), $"refuses painting the front-door tile ({r})");
            }

            // Grow while standing outdoors (a far corner that's never stored).
            {
                var far = new LayoutPosition(-LayoutEdits.MaxExtent, LayoutEdits.MaxExtent);
                string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), far, far + new LayoutPosition(1, 0), _ => false);
                Check(r != null && r.Contains("inside the room"), $"refuses growing from an outdoor source ({r})");
            }

            // Grow toward the street: below a front-row tile that has no door on it. Occupancy is
            // ignored here, because front decorations would otherwise refuse it first.
            if (front != null)
            {
                int frontRow = LayoutHelpers.IsInside(bp[front.Tile1]) ? front.Tile1.y : front.Tile2.y;
                LayoutPosition? row = bp.Tiles.Where(t => t.Key.y == frontRow && LayoutHelpers.IsInside(t.Value) && bp.GetFeatures(t.Key).Count == 0)
                    .Select(t => (LayoutPosition?)t.Key).FirstOrDefault();
                if (row.HasValue)
                {
                    string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), row.Value, row.Value + new LayoutPosition(0, -1), _ => false);
                    Check(r != null && r.Contains("street"), $"refuses growing toward the street ({r})");
                }
            }

            // Grow onto a tile that isn't adjacent to the source.
            {
                KeyValuePair<LayoutPosition, Room> top = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value)).OrderByDescending(t => t.Key.y).First();
                string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), top.Key, top.Key + new LayoutPosition(0, 2), _ => false);
                Check(r != null && r.Contains("right next to"), $"refuses growing onto a non-adjacent tile ({r})");
            }

            // Size limit: on a copy, extend one row to the east limit, then try one more.
            {
                LayoutBlueprint copy = new LayoutBlueprint(bp);
                KeyValuePair<LayoutPosition, Room> east = copy.Tiles.Where(t => LayoutHelpers.IsInside(t.Value)).OrderByDescending(t => t.Key.x).First();
                for (int x = east.Key.x + 1; x <= LayoutEdits.MaxExtent; x++)
                {
                    copy[x, east.Key.y] = east.Value;
                }
                var edge = new LayoutPosition(LayoutEdits.MaxExtent, east.Key.y);
                string r = LayoutEdits.TogglePaint(copy, edge, edge + new LayoutPosition(1, 0), _ => false);
                Check(r != null && r.Contains("maximum"), $"refuses growing past the maximum size ({r})");
            }

            // Edge inside one room (no wall to cycle).
            KeyValuePair<LayoutPosition, Room> any = bp.Tiles.First(t => LayoutHelpers.IsInside(t.Value) && bp.AdjacentInRoom(t.Key).Count > 0);
            string h = LayoutEdits.CycleEdge(new LayoutBlueprint(bp), any.Key, bp.AdjacentInRoom(any.Key)[0], out _);
            Check(h != null && h.Contains("no wall"), $"refuses cycling an edge with no wall ({h})");

            // Outer wall (the front door's edge leads outside).
            if (front != null)
            {
                string r = LayoutEdits.CycleEdge(new LayoutBlueprint(bp), front.Tile1, front.Tile2, out _);
                Check(r != null, $"refuses cycling the front door / outer wall ({r})");
            }

            // A vanilla door that's the only way into a room can't be removed.
            bool neededTested = false;
            foreach (Feature d in bp.Features.Where(f => f.Type == FeatureType.Door))
            {
                LayoutBlueprint copy = new LayoutBlueprint(bp);
                string r = LayoutEdits.CycleEdge(copy, d.Tile1, d.Tile2, out FeatureType? state);
                if (r != null && r.Contains("needed"))
                {
                    Check(state == FeatureType.Door && copy.Features.Any(f => f.Type == FeatureType.Door && SameEdge(f, d.Tile1, d.Tile2)),
                        $"refuses removing a needed door and leaves it in place ({r})");
                    neededTested = true;
                    break;
                }
            }
            if (!neededTested)
            {
                Log("INFO no vanilla door is the only way into a room here; needed-door refusal not exercised.");
            }

            // Disconnection rule: try removing each free indoor tile on a copy; see if any trips it.
            bool exercised = false;
            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
            {
                if (!LayoutHelpers.IsInside(t.Value) || Occupied(t.Key))
                {
                    continue;
                }
                string r = LayoutEdits.TogglePaint(new LayoutBlueprint(bp), t.Key, t.Key, BlocksEdit);
                if (r != null && r.Contains("cut off"))
                {
                    exercised = true;
                    Log($"INFO connectivity rule refused removing {Fmt(t.Key)}: {r}");
                    break;
                }
            }
            if (!exercised)
            {
                Log("INFO no single-tile removal disconnects this layout; connectivity refusal not exercised.");
            }

            // The live layout must be untouched by all of the above (all were on copies).
            Check(SameLayout(bp, ReadLayout(), out _), "refusal checks didn't modify the live layout");
        }

        private void StepRoundTrip()
        {
            bool same = SameLayout(_baseline, ReadLayout(), out string diff);
            Check(same, $"after all edits and reverts the layout matches the baseline{(same ? "" : ": " + diff)}");
            Shot("05-final");
        }

        // ---------------- persistence + day ----------------

        private static List<LayoutPosition> _dayRegion = new List<LayoutPosition>();
        private static LayoutBlueprint _dayExpected;
        private static Bounds _dayBounds;
        private static Vector3 _dayTablePos;
        private static List<Vector3> _dayChairPos = new List<Vector3>();
        private static bool _haveDay;
        private static int _pausedLogs, _dayShots;
        private static float _nextDayLog;
        private static bool _loggedAssignment;
        private static bool _seated;
        private static float _lastTimeOfDay = -1f, _clockMovedAt, _nextPausedLog;

        /// <summary>
        /// Grows a block of new dining floor, moves one table (with its chairs) into it, and removes
        /// the other tables. Customers then have to walk onto the new floor to sit down.
        /// </summary>
        // ---- lighting probe: how do the shaders read the night-lighting map? (investigation) ----

        private LightingProbe _probe;
        private readonly List<float[,]> _probeGrids = new List<float[,]>();
        private const int ProbePx = 5, ProbePy = 7; // the single lit map pixel in patterns 2 and 3

        private static readonly string[] ProbeNames =
        {
            "all outdoor", "all indoor", "one pixel, clamp", "one pixel, repeat", "global outdoor + per-renderer indoor",
        };

        private void ProbeRequest(int index)
        {
            switch (index)
            {
                case 0: _probe.Request(LightingProbe.Pattern32((x, y) => false, TextureWrapMode.Clamp)); break;
                case 1: _probe.Request(LightingProbe.Pattern32((x, y) => true, TextureWrapMode.Clamp)); break;
                case 2: _probe.Request(LightingProbe.Pattern32((x, y) => x == ProbePx && y == ProbePy, TextureWrapMode.Clamp)); break;
                case 3: _probe.Request(LightingProbe.Pattern32((x, y) => x == ProbePx && y == ProbePy, TextureWrapMode.Repeat)); break;
                case 4:
                    int n = _probe.OverridePerRenderer(LightingProbe.Pattern32((x, y) => true, TextureWrapMode.Clamp));
                    Log($"probe: gave {n} renderers their own all-indoor map.");
                    _probe.Request(LightingProbe.Pattern32((x, y) => false, TextureWrapMode.Clamp));
                    break;
            }
        }

        private void StepLightingProbe()
        {
            if (_probe == null)
            {
                _probe = new LightingProbe();
                _probe.Setup();
                _probeGrids.Clear();
                ProbeRequest(0);
                Retry(0.4f);
                return;
            }
            int index = _probeGrids.Count;
            string png = SelfTest.ShotDir != null ? Path.Combine(SelfTest.ShotDir, $"probe-{index}.png") : null;
            _probeGrids.Add(_probe.Read(png));
            if (index == 4)
            {
                _probe.RestorePerRenderer();
            }
            if (_probeGrids.Count < ProbeNames.Length)
            {
                ProbeRequest(_probeGrids.Count);
                Retry(0.4f);
                return;
            }
            _probe.Dispose();
            ReportLightingProbe();
        }

        private void ReportLightingProbe()
        {
            int half = LightingProbe.Half;
            float[,] outdoor = _probeGrids[0], indoor = _probeGrids[1];
            var sensitive = new List<(int x, int z)>();
            for (int i = 0; i < half * 2; i++)
            {
                for (int j = 0; j < half * 2; j++)
                {
                    if (Mathf.Abs(indoor[i, j] - outdoor[i, j]) > 0.03f)
                    {
                        sensitive.Add((i - half, j - half));
                    }
                }
            }
            if (sensitive.Count == 0)
            {
                Log("INFO probe: no tile's brightness depends on the map (night fade had no effect?)");
                return;
            }
            Log($"probe: {sensitive.Count} tiles react to the map; x {sensitive.Min(t => t.x)}..{sensitive.Max(t => t.x)}, z {sensitive.Min(t => t.z)}..{sensitive.Max(t => t.z)}; " +
                $"mean brightness outdoor {sensitive.Average(t => outdoor[t.x + half, t.z + half]):0.000}, indoor {sensitive.Average(t => indoor[t.x + half, t.z + half]):0.000}");

            for (int k = 2; k < _probeGrids.Count; k++)
            {
                float[,] g = _probeGrids[k];
                var lit = sensitive.Where(t =>
                {
                    float o = outdoor[t.x + half, t.z + half], n = indoor[t.x + half, t.z + half];
                    return (g[t.x + half, t.z + half] - o) / (n - o) > 0.5f;
                }).ToList();
                string list = string.Join(" ", lit.Take(24).Select(t => $"({t.x},{t.z})"));
                Log($"probe [{ProbeNames[k]}]: {lit.Count} of {sensitive.Count} reacting tiles lit{(lit.Count > 0 && lit.Count <= 24 ? ": " + list : lit.Count > 24 ? " (first 24: " + list + ")" : "")}");
            }
            Check(true, "lighting probe ran (results above)");
        }

        // ---- far lighting: floor beyond the lighting map's 32 x 32 tiles is lit correctly at night ----

        private const int FarWest = -26;
        private List<(LayoutPosition, LayoutPosition)> _farStrip;
        private readonly List<float[,]> _farGrids = new List<float[,]>();
        private LightingProbe _farProbe;
        private Bounds _boundsBeforeFar;

        private void StepGrowFarWest()
        {
            LayoutBlueprint bp = ReadLayout();
            var inside = bp.Tiles.Where(t => LayoutHelpers.IsInside(t.Value) && t.Value.Type != RoomType.Garden).Select(t => t.Key).ToList();
            foreach (int y in inside.Select(p => p.y).Distinct().OrderBy(y => Math.Abs(y - 2)))
            {
                var edge = new LayoutPosition(inside.Where(p => p.y == y).Min(p => p.x), y);
                LayoutBlueprint trial = new LayoutBlueprint(bp);
                var strip = new List<(LayoutPosition, LayoutPosition)>();
                LayoutPosition src = edge;
                bool ok = true;
                while (src.x > FarWest)
                {
                    LayoutPosition dst = src + new LayoutPosition(-1, 0);
                    if (LayoutEdits.TogglePaint(trial, src, dst, BlocksEdit) != null)
                    {
                        ok = false;
                        break;
                    }
                    strip.Add((src, dst));
                    src = dst;
                }
                if (!ok || strip.Count == 0)
                {
                    continue;
                }
                _farStrip = strip;
                _boundsBeforeFar = CurrentBounds();
                Log($"far-lighting strip: {strip.Count} tiles from {Fmt(edge)} to {Fmt(strip.Last().Item2)}");
                Apply(trial);
                return;
            }
            Fail($"no row could grow west to x = {FarWest} for the far-lighting test");
        }

        private void FarRequest(int index)
        {
            switch (index)
            {
                case 0: FarLighting.Suspended = true; FarLighting.Scan(); _farProbe.Request(LightingProbe.Pattern32((x, y) => false, TextureWrapMode.Clamp)); break;
                case 1: _farProbe.Request(LightingProbe.Pattern32((x, y) => true, TextureWrapMode.Clamp)); break;
                case 2: _farProbe.Request(null); break; // the real map, far lighting off
                case 3: FarLighting.Suspended = false; FarLighting.Scan(); _farProbe.Request(null); break; // and on
            }
        }

        private void StepVerifyFarLighting()
        {
            if (_farStrip == null)
            {
                return;
            }
            if (_farProbe == null)
            {
                if (FarLighting.LastOverridden == 0 && StepElapsed < 6f)
                {
                    Retry(0.5f); // wait for the view rebuild and the first scan
                    return;
                }
                Check(FarLighting.LastOverridden > 0, $"far lighting gives renderers beyond the map their own map ({FarLighting.LastOverridden}, {FarLighting.LastOversized} oversized)");
                _farProbe = new LightingProbe();
                _farProbe.Setup();
                _farGrids.Clear();
                FarRequest(0);
                Retry(0.4f);
                return;
            }
            string png = SelfTest.ShotDir != null ? Path.Combine(SelfTest.ShotDir, $"far-{_farGrids.Count}.png") : null;
            _farGrids.Add(_farProbe.Read(png));
            if (_farGrids.Count < 4)
            {
                FarRequest(_farGrids.Count);
                Retry(0.4f);
                return;
            }
            _farProbe.Dispose();
            _farProbe = null;
            FarLighting.Suspended = false;

            int half = LightingProbe.Half;
            LayoutBlueprint bp = ReadLayout();
            float[,] o = _farGrids[0], n = _farGrids[1];
            int indoor = 0, outdoor = 0;
            int[] litIndoor = new int[2], darkOutdoor = new int[2];
            // The far region: tiles past the map's west edge, in the strip's rows and the two either side.
            int y0 = _farStrip[0].Item1.y;
            for (int x = FarWest - 2; x < RoomLayoutMapPatch.First; x++)
            {
                for (int z = y0 - 2; z <= y0 + 2; z++)
                {
                    int i = x + half, j = z + half;
                    if (Mathf.Abs(n[i, j] - o[i, j]) < 0.03f)
                    {
                        continue; // this spot doesn't react to the map (e.g. a tree on top)
                    }
                    bool isIndoor = bp.Tiles.TryGetValue(new LayoutPosition(x, z), out Room r) && LayoutHelpers.IsInside(r);
                    if (isIndoor)
                    {
                        indoor++;
                    }
                    else
                    {
                        outdoor++;
                    }
                    for (int k = 0; k < 2; k++)
                    {
                        float g = _farGrids[2 + k][i, j];
                        bool lit = (g - o[i, j]) / (n[i, j] - o[i, j]) > 0.5f;
                        if (isIndoor && lit)
                        {
                            litIndoor[k]++;
                        }
                        if (!isIndoor && !lit)
                        {
                            darkOutdoor[k]++;
                        }
                    }
                }
            }
            Log($"far lighting, tiles past the map: without the fix, indoor lit {litIndoor[0]}/{indoor}, outdoor dark {darkOutdoor[0]}/{outdoor}; with it, indoor lit {litIndoor[1]}/{indoor}, outdoor dark {darkOutdoor[1]}/{outdoor}");
            Check(indoor > 0 && litIndoor[1] >= indoor * 0.9f, $"floor past the lighting map is lit at night ({litIndoor[1]} of {indoor} tiles)");
            Check(outdoor == 0 || darkOutdoor[1] >= outdoor * 0.9f, $"ground past the lighting map stays dark at night ({darkOutdoor[1]} of {outdoor} tiles)");
        }

        // ---- weather beyond the map: force rain over the far strip (it spawns only near the original restaurant) ----

        private UnityEngine.VFX.VisualEffect _rain;
        private UnityEngine.VFX.VisualEffect _weatherWasActive;
        private bool _rainWasOn;

        private void StepWeatherForce()
        {
            if (_farStrip == null)
            {
                return;
            }
            WeatherView view = UnityEngine.Object.FindObjectOfType<WeatherView>();
            var lookups = HarmonyLib.AccessTools.Field(typeof(WeatherView), "WeatherLookups")?.GetValue(view) as System.Collections.IList;
            if (view == null || lookups == null)
            {
                Log("INFO no weather view; weather not exercised.");
                return;
            }
            foreach (object o in lookups)
            {
                var l = (WeatherView.WeatherLookup)o;
                if (l.Mode == KitchenData.WeatherMode.Rain)
                {
                    _rain = l.Effect;
                }
            }
            if (_rain == null)
            {
                Log("INFO no rain effect in the weather view; weather not exercised.");
                return;
            }
            var activeField = HarmonyLib.AccessTools.FieldRefAccess<WeatherView, UnityEngine.VFX.VisualEffect>("ActiveEffect");
            var hasMapField = HarmonyLib.AccessTools.FieldRefAccess<WeatherView, bool>("HasLayoutTexture");
            _weatherWasActive = activeField(view);
            _rainWasOn = _rain.gameObject.activeSelf;
            // As WeatherView.UpdateData does for rainy weather (the night system keeps the real weather off).
            _rain.gameObject.SetActive(true);
            _rain.SetBool("Active", true);
            _rain.SetTexture("_RoomLayout", Shader.GetGlobalTexture("_RoomLayout"));
            activeField(view) = _rain;
            hasMapField(view) = true;
            FarLighting.Scan();
            Renderer r = _rain.GetComponent<Renderer>();
            Log($"rain forced on: effect at {_rain.transform.position} (rotation {_rain.transform.rotation.eulerAngles}, scale {_rain.transform.lossyScale}); renderer bounds {(r != null ? r.bounds.ToString() : "none")}; has _RoomLayout {_rain.HasTexture("_RoomLayout")}");
        }

        // Rain measurement: where does the effect spawn, does it follow its transform, and which
        // position (world or local) does it mask with? Three configurations, each settled for 2 s
        // and then accumulated for 1.5 s, with only the rain rendered (spare layer, black background).
        private const int RainLayer = 31;
        private const float RainShift = 8f;
        private LightingProbe _rainProbe;
        private int _rainConfig;
        private float _rainConfigStart;
        private float[,] _rainHits;
        private int _rainOldLayer;
        private readonly List<string> _rainReport = new List<string>();

        private void RainConfigure(int config)
        {
            _rainConfig = config;
            _rainConfigStart = UnityEngine.Time.realtimeSinceStartup;
            _rainHits = new float[LightingProbe.Half * 2, LightingProbe.Half * 2];
            _rain.transform.position = config == 0 ? Vector3.zero : new Vector3(RainShift, 0f, 0f);
            Texture map = config == 2
                ? LightingProbe.Pattern32((x, y) => x < 16, TextureWrapMode.Clamp) // left half indoors
                : LightingProbe.Pattern32((x, y) => false, TextureWrapMode.Clamp);
            _rain.SetTexture("_RoomLayout", map);
        }

        private void StepRainMeasure()
        {
            if (_rain == null)
            {
                return;
            }
            if (_rainProbe == null)
            {
                _rainOldLayer = _rain.gameObject.layer;
                foreach (Transform t in _rain.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = RainLayer;
                }
                _rainProbe = new LightingProbe { ForceNight = false };
                _rainProbe.Setup();
                _rainProbe.SetCullingMask(1 << RainLayer);
                _rainProbe.Request(null);
                _rainReport.Clear();
                RainConfigure(0);
                Retry(0.1f);
                return;
            }
            float t0 = UnityEngine.Time.realtimeSinceStartup - _rainConfigStart;
            if (t0 >= 2f)
            {
                float[,] g = _rainProbe.ReadMax();
                for (int i = 0; i < g.GetLength(0); i++)
                {
                    for (int j = 0; j < g.GetLength(1); j++)
                    {
                        _rainHits[i, j] = Mathf.Max(_rainHits[i, j], g[i, j]);
                    }
                }
            }
            if (t0 < 3.5f)
            {
                Retry(0.1f);
                return;
            }
            // Summarise this configuration.
            int half = LightingProbe.Half, hits = 0, minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            var columns = new int[half * 2];
            for (int i = 0; i < half * 2; i++)
            {
                for (int j = 0; j < half * 2; j++)
                {
                    if (_rainHits[i, j] > 0.12f)
                    {
                        hits++;
                        columns[i]++;
                        minX = Math.Min(minX, i - half);
                        maxX = Math.Max(maxX, i - half);
                        minZ = Math.Min(minZ, j - half);
                        maxZ = Math.Max(maxZ, j - half);
                    }
                }
            }
            string cols = string.Join(",", Enumerable.Range(0, half * 2).Where(i => i % 2 == 0).Select(i => columns[i] + columns[i + 1]));
            string line = $"rain config {_rainConfig} ({(_rainConfig == 0 ? "at origin" : "moved +" + RainShift + " x")}{(_rainConfig == 2 ? ", map indoor for pixels x<16" : "")}): {hits} tiles hit, x {minX}..{maxX}, z {minZ}..{maxZ}; hits per 2-tile column from x={-half}: {cols}";
            _rainReport.Add(line);
            Log(line);
            if (_rainConfig < 2)
            {
                RainConfigure(_rainConfig + 1);
                Retry(0.1f);
                return;
            }
            // Restore.
            _rainProbe.Dispose();
            _rainProbe = null;
            _rain.transform.position = Vector3.zero;
            _rain.SetTexture("_RoomLayout", Shader.GetGlobalTexture("_RoomLayout"));
            foreach (Transform t in _rain.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = _rainOldLayer;
            }
            Check(true, "rain measurement ran (results above)");
        }

        private void StepWeatherShot()
        {
            if (_rain == null)
            {
                return;
            }
            if (StepElapsed < 5f)
            {
                Retry(0.5f); // let the rain fill the sky
                return;
            }
            Shot("16-rain-far-strip");
            Check(_rain.GetTexture("_RoomLayout") == Shader.GetGlobalTexture("_RoomLayout"), "the rain keeps the game's own (origin-centred) map while far lighting is on");
        }

        private void StepWeatherOff()
        {
            if (_rain == null)
            {
                return;
            }
            WeatherView view = UnityEngine.Object.FindObjectOfType<WeatherView>();
            if (view != null)
            {
                HarmonyLib.AccessTools.FieldRefAccess<WeatherView, UnityEngine.VFX.VisualEffect>("ActiveEffect")(view) = _weatherWasActive;
            }
            _rain.SetBool("Active", false);
            _rain.gameObject.SetActive(_rainWasOn);
            _rain = null;
        }

        private void StepShrinkFarWest()
        {
            if (_farStrip == null)
            {
                return;
            }
            LayoutBlueprint bp = ReadLayout();
            for (int i = _farStrip.Count - 1; i >= 0; i--)
            {
                string refused = LayoutEdits.TogglePaint(bp, _farStrip[i].Item1, _farStrip[i].Item2, BlocksEdit);
                if (refused != null)
                {
                    Fail($"shrinking the far-lighting strip at {Fmt(_farStrip[i].Item2)} refused: {refused}");
                    return;
                }
            }
            Apply(bp);
            Check(CurrentBounds() == _boundsBeforeFar, "CBounds back to what they were before the far-lighting strip");
        }

        private void StepPrepareDayLayout()
        {
            LayoutBlueprint bp = ReadLayout();
            var tables = AllWith<CApplianceTable>().Where(t => !EntityManager.GetComponentData<CApplianceTable>(t).IsWaitingTable).ToList();
            var chairs = AllWith<CApplianceChair>();
            Log($"found {tables.Count} dining table(s), {chairs.Count} chair(s).");

            Entity table = default;
            List<Entity> ourChairs = null;
            foreach (Entity t in tables)
            {
                Vector3 tp = Pos(t);
                List<Entity> adj = chairs.Where(c => IsOrthNeighbour(Pos(c), tp)).ToList();
                if (ourChairs == null || adj.Count > ourChairs.Count)
                {
                    table = t;
                    ourChairs = adj;
                }
            }
            if (table == default || ourChairs.Count == 0)
            {
                Fail("no dining table with chairs to move");
                return;
            }

            Vector3 tablePos = Pos(table).Rounded();
            var offsets = new List<LayoutPosition> { new LayoutPosition(0, 0) };
            offsets.AddRange(ourChairs.Select(c => (LayoutPosition)(Pos(c).Rounded() - tablePos)));
            int ox0 = offsets.Min(o => o.x) - 1, ox1 = offsets.Max(o => o.x) + 1;
            int oy0 = offsets.Min(o => o.y) - 1, oy1 = offsets.Max(o => o.y) + 1;

            LayoutPosition[] dirs = { new LayoutPosition(0, 1), new LayoutPosition(-1, 0), new LayoutPosition(1, 0) };
            OccupancySnapshot occ = OccupancySnapshot.Take(EntityManager, TileManager);
            // Scenery doesn't block (the commit bulldozes it); only real appliances do.
            {
                Func<LayoutPosition, bool> blocked = occ.BlocksEdit;
                foreach (LayoutPosition d in dirs)
                {
                    // Only dining tiles from the start of the run: rooms added by earlier steps (e.g. the
                    // edit-mode test's room behind the kitchen) may only be reachable through a
                    // kitchen whose counters block walking, which vanilla rightly marks unusable.
                    IEnumerable<LayoutPosition> edges = bp.Tiles.Where(t => t.Value.Type == RoomType.Dining && !bp.Tiles.ContainsKey(t.Key + d)
                            && _baseline.Tiles.TryGetValue(t.Key, out Room original) && original.ID == t.Value.ID)
                        .Select(t => t.Key).OrderBy(p => Math.Abs(p.x) + Math.Abs(p.y));
                    foreach (LayoutPosition s in edges)
                    {
                        int diningId = bp[s].ID;
                        for (int inset = 0; inset <= 1; inset++)
                        {
                            LayoutPosition q0 = d.y == 1 ? new LayoutPosition(s.x, s.y + 1 - oy0)
                                : d.x == -1 ? new LayoutPosition(s.x - 1 - ox1, s.y)
                                : new LayoutPosition(s.x + 1 - ox0, s.y);
                            LayoutPosition q = new LayoutPosition(q0.x - d.x * inset, q0.y - d.y * inset);
                            var region = new List<LayoutPosition>();
                            for (int x = ox0; x <= ox1; x++)
                            {
                                for (int y = oy0; y <= oy1; y++)
                                {
                                    region.Add(new LayoutPosition(q.x + x, q.y + y));
                                }
                            }
                            // The table must stand on new floor; the block may overlap free tiles of the same dining room.
                            if (bp.Tiles.ContainsKey(q)
                                || region.Any(r => bp.Tiles.TryGetValue(r, out Room rr) && (rr.ID != diningId || blocked(r))))
                            {
                                continue;
                            }
                            var newTiles = region.Where(r => !bp.Tiles.ContainsKey(r)).ToList();
                            LayoutBlueprint trial = new LayoutBlueprint(bp);
                            if (!GrowRegion(trial, newTiles, diningId, blocked))
                            {
                                continue;
                            }

                            Log($"day layout: {newTiles.Count} new of {region.Count} block tiles toward {Fmt(d)} (inset {inset}), table moves {Fmt((LayoutPosition)tablePos)} -> {Fmt(q)}");
                            Apply(trial);
                            // Nothing may be left standing on the new floor, scenery included, any layer, by position.
                            var inBlock = AllWith<CAppliance>().Where(x => newTiles.Contains((LayoutPosition)Pos(x).Rounded())).ToList();
                            foreach (Entity x in inBlock)
                            {
                                Log($"  on new floor: appliance {EntityManager.GetComponentData<CAppliance>(x).ID} at {Pos(x)}");
                            }
                            Check(inBlock.Count == 0, $"no appliances or scenery stand on the new floor ({inBlock.Count})");

                            // Dissolve the table's CTableSet first (what vanilla's DestroySet does when a
                            // player picks a table up), so vanilla reassembles it at the new spot. Moving
                            // only the appliances left the set, which customers are assigned to, behind.
                            int dissolved = DissolveTableSet(table);
                            Vector3 delta = (Vector3)q - tablePos;
                            MoveBy(table, delta);
                            foreach (Entity c in ourChairs)
                            {
                                MoveBy(c, delta);
                            }
                            Log($"dissolved {dissolved} table set(s) so vanilla rebuilds them at the new position.");
                            // Collect first: a chair can sit between two tables, so destroying while
                            // iterating would read an already-destroyed chair.
                            var doomed = new HashSet<Entity>();
                            foreach (Entity t in tables.Where(t => t != table))
                            {
                                Vector3 tp = Pos(t);
                                doomed.Add(t);
                                foreach (Entity c in chairs.Where(c => !ourChairs.Contains(c) && IsOrthNeighbour(Pos(c), tp)))
                                {
                                    doomed.Add(c);
                                }
                            }
                            int removed = 0;
                            foreach (Entity e in doomed)
                            {
                                if (EntityManager.Exists(e))
                                {
                                    EntityManager.DestroyEntity(e);
                                    removed++;
                                }
                            }
                            int post = ClearPostBlockers();
                            Log($"moved table + {ourChairs.Count} chair(s); removed {removed} other table/chair entities; cleared {post} post blocker(s).");

                            TileManager.InvalidateTileCache();
                            _dayRegion = region;
                            _dayExpected = ReadLayout();
                            _dayBounds = CurrentBounds();
                            _dayTablePos = (Vector3)q;
                            _dayChairPos = ourChairs.Select(c => Pos(c)).ToList();
                            _haveDay = true;
                            return;
                        }
                    }
                }
            }
            Fail("couldn't fit a dining block with the table anywhere (scenery or limits in the way)");
        }

        private void StepVerifyDayLayout()
        {
            if (!_haveDay)
            {
                return;
            }
            Check(AllWith<CApplianceTable>().Any(t => (Pos(t) - _dayTablePos).sqrMagnitude < 0.01f), "table is at its new position");
            LayoutPosition? door = FrontInside(ReadLayout());
            if (door.HasValue)
            {
                var free = _dayRegion.Where(r => !Occupied(r)).ToList();
                bool reach = free.Any(r => PathLength(door.Value, r) > 0f);
                Check(reach, $"navmesh path from the front door into the new dining block ({free.Count} free tiles)");
            }
            Shot("08-day-layout");
        }

        private void StepSaveToMemory()
        {
            if (!_haveDay)
            {
                return;
            }
            SelfTestMemorySaves.Install();
            if (!TryGetSingleton(out SSelectedLocation loc) || !loc.Valid)
            {
                // The random-restaurant start doesn't pick a save slot; the test needs one to save into.
                Set(new SSelectedLocation { Valid = true, Selected = new CLocationChoice { Slot = 1 } });
                Log("SSelectedLocation wasn't valid; set to slot 1 (memory-only).");
            }
            else
            {
                Log($"SSelectedLocation slot {loc.Selected.Slot}.");
            }
            Entity request = EntityManager.CreateEntity(typeof(CRequestSave));
            EntityManager.SetComponentData(request, new CRequestSave { SaveType = SaveType.AutoFull });
            Log("requested a full-world save through vanilla's PerformSaveRequests.");
        }

        private void StepVerifySaved()
        {
            if (_haveDay)
            {
                Check(SelfTestMemorySaves.SaveCount >= 1, $"a full-world save landed in memory ({SelfTestMemorySaves.SaveCount})");
            }
        }

        private void StepReload()
        {
            if (!_haveDay || SelfTestMemorySaves.SaveCount == 0)
            {
                return;
            }
            Entity e = EntityManager.CreateEntity(typeof(SPerformSceneTransition), typeof(CDoNotPersist));
            EntityManager.SetComponentData(e, new SPerformSceneTransition { NextScene = SceneType.LoadFullAutosave });
            Log("started vanilla's LoadFullAutosave transition (same path as 'restart day').");
            float now = UnityEngine.Time.realtimeSinceStartup;
            _stage = Stage.WaitReload;
            _reloadEarliest = now + 5f;
            _waitUntil = now + 120f;
        }

        private void StepVerifyReload()
        {
            if (!_haveDay)
            {
                return;
            }
            bool same = SameLayout(_dayExpected, ReadLayout(), out string diff);
            Check(same, $"reloaded layout matches what was saved{(same ? "" : ": " + diff)}");
            Check(CurrentBounds() == _dayBounds, "reloaded CBounds matches");
            CheckCameraMarkers(CurrentBounds(), "after reload");
            LayoutView[] views = LayoutViews();
            Check(views.Length == 1 && views[0].IsInitialised, $"one initialised LayoutView after reload (found {views.Length})");
            LayoutView v = views.FirstOrDefault();
            Check(v?.Builder != null && _dayRegion.All(r => v.Builder.Blueprint.Tiles.ContainsKey(r)), "reloaded view geometry includes the new dining block");
            var freeTiles = _dayRegion.Where(r => !Occupied(r)).ToList();
            Check(freeTiles.All(r => NavMesh.SamplePosition((Vector3)r, out NavMeshHit _, 0.3f, NavMesh.AllAreas)),
                $"navmesh covers the free tiles of the new dining block after reload ({freeTiles.Count})");
            LayoutPosition? door = FrontInside(ReadLayout());
            if (door.HasValue)
            {
                Check(freeTiles.Any(r => PathLength(door.Value, r) > 0f), "after reload: navmesh path from the front door into the new dining block");
            }
            Check(AllWith<CApplianceTable>().Any(t => (Pos(t) - _dayTablePos).sqrMagnitude < 0.01f), "moved table is still in place after reload");
            Check(AllWith<CTableSet>().Any(t => HasComponent<CPosition>(t) && (Pos(t) - _dayTablePos).sqrMagnitude < 0.01f), "vanilla rebuilt the table set at the moved table's position");
            Shot("09-reloaded");
        }

        private void StepStartDay()
        {
            if (!_haveDay)
            {
                return;
            }
            if (StepElapsed > 90f)
            {
                Fail("couldn't start the day within 90s (game stayed paused?)");
                return;
            }
            ClearPostBlockers();
            PickFirstCardInOpenChoices();
            bool blockedByPopup = TryGetSingleton(out SStartDayWarnings w) && (w.PopupsOpen.IsBlocking() || w.PostUnopened.IsBlocking());
            if (Time.IsPaused || blockedByPopup)
            {
                // The runner script watches for this line and presses Enter to close the popup.
                if (StepElapsed >= _pausedLogs * 5f)
                {
                    _pausedLogs++;
                    Log($"PAUSED #{_pausedLogs}: a popup is open (IsPaused={Time.IsPaused}; start-day warning levels: popups={w.PopupsOpen}, post={w.PostUnopened}); waiting for it to close.");
                    Shot($"paused-{_pausedLogs}");
                }
                Retry(0.5f);
                return;
            }
            Set<StartNewDay.STriggerStartDay>();
            Log("triggered start of day (STriggerStartDay).");
        }

        private void StepWaitForDay()
        {
            if (!_haveDay)
            {
                return;
            }
            if (Has<SIsDayTime>())
            {
                Check(true, "day started");
                return;
            }
            if (StepElapsed > 30f)
            {
                string warnings = TryGetSingleton(out SStartDayWarnings w)
                    ? $"post={w.PostUnopened} players={w.PlayersNotReady} tables={w.TableSize} popups={w.PopupsOpen}"
                    : "no warnings singleton";
                Fail($"day didn't start within 30s ({warnings})");
                return;
            }
            Retry(0.5f);
        }

        private void StepWatchCustomers()
        {
            if (!_haveDay || !Has<SIsDayTime>())
            {
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;

            foreach (Entity c in AllWith<CApplianceChair>())
            {
                if (_dayChairPos.Any(p => (Pos(c) - p).sqrMagnitude < 0.01f) && EntityManager.GetComponentData<CApplianceChair>(c).Occupant != default)
                {
                    Check(true, $"a customer walked onto the new floor and sat at the moved table after {StepElapsed:0}s");
                    _seated = true;
                    Shot("10-seated");
                    return;
                }
            }
            if (!_loggedAssignment)
            {
                foreach (Entity g in AllWith<CAssignedTable>())
                {
                    Entity t = EntityManager.GetComponentData<CAssignedTable>(g).Table;
                    if (EntityManager.Exists(t) && HasComponent<CPosition>(t) && (Pos(t) - _dayTablePos).sqrMagnitude < 0.01f)
                    {
                        _loggedAssignment = true;
                        Log($"a group was assigned to the moved table after {StepElapsed:0}s.");
                    }
                }
            }
            // The dish card can pause the game after the day starts. If the day clock stops, say
            // so; the runner script presses Enter to close the popup.
            STime clock = GetSingleton<STime>();
            if (clock.TimeOfDayUnbounded != _lastTimeOfDay)
            {
                _lastTimeOfDay = clock.TimeOfDayUnbounded;
                _clockMovedAt = now;
            }
            else if (now - _clockMovedAt > 4f && now >= _nextPausedLog)
            {
                _nextPausedLog = now + 5f;
                _pausedLogs++;
                Log($"PAUSED #{_pausedLogs}: the day clock stopped (IsPaused={Time.IsPaused}, ForcePause={clock.ForcePause}); waiting for the popup to close.");
                Shot($"paused-{_pausedLogs}");
            }
            if (now >= _nextDayLog)
            {
                _nextDayLog = now + 15f;
                Log($"day t+{StepElapsed:0}s: clock {clock.TimeOfDay:0.00}, {AllWith<CCustomerGroup>().Count} group(s), {AllWith<CCustomer>().Count} customer(s), going-to-table {AllWith<CGroupGoingToTable>().Count}");
                Shot($"day-{++_dayShots:00}");
            }
            if (StepElapsed > 180f)
            {
                Fail("no customer sat at the moved table within 180s (stuck? see day-*.png)");
                return;
            }
            Retry(1f);
        }

        /// <summary>
        /// User report (2026-11-12): a seated customer's green patience bar appeared in the wrong
        /// place. After someone sits, wait for them to order, then compare where everything is:
        /// the group, its assigned table, the moved table, each indicator entity, and each
        /// indicator's actual on-screen object.
        /// </summary>
        private void StepWatchIndicator()
        {
            if (!_haveDay || !_seated)
            {
                return;
            }
            if (StepElapsed < 12f)
            {
                Retry(1f);
                return;
            }
            Log($"moved table is at {_dayTablePos}");
            foreach (Entity g in AllWith<CAssignedTable>())
            {
                Entity t = EntityManager.GetComponentData<CAssignedTable>(g).Table;
                string tPos = EntityManager.Exists(t) && HasComponent<CPosition>(t) ? Pos(t).ToString() : "n/a";
                Log($"group {g.Index}: at table={HasComponent<CAtTable>(g)}, group pos {(HasComponent<CPosition>(g) ? Pos(g).ToString() : "n/a")}, assigned table {t.Index} at {tPos}, is moved table: {tPos != "n/a" && (Pos(t) - _dayTablePos).sqrMagnitude < 0.01f}");
            }
            int checkedBars = 0;
            foreach (Entity i in AllWith<CCustomerIndicator>())
            {
                Entity src = HasComponent<CIndicator>(i) ? EntityManager.GetComponentData<CIndicator>(i).Source : default;
                Vector3 ip = HasComponent<CPosition>(i) ? Pos(i) : Vector3.positiveInfinity;
                Log($"indicator {i.Index}: entity pos {ip}, source {src.Index}, has view {HasComponent<CLinkedView>(i)}");
                if (src != default && EntityManager.Exists(src) && HasComponent<CAtTable>(src) && HasComponent<CAssignedTable>(src))
                {
                    Entity t = EntityManager.GetComponentData<CAssignedTable>(src).Table;
                    if (EntityManager.Exists(t) && HasComponent<CPosition>(t))
                    {
                        Vector3 tp = Pos(t);
                        float off = new Vector2(ip.x - tp.x, ip.z - tp.z).magnitude;
                        Check(off < 1.0f, $"indicator entity {i.Index} sits on its group's table (offset {off:0.00})");
                        checkedBars++;
                    }
                }
            }
            // Settings with more customers (Turbo) seat groups at other tables too: every bar must
            // sit on some seated group's table (bars of groups still queueing show at the front door),
            // and one must be at the moved table.
            var groupTables = new List<Vector3>();
            foreach (Entity g in AllWith<CAssignedTable>())
            {
                Entity t = EntityManager.GetComponentData<CAssignedTable>(g).Table;
                if (HasComponent<CAtTable>(g) && EntityManager.Exists(t) && HasComponent<CPosition>(t))
                {
                    groupTables.Add(Pos(t));
                }
            }
            bool atMoved = false;
            int activeBars = 0;
            foreach (CustomerIndicatorView view in UnityEngine.Object.FindObjectsOfType<CustomerIndicatorView>())
            {
                Vector3 vp = view.transform.position;
                float off = new Vector2(vp.x - _dayTablePos.x, vp.z - _dayTablePos.z).magnitude;
                float nearest = groupTables.Count == 0 ? float.MaxValue : groupTables.Min(t => new Vector2(vp.x - t.x, vp.z - t.z).magnitude);
                Log($"indicator view at {vp} (offset from moved table {off:0.00}, from the nearest seated group's table {nearest:0.00}, active {view.gameObject.activeInHierarchy})");
                Vector3 door = GetFrontDoor(get_external_tile: true);
                if (new Vector2(vp.x - door.x, vp.z - door.z).magnitude < 2.5f)
                {
                    Log("  (a waiting group's bar at the front door; not a table bar)");
                    continue;
                }
                if (view.gameObject.activeInHierarchy)
                {
                    activeBars++;
                    atMoved |= off < 1.5f;
                    Check(Mathf.Min(off, nearest) < 1.5f, $"on-screen patience bar is drawn at a seated group's table (offset {Mathf.Min(off, nearest):0.00})");
                }
            }
            if (activeBars > 0)
            {
                Check(atMoved, "a patience bar is drawn at the moved table");
            }
            if (checkedBars == 0)
            {
                Log("INFO no seated group had an indicator yet; nothing to compare.");
            }
            Shot("11-indicator");
        }

        private bool GrowRegion(LayoutBlueprint bp, List<LayoutPosition> region, int roomId, Func<LayoutPosition, bool> blocked)
        {
            var remaining = new HashSet<LayoutPosition>(region);
            bool progress = true;
            while (remaining.Count > 0 && progress)
            {
                progress = false;
                foreach (LayoutPosition t in remaining.ToList())
                {
                    foreach (LayoutPosition d in LayoutHelpers.Directions)
                    {
                        LayoutPosition n = t + d;
                        if (bp.Tiles.TryGetValue(n, out Room r) && r.ID == roomId && LayoutEdits.TogglePaint(bp, n, t, p => blocked(p)) == null)
                        {
                            remaining.Remove(t);
                            progress = true;
                            break;
                        }
                    }
                }
            }
            return remaining.Count == 0;
        }

        /// <summary>
        /// Some dishes open a card choice (e.g. "Spaghetti I or II") that blocks the day until one
        /// is picked. Choose the first option the same way vanilla's UnlockSelectPopupView does
        /// (add CUnlockSelectPopupResult); HandleUnlockChoice then applies it.
        /// </summary>
        private void PickFirstCardInOpenChoices()
        {
            EntityQuery q = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CUnlockSelectPopupOption>() },
                None = new[] { ComponentType.ReadOnly<CUnlockSelectPopupResult>() },
            });
            using (Unity.Collections.NativeArray<Entity> popups = q.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (Entity popup in popups)
                {
                    DynamicBuffer<CUnlockSelectPopupOption> options = EntityManager.GetBuffer<CUnlockSelectPopupOption>(popup);
                    if (options.Length == 0)
                    {
                        continue;
                    }
                    Entity first = options[0].Entity;
                    EntityManager.AddComponentData(popup, new CUnlockSelectPopupResult { Selection = first });
                    Log($"picked the first card in an open card choice ({options.Length} options).");
                }
            }
            q.Dispose();
        }

        /// <summary>Mirror of vanilla AssembleTableSets.DestroySet for the set this part belongs to.</summary>
        private int DissolveTableSet(Entity part)
        {
            if (!HasComponent<CPartOfTableSet>(part))
            {
                return 0;
            }
            Entity set = EntityManager.GetComponentData<CPartOfTableSet>(part).TableSet;
            if (!EntityManager.Exists(set) || !EntityManager.HasComponent<CTableSetParts>(set))
            {
                EntityManager.RemoveComponent<CPartOfTableSet>(part);
                return 0;
            }
            var parts = EntityManager.GetBuffer<CTableSetParts>(set).ToNativeArray(Unity.Collections.Allocator.Temp);
            foreach (CTableSetParts p in parts)
            {
                if (EntityManager.Exists(p.Entity))
                {
                    EntityManager.RemoveComponent<CPartOfTableSet>(p.Entity);
                }
            }
            parts.Dispose();
            EntityManager.DestroyEntity(set);
            return 1;
        }

        private int ClearPostBlockers()
        {
            EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CPreventStartDayPost>());
            int n = q.CalculateEntityCount();
            EntityManager.DestroyEntity(q);
            q.Dispose();
            return n;
        }

        private List<Entity> AllWith<T>() where T : struct, IComponentData
        {
            EntityQuery q = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            using (Unity.Collections.NativeArray<Entity> a = q.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                q.Dispose();
                return a.ToList();
            }
        }

        private Vector3 Pos(Entity e) => EntityManager.GetComponentData<CPosition>(e).Position;

        private void MoveBy(Entity e, Vector3 delta)
        {
            CPosition p = EntityManager.GetComponentData<CPosition>(e);
            EntityManager.SetComponentData(e, new CPosition(p.Position + delta, p.Rotation));
            _occupancy = null;
        }

        private static bool IsOrthNeighbour(Vector3 a, Vector3 b)
        {
            Vector3 d = (a - b).Rounded();
            return Mathf.Abs(d.x) + Mathf.Abs(d.z) == 1f && Mathf.Abs(d.y) < 0.5f;
        }

        // ---------------- helpers ----------------

        private LayoutBlueprint ReadLayout() => LayoutWriter.Read(EntityManager, GetSingletonEntity<SLayout>());

        private void Apply(LayoutBlueprint bp)
        {
            // The mod's real commit path: bulldoze scenery, write, refresh tile cache, rebuild views.
            LayoutCommit.Result r = LayoutCommit.Commit(EntityManager, TileManager, GetSingletonEntity<SLayout>(), bp);
            _occupancy = null;
            Check(r.RebuiltViews == 1, $"rebuild replaced {r.RebuiltViews} floorplan view entity(ies)");
            if (r.SceneryMoved + r.SceneryRemoved > 0)
            {
                Log($"commit cleared scenery: moved {r.SceneryMoved}, removed {r.SceneryRemoved}");
            }
        }

        // One snapshot per step run, refreshed after anything moves (Apply, MoveBy).
        private static OccupancySnapshot _occupancy;
        private bool Occupied(LayoutPosition p) => (_occupancy ??= OccupancySnapshot.Take(EntityManager, TileManager)).IsOccupied(p);
        private bool BlocksEdit(LayoutPosition p) => (_occupancy ??= OccupancySnapshot.Take(EntityManager, TileManager)).BlocksEdit(p);

        private static LayoutView[] LayoutViews() => UnityEngine.Object.FindObjectsOfType<LayoutView>();

        private static bool SameEdge(Feature f, LayoutPosition a, LayoutPosition b) =>
            (f.Tile1 == a && f.Tile2 == b) || (f.Tile1 == b && f.Tile2 == a);

        private static bool SameLayout(LayoutBlueprint x, LayoutBlueprint y, out string diff)
        {
            var problems = new List<string>();
            foreach (KeyValuePair<LayoutPosition, Room> t in x.Tiles)
            {
                if (!y.Tiles.TryGetValue(t.Key, out Room r))
                {
                    problems.Add($"{Fmt(t.Key)} missing");
                }
                else if (r.Type != t.Value.Type || (r.Type != RoomType.NoRoom && r.ID != t.Value.ID))
                {
                    problems.Add($"{Fmt(t.Key)} {t.Value.Type}#{t.Value.ID} -> {r.Type}#{r.ID}");
                }
            }
            if (x.Tiles.Count != y.Tiles.Count)
            {
                problems.Add($"tile count {x.Tiles.Count} -> {y.Tiles.Count}");
            }
            string Key(Feature f) => $"{f.Type}:{Math.Min(f.Tile1.x, f.Tile2.x)},{Math.Min(f.Tile1.y, f.Tile2.y)},{Math.Max(f.Tile1.x, f.Tile2.x)},{Math.Max(f.Tile1.y, f.Tile2.y)}";
            var fx = new HashSet<string>(x.Features.Select(Key));
            var fy = new HashSet<string>(y.Features.Select(Key));
            if (!fx.SetEquals(fy))
            {
                problems.Add($"features differ: -[{string.Join(" ", fx.Except(fy))}] +[{string.Join(" ", fy.Except(fx))}]");
            }
            diff = string.Join("; ", problems.Take(10));
            return problems.Count == 0;
        }

        private static string DumpGrid(LayoutBlueprint bp)
        {
            int minX = bp.Tiles.Keys.Min(p => p.x), maxX = bp.Tiles.Keys.Max(p => p.x);
            int minY = bp.Tiles.Keys.Min(p => p.y), maxY = bp.Tiles.Keys.Max(p => p.y);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"grid x[{minX}..{maxX}] y[{minY}..{maxY}] (. outdoor, K kitchen, D dining, G garden, ? other, blank = not stored); top row = max y");
            for (int y = maxY; y >= minY; y--)
            {
                sb.Append($"{y,4} ");
                for (int x = minX; x <= maxX; x++)
                {
                    if (!bp.Tiles.TryGetValue(new LayoutPosition(x, y), out Room r))
                    {
                        sb.Append(' ');
                        continue;
                    }
                    sb.Append(r.Type switch { RoomType.NoRoom => '.', RoomType.Kitchen => 'K', RoomType.Dining => 'D', RoomType.Garden => 'G', _ => '?' });
                }
                sb.AppendLine();
            }
            foreach (Feature f in bp.Features)
            {
                sb.AppendLine($"     {f.Type} {Fmt(f.Tile1)}-{Fmt(f.Tile2)}");
            }
            return sb.ToString();
        }

        private static void Shot(string name)
        {
            if (SelfTest.ShotDir == null)
            {
                return;
            }
            string path = Path.Combine(SelfTest.ShotDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"screenshot -> {path}");
        }

        private static void Check(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Log("PASS " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            ModMain.LogWarning("[SELFTEST] FAIL " + what);
        }

        private static void Log(string msg) => ModMain.Log("[SELFTEST] " + msg);

        private static string Fmt(LayoutPosition p) => LayoutEdits.Fmt(p);

        private static void Finish()
        {
            _stage = Stage.Done;
            Log($"DONE pass={_pass} fail={_fail}");
            if (SelfTest.QuitWhenDone)
            {
                // Give the last screenshot a few frames to be written.
                _runner = _runner ?? new GameObject("EasyFloorPlanner SelfTest Runner");
                _runner.AddComponent<CoroutineRunner>().StartCoroutine(QuitSoon());
            }
        }

        private static System.Collections.IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(3f);
            Log("quitting.");
            Application.Quit();
        }

        private class CoroutineRunner : MonoBehaviour { }
    }
}
#endif
