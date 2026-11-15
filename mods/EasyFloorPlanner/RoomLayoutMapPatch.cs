using System;
using HarmonyLib;
using Kitchen;
using Kitchen.Layouts;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Keeps the night lighting lined up with an edited restaurant.
    ///
    /// LayoutView builds a 32 x 32 "room layout" texture (LayoutMapGenerator.GenerateFor) and sets
    /// it as the shader global _RoomLayout: one pixel per tile, marking indoor and outdoor. The
    /// game's shaders (lit materials and the weather particle effects) use it to keep indoors lit
    /// and dry at night. Vanilla centres the texture on (int)bounds.center, but no offset is ever
    /// passed to the shaders (only the texture is a global, and the shaders have no size or offset
    /// property), so they assume the texture covers tiles -16..15 on both axes. That always holds
    /// for vanilla: every layout is re-centred on the origin (RecentreLayout), so (int)center is 0.
    ///
    /// Growing the restaurant to one side moves its centre, the texture moves with it, and the
    /// shaders read it shifted: the lit area slid off the building (user report, 2026-11-13).
    /// This builds the texture exactly as vanilla does, but always centred on the origin, which
    /// is identical for every vanilla layout.
    ///
    /// The shaders only map 32 x 32 tiles; FarLighting handles anything beyond (per-renderer maps).
    /// </summary>
    [HarmonyPatch(typeof(LayoutMapGenerator), nameof(LayoutMapGenerator.GenerateFor))]
    internal static class RoomLayoutMapPatch
    {
        /// <summary>The texture size, and the first tile it covers on each axis (what the shaders assume).</summary>
        public const int Size = 32;
        public const int First = -Size / 2;

        private static bool Prefix(LayoutBlueprint blueprint, bool setShaderGlobal, ref Texture2D __result)
        {
            try
            {
                __result = Generate(blueprint, setShaderGlobal);
                return false;
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't build the night-lighting map; using the game's own: {e.Message}");
                return true;
            }
        }

        /// <summary>Vanilla's GenerateFor, with the texture fixed on tiles First..First+Size-1.</summary>
        internal static Texture2D Generate(LayoutBlueprint blueprint, bool setShaderGlobal)
        {
            var texture = new Texture2D(Size, Size)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Color[] pixels = texture.GetPixels();
            for (int x = First; x < First + Size; x++)
            {
                for (int y = First; y < First + Size; y++)
                {
                    pixels[(y - First) * Size + (x - First)] = Encode(blueprint[new Vector2(x, y)]);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            if (setShaderGlobal)
            {
                Shader.SetGlobalTexture("_RoomLayout", texture);
                FarLighting.SetLayout(blueprint);
            }
            return texture;
        }

        /// <summary>Vanilla's pixel for one tile: indoor rooms have G = 0, outdoors G = 1.</summary>
        internal static Color Encode(Room room) => LayoutHelpers.IsInside(room)
            ? new Color(room.ID * 0.05f + 0.025f, 0f, (float)room.Type * 0.05f + 0.025f)
            : new Color(0f, 1f, (float)room.Type * 0.05f + 0.025f);
    }
}
