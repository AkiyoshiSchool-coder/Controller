using System.Collections.Generic;
using UnityEngine;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// A body of water: a flat surface at this object's height, as big as Size (x, z), drawn with the Paint Land/Water shader
    /// (clear and light at the shore, deeper colour further down, foam where it meets the land, gentle waves). It fills
    /// whatever land is lower than it, so a lake is just water put over a hollow (Paint Land > Water adds one; Paint Land >
    /// Shape digs the hollow). Move it up or down to change the water level. The Grass Field and the Scatter brush keep
    /// grass and plants out of it. It has no collider (nothing stands on it).
    /// Painted water (Paint Land > Water's brush) only shows where it's been painted: how much water there is at each spot
    /// is kept here (a map over its size, one byte a spot), and the surface is cut to it.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("World/Water")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LandWater : MonoBehaviour
    {
        [Tooltip("How big the water is (metres across, x and z). Its level is this object's height: move it up or down.")]
        public Vector2 size = new Vector2(200f, 200f);

        [SerializeField, HideInInspector] private byte[] maskData;     // painted water: how much water at each spot (0-255)
        [SerializeField, HideInInspector] private int maskWidth, maskHeight;

        private static readonly List<LandWater> all = new List<LandWater>();

        // With Enter Play Mode Options (no domain reload) statics keep their values between plays: drop any left over from before
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => all.RemoveAll(w => w == null || !w.isActiveAndEnabled);
        private static readonly int MaskId = Shader.PropertyToID("_WaterMask");
        private static readonly int MaskOnId = Shader.PropertyToID("_MaskOn");
        private static readonly int MaskSizeId = Shader.PropertyToID("_MaskSize");
        private Mesh mesh;
        private Texture2D maskTexture;
        private MaterialPropertyBlock block;
        private Vector2 builtSize;
        private Vector3 lastPosition, lastScale;
        private Quaternion lastRotation;

        /// <summary>The water in the open scenes.</summary>
        public static IReadOnlyList<LandWater> All => all;

        /// <summary>The water's level (its height in the world).</summary>
        public float Level => transform.position.y;

        /// <summary>Is a point in the world under any water (more than margin metres below its surface)?</summary>
        public static bool UnderWater(Vector3 world, float margin = 0f)
        {
            foreach (LandWater water in all)
            {
                if (water != null && water.isActiveAndEnabled && world.y < water.Level - margin && water.Covers(world)) return true;
            }
            return false;
        }

        /// <summary>Is a point in the world over or under this water (from above; for painted water, where it's painted)?</summary>
        public bool Covers(Vector3 world)
        {
            Vector3 local = transform.InverseTransformPoint(world);
            if (Mathf.Abs(local.x) > size.x * 0.5f || Mathf.Abs(local.z) > size.y * 0.5f) return false;
            return !Painted || MaskAt(world) >= 0.5f;
        }

        /// <summary>Is this painted water (it only shows where it's been painted)?</summary>
        public bool Painted => maskData != null && maskWidth > 0 && maskHeight > 0 && maskData.Length == maskWidth * maskHeight;

        /// <summary>Painted water: how much water at each spot (row by row from the -x, -z corner), for Paint Land to paint into.</summary>
        public byte[] MaskData => maskData;
        public int MaskWidth => maskWidth;
        public int MaskHeight => maskHeight;

        /// <summary>Gives it a new map of where the water is (Paint Land), and shows it.</summary>
        public void SetMask(byte[] data, int width, int height)
        {
            maskData = data;
            maskWidth = width;
            maskHeight = height;
            ApplyMask();
        }

        /// <summary>How much water is painted at a point in the world (0 to 1; 1 everywhere over unpainted water).</summary>
        public float MaskAt(Vector3 world)
        {
            if (!Painted) return 1f;
            Vector3 local = transform.InverseTransformPoint(world);
            float x = (local.x / size.x + 0.5f) * maskWidth - 0.5f, y = (local.z / size.y + 0.5f) * maskHeight - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float below = Mathf.Lerp(MaskPixel(x0, y0), MaskPixel(x0 + 1, y0), fx);
            float above = Mathf.Lerp(MaskPixel(x0, y0 + 1), MaskPixel(x0 + 1, y0 + 1), fx);
            return Mathf.Lerp(below, above, fy);
        }

        private float MaskPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= maskWidth || y >= maskHeight) return 0f;
            return maskData[y * maskWidth + x] / 255f;
        }

        /// <summary>
        /// Shows the map of where the water's painted (after it was painted into). mips: work out how it looks from far
        /// off too (false while it's being painted, to keep the brush quick: it's done again when the stroke ends).
        /// </summary>
        public void ApplyMask(bool mips = true)
        {
            if (!TryGetComponent(out MeshRenderer shown)) return;
            block ??= new MaterialPropertyBlock();
            block.Clear();
            if (Painted)
            {
                if (maskTexture == null || maskTexture.width != maskWidth || maskTexture.height != maskHeight)
                {
                    DestroyMaskTexture();
                    mips = true;   // (a new one: all of it)
                    maskTexture = new Texture2D(maskWidth, maskHeight, TextureFormat.R8, true, true)
                    {
                        name = "Painted Water",
                        hideFlags = HideFlags.DontSave,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear,
                    };
                }
                maskTexture.SetPixelData(maskData, 0);
                maskTexture.Apply(mips);
                block.SetTexture(MaskId, maskTexture);
                block.SetFloat(MaskOnId, 1f);
                block.SetVector(MaskSizeId, new Vector4(size.x, size.y, maskWidth, maskHeight));
            }
            else
            {
                block.SetTexture(MaskId, Texture2D.whiteTexture);
                block.SetFloat(MaskOnId, 0f);
                block.SetVector(MaskSizeId, new Vector4(size.x, size.y, 1f, 1f));
            }
            shown.SetPropertyBlock(block);
        }

        private void DestroyMaskTexture()
        {
            if (maskTexture == null) return;
            if (Application.isPlaying) Destroy(maskTexture);
            else DestroyImmediate(maskTexture);
            maskTexture = null;
        }

        /// <summary>After its size was changed from a script (Paint Land): the surface is made again, and (grass) the grass told.</summary>
        public void Refresh(bool grass = true)
        {
            if (isActiveAndEnabled) Build();
            ApplyMask();
            Remember();
            if (grass) GrassField.LandChanged();
        }

        private void OnEnable()
        {
            if (!all.Contains(this)) all.Add(this);
            Build();
            ApplyMask();
            Remember();
            GrassField.LandChanged();   // (no grass under it)
        }

        private void OnDisable()
        {
            all.Remove(this);
            GrassField.LandChanged();
        }

        private void OnDestroy()
        {
            DestroyMaskTexture();
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }

        private void OnValidate()
        {
            size = new Vector2(Mathf.Max(size.x, 1f), Mathf.Max(size.y, 1f));
            // (only reshaping the mesh it already shows: swapping meshes isn't allowed in here)
            if (mesh != null && size != builtSize && TryGetComponent(out MeshFilter filter) && filter.sharedMesh == mesh) Build();
            if (mesh != null) ApplyMask();   // (painted again, or put back by Ctrl+Z)
            GrassField.LandChanged();
        }

        private void Update()
        {
            if (mesh == null || size != builtSize || (TryGetComponent(out MeshFilter filter) && filter.sharedMesh != mesh)) Build();

            // Moved (a new water level, say): the grass is worked out again
            Transform t = transform;
            if (t.position == lastPosition && t.rotation == lastRotation && t.lossyScale == lastScale) return;
            Remember();
            GrassField.LandChanged();
        }

        private void Remember()
        {
            lastPosition = transform.position;
            lastRotation = transform.rotation;
            lastScale = transform.lossyScale;
        }

        // A flat grid of squares, Size across, at the object's height (made when needed, not saved with the scene)
        private void Build()
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = "Water", hideFlags = HideFlags.DontSave };
            }
            const int squares = 16;
            var vertices = new Vector3[(squares + 1) * (squares + 1)];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            for (int z = 0; z <= squares; z++)
            {
                for (int x = 0; x <= squares; x++)
                {
                    int i = z * (squares + 1) + x;
                    float u = x / (float)squares, v = z / (float)squares;
                    vertices[i] = new Vector3((u - 0.5f) * size.x, 0f, (v - 0.5f) * size.y);
                    normals[i] = Vector3.up;
                    uvs[i] = new Vector2(u, v);
                }
            }
            var triangles = new int[squares * squares * 6];
            int n = 0;
            for (int z = 0; z < squares; z++)
            {
                for (int x = 0; x < squares; x++)
                {
                    int a = z * (squares + 1) + x, b = a + 1, c = a + squares + 1, d = c + 1;
                    triangles[n++] = a;
                    triangles[n++] = c;
                    triangles[n++] = d;
                    triangles[n++] = a;
                    triangles[n++] = d;
                    triangles[n++] = b;
                }
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(size.x, 2f, size.y));
            builtSize = size;
            MeshFilter shown = GetComponent<MeshFilter>();
            if (shown.sharedMesh != mesh) shown.sharedMesh = mesh;
        }
    }
}
