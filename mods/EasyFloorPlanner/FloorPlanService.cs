using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using KitchenData;
using KitchenMods;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Host side of the edit mode, at night in the restaurant:
    ///   - keeps one "Floor Plan" terminal outside, next to the practice-mode pad (a reroll-trigger
    ///     model with its reroll behaviour removed; never saved);
    ///   - opens an edit session for a player who presses the terminal, one editor at a time;
    ///     anyone else who presses it while it's in use watches the editor's draft instead;
    ///   - applies the editor's Apply/Cancel. A draft is validated against the live layout and
    ///     the host's own occupancy before it's committed;
    ///   - relays the editor's latest draft and cursor (Preview) to spectators. Previews are
    ///     display-only: never validated or committed.
    /// </summary>
    public class FloorPlanService : NightSystem, IModSystem
    {
        public struct SessionStatus
        {
            public int AckSequence;
            public bool LastSucceeded;
            public string Message;
        }

        public class EditorPreview
        {
            public int Sequence;
            public LayoutBlueprint Draft;
            public int DraftHash;
            public FloorPlanCursor Cursor;
            public FloorPlanCameraArea Area;
        }

        private EntityQuery Terminals;
        private EntityQuery Sessions;
        private EntityQuery Spectators;
        private readonly List<Entity> OpenRequests = new List<Entity>();
        private readonly Dictionary<Entity, SessionStatus> Statuses = new Dictionary<Entity, SessionStatus>();
        private readonly Dictionary<Entity, EditorPreview> Previews = new Dictionary<Entity, EditorPreview>();

        protected override void Initialise()
        {
            base.Initialise();
            Terminals = GetEntityQuery(typeof(CFloorPlanTerminal));
            Sessions = GetEntityQuery(typeof(CFloorPlanSession));
            Spectators = GetEntityQuery(typeof(CFloorPlanSpectator));
        }

        protected override void OnUpdate()
        {
            if (!Has<SKitchenMarker>() || !HasSingleton<SLayout>())
            {
                return;
            }
            RelocateUnreachableTerminal();
            EnsureTerminal();
            StripRerollBehaviour();
            HandleOpenRequests();
            CloseStaleSessions();
        }

        // ----------------------------------------------------------------- terminal

        private void EnsureTerminal()
        {
            if (!Terminals.IsEmpty)
            {
                return;
            }
            Vector3 tile = FindTerminalTile();
            // If even the fallback is out of reach, don't let RelocateUnreachableTerminal respawn
            // it every frame.
            TerminalFallbackUnreachable = !CurrentPlayerReach().Contains(tile);
            Entity e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new CCreateAppliance { ID = AssetReference.ShopRerollTrigger });
            EntityManager.AddComponentData(e, new CPosition(tile));
            EntityManager.AddComponent<CDoNotPersist>(e);
            EntityManager.AddComponent<CFloorPlanTerminal>(e);
            ModMain.Log($"Spawned the Floor Plan terminal at {tile}.");
        }

        /// <summary>
        /// Where players can stand in the restaurant: an exact copy of vanilla's
        /// EnforcePlayerBounds (CBounds, stretched to 2 tiles in front of the front door, expanded
        /// by 0.5). A player outside it is teleported back to the front door, so a terminal must
        /// stand inside it.
        /// </summary>
        internal static Bounds PlayerReach(Bounds layoutBounds, Vector3 frontDoor)
        {
            Bounds b = layoutBounds;
            b.Encapsulate(frontDoor + new Vector3(0f, 0f, -2f));
            b.Expand(0.5f);
            return b;
        }

        private Bounds CurrentPlayerReach() => PlayerReach(Bounds, GetFrontDoor());

        /// <summary>
        /// Outside the front door, in the row in front of the building or the next one out (neither
        /// can ever become floor: no growing toward the street), and only where players can reach.
        /// Vanilla puts the reroll trigger 3 tiles from the door toward the middle and the practice
        /// pad 4 tiles; the terminal prefers 5, else the nearest free tile in either row.
        ///
        /// Both rows are searched together, nearest first: searching the front row first put the
        /// terminal past the building's side wherever a flower border filled the front row, out of
        /// the players' reach (user report, 2026-11-13).
        /// </summary>
        private Vector3 FindTerminalTile()
        {
            Vector3 door = GetFrontDoor(get_external_tile: true);
            int toward = door.x > 0f ? -1 : 1;
            Vector3 preferred = door + new Vector3(toward * 5, 0f, 0f);
            Bounds reach = CurrentPlayerReach();
            var reserved = new List<Vector3>();
            GetReservedTiles(reserved);
            reserved.Add(GetPracticeTile());
            OccupancySnapshot occupancy = OccupancySnapshot.Take(EntityManager, TileManager);

            var candidates = new List<Vector3>();
            for (int x = Mathf.FloorToInt(reach.min.x); x <= Mathf.CeilToInt(reach.max.x); x++)
            {
                if (x == Mathf.RoundToInt(door.x))
                {
                    continue; // keep the doorway clear (both rows)
                }
                foreach (float rowOffset in new[] { 0f, -1f })
                {
                    var c = new Vector3(x, 0f, door.z + rowOffset);
                    if (reach.Contains(c) && !reserved.Any(r => (r - c).sqrMagnitude < 0.01f))
                    {
                        candidates.Add(c);
                    }
                }
            }
            // Nearest the preferred spot; the front row wins ties.
            candidates.Sort((a, b) =>
                (Mathf.Abs(a.x - preferred.x) + (a.z < door.z ? 0.5f : 0f))
                .CompareTo(Mathf.Abs(b.x - preferred.x) + (b.z < door.z ? 0.5f : 0f)));

            foreach (Vector3 c in candidates)
            {
                if (!occupancy.IsOccupied(c))
                {
                    return c;
                }
            }
            // Nothing free: share a tile with scenery (flowers) rather than leave the reach.
            foreach (Vector3 c in candidates)
            {
                if (!occupancy.BlocksEdit(c))
                {
                    ModMain.LogWarning($"No free tile for the Floor Plan terminal; placing it over scenery at {c}.");
                    return c;
                }
            }
            ModMain.LogWarning("No reachable tile for the Floor Plan terminal; placing it beside the practice pad.");
            return candidates.Count > 0 ? candidates[0] : door + new Vector3(toward * 5, 0f, -1f);
        }

        /// <summary>
        /// An edit that shrinks the building can leave the terminal outside the players' reach
        /// (it doesn't move with the walls): respawn it in a reachable spot.
        /// </summary>
        private bool TerminalFallbackUnreachable;

        private void RelocateUnreachableTerminal()
        {
            if (Terminals.IsEmpty || TerminalFallbackUnreachable)
            {
                return;
            }
            Bounds reach = CurrentPlayerReach();
            using NativeArray<Entity> terminals = Terminals.ToEntityArray(Allocator.Temp);
            foreach (Entity t in terminals)
            {
                if (Require(t, out CPosition p) && !reach.Contains(p.Position))
                {
                    ModMain.Log($"The Floor Plan terminal at {p.Position} is out of the players' reach; moving it.");
                    EntityManager.DestroyEntity(t);
                }
            }
        }

        /// <summary>Makes the reroll-trigger model a plain click-to-open terminal (and stops vanilla treating it as a reroll).</summary>
        private void StripRerollBehaviour()
        {
            if (Terminals.IsEmpty)
            {
                return;
            }
            EntityManager.RemoveComponent<CRerollShopAfterDuration>(Terminals);
            EntityManager.RemoveComponent<CTakesDuration>(Terminals);
            EntityManager.RemoveComponent<CDisplayDuration>(Terminals);
            EntityManager.RemoveComponent<CHasIndicator>(Terminals);
            EntityManager.RemoveComponent<CIsInactive>(Terminals);
            EntityManager.RemoveComponent<CHideView>(Terminals);
        }

        // ----------------------------------------------------------------- sessions

        /// <summary>Called by OpenFloorPlanInteraction (inside the interaction pass).</summary>
        public void RequestOpen(Entity player)
        {
            if (!OpenRequests.Contains(player))
            {
                OpenRequests.Add(player);
            }
        }

        public bool IsInUse => !Sessions.IsEmpty;

        private void HandleOpenRequests()
        {
            foreach (Entity player in OpenRequests)
            {
                if (!EntityManager.Exists(player) || !Require(player, out CPlayer cPlayer))
                {
                    continue;
                }
                if (!Sessions.IsEmpty)
                {
                    // One editor at a time; everyone else can watch (the terminal shows "Watch").
                    OpenSpectator(player, cPlayer, selfTest: false);
                    continue;
                }
                Entity session = EntityManager.CreateEntity();
                EntityManager.AddComponentData(session, new CFloorPlanSession
                {
                    Player = player,
                    PlayerID = cPlayer.ID,
                    InputSource = cPlayer.InputSource
                });
                EntityManager.AddComponentData(session, new CPosition(Vector3.zero));
                EntityManager.AddComponentData(session, new CRequiresView
                {
                    Type = FloorPlanIds.ViewType,
                    ViewMode = ViewMode.Screen
                });
                EntityManager.AddComponent<CDoNotPersist>(session);
                ModMain.Log($"Player {cPlayer.ID} opened the floor plan editor.");
            }
            OpenRequests.Clear();
        }

        /// <summary>
        /// Lets <paramref name="player"/> watch the open session. Players on the editor's own
        /// machine (local co-op) share the editor's screen and already see the draft, so they're
        /// skipped, as is the editor. <paramref name="selfTest"/> bypasses both: the self-test's
        /// only player is the editor.
        /// </summary>
        private void OpenSpectator(Entity player, CPlayer cPlayer, bool selfTest)
        {
            using NativeArray<Entity> sessions = Sessions.ToEntityArray(Allocator.Temp);
            if (sessions.Length == 0)
            {
                return;
            }
            Entity session = sessions[0];
            CFloorPlanSession s = EntityManager.GetComponentData<CFloorPlanSession>(session);
            if (!selfTest && s.Player == player)
            {
                return; // the editor pressed it again (can't happen while their input is taken)
            }
            if (!selfTest && s.InputSource == cPlayer.InputSource)
            {
                return;
            }
            using NativeArray<CFloorPlanSpectator> watching = Spectators.ToComponentDataArray<CFloorPlanSpectator>(Allocator.Temp);
            if (watching.Any(w => w.Player == player))
            {
                return;
            }
            Entity spectator = EntityManager.CreateEntity();
            EntityManager.AddComponentData(spectator, new CFloorPlanSpectator
            {
                Session = session,
                Player = player,
                PlayerID = cPlayer.ID,
                InputSource = cPlayer.InputSource
            });
            EntityManager.AddComponentData(spectator, new CPosition(Vector3.zero));
            EntityManager.AddComponentData(spectator, new CRequiresView
            {
                Type = FloorPlanIds.ViewType,
                ViewMode = ViewMode.Screen
            });
            EntityManager.AddComponent<CDoNotPersist>(spectator);
            ModMain.Log($"Player {cPlayer.ID} is watching player {s.PlayerID}'s floor plan.");
        }

        private void CloseStaleSessions()
        {
            // Sessions destroyed elsewhere (FloorPlanDayCleanup) leave their bookkeeping behind.
            foreach (Entity gone in Statuses.Keys.Concat(Previews.Keys).Where(e => !EntityManager.HasComponent<CFloorPlanSession>(e)).ToList())
            {
                Statuses.Remove(gone);
                Previews.Remove(gone);
            }
            using (NativeArray<Entity> entities = Sessions.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity session in entities)
                {
                    CFloorPlanSession s = EntityManager.GetComponentData<CFloorPlanSession>(session);
                    if (!EntityManager.Exists(s.Player) || !Has<CPlayer>(s.Player))
                    {
                        CloseSession(session, "player left");
                    }
                }
            }
            using (NativeArray<Entity> spectators = Spectators.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity spectator in spectators)
                {
                    CFloorPlanSpectator w = EntityManager.GetComponentData<CFloorPlanSpectator>(spectator);
                    if (!EntityManager.HasComponent<CFloorPlanSession>(w.Session))
                    {
                        CloseSpectator(spectator, "the editor closed");
                    }
                    else if (!EntityManager.Exists(w.Player) || !Has<CPlayer>(w.Player))
                    {
                        CloseSpectator(spectator, "player left");
                    }
                }
            }
        }

        public void CloseSession(Entity session, string reason)
        {
            if (!EntityManager.Exists(session))
            {
                return;
            }
            Statuses.Remove(session);
            Previews.Remove(session);
            EntityManager.DestroyEntity(session);
            ModMain.Log($"Floor plan editor closed: {reason}.");
            using NativeArray<Entity> spectators = Spectators.ToEntityArray(Allocator.Temp);
            foreach (Entity spectator in spectators)
            {
                if (EntityManager.GetComponentData<CFloorPlanSpectator>(spectator).Session == session)
                {
                    CloseSpectator(spectator, "the editor closed");
                }
            }
        }

        public void CloseSpectator(Entity spectator, string reason)
        {
            if (!EntityManager.HasComponent<CFloorPlanSpectator>(spectator))
            {
                return;
            }
            int id = EntityManager.GetComponentData<CFloorPlanSpectator>(spectator).PlayerID;
            EntityManager.DestroyEntity(spectator);
            ModMain.Log($"Player {id} stopped watching the floor plan: {reason}.");
        }

        public SessionStatus GetStatus(Entity session) => Statuses.TryGetValue(session, out SessionStatus s) ? s : default;

        public EditorPreview GetPreview(Entity session) => Previews.TryGetValue(session, out EditorPreview p) ? p : null;

        /// <summary>A response from a spectator's view: it can only stop watching.</summary>
        public void HandleSpectatorResponse(Entity spectator, FloorPlanResponse response)
        {
            if (response.Action == FloorPlanAction.StopWatching)
            {
                CloseSpectator(spectator, "left");
                return;
            }
#if DEBUG
            TestIgnoredSpectatorActions++;
#endif
            ModMain.LogWarning($"Ignored a '{response.Action}' from a spectator's view (spectators can't edit).");
        }

        public void HandleResponse(Entity session, FloorPlanResponse response)
        {
            if (!EntityManager.HasComponent<CFloorPlanSession>(session))
            {
                return;
            }
            if (response.Action == FloorPlanAction.Preview)
            {
                // Display-only, so it doesn't count toward AckSequence (the editor waits on that
                // for its Apply). Keep only the newest; a null draft means "unchanged".
                EditorPreview p = GetPreview(session) ?? (Previews[session] = new EditorPreview());
                if (response.Sequence > p.Sequence)
                {
                    p.Sequence = response.Sequence;
                    p.Cursor = response.Cursor;
                    p.Area = response.CameraArea;
                    if (response.Draft != null)
                    {
                        p.Draft = response.Draft;
                        p.DraftHash = FloorPlanSerializer.Hash(response.Draft);
                    }
                }
                return;
            }
            SessionStatus status = GetStatus(session);
            if (response.Sequence <= status.AckSequence)
            {
                return; // duplicate
            }
            status.AckSequence = response.Sequence;

            switch (response.Action)
            {
                case FloorPlanAction.Cancel:
                    CloseSession(session, "cancelled");
                    return;

                case FloorPlanAction.Apply:
                    string error = TryApply(response.Draft, response.CameraArea);
                    if (error == null)
                    {
                        CloseSession(session, "changes applied");
                        return;
                    }
                    status.LastSucceeded = false;
                    status.Message = error;
                    break;

                default:
                    status.LastSucceeded = false;
                    status.Message = "unknown action";
                    break;
            }
            Statuses[session] = status;
        }

        /// <summary>
        /// Validates the draft against the live layout and commits it, then sets the camera area.
        /// Either can be unchanged: a camera-area-only Apply doesn't rebuild the restaurant.
        /// Returns the reason if refused (nothing is changed then).
        /// </summary>
        private string TryApply(LayoutBlueprint draft, FloorPlanCameraArea area)
        {
            if (draft == null)
            {
                return "nothing to apply";
            }
            if (!Has<SIsNightTime>())
            {
                return "the layout can only be changed at night";
            }
            string areaProblem = CameraAreaMarkers.Problem(area);
            if (areaProblem != null)
            {
                ModMain.Log($"Refused a floor plan draft: {areaProblem}");
                return areaProblem;
            }
            Entity layout = GetSingletonEntity<SLayout>();
            LayoutBlueprint live = LayoutWriter.Read(EntityManager, layout);
            bool layoutChanged = FloorPlanSerializer.Hash(live) != FloorPlanSerializer.Hash(draft);
            if (layoutChanged)
            {
                Dictionary<LayoutPosition, OccupantFlags> occupants = TileOccupants.Take(EntityManager);
                string error = LayoutEdits.ValidateDraft(live, draft, occupants);
                if (error != null)
                {
                    ModMain.Log($"Refused a floor plan draft: {error}");
                    return error;
                }
                if (!LayoutWriter.IsAvailable)
                {
                    return "layout editing is unavailable (see the log)";
                }
                int binned = TileOccupants.CountBinned(live, draft, occupants);
                LayoutCommit.Result r = LayoutCommit.Commit(EntityManager, TileManager, layout, draft);
                ModMain.Log($"Applied a floor plan draft: {draft.Tiles.Count} tiles, {draft.Features.Count} features; scenery moved {r.SceneryMoved}, removed {r.SceneryRemoved}; "
                            + $"appliances left outdoors {binned}, wall-mounted turned {r.WallMountedTurned}, table groups re-formed {r.TableGroupsReformed}, chairs cleared {r.ChairsCleared}.");
            }

            // After the commit, which may have resized the restaurant (and moved a whole-restaurant framing with it).
            Bounds bounds = EntityManager.GetComponentData<CBounds>(layout).Bounds;
            area = CameraAreaMarkers.Normalise(area, bounds);
            if (!area.SameAs(CameraAreaMarkers.Read(EntityManager, bounds)))
            {
                int placed = CameraAreaMarkers.Write(EntityManager, bounds, area);
                ModMain.Log(placed == 4 ? $"Camera area set to the {area}." : "The camera area couldn't be set (see above).");
            }
            return null;
        }

        /// <summary>The camera area in effect now.</summary>
        public FloorPlanCameraArea CurrentCameraArea()
        {
            Entity layout = GetSingletonEntity<SLayout>();
            return EntityManager.HasComponent<CBounds>(layout)
                ? CameraAreaMarkers.Read(EntityManager, EntityManager.GetComponentData<CBounds>(layout).Bounds)
                : FloorPlanCameraArea.Whole;
        }

        /// <summary>What stands on each tile, as far as edits care (sent to the editor for live feedback).</summary>
        public List<OccupiedTile> OccupiedTiles() =>
            TileOccupants.Take(EntityManager).Select(o => new OccupiedTile { Tile = o.Key, Flags = o.Value }).ToList();

#if DEBUG
        // Self-test hooks (Debug builds only).

        internal int TestIgnoredSpectatorActions;

        /// <summary>
        /// Adds a spectator even if it's the editor on the editor's own machine: the self-test has
        /// one player, who is also the editor, so this is the only way to get a spectator view built.
        /// </summary>
        internal void TestAddSpectator(Entity player)
        {
            if (Require(player, out CPlayer cPlayer))
            {
                OpenSpectator(player, cPlayer, selfTest: true);
            }
        }
#endif
    }

    /// <summary>Press (not hold) Interact on the Floor Plan terminal at night to open the editor.</summary>
    [UpdateBefore(typeof(RotateAppliances))]
    public class OpenFloorPlanInteraction : ApplianceInteractionSystem, IModSystem
    {
        private FloorPlanService Service;

        protected override bool IsPossible(ref InteractionData data)
        {
            if (!Has<CFloorPlanTerminal>(data.Target) || !Has<CPlayer>(data.Interactor) || !Has<SIsNightTime>())
            {
                return false;
            }
            Service = Service ?? World.GetExistingSystem<FloorPlanService>();
            return Service != null;
        }

        protected override void Perform(ref InteractionData data)
        {
            Service.RequestOpen(data.Interactor);
        }
    }

    /// <summary>
    /// Host: pushes the committed layout and what stands on each tile to the open editor, and the editor's
    /// preview to spectators, and applies their responses. Responses arrive through the game's
    /// PerformCommandRouter, which finds this system by its response type; which entity's view
    /// a response arrived on decides whether it's treated as the editor's or a spectator's.
    /// </summary>
    public class FloorPlanViewSystem : ResponsiveViewSystemBase<FloorPlanViewData, FloorPlanResponse>, IModSystem
    {
        private EntityQuery Sessions;
        private EntityQuery Spectators;
        private FloorPlanService Service;

        protected override void Initialise()
        {
            base.Initialise();
            Sessions = GetEntityQuery(new QueryHelper().All(typeof(CFloorPlanSession), typeof(CLinkedView)));
            Spectators = GetEntityQuery(new QueryHelper().All(typeof(CFloorPlanSpectator), typeof(CLinkedView)));
        }

        protected override void OnUpdate()
        {
            if ((Sessions.IsEmpty && Spectators.IsEmpty) || !HasSingleton<SLayout>())
            {
                ClearUpdates();
                return;
            }
            Service = Service ?? World.GetExistingSystem<FloorPlanService>();
            if (Service == null)
            {
                ClearUpdates();
                return;
            }

            using NativeArray<Entity> entities = Sessions.ToEntityArray(Allocator.Temp);
            using NativeArray<CLinkedView> views = Sessions.ToComponentDataArray<CLinkedView>(Allocator.Temp);
            using NativeArray<Entity> spectatorEntities = Spectators.ToEntityArray(Allocator.Temp);
            using NativeArray<CLinkedView> spectatorViews = Spectators.ToComponentDataArray<CLinkedView>(Allocator.Temp);

            var pending = new List<(Entity, FloorPlanResponse)>();
            for (int i = 0; i < entities.Length; i++)
            {
                Entity session = entities[i];
                ApplyUpdates(views[i].Identifier, response => pending.Add((session, response)));
            }
            var fromSpectators = new List<(Entity, FloorPlanResponse)>();
            for (int i = 0; i < spectatorEntities.Length; i++)
            {
                Entity spectator = spectatorEntities[i];
                ApplyUpdates(spectatorViews[i].Identifier, response => fromSpectators.Add((spectator, response)));
            }
            ClearUpdates();
            pending.Sort((a, b) => a.Item2.Sequence.CompareTo(b.Item2.Sequence));
            foreach ((Entity session, FloorPlanResponse response) in pending)
            {
                Service.HandleResponse(session, response);
            }
            foreach ((Entity spectator, FloorPlanResponse response) in fromSpectators)
            {
                Service.HandleSpectatorResponse(spectator, response);
            }

            LayoutBlueprint layout = LayoutWriter.Read(EntityManager, GetSingletonEntity<SLayout>());
            int layoutHash = FloorPlanSerializer.Hash(layout);
            FloorPlanCameraArea cameraArea = Service.CurrentCameraArea();
            List<OccupiedTile> occupied = Service.OccupiedTiles();
            int occupiedHash = 17;
            unchecked
            {
                foreach (OccupiedTile o in occupied)
                {
                    occupiedHash += (o.Tile.x * 73856093) ^ (o.Tile.y * 19349663) ^ ((int)o.Flags * 83492791);
                }
            }

            // Spectators still watching (a response above may have closed some). A closed entity
            // lingers while its system-state CLinkedView awaits view cleanup, so Exists() is still
            // true: check for the component itself (here and below).
            var watching = new List<(CLinkedView view, CFloorPlanSpectator spectator)>();
            for (int i = 0; i < spectatorEntities.Length; i++)
            {
                if (EntityManager.HasComponent<CFloorPlanSpectator>(spectatorEntities[i]))
                {
                    watching.Add((spectatorViews[i], EntityManager.GetComponentData<CFloorPlanSpectator>(spectatorEntities[i])));
                }
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (!EntityManager.HasComponent<CFloorPlanSession>(entities[i]))
                {
                    continue;
                }
                CFloorPlanSession s = EntityManager.GetComponentData<CFloorPlanSession>(entities[i]);
                FloorPlanService.SessionStatus status = Service.GetStatus(entities[i]);
                int spectatorCount = watching.Count(w => w.spectator.Session == entities[i]);
                SendUpdate(views[i], new FloorPlanViewData
                {
                    ViewerPlayerID = s.PlayerID,
                    ViewerInputSource = s.InputSource,
                    Spectating = false,
                    EditorPlayerID = s.PlayerID,
                    Layout = layout,
                    LayoutHash = layoutHash,
                    Occupied = occupied,
                    OccupiedHash = occupiedHash,
                    AckSequence = status.AckSequence,
                    LastSucceeded = status.LastSucceeded,
                    Status = status.Message ?? "",
                    SpectatorCount = spectatorCount,
                    CameraArea = cameraArea
                });
            }

            foreach ((CLinkedView view, CFloorPlanSpectator w) in watching)
            {
                if (!EntityManager.HasComponent<CFloorPlanSession>(w.Session))
                {
                    continue; // FloorPlanService closes it next frame
                }
                CFloorPlanSession s = EntityManager.GetComponentData<CFloorPlanSession>(w.Session);
                FloorPlanService.EditorPreview preview = Service.GetPreview(w.Session);
                SendUpdate(view, new FloorPlanViewData
                {
                    ViewerPlayerID = w.PlayerID,
                    ViewerInputSource = w.InputSource,
                    Spectating = true,
                    EditorPlayerID = s.PlayerID,
                    Layout = layout,
                    LayoutHash = layoutHash,
                    Occupied = new List<OccupiedTile>(), // spectators don't paint
                    Status = "",
                    SpectatorCount = watching.Count(o => o.spectator.Session == w.Session),
                    Preview = preview?.Draft,
                    PreviewHash = preview?.DraftHash ?? 0,
                    Cursor = preview?.Cursor ?? default,
                    CameraArea = cameraArea,
                    PreviewArea = preview != null ? preview.Area : cameraArea
                });
            }
        }
    }

    /// <summary>Host: labels the terminal "Floor Plan", or "Watch" while someone is editing.</summary>
    public class FloorPlanTerminalLabelSystem : IncrementalViewSystemBase<RerollBlueprintView.ViewData>, IModSystem
    {
        private EntityQuery Terminals;
        private EntityQuery Sessions;

        protected override void Initialise()
        {
            base.Initialise();
            Terminals = GetEntityQuery(new QueryHelper().All(typeof(CFloorPlanTerminal), typeof(CLinkedView)));
            Sessions = GetEntityQuery(typeof(CFloorPlanSession));
        }

        protected override void OnUpdate()
        {
            if (Terminals.IsEmpty)
            {
                return;
            }
            int label = Sessions.IsEmpty ? FloorPlanIds.LabelFloorPlan : FloorPlanIds.LabelWatch;
            using NativeArray<CLinkedView> views = Terminals.ToComponentDataArray<CLinkedView>(Allocator.Temp);
            foreach (CLinkedView view in views)
            {
                SendUpdate(view, new RerollBlueprintView.ViewData { Price = label, PlayerMoney = 0 });
            }
        }
    }

    /// <summary>Day start: closes the editor and any spectators, and removes the terminal (recreated at night).</summary>
    public class FloorPlanDayCleanup : StartOfDaySystem, IModSystem
    {
        private EntityQuery Sessions;
        private EntityQuery Spectators;
        private EntityQuery Terminals;

        protected override void Initialise()
        {
            base.Initialise();
            Sessions = GetEntityQuery(typeof(CFloorPlanSession));
            Spectators = GetEntityQuery(typeof(CFloorPlanSpectator));
            Terminals = GetEntityQuery(typeof(CFloorPlanTerminal));
        }

        protected override void OnUpdate()
        {
            EntityManager.DestroyEntity(Spectators);
            EntityManager.DestroyEntity(Sessions);
            EntityManager.DestroyEntity(Terminals);
        }
    }
}
