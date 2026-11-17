#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. How Tools > Paint Land > Rebuild Land turns a land model (any mesh or model) into the game's land: a
    /// mesh of evenly sized triangles over the model's shape, with its hills rounded off, a few small bumps, and
    /// Astroneer-style colours painted into its vertex colours (grass that's richer in the hollows and drier up high, soil
    /// on the slopes). Change these and press Rebuild Land (below, or in the Tools menu). The land's material (Land.mat,
    /// next to this) shows the vertex colours and can take a texture, such as grass, on top.
    /// Paths, coloured-in triangles and textures are painted on, and the land raised and lowered, with Tools > Paint Land >
    /// Paint Land (the palette it offers is here), and they stay when the land is rebuilt.
    /// </summary>
    public class LandSettings : ScriptableObject
    {
        [Tooltip("The land model (any mesh or model, e.g. made in Blender). Its shape is followed; it's left as it is.")]
        public GameObject source;

        [Header("Shape")]
        [Tooltip("How big the triangles are (metres across). Smaller is smoother, but makes more triangles.")]
        [Min(1f)] public float triangleSize = 8f;
        [Tooltip("How much the hills are rounded off between the model's points (0 = as flat-sided as the model).")]
        [Range(0f, 1f)] public float roundness = 0.75f;
        [Tooltip("Small bumps all over the land (metres high), so it catches the light like Astroneer's. 0 = none.")]
        [Min(0f)] public float bumps = 0.8f;
        [Tooltip("How wide the bumps are (metres).")]
        [Min(1f)] public float bumpSize = 30f;

        [Header("Colours")]
        [Tooltip("Paint the colours below into the land. Off: keep the model's own vertex colours (e.g. painted in Blender), or white.")]
        public bool paintColours = true;
        public Color lowGrass = new Color(0.31f, 0.64f, 0.35f);
        public Color grass = new Color(0.49f, 0.79f, 0.35f);
        [Tooltip("Lighter patches here and there in the grass.")]
        public Color lightGrass = new Color(0.61f, 0.84f, 0.39f);
        [Tooltip("The grass up on the hills.")]
        public Color highGrass = new Color(0.76f, 0.81f, 0.39f);
        [Tooltip("The lowest ground.")]
        public Color sand = new Color(0.9f, 0.83f, 0.6f);
        [Tooltip("Steep slopes.")]
        public Color soil = new Color(0.85f, 0.56f, 0.35f);
        [Tooltip("The tops of the highest hills.")]
        public Color rock = new Color(0.79f, 0.64f, 0.48f);
        [Tooltip("Slopes start turning to soil at this steepness (degrees)...")]
        [Range(0f, 90f)] public float soilFrom = 18f;
        [Tooltip("...and are all soil from this steepness.")]
        [Range(0f, 90f)] public float soilFull = 30f;
        [Tooltip("How much darker the hollows are.")]
        [Range(0f, 0.5f)] public float hollowShade = 0.18f;
        [Tooltip("Change for different patches and bumps.")]
        public int seed = 1;

        [Header("Pieces")]
        [Tooltip("The land is split into square pieces this many triangles across, so only the pieces in view are drawn.")]
        [Range(8, 250)] public int pieceSize = 50;

        [Header("Painting (Tools > Paint Land)")]
        [Tooltip("How fine painted paths are: metres per pixel of the paths texture (Land Paths.png), 0.25 to 4. Smaller is " +
                 "sharper, but makes a bigger texture. Changing it resizes the paths already painted.")]
        [Delayed] public float pathDetail = 1f;
        [Tooltip("How fine textures painted with the soft brush are: metres per pixel of Land Textures 1-4.png and 5-8.png " +
                 "(0.5 to 4). Smaller is sharper, but makes bigger textures. Changing it resizes what's already painted.")]
        [Delayed] public float textureDetail = 1f;
        [Tooltip("The colours to pick from when painting.")]
        public List<LandPaintColour> palette = new List<LandPaintColour>
        {
            new LandPaintColour("Dirt path", new Color(0.62f, 0.42f, 0.27f)),
            new LandPaintColour("Light path", new Color(0.82f, 0.66f, 0.45f)),
            new LandPaintColour("Stone path", new Color(0.62f, 0.62f, 0.6f)),
            new LandPaintColour("Sand", new Color(0.9f, 0.83f, 0.6f)),
            new LandPaintColour("Soil", new Color(0.85f, 0.56f, 0.35f)),
            new LandPaintColour("Clay", new Color(0.8f, 0.42f, 0.32f)),
            new LandPaintColour("Rock", new Color(0.79f, 0.64f, 0.48f)),
            new LandPaintColour("Dark grass", new Color(0.27f, 0.55f, 0.3f)),
            new LandPaintColour("Grass", new Color(0.49f, 0.79f, 0.35f)),
            new LandPaintColour("Light grass", new Color(0.61f, 0.84f, 0.39f)),
            new LandPaintColour("Dry grass", new Color(0.76f, 0.81f, 0.39f)),
            new LandPaintColour("Snow", new Color(0.94f, 0.96f, 0.98f)),
            new LandPaintColour("Water", new Color(0.35f, 0.62f, 0.85f)),
            new LandPaintColour("Purple", new Color(0.6f, 0.45f, 0.75f)),
        };

        [Header("Scattering (the Scatter tab in Paint Land)")]
        [Tooltip("The kinds of things the Scatter brush puts on the land (grass, trees, rocks...). Tick them in Paint Land.")]
        public List<LandScatterGroup> scatter = new List<LandScatterGroup>();

        [HideInInspector] public bool scatterSetUp;  // the nature pack's kinds have been added once
        [HideInInspector] public int scatterVersion; // which version of the kinds' settings they've been brought up to
        [HideInInspector] public int builtTriangles;
        [HideInInspector] public int builtPieces;
        [HideInInspector] public Rect pathArea;     // where the paths texture lies over the land (x, z, from above)
        [HideInInspector] public int paintStep;     // how many paint strokes along the undo history is (so Ctrl+Z can undo them)
        [HideInInspector] public float colourLow, colourHigh;   // the land's lowest and highest built height (its colours go by it)
        [HideInInspector] public List<string> textureNames = new List<string>();   // the 8 painted textures' names (Paint Land)
        [HideInInspector] public bool texturesSetUp;   // the first textures have been put in (once)
    }

    /// <summary>The collider each scattered thing gets, so you can't walk through it (Paint Land > Scatter).</summary>
    public enum ScatterCollider
    {
        None,    // walk through (grass, flowers)
        Trunk,   // a pole round the trunk, the branches left free (trees)
        Shape,   // its exact shape (rocks, stumps, logs)
        Box      // a box round it
    }

    /// <summary>
    /// One prefab in a scatter kind: whether it's used, how often it's picked next to the others, how many grow together in
    /// a clump, and the colours each of its parts can be (petals, leaves, trunk...). The same prefab can be in a kind more
    /// than once (say red poppies and white poppies).
    /// </summary>
    [System.Serializable]
    public class ScatterPrefab
    {
        public GameObject prefab;
        [Tooltip("Off: not scattered (and not rubbed out) by the brush.")]
        public bool on = true;
        [Tooltip("How often this one is picked, next to the others in the kind (2 = twice as often as a 1).")]
        [Range(0.05f, 5f)] public float chance = 1f;
        [Tooltip("How many grow together in each clump of this one, from-to (1-1 = each on its own).")]
        public Vector2Int clump;
        [Tooltip("The colours each part of it can be (Petals, Leaves, Trunk...), one picked for each clump. A part with none " +
                 "takes the kind's Plant Colours (or its own).")]
        public List<ScatterPartColours> partColours = new List<ScatterPartColours>();
        [SerializeField, HideInInspector] public List<Color> petalColours = new List<Color>();   // (before part colours)

        public ScatterPrefab() { }
        public ScatterPrefab(GameObject prefab) { this.prefab = prefab; }

        /// <summary>A part's colours (empty if it has none).</summary>
        public List<Color> ColoursFor(string part)
        {
            foreach (ScatterPartColours p in partColours)
            {
                if (p != null && p.part == part) return p.colours;
            }
            var made = new ScatterPartColours { part = part };
            partColours.Add(made);
            return made.colours;
        }

        /// <summary>A part's colours, set (none: the part has none of its own).</summary>
        public void SetColours(string part, List<Color> colours)
        {
            List<Color> list = ColoursFor(part);
            var copy = new List<Color>(colours);
            list.Clear();
            list.AddRange(copy);
        }

        public ScatterPrefab Copy()
        {
            var copy = new ScatterPrefab { prefab = prefab, on = on, chance = chance, clump = clump };
            foreach (ScatterPartColours p in partColours)
            {
                if (p != null) copy.partColours.Add(new ScatterPartColours { part = p.part, colours = new List<Color>(p.colours) });
            }
            return copy;
        }
    }

    /// <summary>The colours one part of a scattered prefab can be (its Petals, Leaves, Trunk...).</summary>
    [System.Serializable]
    public class ScatterPartColours
    {
        public string part;
        public List<Color> colours = new List<Color>();
    }

    /// <summary>A kind of thing the Scatter brush puts on the land, e.g. grass: which prefabs, how many, and how.</summary>
    [System.Serializable]
    public class LandScatterGroup
    {
        public string name = "New kind";
        [Tooltip("Scatter this kind (ticked in Paint Land).")]
        public bool on = true;
        [Tooltip("The prefabs to pick from (at random), each with its own tick, chance and petal colours.")]
        public List<ScatterPrefab> entries = new List<ScatterPrefab>();
        [Tooltip("Every prefab in the kind (kept from Entries; read by the other land tools).")]
        public List<GameObject> prefabs = new List<GameObject>();
        [SerializeField, HideInInspector] private bool entriesMade;

        /// <summary>Makes Entries from Prefabs the first time (kinds made before entries), then keeps Prefabs matching them.</summary>
        public void SyncPrefabs()
        {
            if (!entriesMade)
            {
                entriesMade = true;
                if (entries.Count == 0) foreach (GameObject prefab in prefabs) entries.Add(new ScatterPrefab(prefab));
            }
            prefabs.Clear();
            foreach (ScatterPrefab entry in entries)
            {
                if (entry == null) continue;
                // Petal colours from before part colours; clumps as the kind's Clump Size made them (it to one either side)
                if (entry.petalColours != null && entry.petalColours.Count > 0)
                {
                    entry.SetColours("Petals", entry.petalColours);
                    entry.petalColours.Clear();
                }
                if (entry.clump.x < 1 || entry.clump.y < 1)
                {
                    entry.clump = clumpSize <= 1 ? new Vector2Int(1, 1)
                                                 : new Vector2Int(Mathf.Max(1, clumpSize - 1), Mathf.Min(12, clumpSize + 1));
                }
                if (entry.prefab != null && !prefabs.Contains(entry.prefab)) prefabs.Add(entry.prefab);
            }
        }

        /// <summary>How many grow in a clump on average, over the prefabs switched on (as often as each is picked).</summary>
        public float AverageClump()
        {
            float total = 0f, weight = 0f;
            foreach (ScatterPrefab entry in entries)
            {
                if (entry == null || !entry.on || entry.prefab == null) continue;
                float chance = Mathf.Max(entry.chance, 0.01f);
                total += (Mathf.Max(1, entry.clump.x) + Mathf.Max(1, entry.clump.y)) * 0.5f * chance;
                weight += chance;
            }
            return weight > 0f ? Mathf.Max(1f, total / weight) : Mathf.Max(1, clumpSize);
        }
        [Tooltip("How many in each 10 x 10 m of land.")]
        [Min(0.01f)] public float density = 5f;
        [Tooltip("Random size, from-to (1 = the prefab's own size).")]
        public Vector2 size = new Vector2(0.8f, 1.2f);
        [Tooltip("How much they lean with the slope of the ground (0 = straight up, 1 = square to the ground).")]
        [Range(0f, 1f)] public float followSlope = 0.5f;
        [Tooltip("Not on ground steeper than this (degrees).")]
        [Range(0f, 90f)] public float maxSlope = 90f;
        [Tooltip("How far into the ground they're pushed (metres), so they don't float on slopes.")]
        [Min(0f)] public float sink = 0.05f;

        [Tooltip("How many grew together in each clump before each prefab had its own Clumps (now just where those start from).")]
        [Range(1, 10)] public int clumpSize = 1;
        [Tooltip("How patchy they are: 0 = spread evenly, 1 = thick patches with bare ground between.")]
        [Range(0f, 1f)] public float patchiness;
        [Tooltip("How wide the patches are (metres).")]
        [Min(1f)] public float patchSize = 15f;
        [Tooltip("Random lean each way (degrees), so they don't all stand the same.")]
        [Range(0f, 45f)] public float randomLean;
        [Tooltip("How much taller or shorter than wide each one can be (0 = all the same shape).")]
        [Range(0f, 0.8f)] public float heightVariation;

        [Tooltip("The colours they can be, picked at random (in patches). Empty: the prefab's own colours. Works with the " +
                 "nature pack's plants (and any material with a Top Color, Base Color or Color).")]
        public List<Color> colours = new List<Color>();
        [Tooltip("How much each one's shade varies from the colour picked.")]
        [Range(0f, 0.4f)] public float shadeVariation = 0.15f;
        [Tooltip("How much darker they are at the bottom (plants with a top-to-bottom colour gradient).")]
        [Range(0f, 1f)] public float bottomShade = 0.6f;

        [Tooltip("Painting a path deletes these where it goes (Paint Land > Paths > Clear Under New Paths).")]
        public bool clearOnPaths;

        [Tooltip("The collider each one gets when it's painted (with Add Colliders on), so you can't walk through it: round " +
                 "the trunk (trees), its exact shape (rocks, stumps, logs), a box, or none (grass, flowers).")]
        public ScatterCollider collider = ScatterCollider.None;

        [System.NonSerialized] public bool open;   // (shown open in Paint Land)
    }

    /// <summary>A colour in the painter's palette.</summary>
    [System.Serializable]
    public class LandPaintColour
    {
        public string name;
        public Color colour = Color.white;

        public LandPaintColour() { }

        public LandPaintColour(string name, Color colour)
        {
            this.name = name;
            this.colour = colour;
        }
    }

    [CustomEditor(typeof(LandSettings))]
    internal class LandSettingsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var settings = (LandSettings)target;
            float detail = settings.pathDetail;
            float textureDetail = settings.textureDetail;
            DrawDefaultInspector();
            if (!Mathf.Approximately(detail, settings.pathDetail)) LandPaint.ResizePaths();
            if (!Mathf.Approximately(textureDetail, settings.textureDetail)) LandTextures.Resize();
            EditorGUILayout.Space();
            if (settings.builtTriangles > 0)
            {
                EditorGUILayout.HelpBox($"Last built: {settings.builtTriangles:N0} triangles in {settings.builtPieces} pieces.", MessageType.None);
            }
            if (GUILayout.Button("Rebuild Land", GUILayout.Height(28))) LandBuilder.Build(settings);
            if (GUILayout.Button("Paint Land (paths, colours, textures, shape)", GUILayout.Height(24))) LandPainter.Open();
        }
    }
}
#endif
