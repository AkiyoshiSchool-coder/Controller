#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. What Tools > Paint Land (LandPainter) paints onto the land, and how it's kept:
    ///   - Paths go into Land Paths.png (next to Land Settings), a texture the Land material lays over the land from above,
    ///     so a path can be any width, with crisp edges, whatever size the triangles are. You can open it in a paint
    ///     program too (it's what the land looks like from above: see-through where nothing's painted).
    ///   - Coloured-in triangles go into the land pieces' meshes (each triangle has its own vertices, and its colour is in
    ///     a vertex stream of its own), so a triangle is one flat colour. Textures painted on whole triangles go into a
    ///     stream of their own the same way (LandTextures paints the soft-brushed ones, LandShape the land's shape).
    /// They all stay when the land is rebuilt. Painting can be undone with Ctrl+Z like anything else (each stroke is a step),
    /// and saves by itself a moment after you stop, and whenever the scene is saved or Play is pressed.
    /// </summary>
    [InitializeOnLoad]
    internal static class LandPaint
    {
        internal const string PathsPath = LandBuilder.Folder + "/Land Paths.png";
        private const int MaxPathPixels = 4096;
        private const double SaveDelay = 3.0;

        static LandPaint()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += SaveWhenIdle;
            EditorSceneManager.sceneSaving += (scene, path) => SaveNow();
            EditorSceneManager.sceneOpened += (scene, mode) => Forget();
            AssemblyReloadEvents.beforeAssemblyReload += SaveNow;
            EditorApplication.quitting += SaveNow;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) SaveNow();
            };
        }

        // ================================================================ The land's pieces

        /// <summary>A piece of the land, as painting sees it (its mesh's data, kept here while painting).</summary>
        internal sealed class Piece
        {
            public Mesh Mesh;
            public MeshFilter Filter;
            public Transform Transform;
            public Vector3[] Points;      // its vertices, three a triangle, in its own space
            public Color32[] Ground;      // the ground's own colour at each vertex
            public Color32[] GroundPaint; // the colour painted on the ground there by hand (times how much; alpha = how much)
            public Color32[] Paint;       // the colour painted on each vertex's triangle (alpha 0 = none)
            public Color32[] Textures;    // the texture painted on each vertex's triangle (r = which, 1 to 8, 0 = none; g = how much)
            public Vector2[] Middles;     // each triangle's middle, from above (x, z)
            public Bounds Bounds;
            public float CellSize;
            public Dictionary<long, List<int>> Cells;
            public bool Changed;          // painted since it was last sent to the mesh
            public bool TexturesChanged;  // a texture painted on a triangle since then
        }

        /// <summary>Where the mouse points at the land.</summary>
        internal struct Hit
        {
            public Piece Piece;
            public int Triangle;
            public Vector3 Point;         // in the world
            public Vector3 Local;         // in the piece's space (the same as the land's)
            public Vector3 Normal;        // in the world
            public Vector3 Weights;       // how near each of the triangle's corners
            public float Distance;
        }

        private static GameObject land;
        private static readonly List<Piece> pieces = new List<Piece>();
        private static readonly Dictionary<Mesh, Piece> pieceOf = new Dictionary<Mesh, Piece>();

        internal static GameObject Land => land;

        /// <summary>Which pieces of the land couldn't be read for painting, and why (null if none).</summary>
        internal static string Skipped;
        internal static IReadOnlyList<Piece> Pieces => pieces;
        internal static bool Ready => land != null && pieces.Count > 0 && StillThere();

        /// <summary>Goes up each time the land is let go of or read again (so what's worked out from the pieces is redone).</summary>
        internal static int Generation { get; private set; }

        // The pieces read are still the ones the Land shows (nothing has swapped its mesh)
        private static bool StillThere()
        {
            foreach (Piece piece in pieces)
            {
                if (piece.Filter == null || piece.Transform == null || piece.Filter.sharedMesh != piece.Mesh) return false;
            }
            return true;
        }

        /// <summary>
        /// Finds the Land in the open scene and reads its pieces, if it hasn't already. Returns null if it can be painted,
        /// or what's stopping it.
        /// </summary>
        internal static string Load()
        {
            GameObject found = LandBuilder.FindLand();
            if (found == null)
            {
                Forget();
                return "There's no Land in the open scene. Build it with Tools > Paint Land > Rebuild Land first.";
            }
            if (found == land && pieces.Count > 0 && StillThere()) return null;

            Forget();
            // Pieces that Polybrush swapped its own copy onto go back on their own meshes (keeping their colours)
            int taken = LandBuilder.TakeBackPieces(found, true);
            if (taken > 0) Debug.Log($"Paint Land: put {taken} pieces of the land back on their own meshes (Polybrush had swapped in copies), keeping their colours.");
            var read = new List<Piece>();
            var skipped = new List<string>();
            int upgraded = 0;
            foreach (MeshFilter filter in found.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!LandBuilder.IsPieceObject(found, filter.transform)) continue;   // (the trees and plants placed under the Land)
                Mesh mesh = filter.sharedMesh;
                if (!LandBuilder.CanPaint(mesh))
                {
                    // What it shows has been changed (by Polybrush, say): its own mesh, as saved, goes back on
                    Mesh own = LandBuilder.PieceMesh(filter.gameObject.name);
                    if (own != null && !LandBuilder.CanPaint(own)) Resources.UnloadAsset(own);   // (read again from its file)
                    if (own != null && LandBuilder.CanPaint(own))
                    {
                        Debug.Log($"Paint Land: put {filter.gameObject.name} back on its own mesh ({LandBuilder.WhyNotPaintable(mesh)}).");
                        Undo.RecordObject(filter, "Take Back Land Piece");
                        filter.sharedMesh = own;
                        if (filter.TryGetComponent(out MeshCollider collider))
                        {
                            Undo.RecordObject(collider, "Take Back Land Piece");
                            collider.sharedMesh = null;
                            collider.sharedMesh = own;
                        }
                        EditorSceneManager.MarkSceneDirty(found.scene);
                        mesh = own;
                    }
                    else
                    {
                        skipped.Add($"{filter.gameObject.name}: {LandBuilder.WhyNotPaintable(mesh)}");
                        continue;
                    }
                }
                // (pieces built before textures could be painted on triangles get the stream they go in)
                if (LandBuilder.AddStreams(mesh)) upgraded++;
                Piece piece = ReadPiece(filter.transform, mesh);
                piece.Filter = filter;
                read.Add(piece);
            }
            if (upgraded > 0) Debug.Log($"Paint Land: {upgraded} pieces of the land can now have textures and ground colours painted on them.");
            Skipped = skipped.Count > 0
                ? $"{skipped.Count} pieces of the land can't be painted until it's rebuilt (the button below):\n" + string.Join("\n", skipped)
                : null;
            if (Skipped != null) Debug.LogWarning("Paint Land: " + Skipped);
            if (read.Count == 0) return Skipped ?? "The Land has no pieces. Rebuild it with Tools > Paint Land > Rebuild Land.";
            land = found;
            Generation++;
            foreach (Piece piece in read)
            {
                pieces.Add(piece);
                pieceOf[piece.Mesh] = piece;
            }
            return null;
        }

        /// <summary>Lets go of the land (saving what's painted first), e.g. before it's rebuilt.</summary>
        internal static void Forget()
        {
            EndStroke();
            SaveNow();
            land = null;
            pieces.Clear();
            pieceOf.Clear();
            ClearHistory();
            Generation++;
        }

        private static Piece ReadPiece(Transform transform, Mesh mesh)
        {
            var piece = new Piece { Mesh = mesh, Transform = transform };
            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData data = dataArray[0];
                int count = data.vertexCount;
                using (var points = new NativeArray<Vector3>(count, Allocator.Temp))
                {
                    data.GetVertices(points);
                    piece.Points = points.ToArray();
                }
                using (var colours = new NativeArray<Color32>(count, Allocator.Temp))
                {
                    if (data.HasVertexAttribute(VertexAttribute.Color)) data.GetColors(colours);
                    piece.Ground = colours.ToArray();
                }
                piece.Paint = data.GetVertexData<Color32>(LandBuilder.PaintStream).ToArray();
                piece.Textures = LandBuilder.CanTexture(mesh) ? data.GetVertexData<Color32>(LandBuilder.TextureStream).ToArray() : new Color32[count];
                piece.GroundPaint = LandBuilder.CanPaintGround(mesh) ? data.GetVertexData<Color32>(LandBuilder.GroundPaintStream).ToArray() : new Color32[count];
            }

            // Each triangle's middle, and a grid of buckets over the piece listing the triangles in each (to find the one
            // under a point quickly)
            int triangles = piece.Points.Length / 3;
            piece.Middles = new Vector2[triangles];
            var bounds = new Bounds(piece.Points.Length > 0 ? piece.Points[0] : Vector3.zero, Vector3.zero);
            float size = 0.25f;
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = piece.Points[t * 3], b = piece.Points[t * 3 + 1], c = piece.Points[t * 3 + 2];
                piece.Middles[t] = new Vector2((a.x + b.x + c.x) / 3f, (a.z + b.z + c.z) / 3f);
                bounds.Encapsulate(a);
                bounds.Encapsulate(b);
                bounds.Encapsulate(c);
                size = Mathf.Max(size, Mathf.Max(a.x, b.x, c.x) - Mathf.Min(a.x, b.x, c.x), Mathf.Max(a.z, b.z, c.z) - Mathf.Min(a.z, b.z, c.z));
            }
            piece.Bounds = bounds;
            piece.CellSize = size;
            piece.Cells = new Dictionary<long, List<int>>();
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = piece.Points[t * 3], b = piece.Points[t * 3 + 1], c = piece.Points[t * 3 + 2];
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x) / size), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x, c.x) / size);
                int z0 = Mathf.FloorToInt(Mathf.Min(a.z, b.z, c.z) / size), z1 = Mathf.FloorToInt(Mathf.Max(a.z, b.z, c.z) / size);
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        long key = Key(x, z);
                        if (!piece.Cells.TryGetValue(key, out List<int> cell)) piece.Cells[key] = cell = new List<int>();
                        cell.Add(t);
                    }
                }
            }
            return piece;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>After a piece's points were moved up or down (LandShape): its box takes their new heights.</summary>
        internal static void RefreshBounds(Piece piece)
        {
            Vector3[] p = piece.Points;
            if (p.Length == 0) return;
            float low = p[0].y, high = p[0].y;
            for (int i = 1; i < p.Length; i++)
            {
                float y = p[i].y;
                if (y < low) low = y;
                else if (y > high) high = y;
            }
            Bounds b = piece.Bounds;
            piece.Bounds = new Bounds(new Vector3(b.center.x, (low + high) * 0.5f, b.center.z), new Vector3(b.size.x, high - low, b.size.z));
        }

        // The piece for a mesh, read now if painting hasn't read it (undoing after the painter was closed, say)
        internal static Piece PieceFor(Mesh mesh)
        {
            if (mesh == null) return null;
            if (pieceOf.TryGetValue(mesh, out Piece piece)) return piece;
            if (!LandBuilder.CanPaint(mesh)) return null;
            piece = ReadPiece(null, mesh);
            pieceOf[mesh] = piece;
            return piece;
        }

        // ================================================================ Finding the ground

        /// <summary>Where a ray from the camera first meets the land.</summary>
        internal static bool Raycast(Ray ray, out Hit hit)
        {
            hit = default;
            hit.Distance = float.MaxValue;
            bool any = false;
            foreach (Piece piece in pieces)
            {
                if (piece.Transform == null) continue;
                Matrix4x4 toLocal = piece.Transform.worldToLocalMatrix;
                var local = new Ray(toLocal.MultiplyPoint3x4(ray.origin), toLocal.MultiplyVector(ray.direction));
                if (!piece.Bounds.IntersectRay(local)) continue;
                Vector3[] p = piece.Points;
                for (int t = 0, v = 0; t < piece.Middles.Length; t++, v += 3)
                {
                    if (!RayTriangle(local, p[v], p[v + 1], p[v + 2], out float along, out float u, out float w)) continue;
                    Vector3 localPoint = local.origin + local.direction * along;
                    Vector3 world = piece.Transform.TransformPoint(localPoint);
                    float distance = Vector3.Distance(ray.origin, world);
                    if (distance >= hit.Distance) continue;
                    Vector3 normal = Vector3.Cross(p[v + 1] - p[v], p[v + 2] - p[v]);
                    if (normal.y < 0f) normal = -normal;
                    hit = new Hit
                    {
                        Piece = piece,
                        Triangle = t,
                        Point = world,
                        Local = localPoint,
                        Normal = piece.Transform.TransformDirection(normal.normalized),
                        Weights = new Vector3(1f - u - w, u, w),
                        Distance = distance,
                    };
                    any = true;
                }
            }
            return any;
        }

        private static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out float u, out float v)
        {
            distance = u = v = 0f;
            Vector3 ab = b - a, ac = c - a;
            Vector3 p = Vector3.Cross(ray.direction, ac);
            float det = Vector3.Dot(ab, p);
            if (det > -1e-9f && det < 1e-9f) return false;
            float inverse = 1f / det;
            Vector3 s = ray.origin - a;
            u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, ab);
            v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(ac, q) * inverse;
            return distance > 0f;
        }

        /// <summary>The land straight below or above a point (in the world): which triangle, and the point on it.</summary>
        internal static bool GroundAt(Vector3 world, out Piece piece, out int triangle, out Vector3 ground)
        {
            foreach (Piece p in pieces)
            {
                if (p.Transform == null) continue;
                Vector3 local = p.Transform.InverseTransformPoint(world);
                if (local.x < p.Bounds.min.x || local.x > p.Bounds.max.x || local.z < p.Bounds.min.z || local.z > p.Bounds.max.z) continue;
                if (!TriangleAt(p, new Vector2(local.x, local.z), out triangle, out float height)) continue;
                piece = p;
                ground = p.Transform.TransformPoint(new Vector3(local.x, height, local.z));
                return true;
            }
            piece = null;
            triangle = -1;
            ground = world;
            return false;
        }

        /// <summary>The land straight below or above a point (in the world), and which way it faces there.</summary>
        internal static bool GroundAt(Vector3 world, out Vector3 ground, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!GroundAt(world, out Piece piece, out int triangle, out ground)) return false;
            Vector3 a = piece.Points[triangle * 3], b = piece.Points[triangle * 3 + 1], c = piece.Points[triangle * 3 + 2];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.y < 0f) n = -n;
            normal = piece.Transform.TransformDirection(n.normalized);
            return true;
        }

        private static bool TriangleAt(Piece piece, Vector2 point, out int triangle, out float height)
        {
            triangle = -1;
            height = 0f;
            long key = Key(Mathf.FloorToInt(point.x / piece.CellSize), Mathf.FloorToInt(point.y / piece.CellSize));
            if (!piece.Cells.TryGetValue(key, out List<int> cell)) return false;
            foreach (int t in cell)
            {
                Vector3 a = piece.Points[t * 3], b = piece.Points[t * 3 + 1], c = piece.Points[t * 3 + 2];
                float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(det) < 1e-9f) continue;
                float w0 = ((b.z - c.z) * (point.x - c.x) + (c.x - b.x) * (point.y - c.z)) / det;
                float w1 = ((c.z - a.z) * (point.x - c.x) + (a.x - c.x) * (point.y - c.z)) / det;
                float w2 = 1f - w0 - w1;
                const float edge = -1e-4f;
                if (w0 < edge || w1 < edge || w2 < edge) continue;
                triangle = t;
                height = w0 * a.y + w1 * b.y + w2 * c.y;
                return true;
            }
            return false;
        }

        /// <summary>The triangles with their middles within a circle (from above) round a point in the world.</summary>
        internal static void TrianglesNear(Vector3 world, float radius, List<(Piece piece, int triangle)> found)
        {
            if (GroundAt(world, out Piece under, out int triangle, out _)) found.Add((under, triangle));
            if (radius <= 0f) return;
            float reach = radius * radius;
            foreach (Piece piece in pieces)
            {
                if (piece.Transform == null) continue;
                Vector3 local = piece.Transform.InverseTransformPoint(world);
                if (local.x < piece.Bounds.min.x - radius || local.x > piece.Bounds.max.x + radius ||
                    local.z < piece.Bounds.min.z - radius || local.z > piece.Bounds.max.z + radius) continue;
                var centre = new Vector2(local.x, local.z);
                Vector2[] middles = piece.Middles;
                for (int t = 0; t < middles.Length; t++)
                {
                    if ((middles[t] - centre).sqrMagnitude > reach) continue;
                    if (piece == under && t == triangle) continue;
                    found.Add((piece, t));
                }
            }
        }

        /// <summary>The colour of the land at a point: a path there, a coloured triangle, or the ground's own colour.</summary>
        internal static Color PickColour(Hit hit)
        {
            if (levels != null && PathPixel(new Vector2(hit.Local.x, hit.Local.z), out int index) && levels[0][index].a >= 128)
            {
                Color32 path = levels[0][index];
                return new Color32(path.r, path.g, path.b, 255);
            }
            int v = hit.Triangle * 3;
            Color32 painted = hit.Piece.Paint[v];
            if (painted.a > 0) return new Color32(painted.r, painted.g, painted.b, 255);
            Color ground = Shown(hit.Piece, v) * hit.Weights.x + Shown(hit.Piece, v + 1) * hit.Weights.y + Shown(hit.Piece, v + 2) * hit.Weights.z;
            ground.a = 1f;
            return ground;
        }

        // The ground's colour at a vertex as it shows: its own, with the colour painted by hand over it
        private static Color Shown(Piece piece, int v)
        {
            Color own = piece.Ground[v];
            if (piece.GroundPaint == null || v >= piece.GroundPaint.Length || piece.GroundPaint[v].a == 0) return own;
            Color32 p = piece.GroundPaint[v];
            float a = p.a / 255f;
            return new Color(own.r * (1f - a) + p.r / 255f, own.g * (1f - a) + p.g / 255f, own.b * (1f - a) + p.b / 255f, 1f);
        }

        // ================================================================ Paths

        private static Texture2D paths;
        private static Color32[][] levels;          // the paths texture's pixels, each mip level (kept here while painting)
        private static int[] levelWidth, levelHeight;
        private static Rect pathArea;               // where it lies over the land (x, z, from above)
        private static RectInt dirty;               // painted since last sent to the texture (in level 0's pixels)
        private static bool dirtyAny;
        private static bool pathsUnsaved;

        internal static bool PathsReady => levels != null && paths != null;
        internal static bool PathsExist => AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath) != null;

        /// <summary>
        /// Gets the paths texture ready to paint (making it if there isn't one yet, resizing it if the path detail in Land
        /// Settings changed). Returns null if it's ready, or what's wrong.
        /// </summary>
        internal static string LoadPaths()
        {
            if (PathsReady) return null;
            LandSettings settings = LandBuilder.GetSettings();
            var material = AssetDatabase.LoadAssetAtPath<Material>(LandBuilder.MaterialPath);
            if (material == null) return "Land.mat is missing. Rebuild the land (Tools > Paint Land > Rebuild Land) first.";
            if (settings.pathArea.width <= 0f || settings.pathArea.height <= 0f)
            {
                if (pieces.Count == 0) return "Start painting on the land first.";
                Bounds all = pieces[0].Bounds;
                foreach (Piece piece in pieces) all.Encapsulate(piece.Bounds);
                settings.pathArea = new Rect(all.min.x, all.min.z, all.size.x, all.size.z);
                EditorUtility.SetDirty(settings);
            }
            PreparePaths(settings, material, true);
            paths = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
            if (paths == null) return "Couldn't make Land Paths.png.";
            if (!paths.isReadable)
            {
                AssetDatabase.ImportAsset(PathsPath, ImportAssetOptions.ForceUpdate);
                paths = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
                if (paths == null || !paths.isReadable) return "Land Paths.png needs Read/Write turned on in its import settings.";
            }
            pathArea = settings.pathArea;
            ReadLevels();
            return null;
        }

        /// <summary>After the land is rebuilt: the paths keep their place, and the material keeps showing them.</summary>
        internal static void AfterRebuild(LandSettings settings, Material material, Rect area)
        {
            if (settings.pathArea.width <= 0f || settings.pathArea.height <= 0f)
            {
                settings.pathArea = area;
                EditorUtility.SetDirty(settings);
            }
            PreparePaths(settings, material, false);
        }

        /// <summary>Makes the paths texture match the path detail in Land Settings (resizing what's painted).</summary>
        internal static void ResizePaths()
        {
            SaveNow();
            levels = null;
            paths = null;
            ClearHistory();
            var material = AssetDatabase.LoadAssetAtPath<Material>(LandBuilder.MaterialPath);
            if (material != null) PreparePaths(LandBuilder.GetSettings(), material, false);
        }

        // Makes (or resizes) Land Paths.png as Land Settings asks, and has the material lay it over the land
        private static void PreparePaths(LandSettings settings, Material material, bool make)
        {
            Rect area = settings.pathArea;
            if (area.width <= 0f || area.height <= 0f) return;
            float detail = Mathf.Clamp(settings.pathDetail, 0.25f, 4f);
            int width = Mathf.Clamp(Mathf.CeilToInt(area.width / detail), 16, MaxPathPixels);
            int height = Mathf.Clamp(Mathf.CeilToInt(area.height / detail), 16, MaxPathPixels);

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
            if (existing == null && !make) return;
            if (existing == null)
            {
                WritePaths(new Color32[width * height], width, height);
            }
            else if (existing.width != width || existing.height != height)
            {
                if (!existing.isReadable) AssetDatabase.ImportAsset(PathsPath, ImportAssetOptions.ForceUpdate);
                existing = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
                Color32[] resized = existing != null && existing.isReadable
                    ? Resample(existing.GetPixels32(), existing.width, existing.height, width, height)
                    : new Color32[width * height];
                WritePaths(resized, width, height);
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
            if (texture == null) return;
            material.SetTexture("_PathMap", texture);
            material.SetVector("_PathRect", new Vector4(area.x, area.y, 1f / area.width, 1f / area.height));
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }

        private static void WritePaths(Color32[] pixels, int width, int height)
        {
            LandBuilder.EnsureFolder(LandBuilder.Folder);
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
            File.WriteAllBytes(FullPath(PathsPath), png);
            AssetDatabase.ImportAsset(PathsPath, ImportAssetOptions.ForceUpdate);
        }

        private static string FullPath(string assetPath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);

        private static Color32[] Resample(Color32[] source, int sourceWidth, int sourceHeight, int width, int height)
        {
            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float sy = Mathf.Clamp((y + 0.5f) / height * sourceHeight - 0.5f, 0f, sourceHeight - 1);
                int y0 = Mathf.FloorToInt(sy), y1 = Mathf.Min(y0 + 1, sourceHeight - 1);
                float fy = sy - y0;
                for (int x = 0; x < width; x++)
                {
                    float sx = Mathf.Clamp((x + 0.5f) / width * sourceWidth - 0.5f, 0f, sourceWidth - 1);
                    int x0 = Mathf.FloorToInt(sx), x1 = Mathf.Min(x0 + 1, sourceWidth - 1);
                    float fx = sx - x0;
                    Color32 back = Color32.Lerp(source[y0 * sourceWidth + x0], source[y0 * sourceWidth + x1], fx);
                    Color32 front = Color32.Lerp(source[y1 * sourceWidth + x0], source[y1 * sourceWidth + x1], fx);
                    result[y * width + x] = Color32.Lerp(back, front, fy);
                }
            }
            return result;
        }

        private static void ReadLevels()
        {
            int count = Mathf.Max(1, paths.mipmapCount);
            levels = new Color32[count][];
            levelWidth = new int[count];
            levelHeight = new int[count];
            for (int level = 0; level < count; level++)
            {
                levels[level] = paths.GetPixels32(level);
                levelWidth[level] = Mathf.Max(1, paths.width >> level);
                levelHeight[level] = Mathf.Max(1, paths.height >> level);
            }
            dirtyAny = false;
        }

        /// <summary>When Land Paths.png is (re)imported: carry on with what's painted, or pick up changes made to the file.</summary>
        internal static void AfterPathsImported()
        {
            if (levels == null) return;
            paths = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
            if (paths == null || !paths.isReadable)
            {
                levels = null;
                return;
            }
            bool same = paths.width == levelWidth[0] && paths.height == levelHeight[0] && Mathf.Max(1, paths.mipmapCount) == levels.Length;
            if (!same)
            {
                ReadLevels();
                ClearHistory();
            }
            else if (pathsUnsaved)
            {
                // Painted since it was saved: put that back
                for (int level = 0; level < levels.Length; level++) paths.SetPixels32(levels[level], level);
                paths.Apply(false);
            }
            else if (Differs(paths.GetPixels32(0), levels[0]))
            {
                // Changed in a paint program, say
                ReadLevels();
                ClearHistory();
            }
        }

        private static bool Differs(Color32[] file, Color32[] painted)
        {
            if (file.Length != painted.Length) return true;
            for (int i = 0; i < file.Length; i++)
            {
                Color32 a = file[i], b = painted[i];
                if (a.a != b.a) return true;
                if (a.a > 0 && (a.r != b.r || a.g != b.g || a.b != b.b)) return true;
            }
            return false;
        }

        // The pixel of the paths texture at a point on the land (x, z)
        private static bool PathPixel(Vector2 point, out int index)
        {
            index = -1;
            int w = levelWidth[0], h = levelHeight[0];
            int x = Mathf.FloorToInt((point.x - pathArea.x) / pathArea.width * w);
            int y = Mathf.FloorToInt((point.y - pathArea.y) / pathArea.height * h);
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            index = y * w + x;
            return true;
        }

        // ---------------------------------------------------------------- Painting paths

        private static readonly Dictionary<int, Color32> strokeBefore = new Dictionary<int, Color32>();
        private static readonly Dictionary<int, float> strokeAmount = new Dictionary<int, float>();
        private static RectInt strokeRect;
        private static bool strokeHasRect;
        private static bool strokeErases;

        /// <summary>
        /// Paints a round dab of path (or rubs it out) at a point on the land (x, z), its edge soft over a pixel or two so the
        /// Land material can draw it crisp and smooth. Within a stroke, dabs don't build up on each other.
        /// </summary>
        internal static void StampPath(Vector2 centre, float radius, Color32 colour, bool erase)
        {
            if (levels == null) return;
            strokeErases = erase;
            int w = levelWidth[0], h = levelHeight[0];
            float texelX = pathArea.width / w, texelZ = pathArea.height / h;
            float texel = Mathf.Max(texelX, texelZ);
            float ramp = Mathf.Max(2f * texel, radius * 0.3f);
            float reach = radius + ramp * 0.5f + 2f * texel;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - reach - pathArea.x) / texelX));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt((centre.x + reach - pathArea.x) / texelX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((centre.y - reach - pathArea.y) / texelZ));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt((centre.y + reach - pathArea.y) / texelZ));
            if (x0 > x1 || y0 > y1) return;

            Color32[] pixels = levels[0];
            var bleed = new Color32(colour.r, colour.g, colour.b, 0);
            for (int y = y0; y <= y1; y++)
            {
                float dz = pathArea.y + (y + 0.5f) * texelZ - centre.y;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = pathArea.x + (x + 0.5f) * texelX - centre.x;
                    float amount = Mathf.Clamp01(0.5f - (Mathf.Sqrt(dx * dx + dz * dz) - radius) / ramp);
                    int i = y * w + x;
                    if (amount <= 0f)
                    {
                        // Just outside: give the see-through pixels the path's colour, so its edge doesn't blend toward black
                        if (!erase && pixels[i].a == 0 && !strokeBefore.ContainsKey(i)) pixels[i] = bleed;
                        continue;
                    }
                    if (strokeAmount.TryGetValue(i, out float had) && had >= amount) continue;
                    if (!strokeBefore.TryGetValue(i, out Color32 before))
                    {
                        before = pixels[i];
                        strokeBefore[i] = before;
                    }
                    strokeAmount[i] = amount;
                    pixels[i] = erase ? RubOut(before, amount) : Over(before, colour, amount);
                }
            }
            var rect = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            strokeRect = strokeHasRect ? Union(strokeRect, rect) : rect;
            strokeHasRect = true;
            MarkDirty(rect);
        }

        private static Color32 Over(Color32 under, Color32 colour, float amount)
        {
            float alpha = amount + under.a / 255f * (1f - amount);
            bool take = amount >= 0.5f || under.a < 128;
            return new Color32(take ? colour.r : under.r, take ? colour.g : under.g, take ? colour.b : under.b,
                               (byte)Mathf.RoundToInt(alpha * 255f));
        }

        private static Color32 RubOut(Color32 under, float amount)
        {
            return new Color32(under.r, under.g, under.b, (byte)Mathf.RoundToInt(under.a * (1f - amount)));
        }

        private static RectInt Union(RectInt a, RectInt b)
        {
            int xMin = Mathf.Min(a.xMin, b.xMin), yMin = Mathf.Min(a.yMin, b.yMin);
            int xMax = Mathf.Max(a.xMax, b.xMax), yMax = Mathf.Max(a.yMax, b.yMax);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static void MarkDirty(RectInt rect)
        {
            dirty = dirtyAny ? Union(dirty, rect) : rect;
            dirtyAny = true;
        }

        // Sends what's been painted to the texture: the smaller mip levels worked out again for the part painted (each
        // pixel's colour weighted by how much path is in it, so paths don't darken in the distance), then uploaded
        private static void UploadPaths()
        {
            if (!dirtyAny || levels == null) return;
            dirtyAny = false;
            if (paths == null) paths = AssetDatabase.LoadAssetAtPath<Texture2D>(PathsPath);
            if (paths == null) return;
            RectInt rect = dirty;
            for (int level = 0; level < levels.Length; level++)
            {
                int w = levelWidth[level], h = levelHeight[level];
                int x0 = Mathf.Clamp(rect.xMin >> level, 0, w - 1), x1 = Mathf.Clamp((rect.xMax - 1) >> level, 0, w - 1);
                int y0 = Mathf.Clamp(rect.yMin >> level, 0, h - 1), y1 = Mathf.Clamp((rect.yMax - 1) >> level, 0, h - 1);
                if (level > 0) Shrink(level, x0, x1, y0, y1);
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                var block = new Color32[bw * bh];
                Color32[] pixels = levels[level];
                for (int y = 0; y < bh; y++) System.Array.Copy(pixels, (y0 + y) * w + x0, block, y * bw, bw);
                paths.SetPixels32(x0, y0, bw, bh, block, level);
            }
            paths.Apply(false);

            // No grass on the paths
            float texelX = pathArea.width / levelWidth[0], texelZ = pathArea.height / levelHeight[0];
            GrassField.AreaChanged(new Rect(pathArea.x + rect.xMin * texelX, pathArea.y + rect.yMin * texelZ, rect.width * texelX, rect.height * texelZ));
        }

        private static void Shrink(int level, int x0, int x1, int y0, int y1)
        {
            Color32[] below = levels[level - 1], here = levels[level];
            int bw = levelWidth[level - 1], bh = levelHeight[level - 1], w = levelWidth[level];
            for (int y = y0; y <= y1; y++)
            {
                int sy0 = Mathf.Min(y * 2, bh - 1), sy1 = Mathf.Min(y * 2 + 1, bh - 1);
                for (int x = x0; x <= x1; x++)
                {
                    int sx0 = Mathf.Min(x * 2, bw - 1), sx1 = Mathf.Min(x * 2 + 1, bw - 1);
                    Color32 a = below[sy0 * bw + sx0], b = below[sy0 * bw + sx1], c = below[sy1 * bw + sx0], d = below[sy1 * bw + sx1];
                    int weight = a.a + b.a + c.a + d.a;
                    int r, g, bl;
                    if (weight > 0)
                    {
                        r = (a.r * a.a + b.r * b.a + c.r * c.a + d.r * d.a) / weight;
                        g = (a.g * a.a + b.g * b.a + c.g * c.a + d.g * d.a) / weight;
                        bl = (a.b * a.a + b.b * b.a + c.b * c.a + d.b * d.a) / weight;
                    }
                    else
                    {
                        r = (a.r + b.r + c.r + d.r) / 4;
                        g = (a.g + b.g + c.g + d.g) / 4;
                        bl = (a.b + b.b + c.b + d.b) / 4;
                    }
                    here[y * w + x] = new Color32((byte)r, (byte)g, (byte)bl, (byte)((weight + 2) / 4));
                }
            }
        }

        // ---------------------------------------------------------------- Colouring triangles

        private static readonly Dictionary<(Piece, int), Color32> triangleBefore = new Dictionary<(Piece, int), Color32>();

        /// <summary>Colours in a whole triangle (a colour with alpha 0 takes its colour off).</summary>
        internal static void PaintTriangle(Piece piece, int triangle, Color32 colour)
        {
            int v = triangle * 3;
            Color32 now = piece.Paint[v];
            if (colour.a == 0 ? now.a == 0 : Same(now, colour)) return;
            if (colour.a == 0) colour = default;
            var key = (piece, triangle);
            if (!triangleBefore.ContainsKey(key)) triangleBefore[key] = now;
            piece.Paint[v] = piece.Paint[v + 1] = piece.Paint[v + 2] = colour;
            piece.Changed = true;
            Touch(piece, triangle);
        }

        // Where triangles were coloured in since the grass last heard (x, z in the land's space)
        private static Rect touched;
        private static bool anyTouched;

        private static void Touch(Piece piece, int triangle)
        {
            if (triangle < 0 || triangle >= piece.Middles.Length) return;
            Vector2 middle = piece.Middles[triangle];
            float reach = piece.CellSize;
            var rect = new Rect(middle.x - reach, middle.y - reach, reach * 2f, reach * 2f);
            if (!anyTouched) touched = rect;
            else touched = Rect.MinMaxRect(Mathf.Min(touched.xMin, rect.xMin), Mathf.Min(touched.yMin, rect.yMin),
                                           Mathf.Max(touched.xMax, rect.xMax), Mathf.Max(touched.yMax, rect.yMax));
            anyTouched = true;
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static readonly Dictionary<(Piece, int), Color32> triangleTextureBefore = new Dictionary<(Piece, int), Color32>();

        /// <summary>Paints a texture (1 to 8; 0 takes it off) on a whole triangle.</summary>
        internal static void PaintTriangleTexture(Piece piece, int triangle, int layer)
        {
            if (piece.Textures == null || !LandBuilder.CanTexture(piece.Mesh)) return;
            int v = triangle * 3;
            if (v + 2 >= piece.Textures.Length) return;
            Color32 now = piece.Textures[v];
            Color32 value = layer > 0 ? new Color32((byte)layer, 255, 0, 0) : default;
            if (Same(now, value)) return;
            var key = (piece, triangle);
            if (!triangleTextureBefore.ContainsKey(key)) triangleTextureBefore[key] = now;
            piece.Textures[v] = piece.Textures[v + 1] = piece.Textures[v + 2] = value;
            piece.TexturesChanged = true;
            Touch(piece, triangle);
        }

        /// <summary>Which texture (1 to 8) is painted on a whole triangle (0 = none).</summary>
        internal static int TriangleTexture(Piece piece, int triangle)
        {
            int v = triangle * 3;
            if (piece.Textures == null || v >= piece.Textures.Length) return 0;
            Color32 t = piece.Textures[v];
            return t.g > 0 ? t.r : 0;
        }

        private static readonly HashSet<Mesh> meshesUnsaved = new HashSet<Mesh>();

        /// <summary>A piece's mesh was changed (by LandShape, say): it's saved with the rest.</summary>
        internal static void MarkUnsaved(Mesh mesh)
        {
            if (mesh != null) meshesUnsaved.Add(mesh);
            ScheduleSave();
        }

        private static void UploadTriangles()
        {
            const MeshUpdateFlags quick = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds |
                                          MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            foreach (Piece piece in pieceOf.Values)
            {
                if ((!piece.Changed && !piece.TexturesChanged) || piece.Mesh == null) continue;
                if (piece.Changed) piece.Mesh.SetVertexBufferData(piece.Paint, 0, 0, piece.Paint.Length, LandBuilder.PaintStream, quick);
                if (piece.TexturesChanged && LandBuilder.CanTexture(piece.Mesh))
                {
                    piece.Mesh.SetVertexBufferData(piece.Textures, 0, 0, piece.Textures.Length, LandBuilder.TextureStream, quick);
                }
                piece.Changed = piece.TexturesChanged = false;
                meshesUnsaved.Add(piece.Mesh);
                if (anyTouched) GrassField.PaintChanged(piece.Mesh, touched);   // (the grass takes on the colours, and grows by the textures)
            }
            anyTouched = false;
        }

        // ---------------------------------------------------------------- Strokes

        /// <summary>Shows what's been painted (call after each lot of dabs).</summary>
        internal static void Flush()
        {
            UploadPaths();
            UploadTriangles();
        }

        /// <summary>True while a stroke is being painted (nothing's saved until it ends).</summary>
        internal static bool Busy;

        /// <summary>Finishes the stroke being painted, making it one step Ctrl+Z can undo.</summary>
        internal static void EndStroke()
        {
            Busy = false;
            Flush();
            strokeHasRect = false;
            if (strokeBefore.Count > 0)
            {
                var step = new PathStep(strokeRect);
                foreach (KeyValuePair<int, Color32> pixel in strokeBefore)
                {
                    step.Indices.Add(pixel.Key);
                    step.Before.Add(pixel.Value);
                    step.After.Add(levels[0][pixel.Key]);
                }
                strokeBefore.Clear();
                strokeAmount.Clear();
                pathsUnsaved = true;
                Commit(step, strokeErases ? "Erase Path" : "Paint Path");
            }
            if (triangleBefore.Count > 0)
            {
                var step = new TriangleStep();
                bool erased = true;
                foreach (KeyValuePair<(Piece, int), Color32> entry in triangleBefore)
                {
                    (Piece piece, int triangle) = entry.Key;
                    Color32 after = piece.Paint[triangle * 3];
                    if (after.a > 0) erased = false;
                    step.Meshes.Add(piece.Mesh);
                    step.Triangles.Add(triangle);
                    step.Before.Add(entry.Value);
                    step.After.Add(after);
                    meshesUnsaved.Add(piece.Mesh);
                }
                triangleBefore.Clear();
                Commit(step, erased ? "Erase Triangle Colours" : "Colour Triangles");
            }
            if (triangleTextureBefore.Count > 0)
            {
                var step = new TriangleTextureStep();
                bool erased = true;
                foreach (KeyValuePair<(Piece, int), Color32> entry in triangleTextureBefore)
                {
                    (Piece piece, int triangle) = entry.Key;
                    Color32 after = piece.Textures[triangle * 3];
                    if (after.g > 0) erased = false;
                    step.Meshes.Add(piece.Mesh);
                    step.Triangles.Add(triangle);
                    step.Before.Add(entry.Value);
                    step.After.Add(after);
                    meshesUnsaved.Add(piece.Mesh);
                }
                triangleTextureBefore.Clear();
                Commit(step, erased ? "Erase Triangle Textures" : "Texture Triangles");
            }
            CloseUndoGroup();   // (a stroke that only cleared things, or didn't change anything)
        }

        /// <summary>Ctrl+Z was pressed in the middle of a stroke: the other painters put back what it changed.</summary>
        internal static event System.Action Cancelling;

        // Puts back what the stroke being painted changed, without keeping it (Ctrl+Z pressed in the middle of one)
        private static void CancelStroke()
        {
            Busy = false;
            Cancelling?.Invoke();
            if (strokeBefore.Count > 0 && levels != null)
            {
                foreach (KeyValuePair<int, Color32> pixel in strokeBefore) levels[0][pixel.Key] = pixel.Value;
                MarkDirty(strokeRect);
            }
            foreach (KeyValuePair<(Piece, int), Color32> entry in triangleBefore)
            {
                (Piece piece, int triangle) = entry.Key;
                int v = triangle * 3;
                piece.Paint[v] = piece.Paint[v + 1] = piece.Paint[v + 2] = entry.Value;
                piece.Changed = true;
                Touch(piece, triangle);
            }
            foreach (KeyValuePair<(Piece, int), Color32> entry in triangleTextureBefore)
            {
                (Piece piece, int triangle) = entry.Key;
                int v = triangle * 3;
                piece.Textures[v] = piece.Textures[v + 1] = piece.Textures[v + 2] = entry.Value;
                piece.TexturesChanged = true;
                Touch(piece, triangle);
            }
            strokeBefore.Clear();
            strokeAmount.Clear();
            triangleBefore.Clear();
            triangleTextureBefore.Clear();
            strokeHasRect = false;
            openGroup = -1;
            Flush();
        }

        /// <summary>Takes every path off the land (one step Ctrl+Z can undo).</summary>
        internal static void ClearPaths()
        {
            if (LoadPaths() != null) return;
            EndStroke();
            Color32[] pixels = levels[0];
            var step = new PathStep(new RectInt(0, 0, levelWidth[0], levelHeight[0]));
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a == 0) continue;
                step.Indices.Add(i);
                step.Before.Add(pixels[i]);
                step.After.Add(new Color32(pixels[i].r, pixels[i].g, pixels[i].b, 0));
            }
            if (step.Indices.Count == 0) return;
            step.Redo();
            Commit(step, "Clear All Paths");
        }

        /// <summary>Takes the colour off every coloured-in triangle (one step Ctrl+Z can undo).</summary>
        internal static void ClearTriangles()
        {
            EndStroke();
            var step = new TriangleStep();
            foreach (Piece piece in pieces)
            {
                for (int t = 0; t < piece.Middles.Length; t++)
                {
                    Color32 colour = piece.Paint[t * 3];
                    if (colour.a == 0) continue;
                    step.Meshes.Add(piece.Mesh);
                    step.Triangles.Add(t);
                    step.Before.Add(colour);
                    step.After.Add(default);
                }
            }
            if (step.Triangles.Count == 0) return;
            step.Redo();
            Commit(step, "Clear Triangle Colours");
        }

        /// <summary>Takes the texture off every textured triangle (one step Ctrl+Z can undo).</summary>
        internal static void ClearTriangleTextures()
        {
            EndStroke();
            var step = new TriangleTextureStep();
            foreach (Piece piece in pieces)
            {
                if (piece.Textures == null) continue;
                for (int t = 0; t < piece.Middles.Length; t++)
                {
                    Color32 texture = piece.Textures[t * 3];
                    if (texture.g == 0 && texture.r == 0) continue;
                    step.Meshes.Add(piece.Mesh);
                    step.Triangles.Add(t);
                    step.Before.Add(texture);
                    step.After.Add(default);
                }
            }
            if (step.Triangles.Count == 0) return;
            step.Redo();
            Commit(step, "Clear Triangle Textures");
        }

        // ================================================================ Undo

        // Each stroke is a step here; Unity's undo history holds a count of them (in Land Settings), so Ctrl+Z / Ctrl+Y
        // change the count, and the steps between the old count and the new one are undone or redone. The steps are kept
        // in memory only, so strokes from before scripts were last recompiled (or Play was pressed) can't be undone.
        internal abstract class Step
        {
            public abstract void Undo();
            public abstract void Redo();
        }

        private sealed class PathStep : Step
        {
            public readonly List<int> Indices = new List<int>();
            public readonly List<Color32> Before = new List<Color32>();
            public readonly List<Color32> After = new List<Color32>();
            private readonly RectInt rect;

            public PathStep(RectInt rect) => this.rect = rect;

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<Color32> colours)
            {
                if (levels == null && LoadPaths() != null) return;
                if (levels == null) return;
                Color32[] pixels = levels[0];
                for (int k = 0; k < Indices.Count; k++)
                {
                    if (Indices[k] < pixels.Length) pixels[Indices[k]] = colours[k];
                }
                MarkDirty(rect);
                UploadPaths();
                pathsUnsaved = true;
            }
        }

        private sealed class TriangleStep : Step
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<Color32> Before = new List<Color32>();
            public readonly List<Color32> After = new List<Color32>();

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<Color32> colours)
            {
                for (int k = 0; k < Triangles.Count; k++)
                {
                    Piece piece = PieceFor(Meshes[k]);
                    if (piece == null) continue;
                    int v = Triangles[k] * 3;
                    if (v + 2 >= piece.Paint.Length) continue;
                    piece.Paint[v] = piece.Paint[v + 1] = piece.Paint[v + 2] = colours[k];
                    piece.Changed = true;
                    Touch(piece, Triangles[k]);
                }
                UploadTriangles();
            }
        }

        /// <summary>Several steps kept as one (a stroke that changed more than one kind of thing): undone last first.</summary>
        internal sealed class MultiStep : Step
        {
            private readonly List<Step> parts = new List<Step>();

            public MultiStep(params Step[] steps)
            {
                foreach (Step step in steps)
                {
                    if (step != null) parts.Add(step);
                }
            }

            public bool Empty => parts.Count == 0;

            public override void Undo()
            {
                for (int i = parts.Count - 1; i >= 0; i--) parts[i].Undo();
            }

            public override void Redo()
            {
                foreach (Step step in parts) step.Redo();
            }
        }

        private sealed class TriangleTextureStep : Step
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<Color32> Before = new List<Color32>();
            public readonly List<Color32> After = new List<Color32>();

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<Color32> textures)
            {
                for (int k = 0; k < Triangles.Count; k++)
                {
                    Piece piece = PieceFor(Meshes[k]);
                    if (piece == null || piece.Textures == null) continue;
                    int v = Triangles[k] * 3;
                    if (v + 2 >= piece.Textures.Length) continue;
                    piece.Textures[v] = piece.Textures[v + 1] = piece.Textures[v + 2] = textures[k];
                    piece.TexturesChanged = true;
                    Touch(piece, Triangles[k]);
                }
                UploadTriangles();
            }
        }

        private static readonly List<Step> steps = new List<Step>();
        private static int firstStep;       // the count the first of the steps here would take it to, minus one
        private static int applied;         // the count the land is at now
        private static bool historyReady;

        private static void ReadyHistory(LandSettings settings)
        {
            if (historyReady) return;
            historyReady = true;
            applied = firstStep = settings.paintStep;
            steps.Clear();
        }

        private static void ClearHistory()
        {
            steps.Clear();
            historyReady = false;
        }

        private static int openGroup = -1;

        /// <summary>
        /// Starts one undo step for a stroke that also changes other things as it goes (a path deleting the plants under it),
        /// so Ctrl+Z undoes all of it at once. The stroke's end closes it.
        /// </summary>
        internal static void OpenUndoGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            openGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
        }

        internal static void CloseUndoGroup()
        {
            if (openGroup < 0) return;
            Undo.CollapseUndoOperations(openGroup);
            openGroup = -1;
        }

        internal static void Commit(Step step, string name)
        {
            LandSettings settings = LandBuilder.GetSettings();
            ReadyHistory(settings);
            // Anything undone before this is gone (as with any undo)
            if (applied < firstStep)
            {
                steps.Clear();
                firstStep = applied;
            }
            int keep = applied - firstStep;
            if (steps.Count > keep) steps.RemoveRange(keep, steps.Count - keep);

            if (openGroup < 0) Undo.IncrementCurrentGroup();
            Undo.RecordObject(settings, name);
            settings.paintStep = applied + 1;
            Undo.FlushUndoRecordObjects();
            Undo.SetCurrentGroupName(name);
            CloseUndoGroup();
            EditorUtility.SetDirty(settings);
            steps.Add(step);
            applied = settings.paintStep;
            ScheduleSave();
        }

        private static void OnUndoRedo()
        {
            if (!historyReady) return;
            var settings = AssetDatabase.LoadAssetAtPath<LandSettings>(LandBuilder.SettingsPath);
            if (settings == null) return;
            int target = settings.paintStep;
            if (target == applied) return;
            CancelStroke();
            while (applied > target)
            {
                int index = applied - 1 - firstStep;
                if (index >= 0 && index < steps.Count) steps[index].Undo();
                applied--;
            }
            while (applied < target)
            {
                int index = applied - firstStep;
                if (index >= 0 && index < steps.Count) steps[index].Redo();
                applied++;
            }
            ScheduleSave();
            SceneView.RepaintAll();
        }

        // ================================================================ Saving

        private static double saveAt = -1;

        internal static void ScheduleSave() => saveAt = EditorApplication.timeSinceStartup + SaveDelay;

        /// <summary>When painting is saved (the Grass Field's mask saves then too).</summary>
        internal static event System.Action Saving;

        private static void SaveWhenIdle()
        {
            if (saveAt < 0 || Busy || EditorApplication.timeSinceStartup < saveAt) return;
            SaveNow();
        }

        /// <summary>Saves what's been painted: the paths to Land Paths.png, the coloured triangles to the land's pieces.</summary>
        internal static void SaveNow()
        {
            saveAt = -1;
            Saving?.Invoke();
            if (pathsUnsaved && levels != null)
            {
                pathsUnsaved = false;
                try
                {
                    byte[] png = ImageConversion.EncodeArrayToPNG(levels[0], GraphicsFormat.R8G8B8A8_UNorm, (uint)levelWidth[0], (uint)levelHeight[0]);
                    File.WriteAllBytes(FullPath(PathsPath), png);
                }
                catch (System.Exception e)
                {
                    pathsUnsaved = true;
                    Debug.LogWarning("Paint Land: couldn't save the paths (" + e.Message + "). It'll try again.");
                    ScheduleSave();
                }
            }
            if (meshesUnsaved.Count > 0)
            {
                foreach (Mesh mesh in meshesUnsaved)
                {
                    if (mesh == null) continue;
                    EditorUtility.SetDirty(mesh);
                    AssetDatabase.SaveAssetIfDirty(mesh);
                }
                meshesUnsaved.Clear();
            }
        }
    }

    /// <summary>
    /// Editor only. Keeps Land Paths.png set up the way painting needs it (readable, not compressed, sRGB, clamped, with
    /// see-through pixels taking their neighbours' colour), and lets the painter know when it's been reimported.
    /// </summary>
    internal sealed class LandPathsImport : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (assetPath != LandPaint.PathsPath) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.isReadable = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 4;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 8192;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string path in imported)
            {
                if (path != LandPaint.PathsPath) continue;
                EditorApplication.delayCall += LandPaint.AfterPathsImported;
                return;
            }
        }
    }
}
#endif
