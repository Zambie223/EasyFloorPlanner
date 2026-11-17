using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;
using Platforms;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Client side of the edit mode. Only the viewing player's machine shows anything. A view is
    /// either the editor's or a spectator's (FloorPlanViewData.Spectating).
    ///
    /// Editor: the player edits a DRAFT copy of the committed layout. A world-space overlay on
    /// the floor shows it; nothing in the real restaurant changes until Apply, which sends the
    /// whole draft to the host. The host validates it (LayoutEdits.ValidateDraft) and commits it
    /// with one rebuild. Painting checks local rules immediately (LayoutEdits.PaintTile, with the
    /// blocked tiles the host sends); reachability is shown live and enforced on Apply. The draft
    /// and cursor are also sent as Previews (throttled), which the host relays to spectators.
    ///
    /// Spectator: read-only. Draws the editor's latest preview and cursor (in the editor's player
    /// colour) with the same overlay, and can only stop watching.
    ///
    /// Input: registered as an IInputConsumer, so the viewer's chef stands still. WASD / the left
    /// stick pans the camera (FloorPlanCamera), so players can work beyond what's visible.
    /// Keyboard and mouse players point with the mouse; for controller players the cursor is the
    /// tile (or nearest wall) at the centre of the screen.
    /// </summary>
    public class FloorPlanView : ResponsiveObjectView<FloorPlanViewData, FloorPlanResponse>, IInputConsumer
    {
        // The numeric values are sent to spectators (FloorPlanCursor.Mode).
        private enum Mode { Tiles = 0, Walls = 1, Camera = 2 }

        private static readonly Color CameraColour = new Color(0.25f, 0.85f, 1f);

        private class Brush
        {
            public string Label;
            public Room Room;           // NoRoom = erase
            public RoomType NewType;    // set for "new room" brushes until their first tile
            public bool IsNew => NewType != RoomType.NoRoom;
        }

        private const float CloseRetrySeconds = 2f;
        private const float FlashSeconds = 1.2f;

        /// <summary>At most this often, the editor sends its draft/cursor for spectators.</summary>
        private const float PreviewInterval = 0.2f;

        /// <summary>Above the building's floor surface (0.04 drew under it: only outdoor tiles showed).</summary>
        private const float OverlayHeight = 0.12f;

        // --- state
        private FloorPlanViewData Data;
        private bool HasData;
        private bool IsLocalViewer;
        private bool Spectating;
        private bool Built;

        // editor: what was last sent as a preview
        private bool SentPreview;
        private int LastPreviewHash;
        private FloorPlanCursor LastPreviewCursor;
        private float NextPreviewAt;

        // spectator: what's shown
        private int ShownPreviewHash;
        private string EditorName = "";
        private Color EditorColour = Color.white;
        private bool ClosedLocally;
        private float CloseRequestedAt;
        private readonly Queue<FloorPlanResponse> Outgoing = new Queue<FloorPlanResponse>();
        private int Sequence;
        private int AwaitingSequence;

        private InputState LastInput;
        private bool HasFreshInput;
        private bool UsesMouse;

        private LayoutBlueprint Committed;
        private int CommittedHash;
        private LayoutBlueprint Draft;
        private readonly Stack<(LayoutBlueprint draft, FloorPlanCameraArea area)> Undo = new Stack<(LayoutBlueprint, FloorPlanCameraArea)>();

        /// <summary>What the day camera frames now, and in the draft (see CameraAreaMarkers).</summary>
        private FloorPlanCameraArea CommittedArea;
        private FloorPlanCameraArea DraftArea;
        private FloorPlanCameraArea LastPreviewArea;
        private HashSet<LayoutPosition> Blocked = new HashSet<LayoutPosition>();
        private HashSet<LayoutPosition> Unreachable = new HashSet<LayoutPosition>();

        private readonly List<Brush> Brushes = new List<Brush>();
        private int BrushIndex;

        /// <summary>Wall-mode brushes: what a clicked wall becomes. Shown on the cursor and in the HUD.</summary>
        private static readonly (string Label, FeatureType? Type, Color Colour)[] EdgeBrushes =
        {
            ("Wall", null, new Color(0.85f, 0.85f, 0.85f)),
            ("Hatch (serving window)", FeatureType.Hatch, new Color(1f, 0.6f, 0.1f)),
            ("Door", FeatureType.Door, new Color(0.2f, 0.85f, 0.3f)),
        };
        private int EdgeBrushIndex = 2; // Door
        private (LayoutPosition, LayoutPosition)? LastEdge;

        private string CurrentBrushLabel => CurrentMode == Mode.Walls
            ? EdgeBrushes[EdgeBrushIndex].Label
            : CurrentMode == Mode.Camera
                ? AreaLabel(DraftArea)
                : (Brushes.Count > 0 ? Brushes[Mathf.Clamp(BrushIndex, 0, Brushes.Count - 1)].Label : "");

        private static string AreaLabel(FloorPlanCameraArea a) => a.Custom ? $"Day view: {a.Width} x {a.Height} tiles" : "Day view: whole restaurant";
        private Mode CurrentMode = Mode.Tiles;

        private LayoutPosition CursorTile;
        private LayoutPosition CursorFacing = new LayoutPosition(0, 1);
        private bool CursorPlaced;
        private LayoutPosition? LastPainted;

        /// <summary>Shift + drag: the corner where the box started (null when not drawing one).</summary>
        private LayoutPosition? BoxStart;
        private LayoutPosition BoxEnd;
        private Vector2 HeldMovement;
        private float MovementAt = -1f;
        private InputLock.Lock PlayerLock;
        private bool HasPlayerLock;

        private string Message = "";
        private float MessageUntil;
        private LayoutPosition? FlashTile;
        private float FlashUntil;

        // --- visuals
        private GameObject OverlayRoot;
        private RectTransform OverlayRect;
        private readonly List<Image> Pool = new List<Image>();
        private int PoolUsed;
        private readonly List<Image> CursorBars = new List<Image>();
        private Image FlashImage;
        private Image BoxImage;
        private GameObject HudRoot;

        // Holograms: wall / hatch / door symbols for drafted wall changes and the wall under the
        // cursor, on a layer just above the walls (the floor overlay sits under the wall tops).
        private GameObject HoloRoot;
        private RectTransform HoloRect;
        private CanvasGroup HoloGroup;
        private readonly List<Image> HoloPool = new List<Image>();
        private int HoloUsed;
        private readonly List<Image> GhostPool = new List<Image>();
        private int GhostUsed;
        private float HoloHeight = 1.3f;
        private TextMeshProUGUI HudTitle;
        private TextMeshProUGUI HudHelp;
        private TextMeshProUGUI HudStatus;
        private bool OverlayDirty = true;

        public override void Initialise()
        {
            base.Initialise();
            // The router instantiates our (inactive) template; activate the instance.
            gameObject.SetActive(true);
        }

        protected override void UpdateData(FloorPlanViewData data)
        {
            Data = data;
            if (!HasData)
            {
                HasData = true;
                Spectating = data.Spectating;
                IsLocalViewer = DetermineLocalViewer(data);
                if (IsLocalViewer)
                {
                    try
                    {
                        Build();
                    }
                    catch (Exception e)
                    {
                        ModMain.LogError($"Failed to build the floor plan {(Spectating ? "spectator view" : "editor")}: {e}");
                        RequestClose();
                    }
                }
            }
            if (!Built)
            {
                return;
            }
            if (Spectating)
            {
                try
                {
                    UpdateSpectatorData(data);
                }
                catch (Exception e)
                {
                    ModMain.LogError($"Floor plan spectator view error: {e}");
                }
                return;
            }

            Blocked = new HashSet<LayoutPosition>(data.Blocked ?? new List<LayoutPosition>());
            CommittedArea = data.CameraArea;
            if (data.Layout != null && data.LayoutHash != CommittedHash)
            {
                bool hadDraft = Committed != null;
                Committed = data.Layout;
                CommittedHash = data.LayoutHash;
                if (hadDraft)
                {
                    ShowMessage("The restaurant layout changed; your draft was reset.");
                }
                ResetDraft();
            }
            if (AwaitingSequence != 0 && data.AckSequence >= AwaitingSequence)
            {
                AwaitingSequence = 0;
                if (!data.LastSucceeded && !string.IsNullOrEmpty(data.Status))
                {
                    ShowMessage("Can't apply: " + data.Status);
                }
            }
            OverlayDirty = true;
        }

        private static bool DetermineLocalViewer(FloorPlanViewData data)
        {
            BaseInputSource input = InputSourceIdentifier.DefaultInputSource;
            if (input == null || data.ViewerInputSource != (int)InputSourceIdentifier.Identifier)
            {
                return false;
            }
            return input.GetCurrentController(data.ViewerPlayerID) != ControllerType.None;
        }

        /// <summary>Spectator: show the editor's latest preview (or the committed layout until one arrives).</summary>
        private void UpdateSpectatorData(FloorPlanViewData data)
        {
            bool layoutChanged = data.Layout != null && data.LayoutHash != CommittedHash;
            if (layoutChanged)
            {
                Committed = data.Layout;
                CommittedHash = data.LayoutHash;
            }
            if (Committed == null)
            {
                return;
            }
            int previewHash = data.Preview != null ? data.PreviewHash : CommittedHash;
            if (layoutChanged || Draft == null || previewHash != ShownPreviewHash)
            {
                ShownPreviewHash = previewHash;
                Draft = data.Preview ?? Committed;
                Unreachable = LayoutEdits.Unreachable(Draft);
                OverlayDirty = true;
            }
            if (!data.PreviewArea.SameAs(DraftArea) || !data.CameraArea.SameAs(CommittedArea))
            {
                OverlayDirty = true;
            }
            CommittedArea = data.CameraArea;
            DraftArea = data.PreviewArea;
            CursorTile = data.Cursor.Tile;
            CursorFacing = data.Cursor.Facing;
            Mode mode = data.Cursor.Mode == 1 ? Mode.Walls : data.Cursor.Mode == 2 ? Mode.Camera : Mode.Tiles;
            if (mode != CurrentMode)
            {
                CurrentMode = mode;
                OverlayDirty = true;
            }
            // The editor's facing is never (0, 0), so a zero facing means no preview has arrived yet.
            CursorPlaced = data.Cursor.Facing.x != 0 || data.Cursor.Facing.y != 0;
        }

        /// <summary>A player-supplied string, safe for a rich-text TMP label.</summary>
        private static string Plain(string s, int max)
        {
            s = (s ?? "").Replace("<", "").Replace(">", "").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        public override bool HasStateUpdate(out IResponseData state)
        {
            if (Outgoing.Count > 0)
            {
                state = Outgoing.Dequeue();
                return true;
            }
            state = null;
            return false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            TearDown();
        }

        private void TearDown()
        {
            LocalInputSourceConsumers.Remove(this);
            UnlockPlayer();
            FloorPlanCamera.End(this);
            if (OverlayRoot != null)
            {
                Destroy(OverlayRoot);
                OverlayRoot = null;
            }
            if (HoloRoot != null)
            {
                Destroy(HoloRoot);
                HoloRoot = null;
            }
            HoloPool.Clear();
            GhostPool.Clear();
            if (HudRoot != null)
            {
                Destroy(HudRoot);
                HudRoot = null;
            }
            Pool.Clear();
            CursorBars.Clear();
            Built = false;
        }

        // ================================================================= input

        public InputConsumerState TakeInput(int player_id, InputState state)
        {
            if (!Built || ClosedLocally || !HasData || player_id != Data.ViewerPlayerID)
            {
                return InputConsumerState.NotConsumed;
            }
            LastInput = state;
            HasFreshInput = true;
            // Movement is a held state, and this isn't necessarily called every frame: keep the
            // latest value briefly so panning stays smooth.
            HeldMovement = state.Movement;
            MovementAt = Time.unscaledTime;
            return InputConsumerState.Consumed; // the game sends neutral input, so the chef stands still
        }

        private static bool Pressed(ButtonState s) => s == ButtonState.Pressed;

        private static bool Down(ButtonState s) => s == ButtonState.Pressed || s == ButtonState.Held;

        private void Update()
        {
            if (!IsLocalViewer)
            {
                return;
            }
            if (ClosedLocally)
            {
                // If the host never saw our Close/Apply (it would destroy this view), ask again.
                if (AwaitingSequence == 0 && Time.unscaledTime - CloseRequestedAt > CloseRetrySeconds)
                {
                    CloseRequestedAt = Time.unscaledTime;
                    Enqueue(CloseAction, null);
                }
                return;
            }
            if (!Built || Draft == null)
            {
                return;
            }
            try
            {
                if (Spectating)
                {
                    SpectatorTick();
                }
                else
                {
                    Tick();
                }
            }
            catch (Exception e)
            {
                ModMain.LogError($"Floor plan {(Spectating ? "spectator view" : "editor")} error: {e}");
            }
        }

        private FloorPlanAction CloseAction => Spectating ? FloorPlanAction.StopWatching : FloorPlanAction.Cancel;

        private void ReadViewerController()
        {
            ControllerType controller = InputSourceIdentifier.DefaultInputSource?.GetCurrentController(Data.ViewerPlayerID) ?? ControllerType.None;
            UsesMouse = (controller & (ControllerType.Keyboard | ControllerType.Mouse)) != 0;
        }

        /// <summary>Spectator: watch until Back/Esc; the editor's changes arrive through UpdateData.</summary>
        private void SpectatorTick()
        {
            ReadViewerController();
            InputState input = HasFreshInput ? LastInput : default;
            HasFreshInput = false;
            LastInput = default;
            bool leave = UsesMouse && Keyboard.current != null
                ? Keyboard.current.escapeKey.wasPressedThisFrame
                : Pressed(input.MenuCancel) || Pressed(input.MenuTrigger);
            if (leave)
            {
                RequestClose();
                return;
            }
            PanFromInput();
            if (OverlayDirty)
            {
                OverlayDirty = false;
                RebuildOverlay();
            }
            UpdateCursorVisual();
            UpdateSpectatorHud();
        }

        private void Tick()
        {
            ReadViewerController();
            InputState input = HasFreshInput ? LastInput : default;
            HasFreshInput = false;
            LastInput = default;

            bool paintDown, paintPressed, pick, nextBrush, prevBrush, toggleMode, undo, apply, cancel;
            if (UsesMouse && Mouse.current != null && Keyboard.current != null)
            {
                Keyboard kb = Keyboard.current;
                if (TryScreenTile(Mouse.current.position.ReadValue(), out LayoutPosition tile, out LayoutPosition facing))
                {
                    CursorTile = tile;
                    CursorFacing = facing;
                    CursorPlaced = true;
                }
                paintPressed = Mouse.current.leftButton.wasPressedThisFrame;
                paintDown = Mouse.current.leftButton.isPressed;
                pick = Mouse.current.rightButton.wasPressedThisFrame;
                // Ctrl + wheel zooms the view instead (PlayCamera).
                float wheel = kb.ctrlKey.isPressed ? 0f : Mouse.current.scroll.ReadValue().y;
                nextBrush = kb.eKey.wasPressedThisFrame || wheel < 0f;
                prevBrush = kb.qKey.wasPressedThisFrame || wheel > 0f;
                toggleMode = kb.tabKey.wasPressedThisFrame;
                undo = kb.zKey.wasPressedThisFrame && kb.ctrlKey.isPressed;
                apply = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                cancel = kb.escapeKey.wasPressedThisFrame;
            }
            else
            {
                // The stick pans the view; the cursor is the tile (or, in wall mode, the nearest
                // wall) at the centre of the screen.
                if (TryScreenTile(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), out LayoutPosition tile, out LayoutPosition facing))
                {
                    CursorTile = tile;
                    CursorFacing = facing;
                    CursorPlaced = true;
                }
                paintPressed = Pressed(input.InteractAction);
                paintDown = Down(input.InteractAction);
                pick = Pressed(input.GrabAction);
                nextBrush = Pressed(input.SecondaryAction1) || Pressed(input.MenuRight);
                prevBrush = Pressed(input.MenuLeft);
                toggleMode = Pressed(input.SecondaryAction2);
                undo = Pressed(input.StopMoving);
                apply = Pressed(input.MenuSelect);
                cancel = Pressed(input.MenuCancel) || Pressed(input.MenuTrigger);
            }

            if (cancel && BoxStart.HasValue)
            {
                BoxStart = null; // Esc / Back while drawing a box cancels just the box
                cancel = false;
                ShowMessage("Box cancelled.");
            }
            if (cancel)
            {
                RequestClose();
                return;
            }
            PanFromInput();
            if (apply)
            {
                TryApply();
            }
            if (toggleMode)
            {
                CurrentMode = CurrentMode == Mode.Tiles ? Mode.Walls : CurrentMode == Mode.Walls ? Mode.Camera : Mode.Tiles;
                OverlayDirty = true;
                LastPainted = null;
                LastEdge = null;
                BoxStart = null;
            }
            if (nextBrush)
            {
                CycleBrush(+1);
            }
            if (prevBrush)
            {
                CycleBrush(-1);
            }
            if (undo && Undo.Count > 0)
            {
                (Draft, DraftArea) = Undo.Pop();
                AfterDraftChanged();
                ShowMessage("Undone.");
            }
            if (pick)
            {
                if (CurrentMode == Mode.Walls)
                {
                    PickEdgeUnderCursor();
                }
                else if (CurrentMode == Mode.Camera)
                {
                    SetCameraArea(FloorPlanCameraArea.Whole);
                }
                else
                {
                    PickRoomUnderCursor();
                }
            }

            bool boxModifier = UsesMouse && Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (CurrentMode == Mode.Tiles)
            {
                if (BoxStart.HasValue)
                {
                    if (paintDown)
                    {
                        BoxEnd = CursorTile;
                    }
                    else
                    {
                        LayoutPosition start = BoxStart.Value;
                        BoxStart = null;
                        PaintBox(start, BoxEnd);
                    }
                }
                else if (paintPressed && boxModifier)
                {
                    BoxStart = CursorTile;
                    BoxEnd = CursorTile;
                    LastPainted = null;
                }
                else if (!paintDown)
                {
                    LastPainted = null;
                }
                else if (paintPressed || LastPainted == null || !LastPainted.Value.Equals(CursorTile))
                {
                    LastPainted = CursorTile;
                    PaintAtCursor(paintPressed);
                }
            }
            else if (CurrentMode == Mode.Camera)
            {
                // Drag a box: the area the day camera frames.
                if (BoxStart.HasValue)
                {
                    if (paintDown)
                    {
                        BoxEnd = CursorTile;
                    }
                    else
                    {
                        LayoutPosition start = BoxStart.Value;
                        BoxStart = null;
                        SetCameraArea(FloorPlanCameraArea.Box(start.x, start.y, BoxEnd.x, BoxEnd.y));
                    }
                }
                else if (paintPressed)
                {
                    BoxStart = CursorTile;
                    BoxEnd = CursorTile;
                }
            }
            else if (!paintDown)
            {
                LastEdge = null;
            }
            else
            {
                // Click a wall, or drag along several: each becomes the wall brush.
                (LayoutPosition, LayoutPosition) edge = EdgeOf(CursorTile, CursorTile + CursorFacing);
                if (paintPressed || LastEdge == null || !LastEdge.Value.Equals(edge))
                {
                    LastEdge = edge;
                    SetEdgeAtCursor(paintPressed);
                }
            }

            if (OverlayDirty)
            {
                OverlayDirty = false;
                RebuildOverlay();
            }
            UpdateCursorVisual();
            UpdateHud();
            MaybeSendPreview();
        }

        /// <summary>
        /// Sends the draft (when it changed) and, while anyone is watching, the cursor, at most
        /// every PreviewInterval. Draft changes are sent even with no spectators, so the host
        /// already has the latest draft for anyone who starts watching.
        /// </summary>
        private void MaybeSendPreview()
        {
            float now = Time.unscaledTime;
            if (now < NextPreviewAt || AwaitingSequence != 0)
            {
                return;
            }
            int hash = FloorPlanSerializer.Hash(Draft);
            var cursor = new FloorPlanCursor
            {
                Tile = CursorTile,
                Facing = CursorFacing,
                Mode = (byte)CurrentMode,
                BrushLabel = CurrentBrushLabel
            };
            bool draftChanged = !SentPreview || hash != LastPreviewHash || !DraftArea.SameAs(LastPreviewArea);
            bool cursorChanged = Data.SpectatorCount > 0 && !cursor.SameAs(LastPreviewCursor);
            if (!draftChanged && !cursorChanged)
            {
                return;
            }
            SentPreview = true;
            LastPreviewHash = hash;
            LastPreviewCursor = cursor;
            LastPreviewArea = DraftArea;
            NextPreviewAt = now + PreviewInterval;
            Outgoing.Enqueue(new FloorPlanResponse
            {
                Sequence = ++Sequence,
                Action = FloorPlanAction.Preview,
                // A copy: the draft is edited in place, and the response may be read later.
                Draft = draftChanged ? new LayoutBlueprint(Draft) : null,
                Cursor = cursor,
                CameraArea = DraftArea
            });
        }

        /// <summary>
        /// The floor tile under a screen point (the mouse, or the screen centre for controllers);
        /// the facing picks the tile's nearest edge, for wall mode.
        /// </summary>
        private static bool TryScreenTile(Vector2 screenPoint, out LayoutPosition tile, out LayoutPosition facing)
        {
            tile = default;
            facing = new LayoutPosition(0, 1);
            Camera cam = Camera.main;
            if (cam == null)
            {
                return false;
            }
            Ray ray = cam.ScreenPointToRay(screenPoint);
            var floor = new Plane(Vector3.up, Vector3.zero);
            if (!floor.Raycast(ray, out float distance))
            {
                return false;
            }
            Vector3 hit = ray.GetPoint(distance);
            int limit = LayoutEdits.MaxExtent + 2;
            tile = new LayoutPosition(Mathf.Clamp(Mathf.RoundToInt(hit.x), -limit, limit), Mathf.Clamp(Mathf.RoundToInt(hit.z), -limit, limit));
            float dx = hit.x - tile.x, dz = hit.z - tile.y;
            facing = Mathf.Abs(dx) >= Mathf.Abs(dz)
                ? new LayoutPosition(dx >= 0f ? 1 : -1, 0)
                : new LayoutPosition(0, dz >= 0f ? 1 : -1);
            return true;
        }

        /// <summary>WASD / left stick pans the view (see FloorPlanCamera).</summary>
        private void PanFromInput()
        {
            Vector2 move = Time.unscaledTime - MovementAt < 0.25f ? HeldMovement : Vector2.zero;
            FloorPlanCamera.Pan(move, Time.unscaledDeltaTime);
        }

        // ================================================================= editing

        private void ResetDraft()
        {
            Draft = new LayoutBlueprint(Committed);
            DraftArea = CommittedArea;
            Undo.Clear();
            AfterDraftChanged();
        }

        private void AfterDraftChanged()
        {
            Unreachable = LayoutEdits.Unreachable(Draft);
            RebuildBrushes();
            OverlayDirty = true;
        }

        private void PaintAtCursor(bool explicitPress)
        {
            Brush brush = Brushes[BrushIndex];
            LayoutBlueprint before = new LayoutBlueprint(Draft);
            Room room = brush.IsNew ? LayoutEdits.NewRoom(Draft, brush.NewType) : brush.Room;
            string refused = LayoutEdits.PaintTile(Draft, CursorTile, room, p => Blocked.Contains(p));
            if (refused != null)
            {
                if (explicitPress)
                {
                    Refuse(refused, CursorTile);
                }
                return;
            }
            if (FloorPlanSerializer.Hash(before) == FloorPlanSerializer.Hash(Draft))
            {
                return; // nothing changed (same room, or erasing outdoors)
            }
            Undo.Push((before, DraftArea));
            if (brush.IsNew)
            {
                // The new room exists now; keep painting into it.
                brush.Room = room;
                brush.NewType = RoomType.NoRoom;
                brush.Label = room.Type == RoomType.Kitchen ? "New kitchen" : "New dining room";
            }
            AfterDraftChanged();
        }

        private static Color BrushColour(Brush b) => b.IsNew
            ? (b.NewType == RoomType.Kitchen ? KitchenColour : DiningColour)
            : b.Room.Type == RoomType.NoRoom ? new Color(1f, 0.35f, 0.3f) : RoomColour(b.Room, 1f);

        /// <summary>Largest box (tiles per side) Shift + drag paints in one go.</summary>
        private const int MaxBoxSide = 40;

        /// <summary>
        /// Shift + drag: paints every tile of the box between two corners with the current brush,
        /// as one undo step. A tile can only be painted next to its room, so the box fills in waves
        /// outward from the room: each pass paints what it can, until a pass paints nothing. Tiles
        /// still refused (an appliance in the way, a door tile, ...) are reported.
        /// </summary>
        private void PaintBox(LayoutPosition a, LayoutPosition b)
        {
            int x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            if (x1 - x0 + 1 > MaxBoxSide || y1 - y0 + 1 > MaxBoxSide)
            {
                Refuse($"that box is too big (at most {MaxBoxSide} x {MaxBoxSide})", b);
                return;
            }
            Brush brush = Brushes[BrushIndex];
            LayoutBlueprint before = new LayoutBlueprint(Draft);
            Room room = brush.IsNew ? LayoutEdits.NewRoom(Draft, brush.NewType) : brush.Room;

            var remaining = new List<LayoutPosition>();
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    remaining.Add(new LayoutPosition(x, y));
                }
            }
            int total = remaining.Count;
            string lastRefusal = null;
            bool progress = true;
            while (progress && remaining.Count > 0)
            {
                progress = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    string refused = LayoutEdits.PaintTile(Draft, remaining[i], room, p => Blocked.Contains(p));
                    if (refused == null)
                    {
                        remaining.RemoveAt(i);
                        progress = true;
                    }
                    else
                    {
                        lastRefusal = refused;
                    }
                }
            }

            if (FloorPlanSerializer.Hash(before) == FloorPlanSerializer.Hash(Draft))
            {
                if (remaining.Count > 0)
                {
                    Refuse(lastRefusal ?? "nothing in that box can be painted", remaining[0]);
                }
                return;
            }
            Undo.Push((before, DraftArea));
            if (brush.IsNew && Draft.Tiles.Values.Any(r => r.ID == room.ID))
            {
                brush.Room = room;
                brush.NewType = RoomType.NoRoom;
                brush.Label = room.Type == RoomType.Kitchen ? "New kitchen" : "New dining room";
            }
            AfterDraftChanged();
            if (remaining.Count > 0)
            {
                Refuse($"painted {total - remaining.Count} of {total} tiles; the rest can't be ({lastRefusal})", remaining[0]);
            }
            else
            {
                ShowMessage($"Painted a {x1 - x0 + 1} x {y1 - y0 + 1} box.");
            }
        }

        private static (LayoutPosition, LayoutPosition) EdgeOf(LayoutPosition a, LayoutPosition b) =>
            a.x < b.x || (a.x == b.x && a.y < b.y) ? (a, b) : (b, a);

        /// <summary>
        /// Sets the wall under the cursor to the wall brush. Removing a door is allowed even if it
        /// cuts floor off: that floor shows orange and Apply waits until there's a way in again.
        /// </summary>
        private void SetEdgeAtCursor(bool explicitPress)
        {
            var brush = EdgeBrushes[EdgeBrushIndex];
            LayoutBlueprint before = new LayoutBlueprint(Draft);
            string refused = LayoutEdits.SetEdge(Draft, CursorTile, CursorTile + CursorFacing, brush.Type);
            if (refused != null)
            {
                if (explicitPress)
                {
                    Refuse(refused, CursorTile);
                }
                return;
            }
            if (FloorPlanSerializer.Hash(before) == FloorPlanSerializer.Hash(Draft))
            {
                return; // already that
            }
            Undo.Push((before, DraftArea));
            ShowMessage(brush.Label);
            AfterDraftChanged();
        }

        /// <summary>Right click / Grab in wall mode: the wall brush becomes whatever that wall is.</summary>
        private void PickEdgeUnderCursor()
        {
            string refused = LayoutEdits.ReadEdge(Draft, CursorTile, CursorTile + CursorFacing, out FeatureType? type);
            if (refused != null)
            {
                Refuse(refused, CursorTile);
                return;
            }
            int i = Array.FindIndex(EdgeBrushes, b => b.Type == type);
            if (i >= 0)
            {
                EdgeBrushIndex = i;
                ShowMessage("Brush: " + EdgeBrushes[i].Label);
            }
        }

        private void PickRoomUnderCursor()
        {
            if (!Draft.Tiles.TryGetValue(CursorTile, out Room room) || !LayoutHelpers.IsInside(room) || room.Type == RoomType.Garden)
            {
                SelectBrush(b => b.Room.Type == RoomType.NoRoom && !b.IsNew);
                return;
            }
            SelectBrush(b => !b.IsNew && b.Room.ID == room.ID);
        }

        private void TryApply()
        {
            if (AwaitingSequence != 0)
            {
                return;
            }
            if (FloorPlanSerializer.Hash(Draft) == CommittedHash && DraftArea.SameAs(CommittedArea))
            {
                RequestClose(); // nothing to apply
                return;
            }
            if (Unreachable.Count > 0)
            {
                Refuse("some floor can't be reached from the front door (orange). Add a door", Unreachable.First());
                return;
            }
            Enqueue(FloorPlanAction.Apply, Draft);
            AwaitingSequence = Sequence;
            ShowMessage("Applying...");
        }

        private void RequestClose()
        {
            ClosedLocally = true;
            CloseRequestedAt = Time.unscaledTime;
            Enqueue(CloseAction, null);
            if (OverlayRoot != null)
            {
                OverlayRoot.SetActive(false);
            }
            if (HoloRoot != null)
            {
                HoloRoot.SetActive(false);
            }
            if (HudRoot != null)
            {
                HudRoot.SetActive(false);
            }
            LocalInputSourceConsumers.Remove(this);
            UnlockPlayer();
            FloorPlanCamera.End(this);
        }

        /// <summary>
        /// Consuming input (TakeInput) only neutralises what the simulation gets: the local chef's
        /// walking animation reads the player's raw input (PlayerWalkingComponent via
        /// GetCurrentInputData), so the chef visibly walked on WASD while editing. A NonPause input
        /// lock blanks that, the way vanilla's profile editor does; consumers still get the raw input.
        /// </summary>
        private void LockPlayer()
        {
            BaseInputSource source = InputSourceIdentifier.DefaultInputSource;
            if (source != null && !HasPlayerLock)
            {
                PlayerLock = source.SetInputLock(Data.ViewerPlayerID, PlayerLockState.NonPause);
                HasPlayerLock = true;
            }
        }

        private void UnlockPlayer()
        {
            if (!HasPlayerLock)
            {
                return;
            }
            HasPlayerLock = false;
            InputSourceIdentifier.DefaultInputSource?.ReleaseLock(Data.ViewerPlayerID, PlayerLock);
        }

        private void Enqueue(FloorPlanAction action, LayoutBlueprint draft)
        {
            Outgoing.Enqueue(new FloorPlanResponse { Sequence = ++Sequence, Action = action, Draft = draft, CameraArea = DraftArea });
        }

        private void Refuse(string why, LayoutPosition where)
        {
            string text = char.ToUpperInvariant(why[0]) + why.Substring(1);
            ShowMessage(text.EndsWith(".") ? text : text + ".");
            FlashTile = where;
            FlashUntil = Time.unscaledTime + FlashSeconds;
        }

        private void ShowMessage(string text)
        {
            Message = text;
            MessageUntil = Time.unscaledTime + 4f;
        }

        // ================================================================= brushes

        private void RebuildBrushes()
        {
            Brush previous = Brushes.Count > 0 ? Brushes[Mathf.Clamp(BrushIndex, 0, Brushes.Count - 1)] : null;
            var rooms = Draft.Tiles.Values
                .Where(r => LayoutHelpers.IsInside(r) && r.Type != RoomType.Garden)
                .GroupBy(r => r.ID).Select(g => g.First())
                .OrderBy(r => r.Type == RoomType.Kitchen ? 0 : r.Type == RoomType.Dining ? 1 : 2).ThenBy(r => r.ID)
                .ToList();
            var counts = new Dictionary<RoomType, int>();
            Brushes.Clear();
            foreach (Room r in rooms)
            {
                counts.TryGetValue(r.Type, out int n);
                counts[r.Type] = ++n;
                string name = r.Type == RoomType.Kitchen ? "Kitchen" : r.Type == RoomType.Dining ? "Dining room" : "Side room";
                Brushes.Add(new Brush { Label = n > 1 ? $"{name} {n}" : name, Room = r });
            }
            Brushes.Add(new Brush { Label = "New dining room", Room = Room.Null, NewType = RoomType.Dining });
            Brushes.Add(new Brush { Label = "New kitchen", Room = Room.Null, NewType = RoomType.Kitchen });
            Brushes.Add(new Brush { Label = "Outdoors (remove floor)", Room = Room.Null });

            BrushIndex = 0;
            if (previous != null)
            {
                int same = previous.IsNew
                    ? Brushes.FindIndex(b => b.IsNew && b.NewType == previous.NewType)
                    : Brushes.FindIndex(b => !b.IsNew && b.Room.ID == previous.Room.ID && b.Room.Type == previous.Room.Type);
                BrushIndex = same >= 0 ? same : 0;
            }
        }

        private void CycleBrush(int step)
        {
            if (CurrentMode == Mode.Camera)
            {
                return; // the camera area has no brushes
            }
            if (CurrentMode == Mode.Walls)
            {
                EdgeBrushIndex = (EdgeBrushIndex + step + EdgeBrushes.Length) % EdgeBrushes.Length;
                return;
            }
            BrushIndex = (BrushIndex + step + Brushes.Count) % Brushes.Count;
        }

        private void SelectBrush(Func<Brush, bool> match)
        {
            int i = Brushes.FindIndex(b => match(b));
            if (i >= 0)
            {
                BrushIndex = i;
                ShowMessage("Brush: " + Brushes[i].Label);
            }
        }

        /// <summary>Sets the drafted camera area (one undo step), or refuses it.</summary>
        private void SetCameraArea(FloorPlanCameraArea area)
        {
            string problem = CameraAreaMarkers.Problem(area);
            if (problem != null)
            {
                Refuse(problem, CursorTile);
                return;
            }
            if (area.SameAs(DraftArea))
            {
                return;
            }
            Undo.Push((new LayoutBlueprint(Draft), DraftArea));
            DraftArea = area;
            OverlayDirty = true;
            ShowMessage(area.Custom
                ? $"The day camera will frame this {area.Width} x {area.Height} area."
                : "The day camera will frame the whole restaurant.");
        }

        // ================================================================= visuals

        private void Build()
        {
            // World overlay: a world-space canvas lying on the floor. Rotating +90 degrees about X
            // maps canvas (x, y) to world (x, z) and makes its front face upward.
            OverlayRoot = new GameObject("EasyFloorPlanner Overlay");
            OverlayRoot.transform.position = new Vector3(0f, OverlayHeight, 0f);
            OverlayRoot.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Canvas world = OverlayRoot.AddComponent<Canvas>();
            world.renderMode = RenderMode.WorldSpace;
            world.sortingOrder = 50;
            OverlayRect = OverlayRoot.GetComponent<RectTransform>();
            OverlayRect.sizeDelta = new Vector2(200f, 200f);
            OverlayRect.localScale = Vector3.one;

            for (int i = 0; i < 4; i++)
            {
                CursorBars.Add(NewImage(OverlayRect, Color.white));
            }
            BuildHoloLayer();
            FlashImage = NewImage(OverlayRect, new Color(1f, 0.15f, 0.1f, 0.6f));
            BoxImage = NewImage(OverlayRect, new Color(1f, 1f, 1f, 0.3f));
            BoxImage.gameObject.SetActive(false);
            FlashImage.gameObject.SetActive(false);

            // HUD: a strip along the bottom of the screen.
            HudRoot = new GameObject("EasyFloorPlanner HUD");
            Canvas hud = HudRoot.AddComponent<Canvas>();
            hud.renderMode = RenderMode.ScreenSpaceOverlay;
            hud.sortingOrder = 100;
            CanvasScaler scaler = HudRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(HudRoot.transform, false);
            var pr = panel.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(0.5f, 0f);
            pr.anchorMax = new Vector2(0.5f, 0f);
            pr.pivot = new Vector2(0.5f, 0f);
            pr.anchoredPosition = new Vector2(0f, 104f); // above the game's player bar along the bottom edge
            pr.sizeDelta = new Vector2(1500f, 196f);
            panel.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.1f, 0.82f);

            TMP_FontAsset font = FloorPlanFont.Find();
            HudTitle = NewText(pr, font, 30f, new Vector2(0f, 64f), FontStyles.Bold);
            HudHelp = NewText(pr, font, 20f, new Vector2(0f, 4f), FontStyles.Normal, lines: 2);
            HudStatus = NewText(pr, font, 22f, new Vector2(0f, -62f), FontStyles.Normal);

            LocalInputSourceConsumers.Register(this);
            LockPlayer();
            FloorPlanCamera.Begin(this);
            Built = true;
            if (Spectating)
            {
                LookUpEditor();
                ModMain.Log($"Floor plan spectator view opened for player {Data.ViewerPlayerID} (watching player {Data.EditorPlayerID}).");
            }
            else
            {
                ModMain.Log($"Floor plan editor opened for player {Data.ViewerPlayerID}.");
            }
        }

        /// <summary>The editor's name and player colour, from the game's player list (on every machine).</summary>
        private void LookUpEditor()
        {
            EditorName = "another player";
            EditorColour = Color.white;
            try
            {
                if (Players.Main != null && Players.Main.Has(Data.EditorPlayerID))
                {
                    PlayerInfo info = Players.Main.Get(Data.EditorPlayerID);
                    string name = Plain(info.PrimaryName, 32);
                    if (name.Length > 0)
                    {
                        EditorName = name;
                    }
                    Color c = info.Profile.Colour;
                    EditorColour = new Color(c.r, c.g, c.b, 1f);
                }
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't look up the editing player: {e.Message}");
            }
        }

        /// <summary>
        /// A second floor-aligned canvas just above the walls, so wall symbols aren't hidden under
        /// the wall tops. Its height comes from the built restaurant geometry (the LayoutView).
        /// </summary>
        private void BuildHoloLayer()
        {
            float top = 0f;
            try
            {
                LayoutView layout = FindObjectOfType<LayoutView>();
                if (layout != null)
                {
                    foreach (Renderer r in layout.GetComponentsInChildren<Renderer>())
                    {
                        top = Mathf.Max(top, r.bounds.max.y);
                    }
                }
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't measure the wall height: {e.Message}");
            }
            HoloHeight = top > 0.2f ? Mathf.Clamp(top + 0.05f, 0.5f, 3f) : 1.3f;

            HoloRoot = new GameObject("EasyFloorPlanner Holograms");
            HoloRoot.transform.position = new Vector3(0f, HoloHeight, 0f);
            HoloRoot.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Canvas canvas = HoloRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 60;
            HoloGroup = HoloRoot.AddComponent<CanvasGroup>();
            HoloGroup.interactable = false;
            HoloGroup.blocksRaycasts = false;
            HoloRect = HoloRoot.GetComponent<RectTransform>();
            HoloRect.sizeDelta = new Vector2(200f, 200f);
            HoloRect.localScale = Vector3.one;
            ModMain.Log($"Hologram layer at height {HoloHeight:0.00} (walls reach {top:0.00}).");
        }

        private static Image TakeFrom(List<Image> pool, ref int used, RectTransform parent, Color color, Vector2 centre, Vector2 size)
        {
            Image img;
            if (used < pool.Count)
            {
                img = pool[used];
            }
            else
            {
                img = NewImage(parent, color);
                pool.Add(img);
            }
            used++;
            img.gameObject.SetActive(true);
            img.color = color;
            img.rectTransform.anchoredPosition = centre;
            img.rectTransform.sizeDelta = size;
            return img;
        }

        private Image TakeHolo(Color c, Vector2 centre, Vector2 size) => TakeFrom(HoloPool, ref HoloUsed, HoloRect, c, centre, size);

        private Image TakeGhost(Color c, Vector2 centre, Vector2 size) => TakeFrom(GhostPool, ref GhostUsed, HoloRect, c, centre, size);

        private static readonly Color HoloWall = new Color(0.92f, 0.94f, 1f);
        private static readonly Color HoloHatch = new Color(1f, 0.62f, 0.12f);
        private static readonly Color HoloDoor = new Color(0.25f, 0.95f, 0.4f);

        /// <summary>
        /// A floor-plan symbol for the wall between <paramref name="a"/> and <paramref name="b"/>,
        /// readable from above: a solid bar for a wall; a window (two parallel lines with end caps)
        /// for a hatch; an opening with posts and a door leaf swung toward <paramref name="a"/> for a door.
        /// </summary>
        private static void DrawEdgeSymbol(Func<Color, Vector2, Vector2, Image> take, LayoutPosition a, LayoutPosition b, FeatureType? type, float alpha)
        {
            bool alongY = a.x != b.x; // the wall line runs along the canvas y axis
            var mid = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            Vector2 along = alongY ? Vector2.up : Vector2.right;
            Vector2 across = alongY ? Vector2.right : Vector2.up;
            Vector2 Size(float l, float w) => alongY ? new Vector2(w, l) : new Vector2(l, w);
            Color With(Color c, float a2) => new Color(c.r, c.g, c.b, a2);

            if (type == null)
            {
                take(With(HoloWall, alpha * 0.85f), mid, Size(1f, 0.26f));
            }
            else if (type == FeatureType.Hatch)
            {
                take(With(HoloHatch, alpha * 0.3f), mid, Size(0.96f, 0.26f));
                take(With(HoloHatch, alpha), mid + across * 0.1f, Size(0.96f, 0.05f));
                take(With(HoloHatch, alpha), mid - across * 0.1f, Size(0.96f, 0.05f));
                take(With(HoloHatch, alpha), mid + along * 0.47f, Size(0.06f, 0.26f));
                take(With(HoloHatch, alpha), mid - along * 0.47f, Size(0.06f, 0.26f));
            }
            else
            {
                take(With(HoloDoor, alpha * 0.25f), mid, Size(0.76f, 0.22f));
                take(With(HoloDoor, alpha), mid + along * 0.44f, Size(0.12f, 0.3f));
                take(With(HoloDoor, alpha), mid - along * 0.44f, Size(0.12f, 0.3f));
                float side = alongY ? Mathf.Sign(a.x - b.x) : Mathf.Sign(a.y - b.y);
                take(With(HoloDoor, alpha), mid - along * 0.44f + across * (side * 0.4f), Size(0.05f, 0.8f));
            }
        }

        private static TextMeshProUGUI NewText(RectTransform parent, TMP_FontAsset font, float size, Vector2 pos, FontStyles style, int lines = 1)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                t.font = font;
            }
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = TextAlignmentOptions.Center;
            t.lineSpacing = -20f; // the font's own line height is generous
            t.enableWordWrapping = lines > 1;
            // Overflow, not Ellipsis: the game's text font has a tall line height, and with
            // Ellipsis TMP renders nothing at all when one line doesn't fit the box vertically
            // (seen in the self-test: chars=0, overflow=True).
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            var r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 0.5f);
            r.anchorMax = new Vector2(1f, 0.5f);
            r.sizeDelta = new Vector2(-40f, size * 2f * lines);
            r.anchoredPosition = pos;
            return t;
        }

        private static Image NewImage(RectTransform parent, Color color)
        {
            var go = new GameObject("Mark", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private Image Take(Color color, Vector2 centre, Vector2 size)
        {
            Image img;
            if (PoolUsed < Pool.Count)
            {
                img = Pool[PoolUsed];
            }
            else
            {
                img = NewImage(OverlayRect, color);
                Pool.Add(img);
                // Keep the cursor and flash drawn on top of the pooled marks.
                foreach (Image c in CursorBars)
                {
                    c.transform.SetAsLastSibling();
                }
                FlashImage.transform.SetAsLastSibling();
                BoxImage.transform.SetAsLastSibling();
            }
            PoolUsed++;
            img.gameObject.SetActive(true);
            img.color = color;
            var r = img.rectTransform;
            r.anchoredPosition = centre;
            r.sizeDelta = size;
            return img;
        }

        private void RebuildOverlay()
        {
            if (OverlayRect == null || Draft == null)
            {
                return;
            }
            PoolUsed = 0;

            // Tiles: room tint; stronger where the draft differs; red where floor is removed;
            // orange where it can't be reached from the front door.
            foreach (KeyValuePair<LayoutPosition, Room> t in Draft.Tiles)
            {
                if (!LayoutHelpers.IsInside(t.Value))
                {
                    continue;
                }
                bool changed = !Committed.Tiles.TryGetValue(t.Key, out Room was) || !LayoutHelpers.IsInside(was) || was.ID != t.Value.ID;
                Color c = Unreachable.Contains(t.Key) ? new Color(1f, 0.55f, 0.05f, 0.55f) : RoomColour(t.Value, changed ? 0.5f : 0.16f);
                Take(c, new Vector2(t.Key.x, t.Key.y), new Vector2(0.94f, 0.94f));
            }
            foreach (KeyValuePair<LayoutPosition, Room> t in Committed.Tiles)
            {
                if (LayoutHelpers.IsInside(t.Value) && !(Draft.Tiles.TryGetValue(t.Key, out Room now) && LayoutHelpers.IsInside(now)))
                {
                    Take(new Color(0.9f, 0.1f, 0.1f, 0.45f), new Vector2(t.Key.x, t.Key.y), new Vector2(0.94f, 0.94f));
                }
            }

            // Plain hatches and doors in the draft; brighter where they differ from the committed layout.
            foreach (Feature f in Draft.Features.Where(f => f.Type == FeatureType.Hatch || f.Type == FeatureType.Door))
            {
                bool isNew = !Committed.Features.Any(c => c.Type == f.Type && SameEdge(c, f.Tile1, f.Tile2));
                Color c = f.Type == FeatureType.Door ? new Color(0.2f, 0.85f, 0.3f, isNew ? 0.95f : 0.45f) : new Color(1f, 0.6f, 0.1f, isNew ? 0.95f : 0.45f);
                EdgeBar(f.Tile1, f.Tile2, c, 0.16f);
            }
            // Walls that the draft removed a hatch or door from (back to a plain wall).
            foreach (Feature f in Committed.Features.Where(f => f.Type == FeatureType.Hatch || f.Type == FeatureType.Door))
            {
                if (!Draft.Features.Any(d => SameEdge(d, f.Tile1, f.Tile2)) && IsWall(Draft, f.Tile1, f.Tile2))
                {
                    EdgeBar(f.Tile1, f.Tile2, new Color(0.85f, 0.85f, 0.85f, 0.95f), 0.16f);
                }
            }

            DrawCameraArea();

            for (int i = PoolUsed; i < Pool.Count; i++)
            {
                Pool[i].gameObject.SetActive(false);
            }
            RebuildHolograms();
        }

        /// <summary>A hologram on every wall whose draft state differs from the committed layout.</summary>
        private void RebuildHolograms()
        {
            if (HoloRect == null)
            {
                return;
            }
            HoloUsed = 0;
            foreach (Feature f in Draft.Features.Where(f => f.Type == FeatureType.Hatch || f.Type == FeatureType.Door))
            {
                if (!Committed.Features.Any(c => c.Type == f.Type && SameEdge(c, f.Tile1, f.Tile2)))
                {
                    DrawEdgeSymbol(TakeHolo, f.Tile1, f.Tile2, f.Type, 0.95f);
                }
            }
            foreach (Feature f in Committed.Features.Where(f => f.Type == FeatureType.Hatch || f.Type == FeatureType.Door))
            {
                if (!Draft.Features.Any(d => SameEdge(d, f.Tile1, f.Tile2)) && IsWall(Draft, f.Tile1, f.Tile2))
                {
                    DrawEdgeSymbol(TakeHolo, f.Tile1, f.Tile2, null, 0.95f);
                }
            }
            for (int i = HoloUsed; i < HoloPool.Count; i++)
            {
                HoloPool[i].gameObject.SetActive(false);
            }
        }

        /// <summary>In wall mode: a ghost of what clicking the wall under the cursor would make it.</summary>
        private void UpdateGhost()
        {
            if (HoloRect == null)
            {
                return;
            }
            GhostUsed = 0;
            if (CurrentMode == Mode.Walls && CursorPlaced && Draft != null)
            {
                int brush = Spectating ? Array.FindIndex(EdgeBrushes, b => b.Label == (Data.Cursor.BrushLabel ?? "")) : EdgeBrushIndex;
                LayoutPosition other = CursorTile + CursorFacing;
                if (brush >= 0 && LayoutEdits.ReadEdge(Draft, CursorTile, other, out _) == null)
                {
                    DrawEdgeSymbol(TakeGhost, CursorTile, other, EdgeBrushes[brush].Type, 0.75f);
                }
            }
            for (int i = GhostUsed; i < GhostPool.Count; i++)
            {
                GhostPool[i].gameObject.SetActive(false);
            }
            if (HoloGroup != null)
            {
                HoloGroup.alpha = 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 4f); // a gentle holographic pulse
            }
        }

        /// <summary>
        /// The drafted camera area's outline: bright in camera mode, faint otherwise. In camera
        /// mode without a custom area, the whole restaurant is outlined instead.
        /// </summary>
        private void DrawCameraArea()
        {
            bool cameraMode = CurrentMode == Mode.Camera;
            FloorPlanCameraArea area = DraftArea;
            if (!area.Custom)
            {
                if (!cameraMode || !TryIndoorRect(Draft, out area))
                {
                    return;
                }
            }
            Color c = CameraColour;
            c.a = cameraMode ? (DraftArea.Custom ? 0.95f : 0.5f) : 0.4f;
            float x0 = area.MinX - 0.5f, x1 = area.MaxX + 0.5f, y0 = area.MinY - 0.5f, y1 = area.MaxY + 0.5f;
            const float t = 0.14f;
            Take(c, new Vector2((x0 + x1) / 2f, y1), new Vector2(x1 - x0 + t, t));
            Take(c, new Vector2((x0 + x1) / 2f, y0), new Vector2(x1 - x0 + t, t));
            Take(c, new Vector2(x0, (y0 + y1) / 2f), new Vector2(t, y1 - y0 + t));
            Take(c, new Vector2(x1, (y0 + y1) / 2f), new Vector2(t, y1 - y0 + t));
        }

        /// <summary>The rectangle around the layout's indoor tiles.</summary>
        private static bool TryIndoorRect(LayoutBlueprint bp, out FloorPlanCameraArea rect)
        {
            rect = default;
            bool any = false;
            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
            {
                if (!LayoutHelpers.IsInside(t.Value))
                {
                    continue;
                }
                rect = any
                    ? FloorPlanCameraArea.Box(Math.Min(rect.MinX, t.Key.x), Math.Min(rect.MinY, t.Key.y), Math.Max(rect.MaxX, t.Key.x), Math.Max(rect.MaxY, t.Key.y))
                    : FloorPlanCameraArea.Box(t.Key.x, t.Key.y, t.Key.x, t.Key.y);
                any = true;
            }
            return any;
        }

        private void EdgeBar(LayoutPosition a, LayoutPosition b, Color c, float thickness)
        {
            Vector2 mid = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            Vector2 size = a.x != b.x ? new Vector2(thickness, 0.9f) : new Vector2(0.9f, thickness);
            Take(c, mid, size);
        }

        private void UpdateCursorVisual()
        {
            if (CursorBars.Count < 4)
            {
                return;
            }
            UpdateGhost();
            bool boxing = BoxStart.HasValue && !Spectating && BoxImage != null;
            if (BoxImage != null)
            {
                BoxImage.gameObject.SetActive(boxing);
            }
            if (boxing)
            {
                LayoutPosition a = BoxStart.Value;
                float x0 = Mathf.Min(a.x, BoxEnd.x), x1 = Mathf.Max(a.x, BoxEnd.x), y0 = Mathf.Min(a.y, BoxEnd.y), y1 = Mathf.Max(a.y, BoxEnd.y);
                Color boxColour = CurrentMode == Mode.Camera ? CameraColour : BrushColour(Brushes[BrushIndex]);
                boxColour.a = CurrentMode == Mode.Camera ? 0.25f : 0.35f;
                BoxImage.color = boxColour;
                BoxImage.rectTransform.anchoredPosition = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
                BoxImage.rectTransform.sizeDelta = new Vector2(x1 - x0 + 1f, y1 - y0 + 1f);
            }
            if (Spectating && !CursorPlaced)
            {
                foreach (Image bar in CursorBars)
                {
                    bar.gameObject.SetActive(false);
                }
                FlashImage.gameObject.SetActive(false);
                return;
            }
            float pulse = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6f);
            bool blocked = Blocked.Contains(CursorTile);
            // A spectator sees the editor's cursor in the editor's player colour.
            // In wall mode the editor's cursor bar shows the wall brush's colour (wall, hatch, door).
            Color edgeColour = EdgeBrushes[EdgeBrushIndex].Colour;
            Color c = Spectating ? new Color(EditorColour.r, EditorColour.g, EditorColour.b, pulse)
                : CurrentMode == Mode.Walls ? new Color(edgeColour.r, edgeColour.g, edgeColour.b, pulse)
                : CurrentMode == Mode.Camera ? new Color(CameraColour.r, CameraColour.g, CameraColour.b, pulse)
                : blocked ? new Color(1f, 0.25f, 0.2f, pulse) : new Color(1f, 1f, 1f, pulse);
            var p = new Vector2(CursorTile.x, CursorTile.y);
            if (CurrentMode != Mode.Walls)
            {
                SetBar(0, p + new Vector2(0f, 0.47f), new Vector2(1f, 0.07f), c);
                SetBar(1, p + new Vector2(0f, -0.47f), new Vector2(1f, 0.07f), c);
                SetBar(2, p + new Vector2(0.47f, 0f), new Vector2(0.07f, 1f), c);
                SetBar(3, p + new Vector2(-0.47f, 0f), new Vector2(0.07f, 1f), c);
            }
            else
            {
                Vector2 mid = p + new Vector2(CursorFacing.x * 0.5f, CursorFacing.y * 0.5f);
                Vector2 size = CursorFacing.x != 0 ? new Vector2(0.24f, 1.05f) : new Vector2(1.05f, 0.24f);
                SetBar(0, mid, size, c);
                for (int i = 1; i < 4; i++)
                {
                    CursorBars[i].gameObject.SetActive(false);
                }
            }

            bool flashing = FlashTile.HasValue && Time.unscaledTime < FlashUntil;
            FlashImage.gameObject.SetActive(flashing);
            if (flashing)
            {
                FlashImage.rectTransform.anchoredPosition = new Vector2(FlashTile.Value.x, FlashTile.Value.y);
                FlashImage.rectTransform.sizeDelta = new Vector2(0.98f, 0.98f);
            }
        }

        private void SetBar(int i, Vector2 centre, Vector2 size, Color c)
        {
            Image img = CursorBars[i];
            img.gameObject.SetActive(true);
            img.color = c;
            img.rectTransform.anchoredPosition = centre;
            img.rectTransform.sizeDelta = size;
        }

        private void UpdateHud()
        {
            if (HudTitle == null)
            {
                return;
            }
            Color brushColour = CurrentMode == Mode.Walls ? EdgeBrushes[EdgeBrushIndex].Colour
                : CurrentMode == Mode.Camera ? CameraColour
                : BrushColour(Brushes[BrushIndex]);
            string colour = ColorUtility.ToHtmlStringRGB(brushColour);
            string mode = ModeName(CurrentMode);
            string watchers = Data.SpectatorCount > 0 ? $"   -   {Data.SpectatorCount} watching" : "";
            // Plain dashes: the game's font has no "|" or bullet glyphs (they render as "Y").
            string label = CurrentMode == Mode.Camera ? "" : "Brush: ";
            HudTitle.text = $"FLOOR PLAN   -   {mode}   -   {label}<color=#{colour}>{CurrentBrushLabel}</color>{watchers}";
            HudHelp.text = UsesMouse
                ? (CurrentMode == Mode.Tiles
                    ? "Left click/drag: paint   Shift+drag: box   Right click: pick room   Q/E or wheel: change brush   Tab: walls\nWASD: move view   Ctrl+wheel: zoom   Ctrl+Z: undo   Enter: apply   Esc: cancel"
                    : CurrentMode == Mode.Walls
                    ? "Left click/drag walls: set to brush   Right click: pick from a wall   Q/E or wheel: wall / hatch / door   Tab: camera area\nWASD: move view   Ctrl+wheel: zoom   Ctrl+Z: undo   Enter: apply   Esc: cancel"
                    : "Left drag: draw the area the day camera frames   Right click: whole restaurant   Tab: floor\nWASD: move view   Ctrl+wheel: zoom   Ctrl+Z: undo   Enter: apply   Esc: cancel")
                : (CurrentMode == Mode.Tiles
                    ? "Interact (hold): paint   Grab: pick room   Secondary 1: change brush   Secondary 2: walls\nStick: move view (cursor at centre)   RB + right stick: zoom   Stop: undo   Select: apply   Back: cancel"
                    : CurrentMode == Mode.Walls
                    ? "Interact (hold): set the wall nearest the centre   Grab: pick from a wall   Secondary 1: wall / hatch / door   Secondary 2: camera area\nStick: move view   RB + right stick: zoom   Stop: undo   Select: apply   Back: cancel"
                    : "Hold Interact and move the view: draw the area the day camera frames   Grab: whole restaurant   Secondary 2: floor\nStick: move view   RB + right stick: zoom   Stop: undo   Select: apply   Back: cancel");

            string status;
            if (Time.unscaledTime < MessageUntil)
            {
                status = Message;
            }
            else if (Unreachable.Count > 0)
            {
                status = "<color=#FFA040>Orange floor can't be reached from the front door. Add a door before applying.</color>";
            }
            else
            {
                string summary = DraftSummary();
                status = summary == null ? "No changes yet." : summary + " Enter/Select to apply.";
            }
            HudStatus.text = status;
        }

        private static string ModeName(Mode m) => m == Mode.Tiles ? "Paint floor" : m == Mode.Walls ? "Walls, hatches & doors" : "Camera area";

        /// <summary>"Draft: +a floor, ..." or null when the draft equals the committed layout.</summary>
        private string DraftSummary()
        {
            int added = Draft.Tiles.Count(t => LayoutHelpers.IsInside(t.Value) && !(Committed.Tiles.TryGetValue(t.Key, out Room r) && LayoutHelpers.IsInside(r)));
            int removed = Committed.Tiles.Count(t => LayoutHelpers.IsInside(t.Value) && !(Draft.Tiles.TryGetValue(t.Key, out Room r) && LayoutHelpers.IsInside(r)));
            int moved = Draft.Tiles.Count(t => LayoutHelpers.IsInside(t.Value) && Committed.Tiles.TryGetValue(t.Key, out Room r) && LayoutHelpers.IsInside(r) && r.ID != t.Value.ID);
            int edges = Draft.Features.Count(f => !Committed.Features.Any(c => c.Type == f.Type && SameEdge(c, f.Tile1, f.Tile2)))
                        + Committed.Features.Count(f => !Draft.Features.Any(d => d.Type == f.Type && SameEdge(d, f.Tile1, f.Tile2)));
            bool area = !DraftArea.SameAs(CommittedArea);
            if (added + removed + moved + edges == 0)
            {
                return area ? $"Draft: camera area changed ({AreaLabel(DraftArea).Substring("Day view: ".Length)})." : null;
            }
            string camera = area ? ", camera area changed" : "";
            return $"Draft: +{added} floor, -{removed} floor, {moved} moved between rooms, {edges} wall change(s){camera}.";
        }

        private void UpdateSpectatorHud()
        {
            if (HudTitle == null)
            {
                return;
            }
            string who = $"<color=#{ColorUtility.ToHtmlStringRGB(EditorColour)}>{EditorName}</color>";
            string detail = "";
            if (CursorPlaced)
            {
                string mode = CurrentMode == Mode.Tiles ? "Painting floor" : ModeName(CurrentMode);
                string brush = Plain(Data.Cursor.BrushLabel, 40);
                detail = brush.Length > 0 ? $"   -   {mode}   -   Brush: {brush}" : $"   -   {mode}";
            }
            HudTitle.text = $"FLOOR PLAN   -   Watching {who}{detail}";
            HudHelp.text = (UsesMouse ? "WASD: move view   Esc: stop watching" : "Stick: move view   Back: stop watching")
                           + $"\nOnly {who} can change the layout. Press the terminal again when they finish to edit it yourself.";
            string summary = DraftSummary();
            HudStatus.text = Unreachable.Count > 0
                ? "<color=#FFA040>Orange floor can't be reached from the front door yet.</color>"
                : summary ?? "No changes yet.";
        }

#if DEBUG
        // ================================================================= self-test hooks (Debug builds only)

        /// <summary>The local editor's view.</summary>
        internal static FloorPlanView TestInstance { get; private set; }

        /// <summary>The local spectator's view.</summary>
        internal static FloorPlanView TestSpectatorInstance { get; private set; }

        internal bool TestReady => Built && IsLocalViewer && Draft != null && !ClosedLocally;
        internal int TestSpectatorCount => Data.SpectatorCount;
        internal bool TestHasPreview => Data.Preview != null;
        internal bool TestCursorShown => CursorPlaced && CursorBars.Count > 0 && CursorBars[0].gameObject.activeSelf;
        internal FloorPlanCursor TestCursor => new FloorPlanCursor
        {
            Tile = CursorTile,
            Facing = CursorFacing,
            Mode = (byte)CurrentMode,
            BrushLabel = Spectating ? Data.Cursor.BrushLabel : CurrentBrushLabel
        };

        internal void TestStopWatching() => RequestClose();

        internal void TestUndo()
        {
            if (Undo.Count > 0)
            {
                (Draft, DraftArea) = Undo.Pop();
                AfterDraftChanged();
                RebuildOverlay();
            }
        }

        /// <summary>Hides this view's overlay and HUD (the self-test's editor and spectator share one screen).</summary>
        internal void TestSetVisible(bool visible)
        {
            OverlayRoot?.SetActive(visible);
            HoloRoot?.SetActive(visible);
            HudRoot?.SetActive(visible);
        }

        /// <summary>Sends an action straight from this view (e.g. Apply from a spectator), bypassing its UI.</summary>
        internal void TestSendRaw(FloorPlanAction action, LayoutBlueprint draft) => Enqueue(action, draft);
        internal LayoutBlueprint TestDraft => Draft;
        internal int TestUnreachableCount => Unreachable.Count;
        internal string TestMessage => Message;
        internal int TestPooledMarks => PoolUsed;
        internal int TestHologramMarks => HoloUsed;
        internal int TestGhostMarks => GhostUsed;
        internal float TestHoloHeight => HoloHeight;
        internal bool TestAwaiting => AwaitingSequence != 0;

        internal bool TestSelectBrush(string label) => TestSelect(b => b.Label == label);

        internal bool TestSelectRoom(int roomId) => TestSelect(b => !b.IsNew && b.Room.ID == roomId);

        private bool TestSelect(Func<Brush, bool> match)
        {
            int i = Brushes.FindIndex(b => match(b));
            if (i >= 0)
            {
                BrushIndex = i;
            }
            return i >= 0;
        }

        internal void TestPaint(LayoutPosition tile)
        {
            CurrentMode = Mode.Tiles;
            CursorTile = tile;
            Message = "";
            PaintAtCursor(true);
            RebuildOverlay();
        }

        internal void TestPaintBox(LayoutPosition a, LayoutPosition b)
        {
            CurrentMode = Mode.Tiles;
            Message = "";
            PaintBox(a, b);
            RebuildOverlay();
        }

        /// <summary>Sets one wall with the wall brush for <paramref name="type"/> (null = plain wall), as a click would.</summary>
        internal void TestSetEdge(LayoutPosition tile, LayoutPosition facing, FeatureType? type)
        {
            CurrentMode = Mode.Walls;
            CursorTile = tile;
            CursorFacing = facing;
            EdgeBrushIndex = Array.FindIndex(EdgeBrushes, b => b.Type == type);
            Message = "";
            SetEdgeAtCursor(true);
            RebuildOverlay();
        }

        /// <summary>Samples a wall (right click in wall mode); returns the wall brush's label afterwards.</summary>
        internal string TestPickEdge(LayoutPosition tile, LayoutPosition facing)
        {
            CurrentMode = Mode.Walls;
            CursorTile = tile;
            CursorFacing = facing;
            EdgeBrushIndex = 0;
            PickEdgeUnderCursor();
            return EdgeBrushes[EdgeBrushIndex].Label;
        }

        /// <summary>Characters TMP actually laid out on each HUD line (0 = the line is invisible).</summary>
        internal (int title, int help, int status) TestHudCharacters()
        {
            int Count(TextMeshProUGUI t)
            {
                if (t == null)
                {
                    return 0;
                }
                t.ForceMeshUpdate();
                return t.textInfo.characterCount;
            }
            return (Count(HudTitle), Count(HudHelp), Count(HudStatus));
        }

        internal string TestHudReport()
        {
            string One(TextMeshProUGUI t)
            {
                if (t == null)
                {
                    return "null";
                }
                t.ForceMeshUpdate();
                var r = t.rectTransform.rect;
                string text = t.text.Replace("\n", "\\n");
                return $"'{text}' font={t.font?.name} chars={t.textInfo.characterCount} visible={t.textInfo.characterCount - t.textInfo.spaceCount} lines={t.textInfo.lineCount} rect={r.width:0}x{r.height:0} overflow={t.isTextOverflowing} active={t.gameObject.activeInHierarchy} alpha={t.color.a:0.00}";
            }
            return $"title: {One(HudTitle)} | help: {One(HudHelp)} | status: {One(HudStatus)}";
        }

        internal FloorPlanCameraArea TestDraftArea => DraftArea;
        internal FloorPlanCameraArea TestCommittedArea => CommittedArea;

        /// <summary>Draws a camera area as a drag in camera mode would (null = right click: whole restaurant).</summary>
        internal void TestSetCameraArea(LayoutPosition? a, LayoutPosition b)
        {
            CurrentMode = Mode.Camera;
            Message = "";
            SetCameraArea(a.HasValue ? FloorPlanCameraArea.Box(a.Value.x, a.Value.y, b.x, b.y) : FloorPlanCameraArea.Whole);
            RebuildOverlay();
        }

        internal void TestApply()
        {
            Message = "";
            TryApply();
        }

        /// <summary>Sends an arbitrary draft, bypassing the client's checks, to exercise the host's validation.</summary>
        internal void TestSendRawDraft(LayoutBlueprint draft)
        {
            Enqueue(FloorPlanAction.Apply, draft);
            AwaitingSequence = Sequence;
        }

        private void TestTrackInstances()
        {
            bool live = Built && IsLocalViewer;
            if (live && !Spectating)
            {
                TestInstance = this;
            }
            else if (TestInstance == this)
            {
                TestInstance = null;
            }
            if (live && Spectating && !ClosedLocally)
            {
                TestSpectatorInstance = this;
            }
            else if (TestSpectatorInstance == this)
            {
                TestSpectatorInstance = null;
            }
        }
#endif

#if DEBUG
        private void LateUpdate()
        {
            TestTrackInstances();
        }
#endif

        private static readonly Color KitchenColour = new Color(0.25f, 0.6f, 1f);
        private static readonly Color DiningColour = new Color(1f, 0.75f, 0.25f);

        private static Color RoomColour(Room r, float alpha)
        {
            Color baseColour = r.Type == RoomType.Kitchen ? KitchenColour : r.Type == RoomType.Dining ? DiningColour : new Color(0.75f, 0.5f, 1f);
            // Vary the shade per room so neighbouring rooms of the same type are distinguishable.
            float shade = 0.75f + 0.25f * (((r.ID % 7) + 7) % 7) / 6f;
            Color c = baseColour * shade;
            c.a = alpha;
            return c;
        }

        private static bool SameEdge(Feature f, LayoutPosition a, LayoutPosition b) =>
            (f.Tile1.Equals(a) && f.Tile2.Equals(b)) || (f.Tile1.Equals(b) && f.Tile2.Equals(a));

        private static bool IsWall(LayoutBlueprint bp, LayoutPosition a, LayoutPosition b) =>
            bp.Tiles.TryGetValue(a, out Room ra) && bp.Tiles.TryGetValue(b, out Room rb)
            && LayoutHelpers.IsInside(ra) && LayoutHelpers.IsInside(rb) && ra.ID != rb.ID;
    }

    /// <summary>
    /// The game's own TMP text font. Not every game font is a text font: some map letters to
    /// icons, which scrambled Blueprint Shop's text (its v0.1.0), so choose deliberately.
    /// </summary>
    internal static class FloorPlanFont
    {
        private static TMP_FontAsset _font;

        public static TMP_FontAsset Find()
        {
            if (_font != null)
            {
                return _font;
            }
            try
            {
                // The most used text font in the scene (body text, e.g. the dish card). Not the
                // reroll sign's font: that's an all-caps display font, unreadable for help text.
                var counts = new Dictionary<TMP_FontAsset, int>();
                foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                {
                    if (text != null && text.font != null && text.gameObject.scene.IsValid())
                    {
                        counts.TryGetValue(text.font, out int n);
                        counts[text.font] = n + 1;
                    }
                }
                _font = counts.Where(kv => !LooksLikeIconFont(kv.Key.name)).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
                        ?? TMP_Settings.defaultFontAsset;
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't pick a font: {e.Message}");
                _font = TMP_Settings.defaultFontAsset;
            }
            return _font;
        }

        private static bool LooksLikeIconFont(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n.Contains("icon") || n.Contains("symbol") || n.Contains("emoji") || n.Contains("glyph") || n.Contains("dingbat");
        }
    }
}
