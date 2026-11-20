using System.Collections.Generic;
using Kitchen;
using Kitchen.Layouts;
using KitchenMods;
using Unity.Entities;

namespace EasyFloorPlanner
{
    internal static class FloorPlanIds
    {
        // Arbitrary values that don't collide with vanilla ViewType values, union keys, or Blueprint
        // Shop's ("BPSH"). "EFPL" in ASCII, plus offsets.
        public const int Base = 0x4546504C;

        public static readonly ViewType ViewType = (ViewType)Base;

        public const int ViewDataUnionKey = Base + 1;

        public const int ResponseUnionKey = Base + 2;

        // The terminal reuses the reroll trigger's model. Its view gets one of these "prices" and
        // TerminalLabelPatch shows text instead. Blueprint Shop uses -1 for "Shop".
        public const int LabelFloorPlan = -2;

        /// <summary>Someone is editing; pressing the terminal now watches their draft.</summary>
        public const int LabelWatch = -3;

        /// <summary>
        /// Nobody is editing and the host's "Who can edit" is Host only: the host's machine shows
        /// "Floor Plan", the clients' show "Host only" (the label is how clients learn the setting).
        /// </summary>
        public const int LabelHostOnly = -4;

        public static bool IsTerminalLabel(int price) => price == LabelFloorPlan || price == LabelWatch || price == LabelHostOnly;
    }

    /// <summary>
    /// The night-time "Floor Plan" terminal. The entity also has CDoNotPersist, so it's never
    /// saved; FloorPlanService recreates it each night.
    /// </summary>
    public struct CFloorPlanTerminal : IModComponent
    {
    }

    /// <summary>
    /// One open edit mode, owned by one player (one at a time). The entity has CRequiresView with
    /// our ViewType and CDoNotPersist. It's destroyed on Apply, Cancel, at day start, or when the
    /// player leaves.
    /// </summary>
    public struct CFloorPlanSession : IModComponent
    {
        public Entity Player;

        public int PlayerID;

        public int InputSource;
    }

    /// <summary>
    /// A player watching the current editor's draft (read-only). Its own entity, with its own
    /// CRequiresView, so the host knows a response came from a spectator by the view it arrived
    /// on: a spectator's view can stop watching and nothing else. Destroyed with the session.
    /// </summary>
    public struct CFloorPlanSpectator : IModComponent
    {
        public Entity Session;

        public Entity Player;

        public int PlayerID;

        public int InputSource;
    }

    public enum FloorPlanAction : byte
    {
        Apply = 1,
        Cancel = 2,

        /// <summary>Editor: the current draft (null when unchanged) and cursor, for spectators.</summary>
        Preview = 3,

        /// <summary>Spectator: stop watching.</summary>
        StopWatching = 4,

        /// <summary>Editor: apply, and keep the editor open (the "Apply keeps the editor open" setting).</summary>
        ApplyKeepOpen = 5,
    }

    /// <summary>
    /// The editor's cursor and brush, relayed to spectators. Mode is 0 for floor, 1 for walls, 2 for
    /// the camera area.
    /// </summary>
    public struct FloorPlanCursor
    {
        public LayoutPosition Tile;
        public LayoutPosition Facing;
        public byte Mode;
        public string BrushLabel;

        public bool SameAs(FloorPlanCursor o) =>
            Tile.Equals(o.Tile) && Facing.Equals(o.Facing) && Mode == o.Mode && (BrushLabel ?? "") == (o.BrushLabel ?? "");
    }

    /// <summary>
    /// Host to every machine. Only the viewer's machine shows anything: the editor (Spectating
    /// false) or one spectator (Spectating true).
    /// </summary>
    public struct FloorPlanViewData : IViewData, IViewResponseData, IViewData.ICheckForChanges<FloorPlanViewData>
    {
        /// <summary>The player this view is for: the editor, or the spectator.</summary>
        public int ViewerPlayerID;
        public int ViewerInputSource;

        public bool Spectating;
        public int EditorPlayerID;

        /// <summary>The committed layout. The editor edits a draft copy of it.</summary>
        public LayoutBlueprint Layout;
        public int LayoutHash;

        /// <summary>Tiles with appliances an edit has to respect, and how (editor only; see TileOccupants).</summary>
        public List<OccupiedTile> Occupied;
        public int OccupiedHash;

        /// <summary>Highest Apply/Cancel Sequence the host has processed for this session (editor only).</summary>
        public int AckSequence;
        public bool LastSucceeded;
        public string Status;

        /// <summary>How many players are watching (shown to the editor).</summary>
        public int SpectatorCount;

        /// <summary>Spectators only: the editor's latest draft (null until the first one arrives) and cursor.</summary>
        public LayoutBlueprint Preview;
        public int PreviewHash;
        public FloorPlanCursor Cursor;

        /// <summary>The camera area now in effect (what the day camera frames).</summary>
        public FloorPlanCameraArea CameraArea;

        /// <summary>Spectators only: the editor's drafted camera area.</summary>
        public FloorPlanCameraArea PreviewArea;

        public bool IsChangedFrom(FloorPlanViewData check)
        {
            return ViewerPlayerID != check.ViewerPlayerID
                || ViewerInputSource != check.ViewerInputSource
                || Spectating != check.Spectating
                || EditorPlayerID != check.EditorPlayerID
                || LayoutHash != check.LayoutHash
                || OccupiedHash != check.OccupiedHash
                || AckSequence != check.AckSequence
                || LastSucceeded != check.LastSucceeded
                || Status != check.Status
                || SpectatorCount != check.SpectatorCount
                || PreviewHash != check.PreviewHash
                || (Preview == null) != (check.Preview == null)
                || !Cursor.SameAs(check.Cursor)
                || !CameraArea.SameAs(check.CameraArea)
                || !PreviewArea.SameAs(check.PreviewArea);
        }
    }

    /// <summary>Editor's or spectator's machine to host.</summary>
    public struct FloorPlanResponse : IResponseData, IViewResponseData
    {
        public int Sequence;
        public FloorPlanAction Action;

        /// <summary>
        /// Apply: the whole edited layout, validated by the host before committing.
        /// Preview: the draft for spectators, or null if it hasn't changed since the last preview.
        /// </summary>
        public LayoutBlueprint Draft;

        /// <summary>Preview only.</summary>
        public FloorPlanCursor Cursor;

        /// <summary>Apply and Preview: the drafted camera area (applied along with the layout).</summary>
        public FloorPlanCameraArea CameraArea;
    }
}
