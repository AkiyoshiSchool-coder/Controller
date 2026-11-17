#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Raising and lowering the land with Tools > Paint Land > Shape: the corners of the land's
    /// triangles are moved up or down (Raise, Lower, Smooth, Flatten, Ramp, Noise, Restore), the ground's smooth facing and
    /// (if Land Settings paints the colours) its colours worked out again as Rebuild Land would (soil on the new slopes...),
    /// the pieces' meshes and colliders updated, and the trees, rocks and plants standing there moved with the ground.
    /// How far each corner has been moved from the shape Rebuild Land makes is kept in Land Shape.bytes (next to Land
    /// Settings), so rebuilding the land puts the changes back on. Each stroke is a step for Ctrl+Z.
    /// The ground's colours painted by hand (Paint Land > Ground, the smooth brush: the colour at each corner, blended
    /// smoothly across the triangles) are kept there too, over the colours Rebuild Land gives it.
    /// </summary>
    [InitializeOnLoad]
    internal static class LandShape
    {
        internal const string ShapePath = LandBuilder.Folder + "/Land Shape.bytes";
        private const int FileMagic = 0x4C534850;   // "LSHP"
        private const float ThingReach = 2f;        // things standing this close to the ground (metres) move with it

        internal enum Tool { Raise, Lower, Smooth, Flatten, Ramp, Noise, Restore }

        /// <summary>How a brush changes the shape.</summary>
        internal struct Brush
        {
            public Tool Tool;
            public float Radius;        // metres
            public float Strength;      // 0 to 1
            public float Softness;      // 0 = hard edge, 1 = soft all the way in
            public float Target;        // Flatten: the height to flatten to (in the land's space)
            public float NoiseSize;     // Noise: how wide the bumps are (metres)
            public bool Recolour;       // work the ground's colours out again
        }

        static LandShape()
        {
            LandPaint.Saving += Save;
            LandPaint.Cancelling += Cancel;
        }

        // ================================================================ The land as a grid of corners

        private sealed class PieceGrid
        {
            public LandPaint.Piece Piece;
            public LandBuilder.LandVertex[] Vertices;
            public Color32[] GroundPaint;   // the colour painted on the ground at each vertex (times how much; alpha = how much)
            public int[] Corner;            // each vertex's corner of the grid
            public int CellX0, CellZ0;      // its first square, in the whole grid
            public int Columns, Rows;       // how many squares it has
            public int DirtyFrom = int.MaxValue, DirtyTo = -1;   // the vertices changed since they were sent to the mesh
            public float Low = float.MaxValue, High = float.MinValue;   // how low and high those went
            public bool Touched;            // changed in this stroke (its collider needs the new shape)
        }

        private static int generation = -1;
        private static List<PieceGrid> grids;
        private static int width, depth;                // corners across and along
        private static float originX, originZ, stepX, stepZ;
        private static float[] heights;                 // each corner's height now
        private static float[] offsets;                 // how far it's been moved from the shape Rebuild Land makes
        private static Color32[] colours;               // the ground's colour there (as shown)
        private static Color32[] baseColours;           // its colour before any painted by hand
        private static Color32[] paint;                 // the colour painted by hand there (alpha = how much)
        private static Vector3[] normals;               // which way the ground faces there (smoothly)
        private static float colourLow, colourHigh;     // the built land's lowest and highest (its colours go by them)
        private static bool unsaved;

        /// <summary>How far apart the corners are (metres), once ready.</summary>
        internal static float Spacing => grids != null ? Mathf.Max(stepX, stepZ) : 0f;

        /// <summary>Reads the land's pieces as a grid of corners, if it hasn't already. Null if ready, or what's wrong.</summary>
        internal static string Prepare()
        {
            if (grids != null && generation == LandPaint.Generation && LandPaint.Ready) return null;
            if (!LandPaint.Ready)
            {
                string trouble = LandPaint.Load();
                if (trouble != null) return trouble;
            }
            if (unsaved) Save();
            grids = null;
            IReadOnlyList<LandPaint.Piece> pieces = LandPaint.Pieces;
            if (pieces.Count == 0) return "The Land has no pieces. Rebuild it with Tools > Paint Land > Rebuild Land.";

            // The squares' size (from the first square: its back-left corner, front-left corner, and one to the right)
            Vector3[] first = pieces[0].Points;
            if (first.Length < 6) return "The Land's pieces aren't the shape Rebuild Land makes. Rebuild it first.";
            stepZ = first[1].z - first[0].z;
            stepX = first[2].x - first[0].x;
            if (stepX <= 1e-4f || stepZ <= 1e-4f) return "The Land's pieces aren't the shape Rebuild Land makes. Rebuild it first.";
            originX = float.MaxValue;
            originZ = float.MaxValue;
            foreach (LandPaint.Piece piece in pieces)
            {
                if (piece.Points.Length < 6) continue;
                originX = Mathf.Min(originX, piece.Points[0].x);
                originZ = Mathf.Min(originZ, piece.Points[0].z);
            }

            var read = new List<PieceGrid>();
            width = depth = 0;
            int stride = Marshal.SizeOf<LandBuilder.LandVertex>();
            foreach (LandPaint.Piece piece in pieces)
            {
                Mesh mesh = piece.Mesh;
                if (mesh == null || piece.Points.Length < 6 || piece.Points.Length % 6 != 0 || mesh.GetVertexBufferStride(0) != stride)
                {
                    return $"{(mesh != null ? mesh.name : "A piece")} isn't the shape Rebuild Land makes. Rebuild the land first.";
                }
                Vector3 a = piece.Points[0];
                float maxX = float.MinValue, maxZ = float.MinValue;
                foreach (Vector3 p in piece.Points)
                {
                    maxX = Mathf.Max(maxX, p.x);
                    maxZ = Mathf.Max(maxZ, p.z);
                }
                var grid = new PieceGrid
                {
                    Piece = piece,
                    CellX0 = Mathf.RoundToInt((a.x - originX) / stepX),
                    CellZ0 = Mathf.RoundToInt((a.z - originZ) / stepZ),
                    Columns = Mathf.RoundToInt((maxX - a.x) / stepX),
                    Rows = Mathf.RoundToInt((maxZ - a.z) / stepZ),
                };
                if (grid.Columns * grid.Rows * 6 != piece.Points.Length)
                {
                    return $"{mesh.name} isn't the shape Rebuild Land makes. Rebuild the land first.";
                }
                using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    grid.Vertices = data[0].GetVertexData<LandBuilder.LandVertex>(0).ToArray();
                    grid.GroundPaint = LandBuilder.CanPaintGround(mesh)
                        ? data[0].GetVertexData<Color32>(LandBuilder.GroundPaintStream).ToArray()
                        : new Color32[grid.Vertices.Length];
                }
                if (grid.Vertices.Length != piece.Points.Length) return $"{mesh.name} couldn't be read. Rebuild the land first.";
                width = Mathf.Max(width, grid.CellX0 + grid.Columns + 1);
                depth = Mathf.Max(depth, grid.CellZ0 + grid.Rows + 1);
                read.Add(grid);
            }

            heights = new float[width * depth];
            colours = new Color32[width * depth];
            normals = new Vector3[width * depth];
            var seen = new bool[width * depth];
            var meshPainted = new bool[width * depth];   // (the mesh has the hand-painted colour apart from the ground's own)
            foreach (PieceGrid grid in read)
            {
                grid.Corner = new int[grid.Vertices.Length];
                for (int v = 0; v < grid.Vertices.Length; v++)
                {
                    Vector3 p = grid.Vertices[v].Position;
                    int cx = Mathf.Clamp(Mathf.RoundToInt((p.x - originX) / stepX), 0, width - 1);
                    int cz = Mathf.Clamp(Mathf.RoundToInt((p.z - originZ) / stepZ), 0, depth - 1);
                    int i = cz * width + cx;
                    grid.Corner[v] = i;
                    heights[i] = p.y;
                    colours[i] = grid.Vertices[v].Colour;
                    normals[i] = grid.Vertices[v].Normal;
                    seen[i] = true;
                    if (grid.GroundPaint[v].a > 0) meshPainted[i] = true;
                }
            }
            for (int i = 0; i < seen.Length; i++)
            {
                if (!seen[i]) normals[i] = Vector3.up;
            }

            if (!ReadShape(width, depth, originX, originZ, stepX, stepZ, out offsets, out paint))
            {
                offsets = new float[width * depth];
                paint = new Color32[width * depth];
            }
            LandSettings settings = LandBuilder.GetSettings();
            colourLow = settings.colourLow;
            colourHigh = settings.colourHigh;
            if (colourHigh <= colourLow)
            {
                // (a land built before this kept them: from its shape as built)
                colourLow = float.MaxValue;
                colourHigh = float.MinValue;
                for (int i = 0; i < heights.Length; i++)
                {
                    if (!seen[i]) continue;
                    colourLow = Mathf.Min(colourLow, heights[i] - offsets[i]);
                    colourHigh = Mathf.Max(colourHigh, heights[i] - offsets[i]);
                }
            }

            // The colours under what's painted by hand: the mesh's own colours; or, where they still have the painted colour
            // mixed in (painted before it was kept apart), as Rebuild Land gives them (or worked back out)
            baseColours = (Color32[])colours.Clone();
            int fixX0 = int.MaxValue, fixZ0 = int.MaxValue, fixX1 = int.MinValue, fixZ1 = int.MinValue;
            for (int i = 0; i < paint.Length; i++)
            {
                if (paint[i].a == 0 || meshPainted[i]) continue;
                int cx = i % width, cz = i / width;
                if (settings.paintColours)
                {
                    baseColours[i] = LandBuilder.GroundColour(settings, heights, width, depth, cx, cz, stepX, stepZ, originX, originZ,
                                                              colourLow, colourHigh, LandBuilder.SoftHeight(heights, width, depth, cx, cz));
                }
                else
                {
                    float t = paint[i].a / 255f;
                    if (t >= 0.999f) continue;
                    Color painted = new Color32(paint[i].r, paint[i].g, paint[i].b, 255);
                    Color under = ((Color)colours[i] - painted * t) / (1f - t);
                    under.r = Mathf.Clamp01(under.r);
                    under.g = Mathf.Clamp01(under.g);
                    under.b = Mathf.Clamp01(under.b);
                    under.a = 1f;
                    baseColours[i] = under;
                }
                fixX0 = Mathf.Min(fixX0, cx);
                fixX1 = Mathf.Max(fixX1, cx);
                fixZ0 = Mathf.Min(fixZ0, cz);
                fixZ1 = Mathf.Max(fixZ1, cz);
            }
            for (int i = 0; i < colours.Length; i++) colours[i] = Composed(baseColours[i], paint[i]);
            grids = read;
            generation = LandPaint.Generation;

            // (painted before the painted colours were kept apart: they're put in their own stream now, so grass grows by
            // the ground's own colours)
            if (fixX1 >= fixX0)
            {
                WriteVertices(new RectInt(fixX0, fixZ0, fixX1 - fixX0 + 1, fixZ1 - fixZ0 + 1));
                Flush();
                foreach (PieceGrid grid in grids)
                {
                    if (!grid.Touched) continue;
                    grid.Touched = false;
                    LandPaint.MarkUnsaved(grid.Piece.Mesh);
                    GrassField.ShapeChanged(grid.Piece.Mesh, new Rect(originX, originZ, (width - 1) * stepX, (depth - 1) * stepZ));
                }
            }
            return null;
        }

        /// <summary>The height of the land (in its space) at a point (x, z), from the corners round it.</summary>
        internal static float HeightAt(Vector2 local)
        {
            if (grids == null) return 0f;
            float fx = Mathf.Clamp((local.x - originX) / stepX, 0f, width - 1), fz = Mathf.Clamp((local.y - originZ) / stepZ, 0f, depth - 1);
            int x0 = Mathf.Min(Mathf.FloorToInt(fx), width - 2), z0 = Mathf.Min(Mathf.FloorToInt(fz), depth - 2);
            x0 = Mathf.Max(x0, 0);
            z0 = Mathf.Max(z0, 0);
            int x1 = Mathf.Min(x0 + 1, width - 1), z1 = Mathf.Min(z0 + 1, depth - 1);
            float tx = fx - x0, tz = fz - z0;
            float back = Mathf.Lerp(heights[z0 * width + x0], heights[z0 * width + x1], tx);
            float front = Mathf.Lerp(heights[z1 * width + x0], heights[z1 * width + x1], tx);
            return Mathf.Lerp(back, front, tz);
        }

        // ================================================================ Strokes

        private struct Was
        {
            public float Height, Offset;
            public Color32 Colour, Base, Paint;
        }

        private static readonly Dictionary<int, Was> strokeBefore = new Dictionary<int, Was>();
        private static string strokeName = "Shape Land";
        private static double lastGrass;
        private static RectInt grassRect;
        private static bool grassPending;

        private sealed class Thing
        {
            public Transform Transform;
            public Vector3 Start;
            public float GroundStart;
            public bool Recorded;
        }

        private static readonly List<Thing> things = new List<Thing>();

        /// <summary>Starts a stroke (moving the trees, rocks and plants with the ground, if asked).</summary>
        internal static void BeginStroke(string name, bool moveThings)
        {
            strokeName = name;
            strokeBefore.Clear();
            groundReached.Clear();
            things.Clear();
            if (!moveThings || LandPaint.Land == null) return;
            foreach (GameObject root in LandPaint.Land.scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!LandBuilder.IsProp(t)) continue;
                    Vector3 p = t.position;
                    if (!LandPaint.GroundAt(p, out Vector3 ground, out _) || Mathf.Abs(p.y - ground.y) > ThingReach) continue;
                    things.Add(new Thing { Transform = t, Start = p, GroundStart = ground.y });
                }
            }
        }

        /// <summary>One dab of a brush at a point on the land (x, z in its space), for a moment (seconds) of holding it there.</summary>
        internal static void Dab(Vector2 centre, Brush brush, float seconds)
        {
            if (grids == null || brush.Tool == Tool.Ramp) return;
            float radius = Mathf.Max(brush.Radius, Mathf.Max(stepX, stepZ) * 0.75f);
            float strength = Mathf.Clamp01(brush.Strength);
            float rate = strength * Mathf.Max(radius, stepX) * 0.6f * seconds;      // metres up or down, in the middle
            float pull = Mathf.Clamp01(strength * seconds * 8f);                     // how far toward a height, in the middle
            RectInt corners = CornersNear(centre, radius);
            if (corners.width <= 0 || corners.height <= 0) return;

            var changes = new List<(int index, float height)>();
            for (int cz = corners.yMin; cz < corners.yMax; cz++)
            {
                for (int cx = corners.xMin; cx < corners.xMax; cx++)
                {
                    float dx = originX + cx * stepX - centre.x, dz = originZ + cz * stepZ - centre.y;
                    float f = Falloff(Mathf.Sqrt(dx * dx + dz * dz), radius, brush.Softness);
                    if (f <= 0f) continue;
                    int i = cz * width + cx;
                    float h = heights[i], now = h;
                    switch (brush.Tool)
                    {
                        case Tool.Raise:
                            now = h + rate * f;
                            break;
                        case Tool.Lower:
                            now = h - rate * f;
                            break;
                        case Tool.Smooth:
                            now = Mathf.Lerp(h, Average(cx, cz), Mathf.Clamp01(pull * 1.5f * f));
                            break;
                        case Tool.Flatten:
                            now = Mathf.Lerp(h, brush.Target, Mathf.Clamp01(pull * f));
                            break;
                        case Tool.Noise:
                        {
                            float size = Mathf.Max(brush.NoiseSize, stepX);
                            float wx = originX + cx * stepX, wz = originZ + cz * stepZ;
                            float noise = Mathf.PerlinNoise(wx / size + 311.7f, wz / size + 127.3f) * 2f - 1f;
                            now = h + noise * rate * f;
                            break;
                        }
                        case Tool.Restore:
                            now = Mathf.Lerp(h, h - offsets[i], Mathf.Clamp01(pull * f));
                            break;
                    }
                    if (Mathf.Abs(now - h) > 1e-5f) changes.Add((i, now));
                }
            }
            Apply(changes, brush.Recolour);
        }

        /// <summary>
        /// A ramp from one point on the land to another (x, z in its space): a straight slope between the heights there, as
        /// wide as the brush, blended into the ground round it.
        /// </summary>
        internal static void Ramp(Vector2 from, Vector2 to, Brush brush)
        {
            if (grids == null) return;
            float half = Mathf.Max(brush.Radius, Mathf.Max(stepX, stepZ) * 0.75f);
            float hFrom = HeightAt(from), hTo = HeightAt(to);
            Vector2 along = to - from;
            float length = along.magnitude;
            if (length < 0.01f) return;
            float strength = Mathf.Clamp01(brush.Strength <= 0f ? 1f : brush.Strength * 1.5f);
            Vector2 min = Vector2.Min(from, to) - Vector2.one * half, max = Vector2.Max(from, to) + Vector2.one * half;
            RectInt corners = CornersIn(min, max);
            var changes = new List<(int index, float height)>();
            for (int cz = corners.yMin; cz < corners.yMax; cz++)
            {
                for (int cx = corners.xMin; cx < corners.xMax; cx++)
                {
                    var p = new Vector2(originX + cx * stepX, originZ + cz * stepZ);
                    float t = Mathf.Clamp01(Vector2.Dot(p - from, along) / (length * length));
                    float distance = Vector2.Distance(p, from + along * t);
                    float f = Falloff(distance, half, brush.Softness);
                    if (f <= 0f) continue;
                    int i = cz * width + cx;
                    float now = Mathf.Lerp(heights[i], Mathf.Lerp(hFrom, hTo, t), f * strength);
                    if (Mathf.Abs(now - heights[i]) > 1e-5f) changes.Add((i, now));
                }
            }
            Apply(changes, brush.Recolour);
        }

        /// <summary>
        /// Shapes the land to hold painted water, round a point on the land (x, z in its space). The water's edge is drawn
        /// where it's painted (a smooth line), so the ground is shaped to meet it there:
        /// - corners in the water (waterAt: how much, 0 to 1, at a point on the land) go down below its level (level, in the
        ///   land's space): a little at the edge, deeper (down to depth metres) the further in they are;
        /// - corners just outside it, on the shore, are set just under its level (a flat shelf the edge is drawn across), so
        ///   the line where the water meets the land is where it's painted, not where the land's big triangles cross it.
        /// Only lowers in the water; on the shore, only ground near the water's level is moved (not hills or cliffs).
        /// </summary>
        internal static void HoldWater(Vector2 centre, float radius, float level, float depth, System.Func<Vector2, float> waterAt, bool recolour)
        {
            if (grids == null || waterAt == null) return;
            float ring = Mathf.Max(stepX, stepZ);
            RectInt corners = CornersNear(centre, radius + ring);
            if (corners.width <= 0 || corners.height <= 0) return;
            float down = Mathf.Max(depth, 0f);
            var changes = new List<(int index, float height)>();
            for (int cz = corners.yMin; cz < corners.yMax; cz++)
            {
                for (int cx = corners.xMin; cx < corners.xMax; cx++)
                {
                    var p = new Vector2(originX + cx * stepX, originZ + cz * stepZ);
                    if ((p - centre).sqrMagnitude > (radius + ring) * (radius + ring)) continue;
                    int i = cz * width + cx;
                    float h = heights[i];
                    if (waterAt(p) >= 0.5f)
                    {
                        // In the water: deeper the further from the shore
                        float deep = AllWater(p, ring, waterAt) ? (AllWater(p, ring * 2f, waterAt) ? 1f : 0.55f) : 0.2f;
                        float bed = level - 0.35f - down * deep;
                        if (h > bed + 1e-3f) changes.Add((i, bed));
                    }
                    else if (NearWater(p, waterAt))
                    {
                        // The shore: a shelf just under the water, so the painted edge shows (not hills or cliffs)
                        float shelf = level - 0.12f;
                        if (Mathf.Abs(h - shelf) > 1e-3f && h > level - 4f && h < level + 4f) changes.Add((i, shelf));
                    }
                }
            }
            Apply(changes, recolour);
        }

        /// <summary>
        /// Where there was water (wasWater) and isn't now (waterAt), away from the water that's left, round a point on the
        /// land: the hole dug for it is filled back up, to a shallow dip under the water's level (level, in the land's
        /// space) at most, and never higher than the land was before it was reshaped. Only raises.
        /// </summary>
        internal static void FillDry(Vector2 centre, float radius, float level, System.Func<Vector2, float> wasWater, System.Func<Vector2, float> waterAt, bool recolour)
        {
            if (grids == null || wasWater == null || waterAt == null) return;
            float ring = Mathf.Max(stepX, stepZ);
            RectInt corners = CornersNear(centre, radius + ring);
            if (corners.width <= 0 || corners.height <= 0) return;
            float dip = level - 0.3f;
            var changes = new List<(int index, float height)>();
            for (int cz = corners.yMin; cz < corners.yMax; cz++)
            {
                for (int cx = corners.xMin; cx < corners.xMax; cx++)
                {
                    var p = new Vector2(originX + cx * stepX, originZ + cz * stepZ);
                    if ((p - centre).sqrMagnitude > (radius + ring) * (radius + ring)) continue;
                    int i = cz * width + cx;
                    float h = heights[i];
                    float to = Mathf.Min(dip, h - offsets[i]);   // (no higher than it was made)
                    if (to <= h + 1e-3f) continue;
                    if (wasWater(p) <= 0.05f && !NearWater(p, wasWater)) continue;
                    if (waterAt(p) >= 0.5f || NearWater(p, waterAt)) continue;
                    changes.Add((i, to));
                }
            }
            Apply(changes, recolour);
        }

        // Is there water (painted) on any of the four squares of land round a corner? (looked for every third of a square)
        private static bool NearWater(Vector2 p, System.Func<Vector2, float> waterAt)
        {
            const int Steps = 3;
            for (int z = -Steps; z <= Steps; z++)
            {
                for (int x = -Steps; x <= Steps; x++)
                {
                    if (x == 0 && z == 0) continue;
                    if (waterAt(p + new Vector2(x * stepX / Steps, z * stepZ / Steps)) >= 0.5f) return true;
                }
            }
            return false;
        }

        // Is there water all round a point, a distance away?
        private static bool AllWater(Vector2 p, float distance, System.Func<Vector2, float> waterAt)
        {
            for (int k = 0; k < 8; k++)
            {
                float angle = k * Mathf.PI * 0.25f;
                if (waterAt(p + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance) < 0.5f) return false;
            }
            return true;
        }

        private static float Average(int cx, int cz)
        {
            float sum = 0f;
            int count = 0;
            for (int z = Mathf.Max(cz - 1, 0); z <= Mathf.Min(cz + 1, depth - 1); z++)
            {
                for (int x = Mathf.Max(cx - 1, 0); x <= Mathf.Min(cx + 1, width - 1); x++)
                {
                    sum += heights[z * width + x];
                    count++;
                }
            }
            return sum / count;
        }

        private static float Falloff(float distance, float radius, float softness)
        {
            if (distance >= radius) return 0f;
            float inner = radius * (1f - Mathf.Clamp01(softness));
            if (distance <= inner) return 1f;
            float t = (radius - distance) / Mathf.Max(radius - inner, 1e-4f);
            return t * t * (3f - 2f * t);
        }

        // The corners within a circle's square (x, z in the land's space), as [min, max) corner numbers
        private static RectInt CornersNear(Vector2 centre, float radius) => CornersIn(centre - Vector2.one * radius, centre + Vector2.one * radius);

        private static RectInt CornersIn(Vector2 min, Vector2 max)
        {
            int x0 = Mathf.Max(0, Mathf.CeilToInt((min.x - originX) / stepX - 1e-4f));
            int x1 = Mathf.Min(width - 1, Mathf.FloorToInt((max.x - originX) / stepX + 1e-4f));
            int z0 = Mathf.Max(0, Mathf.CeilToInt((min.y - originZ) / stepZ - 1e-4f));
            int z1 = Mathf.Min(depth - 1, Mathf.FloorToInt((max.y - originZ) / stepZ + 1e-4f));
            return x1 < x0 || z1 < z0 ? new RectInt(0, 0, 0, 0) : new RectInt(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        }

        // Puts new heights on corners (keeping what they were, for Ctrl+Z), then works out the facing and colours round them
        private static void Apply(List<(int index, float height)> changes, bool recolour)
        {
            if (changes.Count == 0) return;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach ((int i, float height) in changes)
            {
                Remember(i);
                offsets[i] += height - heights[i];
                heights[i] = height;
                int cx = i % width, cz = i / width;
                minX = Mathf.Min(minX, cx);
                maxX = Mathf.Max(maxX, cx);
                minZ = Mathf.Min(minZ, cz);
                maxZ = Mathf.Max(maxZ, cz);
            }
            Rework(new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1), recolour, true);
        }

        private static void Remember(int i)
        {
            if (!strokeBefore.ContainsKey(i)) strokeBefore[i] = Now(i);
        }

        private static Was Now(int i) => new Was { Height = heights[i], Offset = offsets[i], Colour = colours[i], Base = baseColours[i], Paint = paint[i] };

        private static void Put(int i, Was was)
        {
            heights[i] = was.Height;
            offsets[i] = was.Offset;
            colours[i] = was.Colour;
            baseColours[i] = was.Base;
            paint[i] = was.Paint;
        }

        // A corner's colour as shown: its own, with what's painted by hand over it
        private static Color32 Composed(Color32 under, Color32 painted)
        {
            if (painted.a == 0) return under;
            return Color32.Lerp(under, new Color32(painted.r, painted.g, painted.b, 255), painted.a / 255f);
        }

        // After the heights in a block of corners changed: the facing round them (and the colours, if asked) worked out
        // again, and the vertices on them updated
        private static void Rework(RectInt changed, bool recolour, bool remember)
        {
            RectInt facing = Grow(changed, 1);
            for (int cz = facing.yMin; cz < facing.yMax; cz++)
            {
                for (int cx = facing.xMin; cx < facing.xMax; cx++)
                {
                    normals[cz * width + cx] = LandBuilder.CornerNormal(heights, width, depth, cx, cz, stepX, stepZ);
                }
            }
            RectInt touched = facing;
            LandSettings settings = LandBuilder.GetSettings();
            if (recolour && settings.paintColours)
            {
                // (the slope reaches a corner further, the hollows two)
                touched = Grow(changed, 2);
                for (int cz = touched.yMin; cz < touched.yMax; cz++)
                {
                    for (int cx = touched.xMin; cx < touched.xMax; cx++)
                    {
                        int i = cz * width + cx;
                        float soft = LandBuilder.SoftHeight(heights, width, depth, cx, cz);
                        Color32 under = LandBuilder.GroundColour(settings, heights, width, depth, cx, cz, stepX, stepZ, originX, originZ,
                                                                 colourLow, colourHigh, soft);
                        Color32 shown = Composed(under, paint[i]);
                        if (Same(under, baseColours[i]) && Same(shown, colours[i])) continue;
                        if (remember) Remember(i);
                        baseColours[i] = under;
                        colours[i] = shown;
                    }
                }
            }
            WriteVertices(touched);
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        // ---------------------------------------------------------------- Painting the ground's colours

        private static readonly Dictionary<int, float> groundReached = new Dictionary<int, float>();

        /// <summary>
        /// Paints the ground's colour (at the corners, so it blends smoothly across the triangles) in a round dab at a point on
        /// the land (x, z): strength is how much (1 = all of it), softness how soft the edge is. Erase takes painted colour off
        /// (back to the colours Rebuild Land gives). Within a stroke, dabs don't build up on each other.
        /// </summary>
        internal static void PaintGround(Vector2 centre, float radius, Color32 colour, float strength, float softness, bool erase)
        {
            if (grids == null) return;
            radius = Mathf.Max(radius, Mathf.Max(stepX, stepZ) * 0.6f);
            RectInt corners = CornersNear(centre, radius);
            if (corners.width <= 0 || corners.height <= 0) return;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int cz = corners.yMin; cz < corners.yMax; cz++)
            {
                for (int cx = corners.xMin; cx < corners.xMax; cx++)
                {
                    float dx = originX + cx * stepX - centre.x, dz = originZ + cz * stepZ - centre.y;
                    float t = Falloff(Mathf.Sqrt(dx * dx + dz * dz), radius, softness) * Mathf.Clamp01(strength);
                    if (t <= 0f) continue;
                    int i = cz * width + cx;
                    if (groundReached.TryGetValue(i, out float had) && had >= t) continue;
                    groundReached[i] = t;
                    Remember(i);
                    Color32 before = strokeBefore[i].Paint;
                    float a = before.a / 255f;
                    Color32 now;
                    if (erase)
                    {
                        now = new Color32(before.r, before.g, before.b, (byte)Mathf.RoundToInt(a * (1f - t) * 255f));
                    }
                    else
                    {
                        // (what's shown becomes the old shown colour blended toward the new one by t)
                        float after = a + t - a * t;
                        Color old = new Color32(before.r, before.g, before.b, 255), add = new Color32(colour.r, colour.g, colour.b, 255);
                        Color mixed = after > 1e-4f ? (old * (a * (1f - t)) + add * t) / after : add;
                        mixed.a = after;
                        now = mixed;
                    }
                    paint[i] = now;
                    colours[i] = Composed(baseColours[i], now);
                    minX = Mathf.Min(minX, cx);
                    maxX = Mathf.Max(maxX, cx);
                    minZ = Mathf.Min(minZ, cz);
                    maxZ = Mathf.Max(maxZ, cz);
                }
            }
            if (maxX >= minX) WriteVertices(new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1));
        }

        /// <summary>The colour painted by hand at a point on the land (x, z), and how much (0 = none).</summary>
        internal static Color32 GroundPaintAt(Vector2 local)
        {
            if (grids == null) return default;
            int cx = Mathf.Clamp(Mathf.RoundToInt((local.x - originX) / stepX), 0, width - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt((local.y - originZ) / stepZ), 0, depth - 1);
            return paint[cz * width + cx];
        }

        /// <summary>Takes every colour painted on the ground off (one step for Ctrl+Z). False if there were none.</summary>
        internal static bool ClearGroundPaint()
        {
            if (Prepare() != null) return false;
            BeginStroke("Clear Ground Colours", false);
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < paint.Length; i++)
            {
                if (paint[i].a == 0) continue;
                Remember(i);
                paint[i] = default;
                colours[i] = baseColours[i];
                int cx = i % width, cz = i / width;
                minX = Mathf.Min(minX, cx);
                maxX = Mathf.Max(maxX, cx);
                minZ = Mathf.Min(minZ, cz);
                maxZ = Mathf.Max(maxZ, cz);
            }
            if (maxX < minX) return false;
            WriteVertices(new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1));
            EndStroke();
            return true;
        }

        private static RectInt Grow(RectInt r, int by)
        {
            int x0 = Mathf.Max(0, r.xMin - by), z0 = Mathf.Max(0, r.yMin - by);
            int x1 = Mathf.Min(width, r.xMax + by), z1 = Mathf.Min(depth, r.yMax + by);
            return new RectInt(x0, z0, x1 - x0, z1 - z0);
        }

        // The vertices on a block of corners take the corners' heights, facing and colours
        private static void WriteVertices(RectInt cornerRect)
        {
            // (the squares touching those corners)
            int sx0 = cornerRect.xMin - 1, sx1 = cornerRect.xMax - 1, sz0 = cornerRect.yMin - 1, sz1 = cornerRect.yMax - 1;
            foreach (PieceGrid grid in grids)
            {
                int x0 = Mathf.Max(sx0 - grid.CellX0, 0), x1 = Mathf.Min(sx1 - grid.CellX0, grid.Columns - 1);
                int z0 = Mathf.Max(sz0 - grid.CellZ0, 0), z1 = Mathf.Min(sz1 - grid.CellZ0, grid.Rows - 1);
                if (x0 > x1 || z0 > z1) continue;
                LandPaint.Piece piece = grid.Piece;
                for (int z = z0; z <= z1; z++)
                {
                    int from = (z * grid.Columns + x0) * 6, to = (z * grid.Columns + x1) * 6 + 5;
                    for (int v = from; v <= to; v++)
                    {
                        int i = grid.Corner[v];
                        float h = heights[i];
                        Color32 painted = LandBuilder.PremultipliedPaint(paint[i]);
                        grid.Vertices[v].Position.y = h;
                        grid.Vertices[v].Normal = normals[i];
                        grid.Vertices[v].Colour = baseColours[i];
                        grid.GroundPaint[v] = painted;
                        piece.Points[v].y = h;
                        if (piece.Ground != null && v < piece.Ground.Length) piece.Ground[v] = baseColours[i];
                        if (piece.GroundPaint != null && v < piece.GroundPaint.Length) piece.GroundPaint[v] = painted;
                        if (h < grid.Low) grid.Low = h;
                        if (h > grid.High) grid.High = h;
                    }
                    grid.DirtyFrom = Mathf.Min(grid.DirtyFrom, from);
                    grid.DirtyTo = Mathf.Max(grid.DirtyTo, to);
                }
                grid.Touched = true;
            }
            MarkGrass(cornerRect);
        }

        private static void MarkGrass(RectInt cornerRect)
        {
            grassRect = grassPending ? Union(grassRect, cornerRect) : cornerRect;
            grassPending = true;
        }

        private static RectInt Union(RectInt a, RectInt b)
        {
            int xMin = Mathf.Min(a.xMin, b.xMin), yMin = Mathf.Min(a.yMin, b.yMin);
            int xMax = Mathf.Max(a.xMax, b.xMax), yMax = Mathf.Max(a.yMax, b.yMax);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Shows what's been changed (call after each lot of dabs): the meshes, and the things standing on it.</summary>
        internal static void Flush()
        {
            if (grids == null) return;
            const MeshUpdateFlags quick = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds |
                                          MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            foreach (PieceGrid grid in grids)
            {
                if (grid.DirtyTo < grid.DirtyFrom) continue;
                Mesh mesh = grid.Piece.Mesh;
                if (mesh != null)
                {
                    mesh.SetVertexBufferData(grid.Vertices, grid.DirtyFrom, grid.DirtyFrom, grid.DirtyTo - grid.DirtyFrom + 1, 0, quick);
                    if (LandBuilder.CanPaintGround(mesh))
                    {
                        mesh.SetVertexBufferData(grid.GroundPaint, grid.DirtyFrom, grid.DirtyFrom, grid.DirtyTo - grid.DirtyFrom + 1,
                                                 LandBuilder.GroundPaintStream, quick);
                    }
                    // (its box takes in the new heights, so it isn't culled, and the brush can find it)
                    Bounds b = mesh.bounds;
                    b.Encapsulate(new Vector3(b.center.x, grid.Low, b.center.z));
                    b.Encapsulate(new Vector3(b.center.x, grid.High, b.center.z));
                    mesh.bounds = b;
                    Bounds pb = grid.Piece.Bounds;
                    pb.Encapsulate(new Vector3(pb.center.x, grid.Low, pb.center.z));
                    pb.Encapsulate(new Vector3(pb.center.x, grid.High, pb.center.z));
                    grid.Piece.Bounds = pb;
                }
                grid.DirtyFrom = int.MaxValue;
                grid.DirtyTo = -1;
                grid.Low = float.MaxValue;
                grid.High = float.MinValue;
            }
            MoveThings();
            if (grassPending && EditorApplication.timeSinceStartup - lastGrass > 0.25) TellGrass();
        }

        // The trees, rocks and plants where the ground moved, moved with it
        private static void MoveThings()
        {
            if (things.Count == 0 || !grassPending) return;
            Rect world = WorldRect(grassRect);
            foreach (Thing thing in things)
            {
                if (thing.Transform == null) continue;
                Vector3 p = thing.Start;
                if (p.x < world.xMin || p.x > world.xMax || p.z < world.yMin || p.z > world.yMax) continue;
                if (!LandPaint.GroundAt(p, out Vector3 ground, out _)) continue;
                Vector3 now = new Vector3(p.x, p.y + (ground.y - thing.GroundStart), p.z);
                if ((thing.Transform.position - now).sqrMagnitude < 1e-8f) continue;
                if (!thing.Recorded)
                {
                    Undo.RecordObject(thing.Transform, strokeName);
                    thing.Recorded = true;
                }
                thing.Transform.position = now;
            }
        }

        // A block of corners, from above, in the world (x, z)
        private static Rect WorldRect(RectInt corners)
        {
            var a = new Vector3(originX + (corners.xMin - 1) * stepX, 0f, originZ + (corners.yMin - 1) * stepZ);
            var b = new Vector3(originX + corners.xMax * stepX, 0f, originZ + corners.yMax * stepZ);
            Transform space = LandPaint.Land != null ? LandPaint.Land.transform : null;
            if (space != null)
            {
                a = space.TransformPoint(a);
                b = space.TransformPoint(b);
            }
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z));
        }

        // The Grass Field grows on the new ground
        private static void TellGrass()
        {
            if (!grassPending || grids == null) return;
            grassPending = false;
            lastGrass = EditorApplication.timeSinceStartup;
            var local = Rect.MinMaxRect(originX + (grassRect.xMin - 1) * stepX, originZ + (grassRect.yMin - 1) * stepZ,
                                        originX + grassRect.xMax * stepX, originZ + grassRect.yMax * stepZ);
            foreach (PieceGrid grid in grids)
            {
                if (!grid.Touched || grid.Piece.Mesh == null) continue;
                GrassField.ShapeChanged(grid.Piece.Mesh, local);
            }
        }

        /// <summary>Finishes a stroke: the colliders take the new shape, and it's one step for Ctrl+Z.</summary>
        internal static void EndStroke()
        {
            LandPaint.Step step = FinishStroke();
            if (step != null) LandPaint.Commit(step, strokeName);
        }

        /// <summary>
        /// Finishes a stroke like EndStroke, but hands back its step for Ctrl+Z (null if nothing changed) instead of
        /// keeping it, for a brush that keeps it with its own changes as one step (Paint Land > Water).
        /// </summary>
        internal static LandPaint.Step FinishStroke()
        {
            Flush();
            Finish();
            things.Clear();
            if (strokeBefore.Count == 0)
            {
                groundReached.Clear();
                return null;
            }
            var step = new ShapeStep(width, depth, originX, originZ);
            foreach (KeyValuePair<int, Was> entry in strokeBefore)
            {
                int i = entry.Key;
                step.Corners.Add(i);
                step.Before.Add(entry.Value);
                step.After.Add(Now(i));
            }
            strokeBefore.Clear();
            groundReached.Clear();
            unsaved = true;
            return step;
        }

        // The pieces changed: exact boxes, colliders, saved later, and the grass told
        private static void Finish()
        {
            if (grids == null) return;
            TellGrass();
            foreach (PieceGrid grid in grids)
            {
                if (!grid.Touched) continue;
                grid.Touched = false;
                Mesh mesh = grid.Piece.Mesh;
                if (mesh == null) continue;
                mesh.RecalculateBounds();
                LandPaint.RefreshBounds(grid.Piece);
                RefreshCollider(grid.Piece, mesh);
                LandPaint.MarkUnsaved(mesh);
            }
        }

        private static void RefreshCollider(LandPaint.Piece piece, Mesh mesh)
        {
            MeshCollider collider = null;
            if (piece.Filter != null) piece.Filter.TryGetComponent(out collider);
            if (collider == null)
            {
                GameObject land = LandBuilder.FindLand();
                if (land == null) return;
                foreach (MeshCollider c in land.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (c.sharedMesh != mesh) continue;
                    collider = c;
                    break;
                }
            }
            if (collider == null) return;
            collider.sharedMesh = null;
            collider.sharedMesh = mesh;
        }

        // Ctrl+Z in the middle of a stroke: what it changed is put back
        private static void Cancel()
        {
            things.Clear();
            if (strokeBefore.Count == 0 || grids == null)
            {
                strokeBefore.Clear();
                return;
            }
            var changed = new List<int>(strokeBefore.Keys);
            foreach (KeyValuePair<int, Was> entry in strokeBefore) Put(entry.Key, entry.Value);
            strokeBefore.Clear();
            groundReached.Clear();
            Redraw(changed);
        }

        // Corners were set back (or forward): the facing round them, the vertices, meshes and colliders
        private static void Redraw(List<int> changed)
        {
            if (changed.Count == 0) return;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (int i in changed)
            {
                int cx = i % width, cz = i / width;
                minX = Mathf.Min(minX, cx);
                maxX = Mathf.Max(maxX, cx);
                minZ = Mathf.Min(minZ, cz);
                maxZ = Mathf.Max(maxZ, cz);
            }
            Rework(new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1), false, false);
            Flush();
            Finish();
        }

        /// <summary>Puts the whole land back to the shape Rebuild Land makes (one step for Ctrl+Z). False if nothing changed.</summary>
        internal static bool RestoreAll(bool recolour, bool moveThings)
        {
            if (Prepare() != null) return false;
            var changes = new List<(int index, float height)>();
            for (int i = 0; i < offsets.Length; i++)
            {
                if (Mathf.Abs(offsets[i]) > 1e-5f) changes.Add((i, heights[i] - offsets[i]));
            }
            if (changes.Count == 0) return false;
            LandPaint.OpenUndoGroup("Restore Land Shape");
            BeginStroke("Restore Land Shape", moveThings);
            Apply(changes, recolour);
            EndStroke();
            LandPaint.CloseUndoGroup();
            return true;
        }

        /// <summary>Has the land's shape been changed anywhere?</summary>
        internal static bool Changed()
        {
            if (offsets == null) return File.Exists(FullPath(ShapePath));
            foreach (float offset in offsets)
            {
                if (Mathf.Abs(offset) > 1e-5f) return true;
            }
            return false;
        }

        /// <summary>Has colour been painted on the ground anywhere?</summary>
        internal static bool GroundPainted()
        {
            if (paint == null) return File.Exists(FullPath(ShapePath));
            foreach (Color32 c in paint)
            {
                if (c.a > 0) return true;
            }
            return false;
        }

        private sealed class ShapeStep : LandPaint.Step
        {
            public readonly List<int> Corners = new List<int>();
            public readonly List<Was> Before = new List<Was>();
            public readonly List<Was> After = new List<Was>();
            private readonly int stepWidth, stepDepth;
            private readonly float stepOriginX, stepOriginZ;

            public ShapeStep(int width, int depth, float originX, float originZ)
            {
                stepWidth = width;
                stepDepth = depth;
                stepOriginX = originX;
                stepOriginZ = originZ;
            }

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<Was> values)
            {
                if (Prepare() != null) return;
                if (width != stepWidth || depth != stepDepth || Mathf.Abs(originX - stepOriginX) > stepX * 1e-3f || Mathf.Abs(originZ - stepOriginZ) > stepZ * 1e-3f) return;
                for (int k = 0; k < Corners.Count; k++) LandShape.Put(Corners[k], values[k]);
                unsaved = true;
                Redraw(Corners);
            }
        }

        // ================================================================ Land Shape.bytes

        private static string FullPath(string assetPath) => Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);

        // Saves how far each corner has been moved (when Paint Land saves)
        private static void Save()
        {
            if (!unsaved || offsets == null || grids == null) return;
            unsaved = false;
            try
            {
                bool any = false;
                foreach (float offset in offsets)
                {
                    if (Mathf.Abs(offset) <= 1e-5f) continue;
                    any = true;
                    break;
                }
                bool painted = false;
                foreach (Color32 c in paint)
                {
                    if (c.a == 0) continue;
                    painted = true;
                    break;
                }
                if (!any && !painted)
                {
                    if (File.Exists(FullPath(ShapePath))) AssetDatabase.DeleteAsset(ShapePath);
                    return;
                }
                WriteShape(offsets, painted ? paint : null, width, depth, originX, originZ, stepX, stepZ);
            }
            catch (System.Exception e)
            {
                unsaved = true;
                Debug.LogWarning("Paint Land: couldn't save the land's shape (" + e.Message + "). It'll try again.");
                LandPaint.ScheduleSave();
            }
        }

        private static void WriteShape(float[] values, Color32[] painted, int w, int d, float ox, float oz, float sx, float sz)
        {
            LandBuilder.EnsureFolder(LandBuilder.Folder);
            using (var file = new FileStream(FullPath(ShapePath), FileMode.Create, FileAccess.Write))
            using (var zip = new GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var writer = new BinaryWriter(zip))
            {
                writer.Write(FileMagic);
                writer.Write(2);
                writer.Write(w);
                writer.Write(d);
                writer.Write(ox);
                writer.Write(oz);
                writer.Write(sx);
                writer.Write(sz);
                foreach (float value in values) writer.Write(value);
                writer.Write(painted != null);
                if (painted != null)
                {
                    foreach (Color32 c in painted)
                    {
                        writer.Write(c.r);
                        writer.Write(c.g);
                        writer.Write(c.b);
                        writer.Write(c.a);
                    }
                }
            }
        }

        // How far each corner of a grid has been moved, and the colour painted there, from Land Shape.bytes (worked out for
        // this grid if the file's is different); false if there's no file
        private static bool ReadShape(int w, int d, float ox, float oz, float sx, float sz, out float[] moved, out Color32[] painted)
        {
            moved = null;
            painted = null;
            string path = FullPath(ShapePath);
            if (!File.Exists(path)) return false;
            try
            {
                int fw, fd;
                float fox, foz, fsx, fsz;
                float[] stored;
                Color32[] storedPaint = null;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var zip = new GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
                using (var reader = new BinaryReader(zip))
                {
                    if (reader.ReadInt32() != FileMagic) return false;
                    int version = reader.ReadInt32();
                    fw = reader.ReadInt32();
                    fd = reader.ReadInt32();
                    fox = reader.ReadSingle();
                    foz = reader.ReadSingle();
                    fsx = reader.ReadSingle();
                    fsz = reader.ReadSingle();
                    if (fw <= 0 || fd <= 0 || fw * (long)fd > 64L * 1024 * 1024 || fsx <= 0f || fsz <= 0f) return false;
                    stored = new float[fw * fd];
                    for (int i = 0; i < stored.Length; i++) stored[i] = reader.ReadSingle();
                    if (version >= 2 && reader.ReadBoolean())
                    {
                        storedPaint = new Color32[fw * fd];
                        for (int i = 0; i < storedPaint.Length; i++)
                        {
                            storedPaint[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                        }
                    }
                }
                if (fw == w && fd == d && Mathf.Abs(fox - ox) < sx * 1e-3f && Mathf.Abs(foz - oz) < sz * 1e-3f &&
                    Mathf.Abs(fsx - sx) < sx * 1e-3f && Mathf.Abs(fsz - sz) < sz * 1e-3f)
                {
                    moved = stored;
                    painted = storedPaint ?? new Color32[w * d];
                    return true;
                }

                // A different grid (the land was rebuilt with other triangles): each corner from the four round it
                moved = new float[w * d];
                painted = new Color32[w * d];
                for (int z = 0; z < d; z++)
                {
                    float fz = (oz + z * sz - foz) / fsz;
                    for (int x = 0; x < w; x++)
                    {
                        float fx = (ox + x * sx - fox) / fsx;
                        if (fx < -0.5f || fz < -0.5f || fx > fw - 0.5f || fz > fd - 0.5f) continue;
                        float cx = Mathf.Clamp(fx, 0f, fw - 1), cz = Mathf.Clamp(fz, 0f, fd - 1);
                        int x0 = Mathf.Min(Mathf.FloorToInt(cx), Mathf.Max(fw - 2, 0)), z0 = Mathf.Min(Mathf.FloorToInt(cz), Mathf.Max(fd - 2, 0));
                        int x1 = Mathf.Min(x0 + 1, fw - 1), z1 = Mathf.Min(z0 + 1, fd - 1);
                        float tx = Mathf.Clamp01(cx - x0), tz = Mathf.Clamp01(cz - z0);
                        int i00 = z0 * fw + x0, i10 = z0 * fw + x1, i01 = z1 * fw + x0, i11 = z1 * fw + x1;
                        moved[z * w + x] = Mathf.Lerp(Mathf.Lerp(stored[i00], stored[i10], tx), Mathf.Lerp(stored[i01], stored[i11], tx), tz);
                        if (storedPaint != null)
                        {
                            painted[z * w + x] = Color32.Lerp(Color32.Lerp(storedPaint[i00], storedPaint[i10], tx),
                                                              Color32.Lerp(storedPaint[i01], storedPaint[i11], tx), tz);
                        }
                    }
                }
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Paint Land: couldn't read Land Shape.bytes (" + e.Message + ").");
                moved = null;
                painted = null;
                return false;
            }
        }

        /// <summary>
        /// For Rebuild Land: adds the shape changed in Paint Land to the heights of the new land's grid of corners (and keeps
        /// the changes for that grid). Returns true if there were any. groundPaint: the colours painted on the ground at each
        /// corner (alpha = how much), or null if none, for the new meshes' ground-paint stream.
        /// </summary>
        internal static bool AddTo(float[] gridHeights, int w, int d, float ox, float oz, float sx, float sz, out Color32[] groundPaint)
        {
            Save();
            grids = null;
            offsets = null;
            paint = null;
            strokeBefore.Clear();
            unsaved = false;
            groundPaint = null;
            if (!ReadShape(w, d, ox, oz, sx, sz, out float[] moved, out Color32[] painted)) return false;
            bool any = false, anyPaint = false;
            for (int i = 0; i < gridHeights.Length && i < moved.Length; i++)
            {
                if (Mathf.Abs(moved[i]) <= 1e-5f) continue;
                gridHeights[i] += moved[i];
                any = true;
            }
            foreach (Color32 c in painted)
            {
                if (c.a == 0) continue;
                anyPaint = true;
                break;
            }
            if (anyPaint) groundPaint = painted;
            if (any || anyPaint) WriteShape(moved, anyPaint ? painted : null, w, d, ox, oz, sx, sz);
            return any;
        }
    }
}
#endif
