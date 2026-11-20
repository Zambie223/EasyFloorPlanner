#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Kitchen;
using KitchenData;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Self-test instrument (Debug builds only, survey mode): records what each restaurant setting
    /// uses as ground, so the ground can be extended under very large restaurants to match it.
    ///
    /// Logs every setting's decorators and their "Ground" appliances; every big renderer in the
    /// scene (mesh, bounds, materials, shaders, texture tiling); and the camera background, sky and
    /// fog. Renders a top-down orthographic picture of tiles -Half..Half-1 with a magenta background,
    /// so anything not covered by ground shows as magenta, and logs the covered extent along the
    /// axes through the origin.
    /// </summary>
    internal sealed class GroundSurvey : IDisposable
    {
        public const int Half = 100;
        public const int PxPerTile = 6;
        private static readonly Color Void = new Color(1f, 0f, 1f);

        private Camera Cam;
        private RenderTexture Target;

        public static void LogSettings()
        {
            foreach (RestaurantSetting s in GameData.Main.Get<RestaurantSetting>())
            {
                var parts = new List<string>();
                foreach (IDecorationConfiguration d in s.Decorators ?? new List<IDecorationConfiguration>())
                {
                    if (d == null)
                    {
                        continue;
                    }
                    string ground = d.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.FieldType == typeof(Appliance) && f.Name.IndexOf("Ground", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(f => f.GetValue(d) is Appliance a && a != null ? $"{f.Name}={a.Name} (id {a.ID})" : $"{f.Name}=none")
                        .FirstOrDefault();
                    string name = d.GetType().DeclaringType?.Name ?? d.GetType().Name;
                    parts.Add(ground != null ? $"{name} [{ground}]" : name);
                }
                ModMain.Log($"[SELFTEST] SURVEY setting {s.name} ({s.Name}, id {s.ID}), weather {s.WeatherMode}, always light {s.AlwaysLight}: {string.Join("; ", parts)}");
            }
        }

        public static void LogRenderers()
        {
            Camera main = Camera.main;
            if (main != null)
            {
                ModMain.Log($"[SELFTEST] SURVEY camera: clear {main.clearFlags}, background {main.backgroundColor}, far clip {main.farClipPlane:0}; "
                            + $"skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")}; fog {RenderSettings.fog} {RenderSettings.fogMode} "
                            + $"{RenderSettings.fogColor} start {RenderSettings.fogStartDistance:0} end {RenderSettings.fogEndDistance:0} density {RenderSettings.fogDensity:0.000}; "
                            + $"ambient {RenderSettings.ambientMode} {RenderSettings.ambientLight}");
            }
            // Skip UI, players and the "Shapes" quads that report effectively infinite bounds.
            int ui = LayerMask.NameToLayer("UI"), players = LayerMask.NameToLayer("Players");
            var big = UnityEngine.Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.gameObject.layer != ui && r.gameObject.layer != players)
                .Where(r => r.bounds.size.x < 5000f && (r.bounds.size.x >= 4f || r.bounds.size.z >= 4f))
                .Where(r => r.GetComponentInParent<LayoutView>() == null)
                .OrderByDescending(r => r.bounds.size.x * r.bounds.size.z)
                .Take(40)
                .ToList();
            // Summarise the rest by piece (the street pieces etc. come in long runs).
            var pieces = UnityEngine.Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.gameObject.layer != ui && r.gameObject.layer != players && r.bounds.size.x < 5000f)
                .Where(r => r.GetComponentInParent<LayoutView>() == null)
                .GroupBy(r => Piece(r.transform))
                .Select(g => (name: g.Key, count: g.Count(), min: g.Aggregate(new Vector3(1e9f, 0, 1e9f), (m, r) => Vector3.Min(m, r.bounds.min)),
                              max: g.Aggregate(new Vector3(-1e9f, 0, -1e9f), (m, r) => Vector3.Max(m, r.bounds.max))))
                .OrderByDescending(g => (g.max.x - g.min.x) * (g.max.z - g.min.z))
                .Take(25);
            foreach (var g in pieces)
            {
                ModMain.Log($"[SELFTEST] SURVEY piece '{g.name}': {g.count} renderer(s) spanning x {g.min.x:0.0}..{g.max.x:0.0}, z {g.min.z:0.0}..{g.max.z:0.0}");
            }
            foreach (Renderer r in big)
            {
                var sb = new StringBuilder();
                sb.Append($"[SELFTEST] SURVEY renderer '{Path(r.transform)}' {r.GetType().Name} layer {LayerMask.LayerToName(r.gameObject.layer)}: ");
                sb.Append($"bounds centre {r.bounds.center} size {r.bounds.size}; position {r.transform.position} rotation {r.transform.rotation.eulerAngles} scale {r.transform.lossyScale}");
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    sb.Append($"; mesh '{mf.sharedMesh.name}' {mf.sharedMesh.vertexCount} verts, local bounds {mf.sharedMesh.bounds.size}");
                }
                foreach (Material m in r.sharedMaterials.Where(m => m != null))
                {
                    sb.Append($"; material '{m.name}' shader '{m.shader.name}'");
                    if (m.HasProperty("_MainTex") && m.mainTexture != null)
                    {
                        sb.Append($" tex '{m.mainTexture.name}' scale {m.mainTextureScale} offset {m.mainTextureOffset}");
                    }
                    if (m.HasProperty("_Color"))
                    {
                        sb.Append($" colour {m.color}");
                    }
                    string[] keywords = m.shaderKeywords;
                    if (keywords.Length > 0)
                    {
                        sb.Append($" keywords [{string.Join(",", keywords)}]");
                    }
                }
                Component[] roots = r.transform.root.GetComponents<Component>();
                sb.Append($"; root components [{string.Join(",", roots.Select(c => c.GetType().Name))}]");
                ModMain.Log(sb.ToString());
            }
        }

        /// <summary>
        /// The reroll-terminal models (EFP's Floor Plan terminal reuses one): every object in each
        /// view's hierarchy with its components, renderers, meshes and materials, so a hologram of our
        /// own can replace the dice (user request 2026-11-19).
        /// </summary>
        public static void LogTerminals()
        {
            foreach (RerollBlueprintView v in UnityEngine.Object.FindObjectsOfType<RerollBlueprintView>())
            {
                var title = AccessTools.Field(typeof(RerollBlueprintView), "Title")?.GetValue(v) as TMPro.TextMeshPro;
                ModMain.Log($"[SELFTEST] SURVEY terminal view '{Path(v.transform)}' title '{title?.text}' at {v.transform.position}");
                Dump(v.transform, 0);
            }
        }

        private static void Dump(Transform t, int depth)
        {
            var sb = new StringBuilder($"[SELFTEST] SURVEY terminal {new string(' ', depth * 2)}- '{t.name}' active {t.gameObject.activeSelf} layer {LayerMask.LayerToName(t.gameObject.layer)} "
                                       + $"local pos {t.localPosition} rot {t.localRotation.eulerAngles} scale {t.localScale}; components [{string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().Name))}]");
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                sb.Append($"; mesh '{mf.sharedMesh.name}' {mf.sharedMesh.vertexCount} verts bounds {mf.sharedMesh.bounds.size}");
            }
            Renderer r = t.GetComponent<Renderer>();
            if (r != null)
            {
                foreach (Material m in r.sharedMaterials.Where(m => m != null))
                {
                    sb.Append($"; material '{m.name}' shader '{m.shader.name}'");
                }
            }
            ModMain.Log(sb.ToString());
            if (r != null && r.sharedMaterial != null && depth < 8)
            {
                LogMaterial(r.sharedMaterial, $"of '{t.name}'");
            }
            foreach (Transform c in t)
            {
                Dump(c, depth + 1);
            }
        }

        /// <summary>Every shader property of the material and its value (textures with tiling).</summary>
        public static void LogMaterial(Material m, string label)
        {
            var sb = new StringBuilder($"[SELFTEST] SURVEY material {label} '{m.name}' shader '{m.shader.name}':");
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = m.shader.GetPropertyName(i);
                switch (m.shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture t = m.GetTexture(name);
                        sb.Append(t != null
                            ? $" {name}='{t.name}' {t.width}x{t.height} {t.wrapMode} scale {m.GetTextureScale(name)} offset {m.GetTextureOffset(name)};"
                            : $" {name}=none;");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append($" {name}={m.GetColor(name)};");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append($" {name}={m.GetVector(name)};");
                        break;
                    default:
                        sb.Append($" {name}={m.GetFloat(name):0.###};");
                        break;
                }
            }
            ModMain.Log(sb.ToString());
        }

        /// <summary>
        /// The big flat ground boxes (at least 50 x 50, under 0.5 high, at ground level) that aren't
        /// part of the restaurant.
        /// </summary>
        public static List<Renderer> GroundRenderers() => UnityEngine.Object.FindObjectsOfType<MeshRenderer>()
            .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.size.x >= 50f && r.bounds.size.z >= 50f
                        && r.bounds.size.x < 5000f && r.bounds.size.y < 0.5f && Mathf.Abs(r.bounds.center.y) < 0.5f)
            .Where(r => r.GetComponentInParent<LayoutView>() == null)
            .Cast<Renderer>().ToList();

        private GameObject _extension;

        /// <summary>
        /// Experiment: a copy of the first ground box, 3 times as large and 2 cm lower, so the
        /// original shows where both exist. A close-up of the original's +x edge then shows whether
        /// the material's pattern continues across it (world-space) or changes scale (object-space).
        /// Returns the edge's x, or null if there's no ground box.
        /// </summary>
        public float? AddTestExtension()
        {
            Renderer g = GroundRenderers().FirstOrDefault();
            if (g == null)
            {
                return null;
            }
            LogMaterial(g.sharedMaterial, "ground");
            _extension = UnityEngine.Object.Instantiate(g.gameObject);
            _extension.name = "EasyFloorPlanner Ground Survey Extension";
            foreach (Component c in _extension.GetComponents<Component>())
            {
                if (!(c is Transform) && !(c is MeshRenderer) && !(c is MeshFilter))
                {
                    UnityEngine.Object.Destroy(c);
                }
            }
            _extension.transform.SetParent(null, false);
            _extension.transform.position = g.transform.position + new Vector3(0f, -0.02f, 0f);
            _extension.transform.rotation = g.transform.rotation;
            _extension.transform.localScale = Vector3.Scale(g.transform.lossyScale, new Vector3(3f, 1f, 3f));
            return g.bounds.max.x;
        }

        /// <summary>Renders a close-up (12 x 12 tiles, 100 px per tile) centred on (x, z).</summary>
        public void RenderCloseUp(float x, float z)
        {
            var go = new GameObject("EasyFloorPlanner Ground Survey Close-up");
            Cam = go.AddComponent<Camera>();
            if (Camera.main != null)
            {
                Cam.CopyFrom(Camera.main);
            }
            Cam.orthographic = true;
            Cam.orthographicSize = 6f;
            Cam.aspect = 1f;
            go.transform.position = new Vector3(x, 120f, z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Cam.nearClipPlane = 1f;
            Cam.farClipPlane = 400f;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Void;
            Target = new RenderTexture(1200, 1200, 24, RenderTextureFormat.ARGB32);
            Cam.targetTexture = Target;
            Cam.enabled = true;
        }

        /// <summary>
        /// Saves the top-down picture and counts the tiles inside <paramref name="area"/> (clipped to
        /// the picture) that show the void colour, i.e. have no ground.
        /// </summary>
        public (int voids, int checkedTiles) CountVoid(Rect area, string pngPath)
        {
            Cam.enabled = false;
            int size = Target.width;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            int voids = 0, n = 0;
            for (int x = Mathf.Max(-Half, Mathf.CeilToInt(area.xMin + 0.5f)); x <= Mathf.Min(Half - 1, Mathf.FloorToInt(area.xMax - 0.5f)); x++)
            {
                for (int z = Mathf.Max(-Half, Mathf.CeilToInt(area.yMin + 0.5f)); z <= Mathf.Min(Half - 1, Mathf.FloorToInt(area.yMax - 0.5f)); z++)
                {
                    Color c = tex.GetPixel((x + Half) * PxPerTile + PxPerTile / 2, (z + Half) * PxPerTile + PxPerTile / 2);
                    n++;
                    voids += c.r > 0.9f && c.g < 0.1f && c.b > 0.9f ? 1 : 0;
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
            return (voids, n);
        }

        /// <summary>A perspective close-up of the Floor Plan terminal (from the front, a little above), for SaveRaw.</summary>
        public void RenderTerminal(Vector3 at)
        {
            var go = new GameObject("EasyFloorPlanner Terminal Close-up");
            Cam = go.AddComponent<Camera>();
            if (Camera.main != null)
            {
                Cam.CopyFrom(Camera.main);
            }
            Cam.orthographic = false;
            Cam.fieldOfView = 28f;
            Cam.aspect = 1f;
            go.transform.position = at + new Vector3(0f, 2.4f, -3.4f);
            go.transform.LookAt(at + new Vector3(0f, 0.85f, 0f));
            Cam.nearClipPlane = 0.1f;
            Cam.farClipPlane = 100f;
            Target = new RenderTexture(900, 900, 24, RenderTextureFormat.ARGB32);
            Cam.targetTexture = Target;
            Cam.enabled = true;
        }

        /// <summary>Saves the close-up as is (no coverage analysis).</summary>
        public void SaveRaw(string pngPath)
        {
            Cam.enabled = false;
            var tex = new Texture2D(Target.width, Target.height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            tex.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            try
            {
                File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't save {pngPath}: {e.Message}");
            }
            UnityEngine.Object.Destroy(tex);
        }

        /// <summary>The prefab instance a renderer belongs to: the topmost "(Clone)" under the scene roots, else its root.</summary>
        private static string Piece(Transform t)
        {
            string found = null;
            for (Transform x = t; x != null; x = x.parent)
            {
                if (x.name.EndsWith("(Clone)") && x.name != "Appliance(Clone)")
                {
                    found = x.name;
                }
            }
            return found ?? t.root.name;
        }

        private static string Path(Transform t)
        {
            var names = new List<string>();
            for (Transform x = t; x != null && names.Count < 6; x = x.parent)
            {
                names.Add(x.name);
            }
            names.Reverse();
            return string.Join("/", names);
        }

        public void Render()
        {
            var go = new GameObject("EasyFloorPlanner Ground Survey");
            Cam = go.AddComponent<Camera>();
            if (Camera.main != null)
            {
                Cam.CopyFrom(Camera.main);
            }
            Cam.orthographic = true;
            Cam.orthographicSize = Half;
            Cam.aspect = 1f;
            go.transform.position = new Vector3(-0.5f, 120f, -0.5f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Cam.nearClipPlane = 1f;
            Cam.farClipPlane = 400f;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Void;
            int size = Half * 2 * PxPerTile;
            Target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            Cam.targetTexture = Target;
            Cam.enabled = true; // renders with the next frame (as the lighting probe does); read it with Save
        }

        /// <summary>Saves the picture and logs which tiles along the axes through the origin are covered.</summary>
        public void Save(string pngPath, string label)
        {
            Cam.enabled = false;
            int size = Target.width;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            bool Covered(int x, int z)
            {
                Color c = tex.GetPixel((x + Half) * PxPerTile + PxPerTile / 2, (z + Half) * PxPerTile + PxPerTile / 2);
                return !(c.r > 0.9f && c.g < 0.1f && c.b > 0.9f);
            }
            string Run(Func<int, bool> covered)
            {
                int lo = 0, hi = 0;
                while (lo > -Half && covered(lo - 1))
                {
                    lo--;
                }
                while (hi < Half - 1 && covered(hi + 1))
                {
                    hi++;
                }
                return covered(0) ? $"{lo}..{hi}" : "not covered at 0";
            }
            int total = 0, voids = 0;
            for (int x = -Half; x < Half; x += 2)
            {
                for (int z = -Half; z < Half; z += 2)
                {
                    total++;
                    voids += Covered(x, z) ? 0 : 1;
                }
            }
            ModMain.Log($"[SELFTEST] SURVEY {label}: ground along x (z = 0) {Run(x => Covered(x, 0))}, along x (z = -6) {Run(x => Covered(x, -6))}, "
                        + $"along z (x = 0) {Run(z => Covered(0, z))}; void {100f * voids / total:0}% of tiles -{Half}..{Half - 1}.");
            try
            {
                File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                ModMain.LogWarning($"Couldn't save {pngPath}: {e.Message}");
            }
            UnityEngine.Object.Destroy(tex);
        }

        public void Dispose()
        {
            if (_extension != null)
            {
                UnityEngine.Object.Destroy(_extension);
            }
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
