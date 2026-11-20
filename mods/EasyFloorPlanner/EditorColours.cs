using Kitchen.Layouts;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The floor plan editor's colours, in two palettes (the "Editor colours" setting, 2026-11-19).
    ///
    /// Standard: the colours the editor always had. Checked 2026-11-19 with Machado et al.'s (2009)
    /// colour-vision-deficiency simulation and CIE76 colour differences, two of its pairs are hard to
    /// tell apart: dining room tint and unreachable floor for deuteranopes (difference 12), doors and
    /// hatches for protanopes (13).
    ///
    /// Colourblind: Okabe and Ito's colour-universal-design colours plus black and white, assigned by
    /// a search that maximised the smallest difference, under protanopia, deuteranopia and
    /// tritanopia, between the colours that must be told apart: at least 50 for the floor colours
    /// (kitchen, dining, unreachable, removed, binned) and 48 for the walls (door, front door,
    /// hatch, wall). Hatches are black: they only sit between two indoor rooms, so always on floor.
    /// Text that names a colour uses <see cref="UnreachableName"/> / <see cref="BinnedName"/>.
    /// </summary>
    internal static class EditorColours
    {
        private static bool Cb => Settings.EditorPalette == Settings.Palette.Colourblind;

        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        private static readonly Color CbBlue = Hex(0x0072B2), CbOrange = Hex(0xE69F00), CbYellow = Hex(0xF0E442),
            CbVermillion = Hex(0xD55E00), CbPurple = Hex(0xCC79A7), CbBlack = Hex(0x1A1A1A), CbWhite = Color.white;

        public static Color Kitchen => Cb ? CbBlue : new Color(0.25f, 0.6f, 1f);
        public static Color Dining => Cb ? CbOrange : new Color(1f, 0.75f, 0.25f);
        public static Color SideRoom => Cb ? CbPurple : new Color(0.75f, 0.5f, 1f);

        /// <summary>Floor that can't be reached from the front door.</summary>
        public static Color Unreachable => Cb ? CbWhite : new Color(1f, 0.55f, 0.05f);
        public static string UnreachableName => Cb ? "White" : "Orange";
        public static string UnreachableHex => Cb ? "FFFFFF" : "FFA040";

        /// <summary>Floor the draft removes.</summary>
        public static Color Removed => Cb ? CbBlack : new Color(0.9f, 0.1f, 0.1f);

        /// <summary>An appliance on removed floor, which the game will throw away.</summary>
        public static Color Binned => Cb ? CbYellow : new Color(1f, 0.85f, 0.1f);
        public static string BinnedName => "yellow";
        public static string BinnedHex => Cb ? "F0E442" : "FFD820";

        /// <summary>The erase brush, a refused tile's flash and a blocked cursor.</summary>
        public static Color Erase => Cb ? CbVermillion : new Color(1f, 0.35f, 0.3f);
        public static Color Blocked => Cb ? CbVermillion : new Color(1f, 0.25f, 0.2f);
        public static Color Flash => Cb ? new Color(CbVermillion.r, CbVermillion.g, CbVermillion.b, 0.6f) : new Color(1f, 0.15f, 0.1f, 0.6f);

        public static Color Wall => new Color(0.85f, 0.85f, 0.85f);
        public static Color Hatch => Cb ? CbBlack : new Color(1f, 0.6f, 0.1f);
        public static Color Door => Cb ? CbOrange : new Color(0.2f, 0.85f, 0.3f);
        public static Color FrontDoor => Cb ? CbBlue : new Color(0.3f, 0.6f, 1f);

        // The hologram's wall symbols (slightly brighter than the overlay's, as before).
        public static Color HoloWall => new Color(0.92f, 0.94f, 1f);
        public static Color HoloHatch => Cb ? CbBlack : new Color(1f, 0.62f, 0.12f);
        public static Color HoloDoor => Cb ? CbOrange : new Color(0.25f, 0.95f, 0.4f);

        /// <summary>The camera-area mode (the same in both palettes: it's the only thing drawn in that mode).</summary>
        public static Color Camera => new Color(0.25f, 0.85f, 1f);

        /// <summary>A wall-mode brush or edge: null is a plain wall.</summary>
        public static Color Edge(FeatureType? type) =>
            type == FeatureType.Hatch ? Hatch : type == FeatureType.Door ? Door : type == FeatureType.FrontDoor ? FrontDoor : Wall;
    }
}
