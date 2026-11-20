using System;
using System.Collections.Generic;
using HarmonyLib;
using Kitchen;
using Kitchen.Formatters.Kitchen;
using Kitchen.Layouts;
using Kitchen.Layouts.Features;
using MessagePack;
using Platforms;
using TMPro;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Hand-written MessagePack encoding for the edit mode's two message types. The game's union
    /// formatters are closed generated code; the patches below route our union keys here and leave
    /// every other value (vanilla, Blueprint Shop) to the original code. Lists are length-capped on
    /// read so a malformed packet can't allocate unbounded memory. FormatVersion changes whenever
    /// the layout does, so players on mismatched versions fail loudly instead of desyncing.
    /// </summary>
    internal static class FloorPlanSerializer
    {
        private const int FormatVersion = 5; // 5 (2026-11-19): outside doors and a movable front door
        private const int ViewFields = 18;
        private const int ResponseFields = 6;
        /// <summary>Every tile the editor can address (MaxExtent + 2 each way), so a full-size layout always fits.</summary>
        private const int MaxTiles = (2 * (LayoutEdits.MaxExtent + 2) + 1) * (2 * (LayoutEdits.MaxExtent + 2) + 1);
        private const int MaxFeatures = 4096;
        private const int MaxStatusLength = 512;
        private const int MaxLabelLength = 64;

        public static void Write(ref MessagePackWriter w, FloorPlanViewData d)
        {
            w.WriteArrayHeader(ViewFields);
            w.Write(FormatVersion);
            w.Write(d.ViewerPlayerID);
            w.Write(d.ViewerInputSource);
            w.Write(d.Spectating);
            w.Write(d.EditorPlayerID);
            WriteLayout(ref w, d.Layout);
            w.Write(d.LayoutHash);
            int occupied = d.Occupied?.Count ?? 0;
            w.WriteArrayHeader(occupied * 3);
            for (int i = 0; i < occupied; i++)
            {
                w.Write(d.Occupied[i].Tile.x);
                w.Write(d.Occupied[i].Tile.y);
                w.Write((byte)d.Occupied[i].Flags);
            }
            w.Write(d.OccupiedHash);
            w.Write(d.AckSequence);
            w.Write(d.LastSucceeded);
            w.Write(d.Status ?? "");
            w.Write(d.SpectatorCount);
            WriteLayout(ref w, d.Preview);
            w.Write(d.PreviewHash);
            WriteCursor(ref w, d.Cursor);
            WriteArea(ref w, d.CameraArea);
            WriteArea(ref w, d.PreviewArea);
        }

        public static FloorPlanViewData ReadView(ref MessagePackReader r)
        {
            int fields = r.ReadArrayHeader();
            int version = r.ReadInt32();
            if (version != FormatVersion || fields != ViewFields)
            {
                throw new MessagePackSerializationException(
                    $"{ModMain.Name}: view data format {version}/{fields} doesn't match this version of the mod ({FormatVersion}). All players need the same mod version.");
            }
            FloorPlanViewData d = default;
            d.ViewerPlayerID = r.ReadInt32();
            d.ViewerInputSource = r.ReadInt32();
            d.Spectating = r.ReadBoolean();
            d.EditorPlayerID = r.ReadInt32();
            d.Layout = ReadLayout(ref r);
            d.LayoutHash = r.ReadInt32();
            int occupiedValues = r.ReadArrayHeader();
            if (occupiedValues > MaxTiles * 3 || occupiedValues % 3 != 0)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad occupied-tile list ({occupiedValues})");
            }
            d.Occupied = new List<OccupiedTile>(occupiedValues / 3);
            for (int i = 0; i < occupiedValues / 3; i++)
            {
                d.Occupied.Add(new OccupiedTile
                {
                    Tile = new LayoutPosition(r.ReadInt32(), r.ReadInt32()),
                    Flags = (OccupantFlags)r.ReadByte(),
                });
            }
            d.OccupiedHash = r.ReadInt32();
            d.AckSequence = r.ReadInt32();
            d.LastSucceeded = r.ReadBoolean();
            d.Status = Cap(r.ReadString(), MaxStatusLength);
            d.SpectatorCount = r.ReadInt32();
            d.Preview = ReadLayout(ref r);
            d.PreviewHash = r.ReadInt32();
            d.Cursor = ReadCursor(ref r);
            d.CameraArea = ReadArea(ref r);
            d.PreviewArea = ReadArea(ref r);
            return d;
        }

        public static void Write(ref MessagePackWriter w, FloorPlanResponse d)
        {
            w.WriteArrayHeader(ResponseFields);
            w.Write(FormatVersion);
            w.Write(d.Sequence);
            w.Write((byte)d.Action);
            WriteLayout(ref w, d.Draft);
            WriteCursor(ref w, d.Cursor);
            WriteArea(ref w, d.CameraArea);
        }

        public static FloorPlanResponse ReadResponse(ref MessagePackReader r)
        {
            int fields = r.ReadArrayHeader();
            int version = r.ReadInt32();
            if (version != FormatVersion || fields != ResponseFields)
            {
                throw new MessagePackSerializationException(
                    $"{ModMain.Name}: response format {version}/{fields} doesn't match this version of the mod ({FormatVersion}). All players need the same mod version.");
            }
            FloorPlanResponse d = default;
            d.Sequence = r.ReadInt32();
            d.Action = (FloorPlanAction)r.ReadByte();
            d.Draft = ReadLayout(ref r);
            d.Cursor = ReadCursor(ref r);
            d.CameraArea = ReadArea(ref r);
            return d;
        }

        /// <summary>A camera area as [custom, min x, min y, max x, max y].</summary>
        private static void WriteArea(ref MessagePackWriter w, FloorPlanCameraArea a)
        {
            w.WriteArrayHeader(5);
            w.Write(a.Custom);
            w.Write(a.MinX);
            w.Write(a.MinY);
            w.Write(a.MaxX);
            w.Write(a.MaxY);
        }

        private static FloorPlanCameraArea ReadArea(ref MessagePackReader r)
        {
            if (r.ReadArrayHeader() != 5)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad camera area");
            }
            FloorPlanCameraArea a = default;
            a.Custom = r.ReadBoolean();
            a.MinX = r.ReadInt32();
            a.MinY = r.ReadInt32();
            a.MaxX = r.ReadInt32();
            a.MaxY = r.ReadInt32();
            return a;
        }

        /// <summary>A cursor as [x, y, facing x, facing y, mode, brush label].</summary>
        private static void WriteCursor(ref MessagePackWriter w, FloorPlanCursor c)
        {
            w.WriteArrayHeader(6);
            w.Write(c.Tile.x);
            w.Write(c.Tile.y);
            w.Write(c.Facing.x);
            w.Write(c.Facing.y);
            w.Write(c.Mode);
            w.Write(c.BrushLabel ?? "");
        }

        private static FloorPlanCursor ReadCursor(ref MessagePackReader r)
        {
            if (r.ReadArrayHeader() != 6)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad cursor");
            }
            FloorPlanCursor c = default;
            c.Tile = new LayoutPosition(r.ReadInt32(), r.ReadInt32());
            c.Facing = new LayoutPosition(r.ReadInt32(), r.ReadInt32());
            c.Mode = r.ReadByte();
            c.BrushLabel = Cap(r.ReadString(), MaxLabelLength);
            return c;
        }

        /// <summary>A layout as [tile count*4 ints: x, y, roomID, type][feature count*5 ints: x1, y1, x2, y2, type], or nil.</summary>
        private static void WriteLayout(ref MessagePackWriter w, LayoutBlueprint bp)
        {
            if (bp == null)
            {
                w.WriteNil();
                return;
            }
            w.WriteArrayHeader(2);
            w.WriteArrayHeader(bp.Tiles.Count * 4);
            foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
            {
                w.Write(t.Key.x);
                w.Write(t.Key.y);
                w.Write(t.Value.ID);
                w.Write((int)t.Value.Type);
            }
            w.WriteArrayHeader(bp.Features.Count * 5);
            foreach (Feature f in bp.Features)
            {
                w.Write(f.Tile1.x);
                w.Write(f.Tile1.y);
                w.Write(f.Tile2.x);
                w.Write(f.Tile2.y);
                w.Write((int)f.Type);
            }
        }

        private static LayoutBlueprint ReadLayout(ref MessagePackReader r)
        {
            if (r.TryReadNil())
            {
                return null;
            }
            if (r.ReadArrayHeader() != 2)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad layout");
            }
            var bp = new LayoutBlueprint();
            int tileValues = r.ReadArrayHeader();
            if (tileValues > MaxTiles * 4 || tileValues % 4 != 0)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad tile list ({tileValues})");
            }
            for (int i = 0; i < tileValues / 4; i++)
            {
                var pos = new LayoutPosition(r.ReadInt32(), r.ReadInt32());
                bp.Tiles[pos] = new Room(r.ReadInt32(), (RoomType)r.ReadInt32());
            }
            int featureValues = r.ReadArrayHeader();
            if (featureValues > MaxFeatures * 5 || featureValues % 5 != 0)
            {
                throw new MessagePackSerializationException($"{ModMain.Name}: bad feature list ({featureValues})");
            }
            for (int i = 0; i < featureValues / 5; i++)
            {
                var a = new LayoutPosition(r.ReadInt32(), r.ReadInt32());
                var b = new LayoutPosition(r.ReadInt32(), r.ReadInt32());
                bp.Features.Add(new Feature(a, b, (FeatureType)r.ReadInt32()));
            }
            return bp;
        }

        private static string Cap(string s, int max) => s == null ? "" : s.Length > max ? s.Substring(0, max) : s;

        /// <summary>Order-independent hash of a layout, so the view only resends when it changed.</summary>
        public static int Hash(LayoutBlueprint bp)
        {
            unchecked
            {
                int h = 17;
                foreach (KeyValuePair<LayoutPosition, Room> t in bp.Tiles)
                {
                    h += (t.Key.x * 73856093) ^ (t.Key.y * 19349663) ^ (t.Value.ID * 83492791) ^ ((int)t.Value.Type << 3);
                }
                foreach (Feature f in bp.Features)
                {
                    h += ((f.Tile1.x + f.Tile2.x) * 2654435) ^ ((f.Tile1.y + f.Tile2.y) * 40503) ^ ((int)f.Type * 7919) ^ (f.Tile1.x * f.Tile2.y);
                }
                return h;
            }
        }
    }

    [HarmonyPatch(typeof(IViewDataFormatter), nameof(IViewDataFormatter.Serialize))]
    internal static class FloorPlanViewSerializePatch
    {
        private static bool Prefix(ref MessagePackWriter writer, IViewData value)
        {
            if (!(value is FloorPlanViewData data))
            {
                return true;
            }
            writer.WriteArrayHeader(2);
            writer.Write(FloorPlanIds.ViewDataUnionKey);
            FloorPlanSerializer.Write(ref writer, data);
            return false;
        }
    }

    [HarmonyPatch(typeof(IViewDataFormatter), nameof(IViewDataFormatter.Deserialize))]
    internal static class FloorPlanViewDeserializePatch
    {
        private static bool Prefix(ref MessagePackReader reader, ref IViewData __result)
        {
            if (!UnionPeek.IsKey(ref reader, FloorPlanIds.ViewDataUnionKey))
            {
                return true;
            }
            reader.ReadArrayHeader();
            reader.ReadInt32();
            __result = FloorPlanSerializer.ReadView(ref reader);
            return false;
        }
    }

    [HarmonyPatch(typeof(IResponseDataFormatter), nameof(IResponseDataFormatter.Serialize))]
    internal static class FloorPlanResponseSerializePatch
    {
        private static bool Prefix(ref MessagePackWriter writer, IResponseData value)
        {
            if (!(value is FloorPlanResponse data))
            {
                return true;
            }
            writer.WriteArrayHeader(2);
            writer.Write(FloorPlanIds.ResponseUnionKey);
            FloorPlanSerializer.Write(ref writer, data);
            return false;
        }
    }

    [HarmonyPatch(typeof(IResponseDataFormatter), nameof(IResponseDataFormatter.Deserialize))]
    internal static class FloorPlanResponseDeserializePatch
    {
        private static bool Prefix(ref MessagePackReader reader, ref IResponseData __result)
        {
            if (!UnionPeek.IsKey(ref reader, FloorPlanIds.ResponseUnionKey))
            {
                return true;
            }
            reader.ReadArrayHeader();
            reader.ReadInt32();
            __result = FloorPlanSerializer.ReadResponse(ref reader);
            return false;
        }
    }

    internal static class UnionPeek
    {
        /// <summary>True if the next value is a 2-element union array whose key is <paramref name="key"/>. Doesn't advance the reader.</summary>
        public static bool IsKey(ref MessagePackReader reader, int key)
        {
            try
            {
                MessagePackReader peek = reader.CreatePeekReader();
                if (peek.End || peek.NextMessagePackType != MessagePackType.Array)
                {
                    return false;
                }
                if (peek.ReadArrayHeader() != 2 || peek.NextMessagePackType != MessagePackType.Integer)
                {
                    return false;
                }
                return peek.ReadInt32() == key;
            }
            catch (Exception)
            {
                return false; // not ours; let the vanilla formatter handle (or report) it
            }
        }
    }

    /// <summary>
    /// Supplies the prefab for the edit mode's ViewType. The game looks view prefabs up in
    /// AssetDirectory.ViewPrefabs, which only knows vanilla view types.
    /// </summary>
    [HarmonyPatch(typeof(LocalViewRouter), "GetPrefab")]
    internal static class FloorPlanViewPrefabPatch
    {
        private static GameObject _template;

        private static bool Prefix(ViewType view_type, ref GameObject __result)
        {
            if (view_type != FloorPlanIds.ViewType)
            {
                return true;
            }
            if (_template == null)
            {
                // Inactive so the template never runs; FloorPlanView.Initialise activates each instance.
                _template = new GameObject("EasyFloorPlanner View Template");
                _template.SetActive(false);
                _template.AddComponent<FloorPlanView>();
                UnityEngine.Object.DontDestroyOnLoad(_template);
            }
            __result = _template;
            return false;
        }
    }

    /// <summary>
    /// The terminal reuses the reroll trigger's model. Its view gets a sentinel price (see
    /// FloorPlanTerminalLabelSystem); show "Floor Plan" or "Watch" instead of "Reroll -2".
    /// </summary>
    [HarmonyPatch(typeof(RerollBlueprintView), "UpdateData", typeof(RerollBlueprintView.ViewData))]
    internal static class FloorPlanTerminalLabelPatch
    {
        /// <summary>The terminal's last label on this machine (clients learn "host only" from it).</summary>
        internal static int LastLabel;

        private static void Postfix(RerollBlueprintView __instance, RerollBlueprintView.ViewData data, TextMeshPro ___Title)
        {
            if (FloorPlanIds.IsTerminalLabel(data.Price))
            {
                LastLabel = data.Price;
                FloorPlanHologram.Apply(__instance); // drafting tools instead of the reroll dice
            }
            if (___Title == null)
            {
                return;
            }
            if (data.Price == FloorPlanIds.LabelFloorPlan)
            {
                ___Title.text = "Floor Plan";
            }
            else if (data.Price == FloorPlanIds.LabelWatch)
            {
                ___Title.text = "Watch";
            }
            else if (data.Price == FloorPlanIds.LabelHostOnly)
            {
                ___Title.text = Session.NetworkedPlayState == NetworkedPlayState.Client ? "Host only" : "Floor Plan";
            }
        }
    }
}
