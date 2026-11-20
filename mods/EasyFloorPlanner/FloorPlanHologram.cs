using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The Floor Plan terminal's hologram: drafting tools (a sheet, a set square and a pencil)
    /// instead of the reroll terminal's dice (user request 2026-11-19, "Drafting tools" chosen).
    ///
    /// The terminal reuses the game's reroll-trigger model (surveyed in-game 2026-11-19): its root
    /// Animator spins a "Container" holding two "Dice" (944-vertex cube meshes), each drawn with the
    /// materials "Glowing Blue Soft" and "Glowing Blue Dark Soft" (shader "Indicator Light"). The
    /// dice are hidden and the tools are built inside the same Container, so they spin and float
    /// with the game's own animation, and they use the dice's two materials, so they glow like the
    /// game's other holograms. Visual only; applied on every machine by the terminal's label patch,
    /// and only to the Floor Plan terminal (the shop's reroll terminal keeps its dice).
    /// </summary>
    internal static class FloorPlanHologram
    {
        private const string Name = "EasyFloorPlanner Drafting Hologram";
        private static Mesh Cube, Pyramid;

        public static void Apply(Component view)
        {
            Transform container = view.transform.Find("Container");
            if (container == null || container.Find(Name) != null)
            {
                return; // not the expected model, or already done
            }
            Renderer dice = null;
            foreach (Transform child in container)
            {
                if (child.name.StartsWith("Dice"))
                {
                    Renderer r = child.GetComponent<Renderer>();
                    dice = dice ?? r;
                    child.gameObject.SetActive(false);
                }
            }
            if (dice == null || dice.sharedMaterials.Length == 0)
            {
                return;
            }
            Material glow = dice.sharedMaterials[0];
            Material dark = dice.sharedMaterials.Length > 1 ? dice.sharedMaterials[1] : glow;
            Cube = Cube ?? BuiltInCube();
            Pyramid = Pyramid ?? MakePyramid();

            var root = new GameObject(Name);
            root.layer = container.gameObject.layer;
            root.transform.SetParent(container, false);
            // Where the dice floated, upright and turned a little toward the front.
            root.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            root.transform.localRotation = Quaternion.Euler(-12f, 0f, 0f);
            root.transform.localScale = Vector3.one * 0.85f;

            // The sheet: a dark blueprint board with a bright frame (first render, 2026-11-19: an all-pale
            // sheet swallowed the tools in front of it).
            Part(root, Cube, dark, new Vector3(0f, 0f, 0.03f), Vector3.zero, new Vector3(0.62f, 0.46f, 0.015f));
            foreach ((Vector3 at, Vector3 size) in new[]
                     {
                         (new Vector3(0f, 0.23f, 0.03f), new Vector3(0.64f, 0.02f, 0.025f)),
                         (new Vector3(0f, -0.23f, 0.03f), new Vector3(0.64f, 0.02f, 0.025f)),
                         (new Vector3(0.31f, 0f, 0.03f), new Vector3(0.02f, 0.46f, 0.025f)),
                         (new Vector3(-0.31f, 0f, 0.03f), new Vector3(0.02f, 0.46f, 0.025f)),
                     })
            {
                Part(root, Cube, glow, at, Vector3.zero, size);
            }
            // A few bright plan lines on the sheet (a room outline with a door gap).
            foreach ((Vector3 at, Vector3 size) in new[]
                     {
                         (new Vector3(-0.08f, 0.12f, 0.015f), new Vector3(0.34f, 0.018f, 0.01f)),
                         (new Vector3(-0.25f, -0.01f, 0.015f), new Vector3(0.018f, 0.26f, 0.01f)),
                         (new Vector3(0.09f, -0.01f, 0.015f), new Vector3(0.018f, 0.26f, 0.01f)),
                         (new Vector3(-0.16f, -0.14f, 0.015f), new Vector3(0.18f, 0.018f, 0.01f)),
                         (new Vector3(0.06f, -0.14f, 0.015f), new Vector3(0.06f, 0.018f, 0.01f)),
                     })
            {
                Part(root, Cube, glow, at, Vector3.zero, size);
            }

            // The set square: a right-angled triangle frame in front of the sheet.
            var square = new GameObject("Set Square");
            square.layer = root.layer;
            square.transform.SetParent(root.transform, false);
            square.transform.localPosition = new Vector3(0.1f, 0.05f, -0.07f); // upper right, clear of the pencil
            square.transform.localRotation = Quaternion.Euler(0f, 0f, -6f);
            const float leg = 0.3f, bar = 0.045f, depth = 0.035f;
            Part(square, Cube, glow, new Vector3(0f, -leg / 2f, 0f), Vector3.zero, new Vector3(leg, bar, depth));
            Part(square, Cube, glow, new Vector3(-leg / 2f, 0f, 0f), Vector3.zero, new Vector3(bar, leg, depth));
            Part(square, Cube, glow, Vector3.zero, new Vector3(0f, 0f, -45f), new Vector3(leg * 1.414f, bar, depth));

            // The pencil, across the front: a body, a darker ferrule and a pointed tip.
            var pencil = new GameObject("Pencil");
            pencil.layer = root.layer;
            pencil.transform.SetParent(root.transform, false);
            pencil.transform.localPosition = new Vector3(-0.1f, -0.1f, -0.13f); // lower left
            pencil.transform.localRotation = Quaternion.Euler(0f, 0f, 25f);
            Part(pencil, Cube, glow, Vector3.zero, Vector3.zero, new Vector3(0.62f, 0.06f, 0.06f)); // overhangs the sheet
            Part(pencil, Cube, dark, new Vector3(-0.33f, 0f, 0f), Vector3.zero, new Vector3(0.06f, 0.066f, 0.066f));
            Part(pencil, Pyramid, dark, new Vector3(0.36f, 0f, 0f), new Vector3(0f, 0f, -90f), new Vector3(0.06f, 0.12f, 0.06f));
        }

        private static void Part(GameObject parent, Mesh mesh, Material material, Vector3 position, Vector3 euler, Vector3 size)
        {
            var go = new GameObject("Part") { layer = parent.layer };
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Unity's unit cube mesh (taken from a primitive, which is then destroyed).</summary>
        private static Mesh BuiltInCube()
        {
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(primitive);
            return mesh;
        }

        /// <summary>A square-based pyramid, base at y = -0.5, point at y = 0.5 (the pencil's tip).</summary>
        private static Mesh MakePyramid()
        {
            Vector3 a = new Vector3(-0.5f, -0.5f, -0.5f), b = new Vector3(0.5f, -0.5f, -0.5f), c = new Vector3(0.5f, -0.5f, 0.5f),
                d = new Vector3(-0.5f, -0.5f, 0.5f), top = new Vector3(0f, 0.5f, 0f);
            Vector3[] v = { a, b, top, b, c, top, c, d, top, d, a, top, a, d, c, a, c, b };
            // Both windings, so it shows from every side whatever the shader culls.
            var tris = new int[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                tris[i] = i;
            }
            for (int i = 0; i < v.Length; i += 3)
            {
                tris[v.Length + i] = i;
                tris[v.Length + i + 1] = i + 2;
                tris[v.Length + i + 2] = i + 1;
            }
            var mesh = new Mesh { name = "EFP Pyramid", vertices = v, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
