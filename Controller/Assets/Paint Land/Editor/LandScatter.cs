#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. What the Scatter tab of Tools > Paint Land does: scatters the ticked kinds of things
    /// (Land Settings > scatter: grass, trees, rocks...) over the land under the brush. Each kind fills the ground up to its
    /// own density (so going over the same spot again doesn't pile more on), growing in clumps (several together) and in
    /// patches (thick in places, sparse in others, following a noise pattern), standing on the ground (leaning with the slope
    /// as much as the kind says, plus a random lean), turned, sized and stretched at random, bigger in the middle of a clump.
    /// A kind can have colours: each clump picks one (in patches too) with a slightly different shade per plant, using a
    /// tinted copy of the plant's material (kept in Assets/Paint Land/Data/Materials/Scatter Colours, one per colour and shade, so they
    /// still draw fast). Shift (the eraser) takes away the ticked kinds under the brush instead, including ones placed by
    /// hand or with Polybrush before.
    /// With Add Colliders on, each one gets its kind's collider, so you can't walk through it: round the trunk for trees,
    /// the exact shape for rocks, stumps and logs (on a child called "Collider (Paint Land)"); the ones already there can
    /// be given them too. A prefab with colliders of its own keeps those instead.
    /// What it scatters goes under "Land Scatter" in the scene (a child for each kind). Each stroke is one step for
    /// Ctrl+Z, and it's all ordinary scene objects, so it's saved with the scene. The Scatter Renderer on Land Scatter
    /// draws them all instanced (how far each kind is drawn is set there); what's placed is kept in 4 m squares during a
    /// stroke, so painting stays quick however much is already scattered.
    /// </summary>
    internal static class LandScatter
    {
        internal const string ContainerName = "Land Scatter";
        private const string NatureFolder = "Assets/Paint Land/Sample/Prefabs/";
        private const string ColoursFolder = "Assets/Paint Land/Data/Materials/Scatter Colours";
        private const int Version = 6;
        private const int Tries = 14;

        private sealed class Placed
        {
            public GameObject Go;
            public GameObject Prefab;
            public Vector3 Position;
            public bool CanRemove;
            public float Spread;      // how far it reaches out from where it stands (from above), when clearing under paths
        }

        // What's in the scene already (read at the start of a stroke), in 4 m squares so a dab only looks at what's near it
        private const float CellSize = 4f;
        private static readonly Dictionary<Vector2Int, List<Placed>> placed = new Dictionary<Vector2Int, List<Placed>>();
        private static readonly List<Placed> near = new List<Placed>();
        private static readonly Dictionary<GameObject, LandScatterGroup> kindOf = new Dictionary<GameObject, LandScatterGroup>();
        private static readonly List<LandScatterGroup> ticked = new List<LandScatterGroup>();
        private static readonly Dictionary<string, Material> tints = new Dictionary<string, Material>();
        private static int undoGroup = -1;

        // ================================================================ The kinds

        /// <summary>
        /// The first time: kinds made from the nature pack's grass, flowers, mushrooms, bushes, rocks and trees. Kinds made by
        /// an older version of this get the newer settings (clumps, patches, colours).
        /// </summary>
        internal static void SetUpKinds(LandSettings settings)
        {
            if (settings.scatterSetUp && settings.scatterVersion >= Version) return;
            Undo.RecordObject(settings, "Set Up Scatter");
            if (!settings.scatterSetUp)
            {
                settings.scatterSetUp = true;
                foreach (string name in new[] { "Grass", "Flowers", "Mushrooms", "Bushes", "Rocks", "Trees", "Stumps and logs" })
                {
                    LandScatterGroup kind = Default(name, settings);
                    if (kind == null || kind.prefabs.Count == 0) continue;
                    kind.clearOnPaths = ClearsOnPaths(name);
                    settings.scatter.Add(kind);
                }
            }
            else if (settings.scatterVersion < 2)
            {
                foreach (LandScatterGroup kind in settings.scatter)
                {
                    LandScatterGroup better = kind != null ? Default(kind.name, settings) : null;
                    if (better == null) continue;
                    kind.density = better.density;
                    kind.size = better.size;
                    kind.followSlope = better.followSlope;
                    kind.maxSlope = better.maxSlope;
                    kind.sink = better.sink;
                    kind.clumpSize = better.clumpSize;
                    kind.patchiness = better.patchiness;
                    kind.patchSize = better.patchSize;
                    kind.randomLean = better.randomLean;
                    kind.heightVariation = better.heightVariation;
                    if (kind.colours.Count == 0) kind.colours = better.colours;
                    kind.shadeVariation = better.shadeVariation;
                    kind.bottomShade = better.bottomShade;
                }
            }
            if (settings.scatterVersion < 3) FixColours();
            if (settings.scatterSetUp && settings.scatterVersion < 4)
            {
                foreach (LandScatterGroup kind in settings.scatter)
                {
                    if (kind != null) kind.clearOnPaths = ClearsOnPaths(kind.name);
                }
            }
            if (settings.scatterSetUp && settings.scatterVersion < 5)
            {
                // Colliders came in: trees, rocks, stumps and logs get them (grass and flowers don't)
                foreach (LandScatterGroup kind in settings.scatter)
                {
                    if (kind != null) kind.collider = GuessCollider(kind.name, kind.prefabs);
                }
            }
            // Prefabs became entries (each with a tick, a chance and petal colours)
            foreach (LandScatterGroup kind in settings.scatter) kind?.SyncPrefabs();
            settings.scatterVersion = Version;
            EditorUtility.SetDirty(settings);
        }

        // Paths clear the small things by default (not rocks, trees, stumps: tick those in the Paths tab if wanted)
        private static bool ClearsOnPaths(string name) => name == "Grass" || name == "Flowers" || name == "Mushrooms" || name == "Bushes";

        // The nature pack's kinds, as they start out
        private static LandScatterGroup Default(string name, LandSettings settings)
        {
            switch (name)
            {
                case "Grass":
                    return Kind(name, true, 60f, 0.8f, 1.6f, 0.7f, 60f, 0.05f, 5, 0.55f, 9f, 12f, 0.35f, new[] { "Grass Tuft" },
                        Brighter(settings.grass), Brighter(settings.lightGrass), Brighter(Color.Lerp(settings.lowGrass, settings.grass, 0.4f)));
                case "Flowers":
                    return Kind(name, false, 8f, 0.8f, 1.3f, 0.6f, 50f, 0.02f, 4, 0.75f, 7f, 10f, 0.3f, new[] { "Flower" });
                case "Mushrooms":
                    return Kind(name, false, 2f, 0.7f, 1.4f, 0.5f, 60f, 0.02f, 4, 0.7f, 6f, 8f, 0.25f, new[] { "Mushroom" });
                case "Bushes":
                    return Kind(name, false, 1.5f, 0.8f, 1.4f, 0.3f, 50f, 0.1f, 2, 0.5f, 14f, 5f, 0.2f, new[] { "Bush" });
                case "Rocks":
                    return Kind(name, false, 0.8f, 0.5f, 1.5f, 1f, 90f, 0.15f, 3, 0.5f, 20f, 10f, 0.3f,
                        new[] { "Rock A", "Rock B" });
                case "Trees":
                    return Kind(name, false, 0.3f, 0.8f, 1.25f, 0f, 35f, 0.2f, 1, 0.6f, 35f, 3f, 0.15f,
                        new[] { "Pine Tree", "Round Tree" });
                case "Stumps and logs":
                    return Kind(name, false, 0.15f, 0.8f, 1.2f, 0.8f, 40f, 0.1f, 1, 0.3f, 25f, 5f, 0.15f,
                        new[] { "Stump", "Log" });
                default:
                    return null;
            }
        }

        private static LandScatterGroup Kind(string name, bool on, float density, float smallest, float biggest, float followSlope,
            float maxSlope, float sink, int clumpSize, float patchiness, float patchSize, float randomLean, float heightVariation,
            string[] prefabs, params Color[] colours)
        {
            var kind = new LandScatterGroup
            {
                name = name, on = on, density = density, size = new Vector2(smallest, biggest), followSlope = followSlope,
                maxSlope = maxSlope, sink = sink, clumpSize = clumpSize, patchiness = patchiness, patchSize = patchSize,
                randomLean = randomLean, heightVariation = heightVariation, colours = new List<Color>(colours),
                shadeVariation = 0.18f, bottomShade = 0.65f,
            };
            foreach (string prefab in prefabs)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(NatureFolder + prefab + ".prefab");
                if (asset != null) kind.prefabs.Add(asset);
            }
            kind.SyncPrefabs();
            return kind;
        }

        /// <summary>The land's own greens (from Land Settings), for grass that matches the ground.</summary>
        internal static List<Color> LandColours(LandSettings settings)
        {
            var colours = new List<Color> { settings.grass, settings.lightGrass, Color.Lerp(settings.lowGrass, settings.grass, 0.4f) };
            for (int i = 0; i < colours.Count; i++)
            {
                Color c = colours[i];
                c.a = 1f;
                colours[i] = c;
            }
            return colours;
        }

        // A little brighter than the ground, so the grass stands out from it
        private static Color Brighter(Color colour)
        {
            Color.RGBToHSV(colour, out float h, out float s, out float v);
            Color result = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.05f), Mathf.Clamp01(v * 1.08f));
            result.a = 1f;
            return result;
        }

        /// <summary>The prefabs in what was dragged in: prefabs themselves, and every prefab in any folder.</summary>
        internal static List<GameObject> PrefabsIn(Object[] dragged)
        {
            var found = new List<GameObject>();
            foreach (Object thing in dragged)
            {
                string path = AssetDatabase.GetAssetPath(thing);
                if (thing is GameObject go && !string.IsNullOrEmpty(path) && PrefabUtility.IsPartOfPrefabAsset(go))
                {
                    found.Add(go);
                }
                else if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                        if (prefab != null && !found.Contains(prefab)) found.Add(prefab);
                    }
                }
            }
            return found;
        }

        // ================================================================ Strokes

        /// <summary>Gets a stroke ready. Returns null, or what's stopping it.</summary>
        internal static string Begin(bool erase)
        {
            LandSettings settings = LandBuilder.GetSettings();
            SetUpKinds(settings);
            ticked.Clear();
            kindOf.Clear();
            foreach (LandScatterGroup kind in settings.scatter)
            {
                if (kind == null || !kind.on) continue;
                kind.SyncPrefabs();
                bool any = false;
                foreach (ScatterPrefab entry in kind.entries)
                {
                    if (entry == null || !entry.on || entry.prefab == null) continue;   // (switched off: left alone)
                    any = true;
                    if (!kindOf.ContainsKey(entry.prefab)) kindOf[entry.prefab] = kind;
                }
                if (any) ticked.Add(kind);
            }
            if (ticked.Count == 0) return "Tick at least one kind of thing to scatter (with a prefab in it switched on).";
            if (LandPaint.Land == null) return "Start painting on the land first.";

            // What's already in the scene (to keep things apart, and to rub out). Under Land Scatter only each kind's own
            // children are looked at (not every part inside them); anywhere else, everything (things placed by hand).
            placed.Clear();
            ScatterSceneProcessor.Ready(LandPaint.Land.scene);
            foreach (GameObject root in LandPaint.Land.scene.GetRootGameObjects())
            {
                if (root.name == ContainerName)
                {
                    foreach (Transform kind in root.transform)
                    {
                        foreach (Transform thing in kind) Read(thing);
                    }
                    continue;
                }
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) Read(t);
            }

            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(erase ? "Remove Scattered Things" : "Scatter");
            return null;
        }

        private static void Read(Transform t)
        {
            GameObject go = t.gameObject;
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) return;
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (prefab == null || !kindOf.ContainsKey(prefab)) return;
            Add(new Placed
            {
                Go = go,
                Prefab = prefab,
                Position = t.position,
                CanRemove = PrefabUtility.IsOutermostPrefabInstanceRoot(go) || PrefabUtility.IsAddedGameObjectOverride(go),
            });
        }

        private static Vector2Int Cell(Vector3 point) => new Vector2Int(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.z / CellSize));

        private static void Add(Placed thing)
        {
            Vector2Int cell = Cell(thing.Position);
            if (!placed.TryGetValue(cell, out List<Placed> list)) placed[cell] = list = new List<Placed>();
            list.Add(thing);
        }

        // Everything placed within a distance (from above) of a point, into 'near'
        private static void Near(Vector3 point, float radius)
        {
            near.Clear();
            Vector2Int from = Cell(point - new Vector3(radius, 0f, radius)), to = Cell(point + new Vector3(radius, 0f, radius));
            float reach = radius * radius;
            for (int x = from.x; x <= to.x; x++)
            {
                for (int z = from.y; z <= to.y; z++)
                {
                    if (!placed.TryGetValue(new Vector2Int(x, z), out List<Placed> list)) continue;
                    foreach (Placed thing in list)
                    {
                        if (Flat(thing.Position - point).sqrMagnitude <= reach) near.Add(thing);
                    }
                }
            }
        }

        /// <summary>Scatters (or rubs out) the ticked kinds in a circle round a point on the land.</summary>
        internal static void Dab(Vector3 centre, float radius, bool erase)
        {
            if (undoGroup < 0) return;
            if (erase) RubOut(centre, radius);
            else foreach (LandScatterGroup kind in ticked) Fill(kind, centre, radius);
        }

        /// <summary>Finishes the stroke (one step for Ctrl+Z).</summary>
        internal static void End()
        {
            if (undoGroup < 0) return;
            Undo.CollapseUndoOperations(undoGroup);
            undoGroup = -1;
            placed.Clear();
        }

        // ================================================================ Placing

        // How much of a kind grows at a point, from its patches: 1 on average; up to twice that in the thick of a patch and
        // down to none between, as patchy as the kind is
        private static float Richness(LandScatterGroup kind, Vector3 point)
        {
            if (kind.patchiness <= 0f) return 1f;
            float offset = (kind.name ?? string.Empty).GetHashCode() % 1000;
            float size = Mathf.Max(kind.patchSize, 1f);
            float noise = Mathf.PerlinNoise((point.x + 3137f + offset) / size, (point.z + 1723f - offset) / size);
            float patch = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.7f, noise));
            return Mathf.Lerp(1f, patch * 2f, kind.patchiness);
        }

        // Adds clumps of a kind in the circle until it has as many as the kind's density (and its patches) ask for
        private static void Fill(LandScatterGroup kind, Vector3 centre, float radius)
        {
            // How many it should have here: the density over the circle, more or less as its patches go
            float richness = 0f;
            const int samples = 16;
            for (int i = 0; i < samples; i++)
            {
                float angle = i * 2.39996f, distance = radius * Mathf.Sqrt((i + 0.5f) / samples);
                richness += Richness(kind, centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance);
            }
            richness /= samples;
            float wanted = kind.density * Mathf.PI * radius * radius / 100f * richness;
            int have = 0;
            Near(centre, radius);
            foreach (Placed thing in near)
            {
                if (kindOf[thing.Prefab] == kind) have++;
            }
            float clump = kind.AverageClump();
            float missing = (wanted - have) / clump;
            if (missing <= 0f) return;
            int clumps = Mathf.FloorToInt(missing) + (Random.value < missing - Mathf.Floor(missing) ? 1 : 0);

            // Clumps are kept apart (closer in the thick of a patch); the plants in one gather round its middle
            float apart = Mathf.Sqrt(100f * clump / Mathf.Max(kind.density, 0.01f));
            float clumpRadius = Mathf.Clamp(0.35f * apart, 0.3f, 3f) / Mathf.Sqrt(clump);   // (for one plant: grows with the clump)
            float richest = Mathf.Lerp(1f, 2f, kind.patchiness);
            for (int n = 0; n < clumps; n++)
            {
                for (int attempt = 0; attempt < Tries; attempt++)
                {
                    Vector2 offset = Random.insideUnitCircle * radius;
                    var at = new Vector3(centre.x + offset.x, centre.y, centre.z + offset.y);
                    float here = Richness(kind, at);
                    if (Random.value * richest > here) continue;   // (fewer between the patches)
                    if (!LandPaint.GroundAt(at, out Vector3 ground, out Vector3 normal)) continue;
                    if (Vector3.Angle(normal, Vector3.up) > kind.maxSlope) continue;
                    if (LandWater.UnderWater(ground, 0.1f)) continue;   // (not under water)
                    if (TooClose(kind, ground, 0.5f * apart / Mathf.Sqrt(Mathf.Max(here, 0.25f)))) continue;
                    PlaceClump(kind, ground, normal, clumpRadius, Mathf.Clamp01(here / richest));
                    break;
                }
            }
        }

        private static bool TooClose(LandScatterGroup kind, Vector3 point, float spacing)
        {
            Near(point, spacing);
            foreach (Placed thing in near)
            {
                if (kindOf[thing.Prefab] == kind) return true;
            }
            return false;
        }

        // A clump: one of the kind's prefabs (picked by chance), as many as its Clumps says, one in the middle and the rest
        // round it, smaller toward the edge; bigger in the thick of a patch. They share a colour (each a slightly different
        // shade) and a colour for each of their parts (petals, leaves, trunk...).
        private static void PlaceClump(LandScatterGroup kind, Vector3 middle, Vector3 middleNormal, float plantRadius, float richness)
        {
            ScatterPrefab entry = Pick(kind);
            if (entry == null) return;
            int fewest = Mathf.Clamp(Mathf.Min(entry.clump.x, entry.clump.y), 1, 12), most = Mathf.Clamp(Mathf.Max(entry.clump.x, entry.clump.y), 1, 12);
            int count = Random.Range(fewest, most + 1);
            float clumpRadius = count > 1 ? plantRadius * Mathf.Sqrt(count) : 0f;
            Color? colour = ColourAt(kind, middle);
            Dictionary<string, Color?> parts = PartColours(kind, entry);
            for (int i = 0; i < count; i++)
            {
                Vector3 ground = middle, normal = middleNormal;
                float edge = 0f;
                if (i > 0)
                {
                    Vector2 offset = Random.insideUnitCircle * clumpRadius;
                    if (!LandPaint.GroundAt(middle + new Vector3(offset.x, 0f, offset.y), out ground, out normal)) continue;
                    if (Vector3.Angle(normal, Vector3.up) > kind.maxSlope) continue;
                    if (LandWater.UnderWater(ground, 0.1f)) continue;
                    edge = clumpRadius > 0f ? offset.magnitude / clumpRadius : 0f;
                }
                float smallest = Mathf.Min(kind.size.x, kind.size.y), biggest = Mathf.Max(kind.size.x, kind.size.y);
                float size = Mathf.Lerp(smallest, biggest, Mathf.Pow(Random.value, 1.4f))
                             * Mathf.Lerp(1.1f, 0.8f, edge) * Mathf.Lerp(0.9f, 1.1f, richness);
                Place(kind, entry, ground, normal, size, colour.HasValue ? Shade(colour.Value, kind.shadeVariation) : (Color?)null, parts);
            }
        }

        // One of the kind's switched-on prefabs, as often as each one's chance says
        private static ScatterPrefab Pick(LandScatterGroup kind, GameObject only = null)
        {
            float total = 0f;
            foreach (ScatterPrefab entry in kind.entries)
            {
                if (Usable(entry, only)) total += Mathf.Max(entry.chance, 0.01f);
            }
            if (total <= 0f) return null;
            float roll = Random.value * total;
            ScatterPrefab last = null;
            foreach (ScatterPrefab entry in kind.entries)
            {
                if (!Usable(entry, only)) continue;
                last = entry;
                roll -= Mathf.Max(entry.chance, 0.01f);
                if (roll <= 0f) return entry;
            }
            return last;
        }

        private static bool Usable(ScatterPrefab entry, GameObject only) =>
            entry != null && entry.prefab != null && (only != null ? entry.prefab == only : entry.on);

        // A colour for each of an entry's parts that has some (one of them, a little lighter or darker each time)
        private static Dictionary<string, Color?> PartColours(LandScatterGroup kind, ScatterPrefab entry)
        {
            var picked = new Dictionary<string, Color?>();
            if (entry == null) return picked;
            foreach (ScatterPartColours part in entry.partColours)
            {
                if (part == null || part.colours.Count == 0) continue;
                Color c = part.colours[Random.Range(0, part.colours.Count)];
                c.a = 1f;
                picked[part.part] = Shade(c, kind.shadeVariation * 0.5f);
            }
            return picked;
        }

        private static void Place(LandScatterGroup kind, ScatterPrefab entry, Vector3 ground, Vector3 normal, float size, Color? colour,
            Dictionary<string, Color?> parts)
        {
            GameObject chosen = entry.prefab;
            Transform parent = Container(kind.name);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(chosen, parent);
            Undo.RegisterCreatedObjectUndo(go, "Scatter");

            // Standing on the ground, leaning with it (and a little at random), turned at random
            Quaternion lean = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, normal, kind.followSlope));
            Quaternion wobble = Quaternion.identity;
            if (kind.randomLean > 0f)
            {
                float direction = Random.Range(0f, Mathf.PI * 2f);
                wobble = Quaternion.AngleAxis(Random.Range(0f, kind.randomLean), new Vector3(Mathf.Cos(direction), 0f, Mathf.Sin(direction)));
            }
            Quaternion turn = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            go.transform.SetPositionAndRotation(ground - Vector3.up * (kind.sink * size), wobble * lean * turn * chosen.transform.localRotation);
            Vector3 scale = chosen.transform.localScale * size;
            if (kind.heightVariation > 0f) scale.y *= Random.Range(1f - kind.heightVariation * 0.6f, 1f + kind.heightVariation);
            go.transform.localScale = scale;
            if (colour.HasValue || parts.Count > 0) Colour(go, colour, parts, kind.bottomShade, false);
            if (AddColliders) Solidify(go, chosen, kind.collider, false);

            Add(new Placed { Go = go, Prefab = chosen, Position = go.transform.position, CanRemove = true });
        }

        private static void RubOut(Vector3 centre, float radius)
        {
            Near(centre, radius);
            foreach (Placed thing in near)
            {
                if (thing.Go != null && !thing.CanRemove) continue;
                if (thing.Go != null) Undo.DestroyObjectImmediate(thing.Go);
                placed[Cell(thing.Position)].Remove(thing);
            }
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ================================================================ Colours

        // Which of a kind's colours grows at a point: mostly as its colour patches go (big areas of one colour), sometimes
        // any of them. None if the kind has no colours (it keeps the prefab's own).
        private static Color? ColourAt(LandScatterGroup kind, Vector3 point)
        {
            if (kind.colours == null || kind.colours.Count == 0) return null;
            int count = kind.colours.Count;
            int index;
            if (count == 1 || Random.value < 0.3f)
            {
                index = Random.Range(0, count);
            }
            else
            {
                float size = Mathf.Max(kind.patchSize, 1f) * 1.7f;
                float noise = Mathf.PerlinNoise((point.x - 911f) / size, (point.z + 2459f) / size);
                index = Mathf.Min(count - 1, Mathf.FloorToInt(Mathf.InverseLerp(0.25f, 0.75f, noise) * count));
            }
            Color colour = kind.colours[index];
            colour.a = 1f;
            return colour;
        }

        // A colour a little lighter or darker (in five steps, so there are only a few materials for each colour)
        private static Color Shade(Color colour, float variation)
        {
            if (variation <= 0f) return colour;
            float step = (Random.Range(0, 5) - 2) * 0.5f * variation;
            Color.RGBToHSV(colour, out float h, out float s, out float v);
            Color shaded = Color.HSVToRGB(Mathf.Repeat(h + step * 0.05f, 1f), Mathf.Clamp01(s * (1f - step * 0.3f)), Mathf.Clamp01(v * (1f + step)));
            shaded.a = 1f;
            return shaded;
        }

        /// <summary>
        /// Gives a scattered thing a colour (tinted copies of its prefab's materials), or (null) puts its prefab's own
        /// materials back. Record: so Ctrl+Z can undo it (for things already in the scene).
        /// </summary>
        private static void Colour(GameObject go, Color? colour, Dictionary<string, Color?> parts, float bottomShade, bool record)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            bool leafy = false;
            foreach (Renderer renderer in renderers)
            {
                Renderer source = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
                foreach (Material m in (source != null ? source : renderer).sharedMaterials) leafy |= IsLeaves(m);
            }
            Color? petal = null;
            if (parts != null && parts.TryGetValue(Petals, out Color? p)) petal = p;
            foreach (Renderer renderer in renderers)
            {
                Renderer source = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
                Material[] originals = source != null ? source.sharedMaterials : renderer.sharedMaterials;
                var materials = new Material[originals.Length];
                for (int i = 0; i < originals.Length; i++)
                {
                    // Its part's own colour if it has one, or the kind's
                    Color? own = null;
                    string part = PartOf(originals[i], leafy);
                    if (part != null && parts != null && parts.TryGetValue(part, out Color? c)) own = c;
                    materials[i] = Tinted(originals[i], own ?? colour, bottomShade, petal);
                }
                if (Same(materials, renderer.sharedMaterials)) continue;
                if (record) Undo.RecordObject(renderer, "Recolour");
                renderer.sharedMaterials = materials;
            }
        }

        private static bool Same(Material[] a, Material[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // A copy of a material in a colour: for the nature pack's plants, its "custom colours" gradient from a darker bottom
        // to the colour at the top; otherwise its Base Color or Color. And for flowers, the petals in their own colour.
        // Made once and kept in Scatter Colours. The original if there's nothing to change.
        private static Material Tinted(Material original, Color? colour, float bottomShade, Color? petal)
        {
            if (original == null) return null;
            bool gradient = original.HasProperty("_TopColor") && original.HasProperty("_GroundColor");
            bool tintable = gradient || original.HasProperty("_BaseColor") || original.HasProperty("_Color");
            if (!tintable) colour = null;
            if (!HasPetals(original)) petal = null;
            if (!colour.HasValue && !petal.HasValue) return original;

            string key = original.name;
            if (colour.HasValue) key += $" {ColorUtility.ToHtmlStringRGB(colour.Value)}" + (gradient ? $" {Mathf.RoundToInt(bottomShade * 100f)}" : string.Empty);
            if (petal.HasValue) key += $" Petals {ColorUtility.ToHtmlStringRGB(petal.Value)}";
            string path = $"{ColoursFolder}/{key}.mat";
            if (tints.TryGetValue(path, out Material known) && known != null) return known;
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                LandBuilder.EnsureFolder(ColoursFolder);
                material = new Material(original) { name = key, enableInstancing = true };   // (for the Scatter Renderer)
                if (colour.HasValue) ApplyTint(material, colour.Value, bottomShade);
                if (petal.HasValue) ApplyPetals(material, original, petal.Value);
                AssetDatabase.CreateAsset(material, path);
            }
            tints[path] = material;
            return material;
        }

        // ---------------------------------------------------------------- Parts

        internal const string Petals = "Petals";
        private static readonly string[] PartOrder = { Petals, "Leaves", "Stems", "Blades", "Cap", "Stalk", "Trunk", "Colour", "Rock" };

        private static bool IsLeaves(Material m) => m != null && m.shader != null && m.shader.name.Contains("Foliage");

        // Which part of a thing a material colours: Petals, Leaves, Stems, Blades (grass), Cap and Stalk (a mushroom's),
        // Trunk (wood), Rock, or Colour (the whole of something with one material); null if it can't be coloured.
        // Polytope Studio's nature shaders say it themselves (one material per kind of plant); any other material (like Paint
        // Land's sample plants, one material per part) goes by its name: "Petals", "Leaves", "Bark", "Mushroom Cap"...
        private static string PartOf(Material m, bool leafy)
        {
            if (m == null || m.shader == null) return null;
            string shader = m.shader.name;
            if (shader.Contains("Polytope"))
            {
                if (shader.Contains("Flowers")) return "Stems";
                if (shader.Contains("Foliage")) return "Leaves";
                if (shader.Contains("Plants")) return "Blades";
                if (shader.Contains("Rock")) return "Rock";
                if (shader.Contains("Opaque")) return leafy ? "Trunk" : "Colour";
            }
            bool tintable = (m.HasProperty("_TopColor") && m.HasProperty("_GroundColor")) || m.HasProperty("_BaseColor") || m.HasProperty("_Color");
            if (!tintable) return null;
            string n = m.name.ToLowerInvariant();
            if (n.Contains("petal") || n.Contains("blossom") || n.Contains("flower head") || n.Contains("bloom")) return Petals;
            if (n.Contains("leaf") || n.Contains("leaves") || n.Contains("needle") || n.Contains("foliage") || n.Contains("canopy") ||
                n.Contains("bush") || n.Contains("shrub") || n.Contains("hedge")) return "Leaves";
            if (n.Contains("cap")) return "Cap";
            if (n.Contains("stalk")) return "Stalk";
            if (n.Contains("stem")) return "Stems";
            if (n.Contains("grass") || n.Contains("blade")) return "Blades";
            if (n.Contains("bark") || n.Contains("trunk") || n.Contains("wood") || n.Contains("branch") || n.Contains("log")) return "Trunk";
            if (n.Contains("rock") || n.Contains("stone") || n.Contains("boulder") || n.Contains("cliff")) return "Rock";
            return "Colour";
        }

        /// <summary>The parts of a prefab that can each be given colours (Petals, Leaves, Trunk...), in a set order.</summary>
        internal static List<string> PartsOf(GameObject prefab)
        {
            var found = new HashSet<string>();
            if (prefab != null)
            {
                bool leafy = false;
                var renderers = prefab.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in renderers)
                {
                    foreach (Material m in r.sharedMaterials) leafy |= IsLeaves(m);
                }
                foreach (Renderer r in renderers)
                {
                    foreach (Material m in r.sharedMaterials)
                    {
                        string part = PartOf(m, leafy);
                        if (part != null) found.Add(part);
                        if (HasPetals(m)) found.Add(Petals);
                    }
                }
            }
            var ordered = new List<string>();
            foreach (string part in PartOrder)
            {
                if (found.Contains(part)) ordered.Add(part);
            }
            return ordered;
        }

        /// <summary>Can this material's flower heads be given a colour (the nature pack's flowers: a Flowers Color)?</summary>
        internal static bool HasPetals(Material material) =>
            material != null && material.HasProperty("_FLOWERSCOLOR") && material.HasProperty("_CUSTOMFLOWERSCOLOR");

        /// <summary>Does any of this prefab's materials have flower heads that can be coloured?</summary>
        internal static bool HasPetals(GameObject prefab)
        {
            if (prefab == null) return false;
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (HasPetals(material)) return true;
                }
            }
            return false;
        }

        // The petals' colour: the flower shader takes the petals' brightness from its texture (grey) and multiplies it by
        // the Flowers Color, so the colour is made brighter by however dark the petals are in the texture, to come out as
        // picked
        private static void ApplyPetals(Material material, Material original, Color petal)
        {
            material.SetFloat("_CUSTOMFLOWERSCOLOR", 1f);
            Color linear = petal.linear * PetalBoost(original);
            linear.a = 1f;
            material.SetColor("_FLOWERSCOLOR", linear);
        }

        private static readonly Dictionary<Texture, float> petalBoosts = new Dictionary<Texture, float>();

        // 1 / how bright the petals are on average (the top right quarter of the flower texture, where it isn't see-through)
        private static float PetalBoost(Material material)
        {
            Texture texture = material.HasProperty("_BASETEXTURE") ? material.GetTexture("_BASETEXTURE") : null;
            if (texture == null) return 3f;
            if (petalBoosts.TryGetValue(texture, out float known)) return known;
            const int size = 64;
            RenderTexture copy = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture before = RenderTexture.active;
            Graphics.Blit(texture, copy);
            RenderTexture.active = copy;
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            read.Apply();
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(copy);
            float sum = 0f, weight = 0f;
            for (int y = size / 2; y < size; y++)
            {
                for (int x = size / 2; x < size; x++)
                {
                    Color c = read.GetPixel(x, y);
                    if (c.a < 0.5f) continue;
                    sum += c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                    weight++;
                }
            }
            Object.DestroyImmediate(read);
            float boost = weight > 0f && sum > 0f ? Mathf.Clamp(weight / sum, 1f, 12f) : 3f;
            petalBoosts[texture] = boost;
            return boost;
        }

        // Puts a colour on a tinted material: the plants' top-to-bottom gradient, or its Base Color or Color
        private static void ApplyTint(Material material, Color colour, float bottomShade)
        {
            if (material.HasProperty("_TopColor") && material.HasProperty("_GroundColor"))
            {
                material.SetFloat("_CUSTOMCOLORSTINTING", 1f);
                Color bottom = Color.Lerp(colour, colour * 0.3f, bottomShade);
                bottom.a = 1f;
                material.SetColor("_TopColor", AsSet(material, "_TopColor", colour));
                material.SetColor("_GroundColor", AsSet(material, "_GroundColor", bottom));
            }
            else
            {
                string property = material.HasProperty("_BaseColor") ? "_BaseColor" : material.HasProperty("_Color") ? "_Color" : null;
                if (property != null) material.SetColor(property, AsSet(material, property, colour));
            }
        }

        // A colour (as picked, sRGB) as a material property wants it. Unity converts ordinary colours to linear itself, but
        // not ones marked [HDR] (like the nature pack plants' Top and Ground Colors): those take linear values already, so
        // they're converted here, or the plant comes out much paler than the colour picked.
        private static Color AsSet(Material material, string property, Color colour)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || material.shader == null) return colour;
            int index = material.shader.FindPropertyIndex(property);
            if (index < 0 || (material.shader.GetPropertyFlags(index) & ShaderPropertyFlags.HDR) == 0) return colour;
            Color linear = colour.linear;
            linear.a = colour.a;
            return linear;
        }

        // The colour materials made before the fix above (too pale), set again from their names (the colour and the
        // bottom shade are in them), so everything using them comes out right
        private static void FixColours()
        {
            if (!AssetDatabase.IsValidFolder(ColoursFolder)) return;
            var named = new Regex(@" ([0-9A-Fa-f]{6})(?: (\d+))?$");
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { ColoursFolder }))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (material == null) continue;
                Match match = named.Match(material.name);
                if (!match.Success || !ColorUtility.TryParseHtmlString("#" + match.Groups[1].Value, out Color colour)) continue;
                float bottomShade = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) / 100f : 0.65f;
                ApplyTint(material, colour, bottomShade);
                EditorUtility.SetDirty(material);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Gives everything of a kind already in the scene (scattered, or placed by hand) its colours again, as the kind has
        /// them now (or its prefab's own, if it has none). One step for Ctrl+Z. Returns how many.
        /// </summary>
        internal static int Recolour(LandScatterGroup kind)
        {
            GameObject land = LandBuilder.FindLand();
            UnityEngine.SceneManagement.Scene scene = land != null ? land.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            kind.SyncPrefabs();
            var prefabs = new HashSet<GameObject>();
            foreach (GameObject prefab in kind.prefabs)
            {
                if (prefab != null) prefabs.Add(prefab);
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            int count = 0;
            var clumpColours = new Dictionary<Vector2Int, Color?>();
            var clumpParts = new Dictionary<(Vector2Int, ScatterPrefab), Dictionary<string, Color?>>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                    GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (!prefabs.Contains(prefab)) continue;
                    // Things close together (a clump) share a colour, and the colours of their parts for each of the
                    // kind's entries of their prefab (one picked by chance, when the prefab is in the kind more than once)
                    var cell = new Vector2Int(Mathf.FloorToInt(t.position.x / 1.5f), Mathf.FloorToInt(t.position.z / 1.5f));
                    if (!clumpColours.TryGetValue(cell, out Color? colour)) clumpColours[cell] = colour = ColourAt(kind, t.position);
                    ScatterPrefab entry = Pick(kind, prefab);
                    if (!clumpParts.TryGetValue((cell, entry), out var parts)) clumpParts[(cell, entry)] = parts = PartColours(kind, entry);
                    Colour(go, colour.HasValue ? Shade(colour.Value, kind.shadeVariation) : (Color?)null, parts, kind.bottomShade, true);
                    count++;
                }
            }
            Undo.SetCurrentGroupName($"Recolour {kind.name}");
            Undo.CollapseUndoOperations(group);
            return count;
        }

        // ================================================================ Colliders

        /// <summary>Paint Land's Add Colliders: each thing scattered gets its kind's collider.</summary>
        internal static bool AddColliders = true;

        /// <summary>The name of the objects holding the colliders this adds (so they can be found and changed).</summary>
        internal const string ColliderName = "Collider (Paint Land)";

        // A prefab's collider shapes, in its own space (worked out once)
        private sealed class Outline
        {
            public Vector3 TrunkCentre;
            public float TrunkRadius, TrunkHeight;
            public Bounds Box;
        }

        private static readonly Dictionary<GameObject, Outline> outlines = new Dictionary<GameObject, Outline>();

        /// <summary>The collider a kind should have, from its name (or its prefabs' names): trees round the trunk; rocks,
        /// stumps and logs their exact shape; anything else none.</summary>
        internal static ScatterCollider GuessCollider(string name, List<GameObject> prefabs)
        {
            ScatterCollider guess = GuessFrom(name);
            if (guess != ScatterCollider.None || prefabs == null) return guess;
            foreach (GameObject prefab in prefabs)
            {
                if (prefab == null) continue;
                guess = GuessFrom(prefab.name);
                if (guess != ScatterCollider.None) return guess;
            }
            return ScatterCollider.None;
        }

        private static ScatterCollider GuessFrom(string name)
        {
            if (string.IsNullOrEmpty(name)) return ScatterCollider.None;
            string n = name.ToLowerInvariant();
            if (n.Contains("grass") || n.Contains("flower") || n.Contains("plant") || n.Contains("mushroom") || n.Contains("shrub") ||
                n.Contains("bush") || n.Contains("fern") || n.Contains("weed")) return ScatterCollider.None;
            if (n.Contains("stump") || n.Contains("log") || n.Contains("rock") || n.Contains("stone") || n.Contains("boulder") ||
                n.Contains("cliff")) return ScatterCollider.Shape;
            if (n.Contains("tree") || n.Contains("trunk")) return ScatterCollider.Trunk;
            return ScatterCollider.None;
        }

        /// <summary>
        /// Gives a scattered thing this collider (taking off any this added before). One with colliders of its own is left
        /// as it is. 'record': for Ctrl+Z (things already in the scene). True if anything changed.
        /// </summary>
        internal static bool Solidify(GameObject go, GameObject prefab, ScatterCollider shape, bool record)
        {
            if (go == null) return false;
            bool changed = false;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == go.transform || t.name != ColliderName) continue;
                if (record) Undo.DestroyObjectImmediate(t.gameObject);
                else Object.DestroyImmediate(t.gameObject);
                changed = true;
            }
            if (shape == ScatterCollider.None) return changed;
            if (go.GetComponentInChildren<Collider>(true) != null) return changed; // (it has its own)

            if (shape == ScatterCollider.Shape)
            {
                bool any = false;
                foreach (MeshFilter filter in DetailMeshes(go))
                {
                    GameObject holder = Holder(filter.transform);
                    holder.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                    if (record) Undo.RegisterCreatedObjectUndo(holder, "Add Collider");
                    any = true;
                }
                if (any) return true;
                shape = ScatterCollider.Box; // (no meshes to take the shape of)
            }

            Outline outline = OutlineOf(prefab != null ? prefab : go);
            if (outline == null) return changed;
            GameObject box = Holder(go.transform);
            if (shape == ScatterCollider.Trunk)
            {
                var capsule = box.AddComponent<CapsuleCollider>();
                capsule.direction = 1;
                capsule.center = outline.TrunkCentre;
                capsule.radius = outline.TrunkRadius;
                capsule.height = outline.TrunkHeight;
            }
            else
            {
                var collider = box.AddComponent<BoxCollider>();
                collider.center = outline.Box.center;
                collider.size = outline.Box.size;
            }
            if (record) Undo.RegisterCreatedObjectUndo(box, "Add Collider");
            return true;
        }

        // An empty child (on the same layer) to hold a collider
        private static GameObject Holder(Transform parent)
        {
            var holder = new GameObject(ColliderName) { layer = parent.gameObject.layer };
            holder.transform.SetParent(parent, false);
            return holder;
        }

        // The meshes it's drawn with up close: its first level of detail if it has levels, or all of them
        private static List<MeshFilter> DetailMeshes(GameObject go)
        {
            var found = new List<MeshFilter>();
            LODGroup group = go.GetComponent<LODGroup>();
            LOD[] lods = group != null ? group.GetLODs() : null;
            if (lods != null && lods.Length > 0 && lods[0].renderers != null)
            {
                foreach (Renderer renderer in lods[0].renderers)
                {
                    if (renderer != null && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null && !found.Contains(filter)) found.Add(filter);
                }
            }
            if (found.Count > 0) return found;
            foreach (MeshFilter filter in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && filter.GetComponent<MeshRenderer>() != null) found.Add(filter);
            }
            return found;
        }

        // Where a prefab's trunk is (the middle of its lowest part, a pole as tall as it is) and the box round it, in the
        // prefab's own space
        private static Outline OutlineOf(GameObject source)
        {
            if (outlines.TryGetValue(source, out Outline known) && known != null) return known;
            Transform root = source.transform;
            var points = new List<Vector3>();
            foreach (MeshFilter filter in DetailMeshes(source))
            {
                Vector3[] vertices = ReadVertices(filter.sharedMesh);
                if (vertices == null)
                {
                    // (can't be read: the corners of its bounds)
                    Bounds b = filter.sharedMesh.bounds;
                    vertices = new Vector3[8];
                    for (int c = 0; c < 8; c++)
                    {
                        vertices[c] = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    }
                }
                Matrix4x4 toRoot = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (Vector3 v in vertices) points.Add(toRoot.MultiplyPoint3x4(v));
            }
            if (points.Count == 0) return null;

            var box = new Bounds(points[0], Vector3.zero);
            foreach (Vector3 p in points) box.Encapsulate(p);
            float height = box.size.y;
            if (height < 1e-4f) return null;

            // The trunk: the middle of the lowest bit, and about as wide as most of it
            float sliceTop = box.min.y + height * 0.08f;
            var slice = new Bounds();
            bool started = false;
            foreach (Vector3 p in points)
            {
                if (p.y > sliceTop) continue;
                var flat = new Vector3(p.x, 0f, p.z);
                if (!started) slice = new Bounds(flat, Vector3.zero);
                else slice.Encapsulate(flat);
                started = true;
            }
            Vector3 middle = started ? slice.center : new Vector3(box.center.x, 0f, box.center.z);
            var distances = new List<float>();
            foreach (Vector3 p in points)
            {
                if (p.y <= sliceTop) distances.Add(new Vector2(p.x - middle.x, p.z - middle.z).magnitude);
            }
            float widest = Mathf.Min(box.size.x, box.size.z) * 0.5f;
            float radius;
            if (distances.Count >= 3)
            {
                distances.Sort();
                radius = distances[Mathf.Clamp(Mathf.RoundToInt(distances.Count * 0.6f), 0, distances.Count - 1)];
            }
            else
            {
                radius = widest * 0.15f;
            }
            radius = Mathf.Clamp(radius, Mathf.Min(0.05f, widest), Mathf.Max(widest, 0.05f));

            var outline = new Outline
            {
                TrunkCentre = new Vector3(middle.x, box.center.y, middle.z),
                TrunkRadius = radius,
                TrunkHeight = height,
                Box = box,
            };
            outlines[source] = outline;
            return outline;
        }

        // A mesh's corners, even with Read/Write off (null if they can't be read)
        private static Vector3[] ReadVertices(Mesh mesh)
        {
            if (mesh == null) return null;
            try
            {
                if (mesh.isReadable) return mesh.vertices;
                using (Mesh.MeshDataArray data = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData first = data[0];
                    using (var vertices = new Unity.Collections.NativeArray<Vector3>(first.vertexCount, Unity.Collections.Allocator.Temp))
                    {
                        first.GetVertices(vertices);
                        return vertices.ToArray();
                    }
                }
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Gives everything of these kinds already in the scene (scattered, or placed by hand) its kind's collider as it's
        /// set now (or takes off the ones this added, for a kind set to none). One step for Ctrl+Z. Returns how many changed.
        /// </summary>
        internal static int UpdateColliders(IEnumerable<LandScatterGroup> kinds)
        {
            var kindOfPrefab = new Dictionary<GameObject, LandScatterGroup>();
            foreach (LandScatterGroup kind in kinds)
            {
                if (kind == null) continue;
                foreach (GameObject prefab in kind.prefabs)
                {
                    if (prefab != null && !kindOfPrefab.ContainsKey(prefab)) kindOfPrefab[prefab] = kind;
                }
            }
            outlines.Clear(); // (prefabs may have changed)
            GameObject land = LandBuilder.FindLand();
            UnityEngine.SceneManagement.Scene scene = land != null ? land.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            int count = 0;
            var found = new List<GameObject>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) found.Add(t.gameObject);
                }
            }
            foreach (GameObject go in found)
            {
                if (go == null) continue;
                GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (prefab == null || !kindOfPrefab.TryGetValue(prefab, out LandScatterGroup kind)) continue;
                if (Solidify(go, prefab, kind.collider, true)) count++;
            }
            Undo.SetCurrentGroupName("Update Scatter Colliders");
            Undo.CollapseUndoOperations(group);
            return count;
        }

        // ================================================================ Clearing under a path

        private static readonly List<Placed> clearing = new List<Placed>();

        /// <summary>
        /// Gets ready to delete the kinds that paths clear (Clear Under New Paths), wherever a path is painted in this
        /// stroke. False if there's nothing to clear.
        /// </summary>
        internal static bool BeginClearing(LandSettings settings, bool everything = false)
        {
            clearing.Clear();
            var prefabs = new HashSet<GameObject>();
            foreach (LandScatterGroup kind in settings.scatter)
            {
                if (kind == null || (!kind.clearOnPaths && !everything)) continue;
                foreach (GameObject prefab in kind.prefabs)
                {
                    if (prefab != null) prefabs.Add(prefab);
                }
            }
            if ((prefabs.Count == 0 && !everything) || LandPaint.Land == null) return false;
            foreach (GameObject root in LandPaint.Land.scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                    GameObject prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefab == null) continue;
                    // (everything: the nature pack's trees, rocks and plants too, however they were put there)
                    if (!prefabs.Contains(prefab) && !(everything && LandBuilder.IsProp(t))) continue;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAddedGameObjectOverride(go)) continue;
                    clearing.Add(new Placed { Go = go, Prefab = prefab, Position = t.position, CanRemove = true, Spread = Spread(go, t.position) });
                }
            }
            return clearing.Count > 0;
        }

        /// <summary>
        /// Deletes what's being cleared within a circle (from above) round a point in the world: anything standing in it,
        /// or reaching into it (a tuft of grass beside the path whose blades hang over it).
        /// </summary>
        internal static void ClearAt(Vector3 centre, float radius) => ClearAt(centre, radius, float.MaxValue);

        /// <summary>The same, only things standing where a test says (under water, say: from where they stand, in the world).</summary>
        internal static void ClearAt(Vector3 centre, float radius, System.Func<Vector3, bool> where)
        {
            for (int i = clearing.Count - 1; i >= 0; i--)
            {
                Placed thing = clearing[i];
                if (thing.Go == null)
                {
                    clearing.RemoveAt(i);
                    continue;
                }
                float reach = radius + 0.1f + thing.Spread;
                if (Flat(thing.Position - centre).sqrMagnitude > reach * reach || (where != null && !where(thing.Position))) continue;
                Undo.DestroyObjectImmediate(thing.Go);
                clearing.RemoveAt(i);
            }
        }

        /// <summary>The same, only things standing lower than a height.</summary>
        internal static void ClearAt(Vector3 centre, float radius, float below)
        {
            for (int i = clearing.Count - 1; i >= 0; i--)
            {
                Placed thing = clearing[i];
                if (thing.Go == null)
                {
                    clearing.RemoveAt(i);
                    continue;
                }
                float reach = radius + 0.1f + thing.Spread;
                if (Flat(thing.Position - centre).sqrMagnitude > reach * reach || thing.Position.y >= below) continue;
                Undo.DestroyObjectImmediate(thing.Go);
                clearing.RemoveAt(i);
            }
        }

        internal static void EndClearing() => clearing.Clear();

        // How far a thing reaches out from where it stands, from above (its leaves, blades or sides), going by its most
        // detailed look. A little less than its box (round things don't fill the corners), and no more than 1.5 m, so a
        // tree isn't deleted just because its branches hang over the path.
        private static float Spread(GameObject go, Vector3 root)
        {
            Renderer[] renderers = null;
            if (go.TryGetComponent(out LODGroup group))
            {
                UnityEngine.LOD[] lods = group.GetLODs();
                if (lods.Length > 0) renderers = lods[0].renderers;
            }
            if (renderers == null || renderers.Length == 0) renderers = go.GetComponentsInChildren<Renderer>();
            float spread = 0f;
            bool found = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Bounds b = renderer.bounds;
                spread = Mathf.Max(spread, Mathf.Max(Mathf.Max(b.max.x - root.x, root.x - b.min.x), Mathf.Max(b.max.z - root.z, root.z - b.min.z)));
                found = true;
            }
            return found ? Mathf.Clamp(spread * 0.8f, 0f, 1.5f) : 0.3f;
        }

        // ================================================================ Where they go

        // "Land Scatter" next to the Land in the scene, with a child for each kind
        private static Transform Container(string kind)
        {
            GameObject land = LandPaint.Land;
            Transform root = null;
            foreach (GameObject go in land.scene.GetRootGameObjects())
            {
                if (go.name == ContainerName)
                {
                    root = go.transform;
                    break;
                }
            }
            if (root == null)
            {
                var made = new GameObject(ContainerName);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(made, land.scene);
                made.AddComponent<ScatterRenderer>();
                Undo.RegisterCreatedObjectUndo(made, "Scatter");
                root = made.transform;
                if (land.transform.parent == null) root.SetSiblingIndex(land.transform.GetSiblingIndex() + 1);
            }
            string name = string.IsNullOrEmpty(kind) ? "Other" : kind;
            Transform child = root.Find(name);
            if (child == null)
            {
                var made = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(made, "Scatter");
                made.transform.SetParent(root, false);
                child = made.transform;
            }
            return child;
        }

        /// <summary>Is this under the Land Scatter object (put there by the Scatter brush)?</summary>
        internal static bool IsScattered(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (p.parent == null && p.name == ContainerName) return true;
            }
            return false;
        }
    }
}
#endif
