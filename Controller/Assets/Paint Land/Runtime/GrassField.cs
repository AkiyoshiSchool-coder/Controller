using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Grass over the whole land without any objects: tufts are worked out in 16 m squares ("chunks") near each camera,
    /// only as they come within the grass distance, and drawn straight on the GPU, thousands at a time (the Paint Land/Grass
    /// Field shader). Further away fewer tufts are drawn (and a simpler tuft), and at the edge of the distance they shrink
    /// away into the ground; past that the land's own green does the job.
    ///
    /// Where it grows comes from the land itself: on green ground (the land's colours, and triangles coloured in with Paint
    /// Land), not on paths or slopes steeper than Max Slope, thicker in patches. On top of that, Tools > Paint Land > Paint
    /// Land > Grass paints where there's more or less (or none) by hand: that's kept in the mask texture (Land Grass.png).
    /// Each tuft takes the colour of the ground it grows on (or one of its own colours), and the ground's light, so it sits
    /// in the land. No shadows are cast (the biggest saving), and it works in the Scene view too (Show In Scene View).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("World/Grass Field")]
    public class GrassField : MonoBehaviour
    {
        [Tooltip("The land it grows on (the Land in the scene).")]
        public GameObject land;

        [Header("Tufts")]
        [Tooltip("The tuft drawn close up.")]
        public Mesh nearMesh;
        [Tooltip("A simpler tuft drawn further away (optional).")]
        public Mesh farMesh;
        [Tooltip("The Paint Land/Grass Field material (its texture's alpha is the blades' shape).")]
        public Material material;

        [Header("Where It Grows")]
        [Tooltip("Tufts per square metre where it's thickest.")]
        [Range(0.2f, 12f)] public float density = 3f;
        [Tooltip("No grass on ground steeper than this (degrees).")]
        [Range(0f, 90f)] public float maxSlope = 40f;
        [Tooltip("0 = even, 1 = thick patches with thin grass between.")]
        [Range(0f, 1f)] public float patchiness = 0.35f;
        [Tooltip("How wide the patches are (metres).")]
        [Min(1f)] public float patchSize = 12f;
        [Tooltip("No grass on paths (even where grass was painted on by hand). Off: paths don't change the grass.")]
        public bool pathsClearGrass = true;

        [Header("How Far")]
        [Tooltip("How far from the camera grass is drawn (metres).")]
        [Range(10f, 250f)] public float distance = 60f;
        [Tooltip("Within this, every tuft and the detailed tuft are drawn; beyond, fewer and simpler.")]
        [Range(5f, 150f)] public float detailDistance = 25f;
        [Tooltip("How much of the grass is still drawn at the far edge (fewer tufts = faster).")]
        [Range(0.05f, 1f)] public float farDensity = 0.35f;

        [Header("Each Tuft")]
        [Tooltip("Random size, from-to (1 = the tuft's own size).")]
        public Vector2 size = new Vector2(0.8f, 1.4f);
        [Tooltip("How much taller or shorter than wide each one can be.")]
        [Range(0f, 0.8f)] public float heightVariation = 0.35f;
        [Tooltip("How much they lean with the slope of the ground.")]
        [Range(0f, 1f)] public float followSlope = 0.6f;
        [Tooltip("Random lean each way (degrees).")]
        [Range(0f, 40f)] public float randomLean = 10f;

        [Header("Colour")]
        [Tooltip("Each tuft the colour of the ground it grows on. Off: the colours below.")]
        public bool groundColour = true;
        [Tooltip("The colours it can be when not using the ground's (picked in patches).")]
        public List<Color> colours = new List<Color>();
        [Tooltip("How much each tuft's shade varies.")]
        [Range(0f, 0.4f)] public float shadeVariation = 0.12f;

        [Header("Other")]
        [Tooltip("Draw the grass in the Scene view as well as the game.")]
        public bool showInSceneView = true;
        [Tooltip("Change for a different arrangement of tufts.")]
        public int seed = 1;

        [HideInInspector] public Texture2D mask;      // painted by hand (Paint Land > Grass): r = how much, a = how sure
        [HideInInspector] public Rect maskArea;       // where the mask lies over the land (x, z, in the land's space)

        // ---------------------------------------------------------------- Inside

        private const float ChunkSize = 16f;
        private const int GeneratePerFrame = 6;

        /// <summary>One tuft, as the shader reads it (48 bytes).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Tuft
        {
            public Vector3 position;
            public float scale;
            public float yaw;
            public float stretch;
            public Vector2 lean;
            public Vector3 normal;
            public uint colour;
        }

        private sealed class Chunk
        {
            public GraphicsBuffer Buffer;
            public int Count;
            public Bounds Bounds;
            public int LastUsed;
            public MaterialPropertyBlock Block;
        }

        // A piece of the land, as the grass sees it: a grid of squares, two triangles each, three vertices a triangle
        private sealed class Piece
        {
            public Mesh Mesh;
            public Vector3[] Points;
            public Color32[] Ground;
            public Color32[] GroundPaint;  // the ground's colour painted by hand (times how much; alpha = how much)
            public Color32[] Paint;
            public Color32[] Textures;     // the texture painted on each triangle (r = which, 1 to 8; g = how much)
            public float MinX, MinZ, StepX, StepZ;
            public int Columns, Rows;
            public Bounds WorldBounds;
            public Matrix4x4 ToLocal, ToWorld;
        }

        private static readonly List<GrassField> fields = new List<GrassField>();

        // With Enter Play Mode Options (no domain reload) statics keep their values between plays: drop any left over from before
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => fields.RemoveAll(f => f == null || !f.isActiveAndEnabled);
        private static readonly int InstancesId = Shader.PropertyToID("_Instances");
        private static readonly int FadeStartId = Shader.PropertyToID("_FadeStart");
        private static readonly int FadeEndId = Shader.PropertyToID("_FadeEnd");

        private readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
        private readonly List<Piece> pieces = new List<Piece>();
        private readonly List<Tuft> building = new List<Tuft>();
        private readonly Plane[] planes = new Plane[6];
        private bool landRead;
        private Texture2D pathMap;
        private Vector4 pathRect;
        private float pathEdge;                // the land's Path Edges (0 = crisp, 1 = soft)
        private NativeArray<Color32> pathPixels;
        private Color32[] pathCopy;
        private readonly Texture2D[] textureMaps = new Texture2D[2];        // where the land's painted textures are
        private readonly NativeArray<Color32>[] texturePixels = new NativeArray<Color32>[2];
        private readonly Color32[][] textureCopies = new Color32[2][];
        private readonly float[] layerGrass = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };   // how much grass grows on each painted texture
        private readonly float[] weights = new float[8];
        private bool texturesMatter;                                        // a painted texture has less grass on it
        private static readonly string[] LayerGrassNames =
        {
            "_Layer1Grass", "_Layer2Grass", "_Layer3Grass", "_Layer4Grass", "_Layer5Grass", "_Layer6Grass", "_Layer7Grass", "_Layer8Grass",
        };
        private Color32[] maskPixels;
        private Texture2D maskLoaded;          // the mask texture maskPixels came from
        private Material landMaterial;
        private int maskWidth, maskHeight;
        private int frame;

        /// <summary>How many tufts were drawn for the last camera (for Paint Land).</summary>
        public int Drawn { get; private set; }

        private void OnEnable()
        {
            if (!fields.Contains(this)) fields.Add(this);
            RenderPipelineManager.beginCameraRendering += Draw;
            landRead = false;
            LoadMask();
        }

        private void OnDisable()
        {
            fields.Remove(this);
            RenderPipelineManager.beginCameraRendering -= Draw;
            ClearChunks();
        }

        private void OnValidate()
        {
            if (size.y < size.x) size = new Vector2(size.y, size.x);
            detailDistance = Mathf.Min(detailDistance, distance);
            ClearChunks();
            if (maskPixels == null || maskLoaded != mask) LoadMask();   // (not over what's being painted)
        }

        // ================================================================ When things change (called by the land tools)

        /// <summary>The land was rebuilt, or something changed all over: everything is worked out again.</summary>
        public static void LandChanged()
        {
            foreach (GrassField field in fields)
            {
                field.landRead = false;
                field.ClearChunks();
            }
        }

        /// <summary>Something changed in part of the land (x, z in the land's space): that part is worked out again.</summary>
        public static void AreaChanged(Rect local)
        {
            foreach (GrassField field in fields) field.Drop(local);
        }

        /// <summary>Triangles were coloured in (or textured) on a piece of the land: they're read again, and that part redone.</summary>
        public static void PaintChanged(Mesh piece, Rect local)
        {
            foreach (GrassField field in fields)
            {
                foreach (Piece p in field.pieces)
                {
                    if (p.Mesh != piece) continue;
                    p.Paint = ReadPaint(piece, p.Points.Length, 2);
                    p.Textures = ReadPaint(piece, p.Points.Length, 3);
                    p.GroundPaint = ReadPaint(piece, p.Points.Length, 4);
                }
                field.Drop(local);
            }
        }

        /// <summary>A piece of the land was raised or lowered (Paint Land > Shape): its shape is read again, and that part redone.</summary>
        public static void ShapeChanged(Mesh piece, Rect local)
        {
            foreach (GrassField field in fields)
            {
                foreach (Piece p in field.pieces)
                {
                    if (p.Mesh != piece || !piece.isReadable) continue;
                    Vector3[] points = piece.vertices;
                    if (points.Length != p.Points.Length) continue;
                    p.Points = points;
                    Color32[] ground = piece.colors32;
                    if (ground != null && ground.Length == points.Length) p.Ground = ground;
                    p.GroundPaint = ReadPaint(piece, points.Length, 4);
                    piece.RecalculateBounds();
                    p.WorldBounds = WorldBounds(piece.bounds, p.ToWorld);
                }
                field.Drop(local);
            }
        }

        /// <summary>The hand-painted mask, for Paint Land to paint into (null if there isn't one).</summary>
        public Color32[] MaskPixels => maskPixels;
        public int MaskWidth => maskWidth;
        public int MaskHeight => maskHeight;

        /// <summary>Paint Land made or reloaded the mask.</summary>
        public void SetMask(Texture2D texture, Color32[] pixels, Rect area)
        {
            mask = texture;
            maskLoaded = texture;
            maskArea = area;
            maskPixels = pixels;
            maskWidth = texture != null ? texture.width : 0;
            maskHeight = texture != null ? texture.height : 0;
            ClearChunks();
        }

        private void LoadMask()
        {
            maskPixels = null;
            maskLoaded = mask;
            maskWidth = maskHeight = 0;
            if (mask == null || !mask.isReadable) return;
            maskPixels = mask.GetPixels32();
            maskWidth = mask.width;
            maskHeight = mask.height;
        }

        private void Drop(Rect local)
        {
            pathCopy = null;   // (the paths may have changed)
            textureCopies[0] = textureCopies[1] = null;
            if (chunks.Count == 0) return;
            Transform space = land != null ? land.transform : null;
            Vector3 a = new Vector3(local.xMin, 0f, local.yMin), b = new Vector3(local.xMax, 0f, local.yMax);
            if (space != null)
            {
                a = space.TransformPoint(a);
                b = space.TransformPoint(b);
            }
            float margin = 1f + MaxReach();   // (tufts beside a new path reach over it too)
            float minX = Mathf.Min(a.x, b.x) - margin, maxX = Mathf.Max(a.x, b.x) + margin;
            float minZ = Mathf.Min(a.z, b.z) - margin, maxZ = Mathf.Max(a.z, b.z) + margin;
            var gone = new List<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, Chunk> pair in chunks)
            {
                float x0 = pair.Key.x * ChunkSize, z0 = pair.Key.y * ChunkSize;
                if (x0 > maxX || x0 + ChunkSize < minX || z0 > maxZ || z0 + ChunkSize < minZ) continue;
                pair.Value.Buffer?.Release();
                gone.Add(pair.Key);
            }
            foreach (Vector2Int key in gone) chunks.Remove(key);
        }

        private void ClearChunks()
        {
            foreach (Chunk chunk in chunks.Values) chunk.Buffer?.Release();
            chunks.Clear();
        }

        // ================================================================ Drawing

        private void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (!isActiveAndEnabled || material == null || nearMesh == null || camera == null) return;
            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection) return;
            if (camera.cameraType == CameraType.SceneView && !showInSceneView) return;
            if (!landRead) ReadLand();
            if (pieces.Count == 0) return;
            frame++;
            ReadyPaths();

            Vector3 eye = camera.transform.position;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            int reach = Mathf.CeilToInt(distance / ChunkSize);
            var centre = new Vector2Int(Mathf.FloorToInt(eye.x / ChunkSize), Mathf.FloorToInt(eye.z / ChunkSize));
            int budget = GeneratePerFrame;
            bool waiting = false;
            int drawn = 0;

            // Nearest first, ring by ring, so the grass round the camera is there before the grass further off
            for (int ring = 0; ring <= reach; ring++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring) continue;
                        var key = new Vector2Int(centre.x + dx, centre.y + dz);
                        float x0 = key.x * ChunkSize, z0 = key.y * ChunkSize;
                        float nearX = Mathf.Clamp(eye.x, x0, x0 + ChunkSize) - eye.x, nearZ = Mathf.Clamp(eye.z, z0, z0 + ChunkSize) - eye.z;
                        if (nearX * nearX + nearZ * nearZ > distance * distance) continue;

                        if (!chunks.TryGetValue(key, out Chunk chunk))
                        {
                            if (budget <= 0)
                            {
                                waiting = true;
                                continue;
                            }
                            budget--;
                            chunk = Generate(key);
                            chunks[key] = chunk;
                        }
                        chunk.LastUsed = frame;
                        if (chunk.Count == 0) continue;
                        float away = Vector3.Distance(eye, chunk.Bounds.ClosestPoint(eye));
                        if (away > distance || !GeometryUtility.TestPlanesAABB(planes, chunk.Bounds)) continue;

                        float fraction = Mathf.Lerp(1f, farDensity, Mathf.InverseLerp(detailDistance, distance, away));
                        int count = Mathf.Clamp(Mathf.CeilToInt(chunk.Count * fraction), 1, chunk.Count);
                        Mesh mesh = away > detailDistance && farMesh != null ? farMesh : nearMesh;
                        chunk.Block.SetBuffer(InstancesId, chunk.Buffer);
                        chunk.Block.SetFloat(FadeStartId, distance * 0.8f);
                        chunk.Block.SetFloat(FadeEndId, distance);
                        var settings = new RenderParams(material)
                        {
                            camera = camera,
                            worldBounds = chunk.Bounds,
                            matProps = chunk.Block,
                            shadowCastingMode = ShadowCastingMode.Off,
                            receiveShadows = true,
                            layer = gameObject.layer,
                        };
                        Graphics.RenderMeshPrimitives(settings, mesh, 0, count);
                        drawn += count;
                    }
                }
            }
            Drawn = drawn;

            // Let go of chunks not drawn for a while
            if (frame % 120 == 0) Forget(frame - 600);
#if UNITY_EDITOR
            if (waiting && !Application.isPlaying) AskForRepaint();
#endif
        }

        private void Forget(int before)
        {
            var gone = new List<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, Chunk> pair in chunks)
            {
                if (pair.Value.LastUsed >= before) continue;
                pair.Value.Buffer?.Release();
                gone.Add(pair.Key);
            }
            foreach (Vector2Int key in gone) chunks.Remove(key);
        }

#if UNITY_EDITOR
        private bool repaintAsked;

        // In the editor the Scene view only redraws when something happens, so it's asked to, until the grass round the
        // camera has all been worked out
        private void AskForRepaint()
        {
            if (repaintAsked) return;
            repaintAsked = true;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                repaintAsked = false;
                UnityEditor.SceneView.RepaintAll();
            };
        }
#endif

        // ================================================================ Working out a chunk

        private Chunk Generate(Vector2Int key)
        {
            var chunk = new Chunk { Block = new MaterialPropertyBlock() };
            float x0 = key.x * ChunkSize, z0 = key.y * ChunkSize;
            float spacing = 1f / Mathf.Sqrt(Mathf.Max(density, 0.05f));
            int across = Mathf.CeilToInt(ChunkSize / spacing);
            var random = new System.Random(Hash(key.x, key.y));
            float smallest = Mathf.Min(size.x, size.y), biggest = Mathf.Max(size.x, size.y);
            building.Clear();

            // Paths nearby: each tuft is checked so its blades don't reach over one (not just its roots)
            float reach = MaxReach();
            bool besidePaths = pathsClearGrass && PathsNear(x0 - reach, z0 - reach, x0 + ChunkSize + reach, z0 + ChunkSize + reach);
            TuftSize(out float tuftWidth, out float tuftHeight, out float windLean);
            float touches = Mathf.Lerp(0.35f, 0.05f, pathEdge);   // (how much path counts as path: its visible edge, a bit before)

            for (int gz = 0; gz < across; gz++)
            {
                for (int gx = 0; gx < across; gx++)
                {
                    float px = x0 + (gx + (float)random.NextDouble()) * spacing;
                    float pz = z0 + (gz + (float)random.NextDouble()) * spacing;
                    float roll = (float)random.NextDouble();
                    if (px >= x0 + ChunkSize || pz >= z0 + ChunkSize) continue;
                    if (!Ground(new Vector3(px, 0f, pz), out Vector3 point, out Vector3 normal, out Color colour, out Color growsOn, out Vector2 local,
                                out Matrix4x4 toLocal, out int triangleLayer, out float triangleAmount)) continue;
                    if (LandWater.UnderWater(point, 0.05f)) continue;   // (no grass under water)
                    float amount = Amount(growsOn, normal, local, point, triangleLayer, triangleAmount);
                    if (roll >= amount) continue;

                    float leanAngle = (float)random.NextDouble() * randomLean * Mathf.Deg2Rad;
                    float leanWay = (float)random.NextDouble() * Mathf.PI * 2f;
                    Vector2 slopeLean = new Vector2(normal.x, normal.z) / Mathf.Max(normal.y, 0.3f) * followSlope;
                    var tuft = new Tuft
                    {
                        position = point - Vector3.up * 0.02f,
                        scale = Mathf.Lerp(smallest, biggest, Mathf.Pow((float)random.NextDouble(), 1.3f)) * Mathf.Lerp(0.75f, 1f, Mathf.Clamp01(amount)),
                        yaw = (float)random.NextDouble() * Mathf.PI * 2f,
                        stretch = Mathf.Lerp(1f - heightVariation * 0.6f, 1f + heightVariation, (float)random.NextDouble()),
                        lean = slopeLean + new Vector2(Mathf.Cos(leanWay), Mathf.Sin(leanWay)) * Mathf.Tan(leanAngle),
                        normal = normal,
                        colour = Pack(TuftColour(colour, point, (float)random.NextDouble())),
                    };
                    if (besidePaths)
                    {
                        float fit = FitBesidePaths(tuft, toLocal, tuftWidth, tuftHeight, windLean, touches);
                        if (fit <= 0f) continue;
                        tuft.scale *= fit;
                    }
                    building.Add(tuft);
                }
            }
            if (building.Count == 0) return chunk;

            // Shuffled, so drawing only the first part of the list (further away) leaves an even thinning
            for (int i = building.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (building[i], building[j]) = (building[j], building[i]);
            }

            var bounds = new Bounds(building[0].position, Vector3.zero);
            foreach (Tuft made in building) bounds.Encapsulate(made.position);
            float tall = (nearMesh != null ? nearMesh.bounds.max.y : 1f) * biggest * (1f + heightVariation);
            bounds.Expand(new Vector3(tall * 2f, 0f, tall * 2f));
            bounds.max += Vector3.up * tall;
            chunk.Bounds = bounds;
            chunk.Count = building.Count;
            chunk.Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, building.Count, Marshal.SizeOf<Tuft>());
            chunk.Buffer.SetData(building);
            return chunk;
        }

        private int Hash(int x, int z)
        {
            unchecked
            {
                return (seed * 73856093) ^ (x * 19349663) ^ (z * 83492791);
            }
        }

        // ---------------------------------------------------------------- Beside paths

        private static readonly float[] Fits = { 1f, 0.7f, 0.45f };
        private const int RingPoints = 12;

        // How big the tuft mesh is (metres, at size 1): how far its blades spread from its roots (from above), how tall it
        // is, and how far the wind can push its top (per metre of height)
        private void TuftSize(out float width, out float height, out float windLean)
        {
            width = 0.3f;
            height = 1f;
            if (nearMesh != null)
            {
                Bounds b = nearMesh.bounds;
                width = Mathf.Max(Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), Mathf.Max(Mathf.Abs(b.min.z), Mathf.Abs(b.max.z)));
                height = Mathf.Max(b.max.y, 0.01f);
            }
            windLean = material != null && material.HasProperty("_WindStrength") ? Mathf.Abs(material.GetFloat("_WindStrength")) : 0f;
        }

        // The furthest any tuft can reach from its roots (from above): the biggest size, leaning the most
        private float MaxReach()
        {
            TuftSize(out float width, out float height, out float windLean);
            float biggest = Mathf.Max(size.x, size.y);
            float tall = height * biggest * (1f + heightVariation);
            float lean = Mathf.Tan(Mathf.Min(randomLean, 60f) * Mathf.Deg2Rad) +
                         Mathf.Tan(Mathf.Min(maxSlope + 4f, 73f) * Mathf.Deg2Rad) * followSlope;
            return width * biggest + tall * (lean + windLean);
        }

        // How much of its size a tuft keeps so none of it reaches over a path: 1 = all of it, less = shrunk to fit beside
        // the path (so the grass gets shorter toward the path's edge), 0 = no room at all (it isn't grown)
        private float FitBesidePaths(Tuft tuft, Matrix4x4 toLocal, float tuftWidth, float tuftHeight, float windLean, float touches)
        {
            foreach (float fit in Fits)
            {
                float scale = tuft.scale * fit;
                float height = tuftHeight * tuft.stretch * scale;
                Vector2 top = tuft.lean * height;   // (where its top leans to)
                Vector3 middle = tuft.position + new Vector3(top.x, 0f, top.y) * 0.5f;
                float radius = tuftWidth * scale + top.magnitude * 0.5f + windLean * height;
                if (!ReachesPath(middle, radius, toLocal, touches)) return fit;
            }
            return 0f;
        }

        // Whether any of a circle (from above, in the world) lies on a path
        private bool ReachesPath(Vector3 centre, float radius, Matrix4x4 toLocal, float touches)
        {
            if (PathAt(Flat(toLocal.MultiplyPoint3x4(centre))) >= touches) return true;
            for (int i = 0; i < RingPoints; i++)
            {
                float angle = i * (Mathf.PI * 2f / RingPoints);
                var way = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                if (PathAt(Flat(toLocal.MultiplyPoint3x4(centre + way * radius))) >= touches) return true;
                if (PathAt(Flat(toLocal.MultiplyPoint3x4(centre + way * (radius * 0.5f)))) >= touches) return true;
            }
            return false;
        }

        private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        // How much grass grows at a point: on green ground, not on paths or steep slopes or textures with no grass on them,
        // as painted by hand, in patches
        private float Amount(Color ground, Vector3 normal, Vector2 local, Vector3 world, int triangleLayer, float triangleAmount)
        {
            float grows = Green(ground);
            grows *= 1f - Smooth(maxSlope - 4f, maxSlope + 4f, Vector3.Angle(normal, Vector3.up));
            if (texturesMatter || triangleLayer > 0) grows *= TextureGrass(local, triangleLayer, triangleAmount);
            MaskAt(local, out float painted, out float sure);
            grows = Mathf.Lerp(grows, painted, sure);
            if (pathsClearGrass) grows *= 1f - Smooth(0.3f, 0.6f, PathAt(local));
            if (patchiness > 0f)
            {
                float noise = Mathf.PerlinNoise((world.x + 4111f + seed * 31f) / Mathf.Max(patchSize, 1f), (world.z + 2003f - seed * 17f) / Mathf.Max(patchSize, 1f));
                grows *= Mathf.Lerp(1f, Smooth(0.3f, 0.7f, noise) * 2f, patchiness);
            }
            return grows;
        }

        // How green a colour is: grass greens (and the drier yellow-greens) yes; soil, sand, rock and stone no
        private static float Green(Color colour)
        {
            Color.RGBToHSV(colour, out float h, out float s, out float v);
            float hue = Smooth(0.12f, 0.17f, h) * (1f - Smooth(0.45f, 0.55f, h));
            return hue * Smooth(0.12f, 0.28f, s) * Smooth(0.08f, 0.2f, v);
        }

        private static float Smooth(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private Color TuftColour(Color ground, Vector3 point, float roll)
        {
            Color colour = ground;
            if (!groundColour && colours.Count > 0)
            {
                float noise = Mathf.PerlinNoise((point.x - 911f) / (Mathf.Max(patchSize, 1f) * 1.7f), (point.z + 2459f) / (Mathf.Max(patchSize, 1f) * 1.7f));
                colour = colours[Mathf.Min(colours.Count - 1, Mathf.FloorToInt(Mathf.InverseLerp(0.25f, 0.75f, noise) * colours.Count))];
            }
            if (shadeVariation > 0f)
            {
                float step = (roll * 2f - 1f) * shadeVariation;
                Color.RGBToHSV(colour, out float h, out float s, out float v);
                colour = Color.HSVToRGB(Mathf.Repeat(h + step * 0.04f, 1f), Mathf.Clamp01(s * (1f - step * 0.3f)), Mathf.Clamp01(v * (1f + step)));
            }
            return colour;
        }

        private static uint Pack(Color colour)
        {
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(colour.r * 255f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(colour.g * 255f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(colour.b * 255f), 0, 255);
            return r | (g << 8) | (b << 16);
        }

        // ================================================================ The land

        private void ReadLand()
        {
            landRead = true;
            pieces.Clear();
            if (land == null) land = GameObject.Find("Land");
            if (land == null) return;
            Renderer landRenderer = null;
            foreach (MeshFilter filter in land.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!IsPiece(filter.transform)) continue;
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                Vector3[] points = mesh.vertices;
                if (points.Length < 6 || points.Length % 6 != 0) continue;

                // The grid of squares, from the first square's corners (the land is built back-left first)
                Vector3 a = points[0], c = points[1], third = points[2];
                float stepZ = c.z - a.z, stepX = third.x - a.x;
                if (stepX <= 1e-4f || stepZ <= 1e-4f) continue;
                Bounds local = mesh.bounds;
                int columns = Mathf.RoundToInt((local.max.x - a.x) / stepX);
                int rows = Mathf.RoundToInt((local.max.z - a.z) / stepZ);
                if (columns * rows * 6 != points.Length) continue;

                Color32[] ground = mesh.colors32;
                if (ground == null || ground.Length != points.Length)
                {
                    ground = new Color32[points.Length];
                    for (int i = 0; i < ground.Length; i++) ground[i] = new Color32(255, 255, 255, 255);
                }
                Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
                pieces.Add(new Piece
                {
                    Mesh = mesh,
                    Points = points,
                    Ground = ground,
                    Paint = ReadPaint(mesh, points.Length, 2),
                    GroundPaint = ReadPaint(mesh, points.Length, 4),
                    Textures = ReadPaint(mesh, points.Length, 3),
                    MinX = a.x,
                    MinZ = a.z,
                    StepX = stepX,
                    StepZ = stepZ,
                    Columns = columns,
                    Rows = rows,
                    ToWorld = toWorld,
                    ToLocal = filter.transform.worldToLocalMatrix,
                    WorldBounds = WorldBounds(local, toWorld),
                });
                if (landRenderer == null) filter.TryGetComponent(out landRenderer);
            }

            // The paths painted on the land (no grass on them)
            pathMap = null;
            pathCopy = null;
            landMaterial = landRenderer != null ? landRenderer.sharedMaterial : null;
        }

        // A Land child named like "Land 2_3" (not the plants and trees placed under it)
        private bool IsPiece(Transform t)
        {
            if (t.parent != land.transform) return false;
            string name = t.name;
            if (!name.StartsWith("Land ", System.StringComparison.Ordinal)) return false;
            int split = name.IndexOf('_', 5);
            return split > 5 && int.TryParse(name.Substring(5, split - 5), out _) && int.TryParse(name.Substring(split + 1), out _);
        }

        // A colour kept in a texture coordinate of a piece's vertices (2: the colour painted on its triangle, 3: the texture,
        // 4: the ground's colour painted by hand)
        private static Color32[] ReadPaint(Mesh mesh, int count, int channel)
        {
            if (!mesh.HasVertexAttribute(channel == 2 ? VertexAttribute.TexCoord2 : channel == 3 ? VertexAttribute.TexCoord3 : VertexAttribute.TexCoord4)) return null;
            var uvs = new List<Vector4>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count != count) return null;
            var paint = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                Vector4 p = uvs[i];
                paint[i] = new Color32((byte)Mathf.RoundToInt(p.x * 255f), (byte)Mathf.RoundToInt(p.y * 255f),
                                       (byte)Mathf.RoundToInt(p.z * 255f), (byte)Mathf.RoundToInt(p.w * 255f));
            }
            return paint;
        }

        private static Bounds WorldBounds(Bounds local, Matrix4x4 toWorld)
        {
            var bounds = new Bounds(toWorld.MultiplyPoint3x4(local.min), Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? local.max.x : local.min.x, (i & 2) != 0 ? local.max.y : local.min.y,
                                         (i & 4) != 0 ? local.max.z : local.min.z);
                bounds.Encapsulate(toWorld.MultiplyPoint3x4(corner));
            }
            return bounds;
        }

        // The ground straight below or above a point: where, which way its facet faces, its colour as it shows (with the
        // colours painted on it by hand), the land's own colour there (what the grass grows by: painting colours on the ground
        // changes the grass's colour, not where it grows), and where that is in the land's space
        private bool Ground(Vector3 world, out Vector3 point, out Vector3 normal, out Color colour, out Color growsOn, out Vector2 local,
            out Matrix4x4 toLocal, out int triangleLayer, out float triangleAmount)
        {
            foreach (Piece piece in pieces)
            {
                if (world.x < piece.WorldBounds.min.x || world.x > piece.WorldBounds.max.x ||
                    world.z < piece.WorldBounds.min.z || world.z > piece.WorldBounds.max.z) continue;
                Vector3 l = piece.ToLocal.MultiplyPoint3x4(world);
                int column = Mathf.FloorToInt((l.x - piece.MinX) / piece.StepX), row = Mathf.FloorToInt((l.z - piece.MinZ) / piece.StepZ);
                if (column < 0 || row < 0 || column >= piece.Columns || row >= piece.Rows) continue;
                int square = (row * piece.Columns + column) * 6;
                for (int t = 0; t < 2; t++)
                {
                    int v = square + t * 3;
                    Vector3 a = piece.Points[v], b = piece.Points[v + 1], c = piece.Points[v + 2];
                    float det = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    float w0 = ((b.z - c.z) * (l.x - c.x) + (c.x - b.x) * (l.z - c.z)) / det;
                    float w1 = ((c.z - a.z) * (l.x - c.x) + (a.x - c.x) * (l.z - c.z)) / det;
                    float w2 = 1f - w0 - w1;
                    const float edge = -1e-3f;
                    if (w0 < edge || w1 < edge || w2 < edge) continue;

                    float height = w0 * a.y + w1 * b.y + w2 * c.y;
                    colour = (Color)piece.Ground[v] * w0 + (Color)piece.Ground[v + 1] * w1 + (Color)piece.Ground[v + 2] * w2;
                    growsOn = colour;
                    growsOn.a = 1f;
                    if (piece.GroundPaint != null && v + 2 < piece.GroundPaint.Length)
                    {
                        // (kept as colour times how much, so it blends across the triangle like the land shows it)
                        Color painted = (Color)piece.GroundPaint[v] * w0 + (Color)piece.GroundPaint[v + 1] * w1 + (Color)piece.GroundPaint[v + 2] * w2;
                        if (painted.a > 0.002f) colour = colour * (1f - painted.a) + new Color(painted.r, painted.g, painted.b, 0f);
                    }
                    if (piece.Paint != null && piece.Paint[v].a > 0)
                    {
                        Color32 painted = piece.Paint[v];
                        colour = Color.Lerp(colour, new Color32(painted.r, painted.g, painted.b, 255), painted.a / 255f);
                    }
                    colour.a = 1f;
                    triangleLayer = 0;
                    triangleAmount = 0f;
                    if (piece.Textures != null && v < piece.Textures.Length && piece.Textures[v].g > 0)
                    {
                        triangleLayer = piece.Textures[v].r;
                        triangleAmount = piece.Textures[v].g / 255f;
                    }
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    if (n.y < 0f) n = -n;
                    normal = piece.ToWorld.MultiplyVector(n).normalized;
                    point = piece.ToWorld.MultiplyPoint3x4(new Vector3(l.x, height, l.z));
                    local = new Vector2(l.x, l.z);
                    toLocal = piece.ToLocal;
                    return true;
                }
            }
            point = world;
            normal = Vector3.up;
            colour = Color.white;
            growsOn = Color.white;
            local = Vector2.zero;
            toLocal = Matrix4x4.identity;
            triangleLayer = 0;
            triangleAmount = 0f;
            return false;
        }

        // The paths texture's pixels, read straight from the texture (so paths painted a moment ago count)
        private void ReadyPaths()
        {
            // (looked up each time: the paths texture is made the first time a path is painted)
            if (landMaterial != null && landMaterial.HasProperty("_PathMap"))
            {
                pathMap = landMaterial.GetTexture("_PathMap") as Texture2D;
                pathRect = landMaterial.GetVector("_PathRect");
                pathEdge = landMaterial.HasProperty("_PathEdge") ? Mathf.Clamp01(landMaterial.GetFloat("_PathEdge")) : 0f;
            }
            ReadyTextures();
            pathPixels = default;
            if (pathMap == null || !pathMap.isReadable) return;
            if (pathMap.format == TextureFormat.RGBA32)
            {
                pathPixels = pathMap.GetPixelData<Color32>(0);
                pathCopy = null;
            }
            else if (pathCopy == null)
            {
                pathCopy = pathMap.GetPixels32();
            }
        }

        // Where the land's painted textures are, and how much grass grows on each (from the Land material)
        private void ReadyTextures()
        {
            texturesMatter = false;
            texturePixels[0] = texturePixels[1] = default;
            if (landMaterial == null || !landMaterial.HasProperty("_TextureMap1")) return;
            bool lessGrass = false;
            for (int k = 0; k < layerGrass.Length; k++)
            {
                layerGrass[k] = landMaterial.HasProperty(LayerGrassNames[k]) ? Mathf.Clamp01(landMaterial.GetFloat(LayerGrassNames[k])) : 1f;
                if (layerGrass[k] < 0.999f) lessGrass = true;
            }
            for (int m = 0; m < 2; m++)
            {
                var map = landMaterial.GetTexture(m == 0 ? "_TextureMap1" : "_TextureMap2") as Texture2D;
                if (map != textureMaps[m]) textureCopies[m] = null;
                textureMaps[m] = map;
                if (map == null || !map.isReadable) continue;
                if (map.format == TextureFormat.RGBA32) texturePixels[m] = map.GetPixelData<Color32>(0);
                else if (textureCopies[m] == null) textureCopies[m] = map.GetPixels32();
            }
            texturesMatter = lessGrass && textureMaps[0] != null;
        }

        // How much grass grows by the textures painted at a point (x, z in the land's space): 1 = as much as anywhere
        private float TextureGrass(Vector2 local, int triangleLayer, float triangleAmount)
        {
            System.Array.Clear(weights, 0, weights.Length);
            if (texturesMatter && pathRect.z > 0f && pathRect.w > 0f && textureMaps[0] != null)
            {
                float u = (local.x - pathRect.x) * pathRect.z, v = (local.y - pathRect.y) * pathRect.w;
                if (u >= 0f && v >= 0f && u <= 1f && v <= 1f)
                {
                    for (int m = 0; m < 2; m++)
                    {
                        Texture2D map = textureMaps[m];
                        if (map == null) continue;
                        Color c = MapAt(m, map.width, map.height, u, v);
                        weights[m * 4] = c.r;
                        weights[m * 4 + 1] = c.g;
                        weights[m * 4 + 2] = c.b;
                        weights[m * 4 + 3] = c.a;
                    }
                }
            }
            if (triangleLayer >= 1 && triangleLayer <= 8 && triangleAmount > 0f)
            {
                for (int k = 0; k < 8; k++) weights[k] *= 1f - triangleAmount;
                weights[triangleLayer - 1] += triangleAmount;
            }
            float total = 0f;
            for (int k = 0; k < 8; k++) total += weights[k];
            float scale = total > 1f ? 1f / total : 1f;
            float less = 0f;
            for (int k = 0; k < 8; k++) less += weights[k] * scale * (1f - layerGrass[k]);
            return Mathf.Clamp01(1f - less);
        }

        // A painted-texture map at a point (u, v from 0 to 1), blended between its pixels
        private Color MapAt(int m, int width, int height, float u, float v)
        {
            float x = u * width - 0.5f, y = v * height - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            Color below = Color.Lerp(MapPixel(m, x0, y0, width, height), MapPixel(m, x0 + 1, y0, width, height), fx);
            Color above = Color.Lerp(MapPixel(m, x0, y0 + 1, width, height), MapPixel(m, x0 + 1, y0 + 1, width, height), fx);
            return Color.Lerp(below, above, fy);
        }

        private Color MapPixel(int m, int x, int y, int width, int height)
        {
            int index = Mathf.Clamp(y, 0, height - 1) * width + Mathf.Clamp(x, 0, width - 1);
            if (texturePixels[m].IsCreated) return index < texturePixels[m].Length ? (Color)texturePixels[m][index] : Color.clear;
            Color32[] copy = textureCopies[m];
            return copy != null && index < copy.Length ? (Color)copy[index] : Color.clear;
        }

        // How much path there is at a point (x, z in the land's space), blended between the paths texture's pixels just as
        // the land's shader sees it (its edge is where this is a half)
        private float PathAt(Vector2 local)
        {
            if (pathMap == null || pathRect.z <= 0f || pathRect.w <= 0f) return 0f;
            float u = (local.x - pathRect.x) * pathRect.z, v = (local.y - pathRect.y) * pathRect.w;
            if (u < 0f || v < 0f || u > 1f || v > 1f) return 0f;
            int width = pathMap.width, height = pathMap.height;
            float x = u * width - 0.5f, y = v * height - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float below = Mathf.Lerp(PathAlpha(x0, y0, width, height), PathAlpha(x0 + 1, y0, width, height), fx);
            float above = Mathf.Lerp(PathAlpha(x0, y0 + 1, width, height), PathAlpha(x0 + 1, y0 + 1, width, height), fx);
            return Mathf.Lerp(below, above, fy);
        }

        private float PathAlpha(int x, int y, int width, int height)
        {
            int index = Mathf.Clamp(y, 0, height - 1) * width + Mathf.Clamp(x, 0, width - 1);
            if (pathPixels.IsCreated) return index < pathPixels.Length ? pathPixels[index].a / 255f : 0f;
            if (pathCopy != null && index < pathCopy.Length) return pathCopy[index].a / 255f;
            return 0f;
        }

        // Whether any path is painted in or next to an area (x, z in the world), so the tufts there need checking
        private bool PathsNear(float minX, float minZ, float maxX, float maxZ)
        {
            if (pathMap == null || pieces.Count == 0 || pathRect.z <= 0f || pathRect.w <= 0f) return false;
            if (!pathPixels.IsCreated && pathCopy == null) return false;
            Matrix4x4 toLocal = pieces[0].ToLocal;
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 l = toLocal.MultiplyPoint3x4(new Vector3((i & 1) != 0 ? maxX : minX, 0f, (i & 2) != 0 ? maxZ : minZ));
                float u = (l.x - pathRect.x) * pathRect.z, v = (l.z - pathRect.y) * pathRect.w;
                u0 = Mathf.Min(u0, u);
                v0 = Mathf.Min(v0, v);
                u1 = Mathf.Max(u1, u);
                v1 = Mathf.Max(v1, v);
            }
            int width = pathMap.width, height = pathMap.height;
            int xFrom = Mathf.Max(0, Mathf.FloorToInt(u0 * width) - 1), xTo = Mathf.Min(width - 1, Mathf.CeilToInt(u1 * width) + 1);
            int yFrom = Mathf.Max(0, Mathf.FloorToInt(v0 * height) - 1), yTo = Mathf.Min(height - 1, Mathf.CeilToInt(v1 * height) + 1);
            for (int y = yFrom; y <= yTo; y++)
            {
                for (int x = xFrom; x <= xTo; x++)
                {
                    if (PathAlpha(x, y, width, height) > 0f) return true;
                }
            }
            return false;
        }

        // The hand-painted mask at a point: how much grass it asks for, and how sure (0 = left to the land)
        private void MaskAt(Vector2 local, out float amount, out float sure)
        {
            amount = 0f;
            sure = 0f;
            if (maskPixels == null || maskWidth <= 0 || maskArea.width <= 0f || maskArea.height <= 0f) return;
            float u = (local.x - maskArea.x) / maskArea.width * maskWidth - 0.5f;
            float v = (local.y - maskArea.y) / maskArea.height * maskHeight - 0.5f;
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            float fx = u - x0, fy = v - y0;
            float weighted = 0f, total = 0f;
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    int x = Mathf.Clamp(x0 + dx, 0, maskWidth - 1), y = Mathf.Clamp(y0 + dy, 0, maskHeight - 1);
                    Color32 pixel = maskPixels[y * maskWidth + x];
                    float w = (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy) * (pixel.a / 255f);
                    weighted += w * pixel.r / 255f;
                    total += w;
                }
            }
            sure = Mathf.Clamp01(total);
            amount = total > 1e-4f ? weighted / total : 0f;
        }
    }
}
