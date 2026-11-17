#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Tools > Paint Land > Samples > Create Sample Assets makes the sample content Paint Land ships with:
    /// a rolling sample land to paint on, and simple low-poly trees, rocks, bushes, flowers, mushrooms, stumps, logs and a
    /// grass tuft for the Scatter and Grass tabs (all made here from code, no outside art). Create Demo Scene builds the
    /// sample land into Land and saves a ready-to-paint scene (Sample/Paint Land Demo.unity), without touching the scene
    /// you have open. Both can be run again to put the samples back as they were.
    /// </summary>
    internal static class PaintLandSamples
    {
        private const string Root = "Assets/Paint Land/Sample";
        private const string Meshes = Root + "/Meshes";
        private const string Prefabs = Root + "/Prefabs";
        private const string Materials = Root + "/Materials";
        private const string BladesPath = Root + "/Textures/Grass Blades.png";
        private const string LandPrefab = Root + "/Sample Land.prefab";
        private const string DemoScene = Root + "/Paint Land Demo.unity";

        [MenuItem("Tools/Paint Land/Samples/Create Sample Assets", priority = 1500)]
        public static void CreateSampleAssets()
        {
            LandBuilder.EnsureFolder(Meshes);
            LandBuilder.EnsureFolder(Prefabs);
            LandBuilder.EnsureFolder(Materials);

            Material bark = Mat("Bark", new Color(0.45f, 0.30f, 0.20f));
            Material leaves = Mat("Leaves", new Color(0.36f, 0.66f, 0.27f));
            Material pine = Mat("Pine Needles", new Color(0.18f, 0.45f, 0.30f));
            Material rock = Mat("Rock", new Color(0.55f, 0.55f, 0.58f));
            Material bush = Mat("Bush", new Color(0.30f, 0.58f, 0.25f));
            Material stem = Mat("Stem", new Color(0.30f, 0.60f, 0.25f));
            Material petal = Mat("Petals", new Color(0.92f, 0.30f, 0.30f));
            Material cap = Mat("Mushroom Cap", new Color(0.85f, 0.22f, 0.18f));
            Material stalk = Mat("Mushroom Stalk", new Color(0.93f, 0.90f, 0.82f));
            Material grass = GrassMat();

            var rng = new System.Random(11);

            // Pine: a trunk and three stacked cones
            Save("Pine Tree", new[] { bark, pine }, b =>
            {
                b.Cylinder(0, Vector3.zero, 0.25f, 0.18f, 2f, 6);
                b.Cone(1, new Vector3(0, 1.4f, 0), 2.0f, 3.2f, 7);
                b.Cone(1, new Vector3(0, 3.0f, 0), 1.6f, 2.8f, 7);
                b.Cone(1, new Vector3(0, 4.5f, 0), 1.1f, 2.4f, 7);
            });
            // Round tree: a trunk and a lumpy crown
            Save("Round Tree", new[] { bark, leaves }, b =>
            {
                b.Cylinder(0, Vector3.zero, 0.3f, 0.22f, 2.6f, 6);
                b.Blob(1, new Vector3(0, 3.6f, 0), new Vector3(2.2f, 1.8f, 2.2f), 0.18f, rng);
                b.Blob(1, new Vector3(0.9f, 3.0f, 0.4f), new Vector3(1.3f, 1.1f, 1.3f), 0.15f, rng);
                b.Blob(1, new Vector3(-0.8f, 3.2f, -0.5f), new Vector3(1.2f, 1.0f, 1.2f), 0.15f, rng);
            });
            Save("Rock A", new[] { rock }, b => b.Blob(0, new Vector3(0, 0.35f, 0), new Vector3(1.1f, 0.7f, 0.9f), 0.25f, rng));
            Save("Rock B", new[] { rock }, b =>
            {
                b.Blob(0, new Vector3(0, 0.25f, 0), new Vector3(0.8f, 0.55f, 0.7f), 0.25f, rng);
                b.Blob(0, new Vector3(0.7f, 0.15f, 0.3f), new Vector3(0.45f, 0.35f, 0.4f), 0.25f, rng);
            });
            Save("Bush", new[] { bush }, b =>
            {
                b.Blob(0, new Vector3(0, 0.45f, 0), new Vector3(0.8f, 0.6f, 0.8f), 0.18f, rng);
                b.Blob(0, new Vector3(0.45f, 0.35f, 0.2f), new Vector3(0.55f, 0.45f, 0.55f), 0.18f, rng);
                b.Blob(0, new Vector3(-0.4f, 0.32f, -0.25f), new Vector3(0.5f, 0.4f, 0.5f), 0.18f, rng);
            });
            Save("Flower", new[] { stem, petal }, b =>
            {
                b.Cylinder(0, Vector3.zero, 0.025f, 0.02f, 0.45f, 4);
                b.Blob(1, new Vector3(0, 0.48f, 0), new Vector3(0.14f, 0.05f, 0.14f), 0.05f, rng);
            });
            Save("Mushroom", new[] { stalk, cap }, b =>
            {
                b.Cylinder(0, Vector3.zero, 0.06f, 0.05f, 0.22f, 6);
                b.Cone(1, new Vector3(0, 0.2f, 0), 0.2f, 0.14f, 8);
            });
            Save("Stump", new[] { bark }, b => b.Cylinder(0, Vector3.zero, 0.38f, 0.32f, 0.5f, 7));
            Save("Log", new[] { bark }, b => b.Cylinder(0, new Vector3(-1.2f, 0.25f, 0), 0.25f, 0.22f, 2.4f, 7, sideways: true));
            // Grass tuft: three crossed blade cards (the Grass Field draws it thousands of times)
            Save("Grass Tuft", new[] { grass }, b =>
            {
                for (int i = 0; i < 3; i++) b.Card(0, i * 60f, 1f, 0.8f);
            }, flat: false);

            MakeSampleLand();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Paint Land: sample assets made in " + Root);
        }

        [MenuItem("Tools/Paint Land/Samples/Create Demo Scene", priority = 1501)]
        public static void CreateDemoScene()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(LandPrefab) == null) CreateSampleAssets();
            Scene previous = SceneManager.GetActiveScene();
            Scene demo = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
            SceneManager.SetActiveScene(demo);
            try
            {
                var source = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(LandPrefab), demo);
                source.transform.position = Vector3.zero;
                LandSettings settings = LandBuilder.GetSettings();
                settings.source = AssetDatabase.LoadAssetAtPath<GameObject>(LandPrefab);
                EditorUtility.SetDirty(settings);
                LandBuilder.Build(settings, quiet: true);
                if (source != null) Object.DestroyImmediate(source);
                GrassPaint.Create();
                foreach (GameObject root in demo.GetRootGameObjects())
                {
                    if (root.TryGetComponent(out Camera cam))
                    {
                        cam.transform.position = new Vector3(-60f, 70f, -260f);
                        cam.transform.rotation = Quaternion.Euler(18f, 20f, 0f);
                        cam.farClipPlane = 2000f;
                    }
                    if (root.TryGetComponent(out Light light) && light.type == LightType.Directional)
                    {
                        light.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
                        light.shadows = LightShadows.Soft;
                    }
                }
                EditorSceneManager.SaveScene(demo, DemoScene);
                Debug.Log("Paint Land: demo scene saved to " + DemoScene + ". Open it and use Tools > Paint Land > Open Paint Land.");
            }
            finally
            {
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(demo, true);
            }
        }

        // ---------------------------------------------------------------- Sample land

        private static void MakeSampleLand()
        {
            const int n = 161;          // vertices a side
            const float size = 640f;    // metres
            var vertices = new Vector3[n * n];
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    float px = (x / (float)(n - 1) - 0.5f) * size, pz = (z / (float)(n - 1) - 0.5f) * size;
                    vertices[z * n + x] = new Vector3(px, Height(px, pz), pz);
                }
            }
            var triangles = new int[(n - 1) * (n - 1) * 6];
            int t = 0;
            for (int z = 0; z < n - 1; z++)
            {
                for (int x = 0; x < n - 1; x++)
                {
                    int i = z * n + x;
                    triangles[t++] = i; triangles[t++] = i + n; triangles[t++] = i + 1;
                    triangles[t++] = i + 1; triangles[t++] = i + n; triangles[t++] = i + n + 1;
                }
            }
            var mesh = new Mesh { name = "Sample Land", indexFormat = IndexFormat.UInt32, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh = SaveMesh(mesh, "Sample Land");

            var go = new GameObject("Sample Land");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Mat("Sample Land", new Color(0.45f, 0.7f, 0.35f));
            PrefabUtility.SaveAsPrefabAsset(go, LandPrefab);
            Object.DestroyImmediate(go);
        }

        // Rolling hills, a mountain at the back and a gentle valley through the middle
        private static float Height(float x, float z)
        {
            float h = 0f, amp = 9f, freq = 1f / 160f;
            for (int o = 0; o < 4; o++)
            {
                h += (Mathf.PerlinNoise(x * freq + 37.1f, z * freq + 11.7f) - 0.5f) * 2f * amp;
                amp *= 0.45f;
                freq *= 2.1f;
            }
            float mountain = Mathf.Exp(-((x - 60f) * (x - 60f) + (z - 230f) * (z - 230f)) / (2f * 90f * 90f)) * 55f;
            float valley = -Mathf.Exp(-(x + 20f) * (x + 20f) / (2f * 70f * 70f)) * 6f;
            float edge = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) / 320f;
            float fall = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.82f, 1f, edge)) * 12f;
            return 12f + h + mountain + valley - fall;
        }

        // ---------------------------------------------------------------- Props

        private static Material Mat(string name, Color colour)
        {
            string path = $"{Materials}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Universal Render Pipeline/Lit");
                material = new Material(shader) { name = name, enableInstancing = true };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material GrassMat()
        {
            if (AssetImporter.GetAtPath(BladesPath) is TextureImporter importer)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            Material material = Mat("Grass", new Color(0.45f, 0.75f, 0.35f));
            var blades = AssetDatabase.LoadAssetAtPath<Texture2D>(BladesPath);
            if (blades != null && material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", blades);
            if (material.HasProperty("_AlphaClip"))
            {
                material.SetFloat("_AlphaClip", 1f);
                material.SetFloat("_Cutoff", 0.5f);
                material.EnableKeyword("_ALPHATEST_ON");
            }
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.renderQueue = (int)RenderQueue.AlphaTest;
            return material;
        }

        private static void Save(string name, Material[] materials, System.Action<Builder> make, bool flat = true)
        {
            var b = new Builder(materials.Length);
            make(b);
            Mesh mesh = SaveMesh(b.ToMesh(name, flat), name);
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            PrefabUtility.SaveAsPrefabAsset(go, $"{Prefabs}/{name}.prefab");
            Object.DestroyImmediate(go);
        }

        private static Mesh SaveMesh(Mesh mesh, string name)
        {
            string path = $"{Meshes}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // Low-poly mesh maker: each part goes into its material's submesh
        private sealed class Builder
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int>[] subs;

            public Builder(int submeshes)
            {
                subs = new List<int>[submeshes];
                for (int i = 0; i < submeshes; i++) subs[i] = new List<int>();
            }

            private int V(Vector3 p, Vector2 uv) { vertices.Add(p); uvs.Add(uv); return vertices.Count - 1; }
            private void Tri(int s, int a, int b, int c) { subs[s].Add(a); subs[s].Add(b); subs[s].Add(c); }

            public void Cylinder(int s, Vector3 at, float r0, float r1, float h, int sides, bool sideways = false)
            {
                Quaternion turn = sideways ? Quaternion.Euler(0, 0, -90f) : Quaternion.identity;
                Vector3 P(float a, float r, float y) => at + turn * new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    int b0 = V(P(a0, r0, 0), Vector2.zero), b1 = V(P(a1, r0, 0), Vector2.zero);
                    int t0 = V(P(a0, r1, h), Vector2.up), t1 = V(P(a1, r1, h), Vector2.up);
                    Tri(s, b0, t0, t1); Tri(s, b0, t1, b1);
                    int top = V(at + turn * new Vector3(0, h, 0), Vector2.up);
                    Tri(s, t1, t0, top);
                }
            }

            public void Cone(int s, Vector3 at, float r, float h, int sides)
            {
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    int b0 = V(at + new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), Vector2.zero);
                    int b1 = V(at + new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r), Vector2.zero);
                    int top = V(at + new Vector3(0, h, 0), Vector2.up);
                    int mid = V(at, Vector2.zero);
                    Tri(s, b0, top, b1);
                    Tri(s, b1, mid, b0);
                }
            }

            // A lumpy low-poly ball (an icosahedron split once, its corners pushed in and out a little)
            public void Blob(int s, Vector3 at, Vector3 scale, float lumps, System.Random rng)
            {
                float t = (1f + Mathf.Sqrt(5f)) / 2f;
                var p = new List<Vector3>
                {
                    new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                    new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                    new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
                };
                int[] f =
                {
                    0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                    3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
                };
                var mids = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mids.TryGetValue(key, out int m)) return m;
                    p.Add((p[a] + p[b]) * 0.5f);
                    mids[key] = p.Count - 1;
                    return p.Count - 1;
                }
                var faces = new List<int>();
                for (int i = 0; i < f.Length; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    faces.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                var placed = new Vector3[p.Count];
                for (int i = 0; i < p.Count; i++)
                {
                    float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * lumps;
                    Vector3 d = p[i].normalized * jitter;
                    placed[i] = at + Vector3.Scale(d, scale);
                }
                for (int i = 0; i < faces.Count; i += 3)
                {
                    Tri(s, V(placed[faces[i]], Vector2.zero), V(placed[faces[i + 1]], Vector2.zero), V(placed[faces[i + 2]], Vector2.zero));
                }
            }

            // A grass card: an upright quad turned about the up axis, UVs over the blade texture
            public void Card(int s, float angle, float width, float height)
            {
                Quaternion q = Quaternion.Euler(0, angle, 0);
                int a = V(q * new Vector3(-width / 2, 0, 0), new Vector2(0, 0));
                int b = V(q * new Vector3(width / 2, 0, 0), new Vector2(1, 0));
                int c = V(q * new Vector3(width / 2, height, 0), new Vector2(1, 1));
                int d = V(q * new Vector3(-width / 2, height, 0), new Vector2(0, 1));
                Tri(s, a, d, c); Tri(s, a, c, b);
            }

            public Mesh ToMesh(string name, bool flat)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uvs);
                mesh.subMeshCount = subs.Length;
                for (int i = 0; i < subs.Length; i++) mesh.SetTriangles(subs[i], i);
                mesh.RecalculateNormals();
                if (!flat)
                {
                    var normals = new Vector3[vertices.Count];
                    for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
                    mesh.normals = normals;
                }
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
#endif
