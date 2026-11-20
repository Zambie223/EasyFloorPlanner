#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Self-test instrument (Debug builds only): renders the world top-down to measure the night
    /// lighting (_RoomLayout) and the weather. Used by the far-lighting check and the rain
    /// measurement. (It began as the 2026-11-13 investigation into how the shaders read the map.)
    ///
    /// A hidden orthographic camera looks straight down at tiles -Half..Half-1 and renders into a
    /// texture. Just before it renders (and only for it), the night fade is forced to full and
    /// _RoomLayout is swapped for a test pattern; both are restored right after, so the player's
    /// view never sees them. Reading the brightness at each tile's centre then shows which tiles
    /// each map pixel lights.
    /// </summary>
    internal sealed class LightingProbe : IDisposable
    {
        public const int Half = 40;
        public const int PxPerTile = 4;

        private static readonly int RoomLayoutId = Shader.PropertyToID("_RoomLayout");
        private static readonly int NightFadeId = Shader.PropertyToID("_NightFade");

        private Camera Cam;
        private RenderTexture Target;
        private Texture Pattern;
        private Texture SavedLayout;
        private float SavedFade;

        /// <summary>Force full night while this camera renders (off for the weather measurement).</summary>
        public bool ForceNight = true;

        /// <summary>Render only these layers (the weather measurement isolates the rain on a spare layer).</summary>
        public void SetCullingMask(int mask) => Cam.cullingMask = mask;

        public void Setup()
        {
            var go = new GameObject("EasyFloorPlanner Lighting Probe");
            Cam = go.AddComponent<Camera>();
            if (Camera.main != null)
            {
                Cam.CopyFrom(Camera.main);
            }
            Cam.orthographic = true;
            Cam.orthographicSize = Half;
            Cam.aspect = 1f;
            // Tiles -Half..Half-1 span [-Half-0.5, Half-0.5] on each axis.
            go.transform.position = new Vector3(-0.5f, 80f, -0.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // looking down; screen right = +x, screen up = +z
            Cam.nearClipPlane = 1f;
            Cam.farClipPlane = 200f;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Color.black;
            int size = Half * 2 * PxPerTile;
            Target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            Cam.targetTexture = Target;
            Cam.enabled = false;
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }

        /// <summary>Renders with <paramref name="pattern"/> as the map on the next frame.</summary>
        public void Request(Texture pattern)
        {
            Pattern = pattern;
            Cam.enabled = true;
        }

        private void Begin(ScriptableRenderContext ctx, Camera c)
        {
            if (c != Cam)
            {
                return;
            }
            SavedLayout = Shader.GetGlobalTexture(RoomLayoutId);
            SavedFade = Shader.GetGlobalFloat(NightFadeId);
            if (Pattern != null)
            {
                Shader.SetGlobalTexture(RoomLayoutId, Pattern);
            }
            if (ForceNight)
            {
                Shader.SetGlobalFloat(NightFadeId, 1f);
            }
        }

        private void End(ScriptableRenderContext ctx, Camera c)
        {
            if (c != Cam)
            {
                return;
            }
            if (SavedLayout != null)
            {
                Shader.SetGlobalTexture(RoomLayoutId, SavedLayout);
            }
            if (ForceNight)
            {
                Shader.SetGlobalFloat(NightFadeId, SavedFade);
            }
        }

        /// <summary>Brightness at each tile centre, indexed [x + Half, z + Half]. Also saves the image.</summary>
        public float[,] Read(string pngPath)
        {
            Cam.enabled = false;
            int size = Target.width;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            var grid = new float[Half * 2, Half * 2];
            for (int i = 0; i < Half * 2; i++)
            {
                for (int j = 0; j < Half * 2; j++)
                {
                    int px = i * PxPerTile + PxPerTile / 2, py = j * PxPerTile + PxPerTile / 2;
                    float sum = 0f;
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        for (int dy = -1; dy <= 0; dy++)
                        {
                            Color c = tex.GetPixel(px + dx, py + dy);
                            sum += 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                        }
                    }
                    grid[i, j] = sum / 4f;
                }
            }
            if (pngPath != null)
            {
                try
                {
                    File.WriteAllBytes(pngPath, tex.EncodeToPNG());
                }
                catch (Exception e)
                {
                    ModMain.LogWarning($"Couldn't save {pngPath}: {e.Message}");
                }
            }
            UnityEngine.Object.Destroy(tex);
            return grid;
        }

        /// <summary>The brightest pixel in each tile, indexed [x + Half, z + Half] (keeps the camera on).</summary>
        public float[,] ReadMax()
        {
            int size = Target.width;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            Color[] pixels = tex.GetPixels();
            var grid = new float[Half * 2, Half * 2];
            for (int px = 0; px < size; px++)
            {
                for (int py = 0; py < size; py++)
                {
                    Color c = pixels[py * size + px];
                    float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    int i = px / PxPerTile, j = py / PxPerTile;
                    if (l > grid[i, j])
                    {
                        grid[i, j] = l;
                    }
                }
            }
            UnityEngine.Object.Destroy(tex);
            return grid;
        }

        /// <summary>A 32 x 32 map in vanilla's encoding: indoor pixels (G = 0) where <paramref name="indoor"/> says so.</summary>
        public static Texture2D Pattern32(Func<int, int, bool> indoor, TextureWrapMode wrap)
        {
            var t = new Texture2D(32, 32) { filterMode = FilterMode.Point, wrapMode = wrap };
            var pixels = new Color[32 * 32];
            for (int x = 0; x < 32; x++)
            {
                for (int y = 0; y < 32; y++)
                {
                    pixels[y * 32 + x] = indoor(x, y) ? new Color(0.075f, 0f, 0.075f) : new Color(0f, 1f, 0.025f);
                }
            }
            t.SetPixels(pixels);
            t.Apply();
            return t;
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= Begin;
            RenderPipelineManager.endCameraRendering -= End;
            if (Cam != null)
            {
                Cam.targetTexture = null;
                UnityEngine.Object.Destroy(Cam.gameObject);
            }
            if (Target != null)
            {
                Target.Release();
                UnityEngine.Object.Destroy(Target);
            }
        }
    }
}
#endif
