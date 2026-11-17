#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. The textures painted on the land with Tools > Paint Land > Textures (up to 8: rock, sand,
    /// mud...). The textures themselves, their size, tint, how much they take on the land's colour and how much grass grows
    /// on them are kept in the Land material (Land.mat, "Textures painted with Paint Land"), and their names in Land Settings.
    /// Where they're painted is kept two ways:
    ///   - Soft brush: in Land Textures 1-4.png and Land Textures 5-8.png (next to Land Settings), laid over the land from
    ///     above like the paths; each colour channel is how much of one texture there is (red = texture 1, green = 2...).
    ///   - Whole triangles: in the land pieces' meshes, a texture to a triangle with sharp edges (LandPaint).
    /// Each stroke is a step for Ctrl+Z (with Paint Land's other strokes), and it saves by itself a moment after you stop.
    /// The first time, the textures from the terrain sample assets in the project (rock, sand, mud, pebbles, grass...) are
    /// put in; drop any texture or terrain layer on a slot in Paint Land to change one.
    /// </summary>
    [InitializeOnLoad]
    internal static class LandTextures
    {
        internal const int Count = 8;
        internal static readonly string[] MapPaths =
        {
            LandBuilder.Folder + "/Land Textures 1-4.png",
            LandBuilder.Folder + "/Land Textures 5-8.png",
        };
        private const int MaxPixels = 4096;

        static LandTextures()
        {
            LandPaint.Saving += Save;
            LandPaint.Cancelling += Cancel;
        }

        // ================================================================ The textures (in Land.mat)

        internal static Material Material => AssetDatabase.LoadAssetAtPath<Material>(LandBuilder.MaterialPath);

        /// <summary>Can the land's material take painted textures (the Land shader is up to date)?</summary>
        internal static bool Supported(Material material) => material != null && material.HasProperty("_Layer1") && material.HasProperty("_TextureMap1");

        /// <summary>A texture's property in the Land material (layer: 1 to 8; what: "", "Size", "Tint", "Land" or "Grass").</summary>
        internal static string Property(int layer, string what = "") => $"_Layer{layer}{what}";

        internal static Texture LayerTexture(Material material, int layer)
        {
            return material != null && material.HasProperty(Property(layer)) ? material.GetTexture(Property(layer)) : null;
        }

        /// <summary>A texture's name (its name in Land Settings, or its texture's).</summary>
        internal static string Name(LandSettings settings, Material material, int layer)
        {
            if (settings.textureNames != null && layer - 1 < settings.textureNames.Count && !string.IsNullOrEmpty(settings.textureNames[layer - 1]))
            {
                return settings.textureNames[layer - 1];
            }
            Texture texture = LayerTexture(material, layer);
            return texture != null ? texture.name : $"Texture {layer}";
        }

        internal static void SetName(LandSettings settings, int layer, string name)
        {
            settings.textureNames ??= new List<string>();
            while (settings.textureNames.Count < Count) settings.textureNames.Add(string.Empty);
            settings.textureNames[layer - 1] = name;
        }

        /// <summary>
        /// Puts a texture, or a terrain layer (its texture and size), in a slot (1 to 8). Returns false if it isn't one.
        /// </summary>
        internal static bool SetLayer(LandSettings settings, Material material, int layer, Object from)
        {
            Texture texture = null;
            float size = -1f;
            string name = null;
            if (from is TerrainLayer terrainLayer)
            {
                texture = terrainLayer.diffuseTexture;
                size = terrainLayer.tileSize.x;
                name = Tidy(terrainLayer.name);
            }
            else if (from is Texture2D texture2D)
            {
                texture = texture2D;
                name = Tidy(texture2D.name);
            }
            if (texture == null) return false;
            Undo.RecordObjects(new Object[] { material, settings }, "Set Land Texture");
            material.SetTexture(Property(layer), texture);
            if (size > 0f) material.SetFloat(Property(layer, "Size"), Mathf.Clamp(size, 0.5f, 100f));
            SetName(settings, layer, name);
            EditorUtility.SetDirty(material);
            EditorUtility.SetDirty(settings);
            return true;
        }

        // "Grass_Dry_TerrainLayer" -> "Grass Dry"
        private static string Tidy(string name)
        {
            foreach (string end in new[] { "_TerrainLayer", "TerrainLayer", "_Diffuse", "_diffuse", "_Albedo", "_albedo", "_BaseColor", "_Color" })
            {
                if (name.EndsWith(end, System.StringComparison.Ordinal)) name = name.Substring(0, name.Length - end.Length);
            }
            return name.Replace('_', ' ').Trim();
        }

        /// <summary>The first time: textures from the terrain sample assets in the project put in the slots.</summary>
        internal static void SetUp(LandSettings settings)
        {
            Material material = Material;
            if (settings.texturesSetUp || !Supported(material)) return;
            settings.texturesSetUp = true;
            EditorUtility.SetDirty(settings);
            for (int layer = 1; layer <= Count; layer++)
            {
                if (LayerTexture(material, layer) != null) return;   // (already chosen)
            }

            var layers = new List<TerrainLayer>();
            foreach (string guid in AssetDatabase.FindAssets("t:TerrainLayer"))
            {
                var found = AssetDatabase.LoadAssetAtPath<TerrainLayer>(AssetDatabase.GUIDToAssetPath(guid));
                if (found != null && found.diffuseTexture != null) layers.Add(found);
            }
            // (which to look for, what to call it, how much grass grows on it)
            var wanted = new (string find, string name, float grass)[]
            {
                ("Rock", "Rock", 0f), ("Sand", "Sand", 0f), ("Muddy", "Mud", 0.2f), ("Pebbles_A", "Pebbles", 0f),
                ("Grass_A", "Grass", 1f), ("Grass_Dry", "Dry Grass", 1f), ("Snow", "Snow", 0f), ("Soil_Rocks", "Stony Soil", 0.2f),
            };
            var used = new HashSet<TerrainLayer>();
            int slot = 1;
            foreach ((string find, string name, float grass) in wanted)
            {
                TerrainLayer pick = null;
                foreach (TerrainLayer candidate in layers)
                {
                    if (used.Contains(candidate) || candidate.name.IndexOf(find, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (find == "Sand" && candidate.name.IndexOf("Black", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    pick = candidate;
                    break;
                }
                if (pick == null) continue;
                used.Add(pick);
                material.SetTexture(Property(slot), pick.diffuseTexture);
                material.SetFloat(Property(slot, "Size"), Mathf.Clamp(pick.tileSize.x > 0f ? pick.tileSize.x : 4f, 0.5f, 100f));
                material.SetFloat(Property(slot, "Grass"), grass);
                SetName(settings, slot, name);
                if (++slot > Count) break;
            }
            if (slot > 1)
            {
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
                Debug.Log($"Paint Land: put {slot - 1} textures from the terrain sample assets in the Textures tab (drop your own on a slot to change one).");
            }
        }

        // ================================================================ Where they're painted (soft brush)

        private static Texture2D[] maps = new Texture2D[2];
        private static Color32[][][] levels;          // [map][mip level][pixel], kept here while painting
        private static int[] levelWidth, levelHeight;
        private static Rect area;                     // where the maps lie over the land (x, z in its space)
        private static RectInt dirty;
        private static bool dirtyAny;
        private static bool unsaved;

        internal static bool Ready => levels != null && maps[0] != null && maps[1] != null;
        internal static bool Exist => AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[0]) != null;

        /// <summary>Gets the maps of where the textures are ready to paint (making them the first time). Null if ready, or what's wrong.</summary>
        internal static string Load()
        {
            if (Ready) return null;
            LandSettings settings = LandBuilder.GetSettings();
            Material material = Material;
            if (material == null) return "Land.mat is missing. Rebuild the land (Tools > Paint Land > Rebuild Land) first.";
            if (!Supported(material)) return "The Land material's shader is out of date (Assets/Paint Land/Shaders/LandVertexColours.shader).";
            if (settings.pathArea.width <= 0f || settings.pathArea.height <= 0f)
            {
                IReadOnlyList<LandPaint.Piece> pieces = LandPaint.Pieces;
                if (pieces.Count == 0) return "Start painting on the land first.";
                Bounds all = pieces[0].Bounds;
                foreach (LandPaint.Piece piece in pieces) all.Encapsulate(piece.Bounds);
                settings.pathArea = new Rect(all.min.x, all.min.z, all.size.x, all.size.z);
                EditorUtility.SetDirty(settings);
            }
            Prepare(settings, material);
            for (int m = 0; m < 2; m++)
            {
                maps[m] = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                if (maps[m] == null) return $"Couldn't make {Path.GetFileName(MapPaths[m])}.";
                if (!maps[m].isReadable)
                {
                    AssetDatabase.ImportAsset(MapPaths[m], ImportAssetOptions.ForceUpdate);
                    maps[m] = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                    if (maps[m] == null || !maps[m].isReadable) return $"{Path.GetFileName(MapPaths[m])} needs Read/Write turned on in its import settings.";
                }
            }
            if (maps[0].width != maps[1].width || maps[0].height != maps[1].height) return "Land Textures 1-4.png and 5-8.png need to be the same size.";
            area = settings.pathArea;
            ReadLevels();
            return null;
        }

        /// <summary>Makes the maps the size Land Settings' Texture Detail asks (resizing what's painted).</summary>
        internal static void Resize()
        {
            Save();
            levels = null;
            maps[0] = maps[1] = null;
            Material material = Material;
            if (material != null && Exist) Prepare(LandBuilder.GetSettings(), material);
        }

        private static void Prepare(LandSettings settings, Material material)
        {
            Rect rect = settings.pathArea;
            if (rect.width <= 0f || rect.height <= 0f) return;
            float detail = Mathf.Clamp(settings.textureDetail, 0.5f, 4f);
            int width = Mathf.Clamp(Mathf.CeilToInt(rect.width / detail), 16, MaxPixels);
            int height = Mathf.Clamp(Mathf.CeilToInt(rect.height / detail), 16, MaxPixels);
            for (int m = 0; m < 2; m++)
            {
                var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                if (existing == null)
                {
                    Write(MapPaths[m], new Color32[width * height], width, height);
                    AssetDatabase.ImportAsset(MapPaths[m], ImportAssetOptions.ForceUpdate);
                }
                else if (existing.width != width || existing.height != height)
                {
                    if (!existing.isReadable) AssetDatabase.ImportAsset(MapPaths[m], ImportAssetOptions.ForceUpdate);
                    existing = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                    Color32[] resized = existing != null && existing.isReadable
                        ? Resample(existing.GetPixels32(), existing.width, existing.height, width, height)
                        : new Color32[width * height];
                    Write(MapPaths[m], resized, width, height);
                    AssetDatabase.ImportAsset(MapPaths[m], ImportAssetOptions.ForceUpdate);
                }
            }
            material.SetTexture("_TextureMap1", AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[0]));
            material.SetTexture("_TextureMap2", AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[1]));
            material.SetVector("_PathRect", new Vector4(rect.x, rect.y, 1f / rect.width, 1f / rect.height));
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }

        private static void Write(string assetPath, Color32[] pixels, int width, int height)
        {
            LandBuilder.EnsureFolder(LandBuilder.Folder);
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);
            File.WriteAllBytes(FullPath(assetPath), png);
        }

        private static string FullPath(string assetPath) => Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);

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
            int count = Mathf.Max(1, Mathf.Min(maps[0].mipmapCount, maps[1].mipmapCount));
            levels = new Color32[2][][];
            levelWidth = new int[count];
            levelHeight = new int[count];
            for (int m = 0; m < 2; m++)
            {
                levels[m] = new Color32[count][];
                for (int level = 0; level < count; level++) levels[m][level] = maps[m].GetPixels32(level);
            }
            for (int level = 0; level < count; level++)
            {
                levelWidth[level] = Mathf.Max(1, maps[0].width >> level);
                levelHeight[level] = Mathf.Max(1, maps[0].height >> level);
            }
            dirtyAny = false;
        }

        /// <summary>When one of the maps is (re)imported: carry on with what's painted.</summary>
        internal static void AfterImported()
        {
            if (levels == null) return;
            for (int m = 0; m < 2; m++)
            {
                maps[m] = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                if (maps[m] == null || !maps[m].isReadable)
                {
                    levels = null;
                    return;
                }
            }
            bool same = maps[0].width == levelWidth[0] && maps[0].height == levelHeight[0] && maps[1].width == levelWidth[0] &&
                        maps[1].height == levelHeight[0] && Mathf.Min(maps[0].mipmapCount, maps[1].mipmapCount) == levels[0].Length;
            if (same && unsaved)
            {
                // Painted since it was saved: put that back
                for (int m = 0; m < 2; m++)
                {
                    for (int level = 0; level < levels[m].Length; level++) maps[m].SetPixels32(levels[m][level], level);
                    maps[m].Apply(false);
                }
            }
            else
            {
                ReadLevels();
            }
        }

        // ---------------------------------------------------------------- Painting

        private static readonly Dictionary<int, (Color32 a, Color32 b)> strokeBefore = new Dictionary<int, (Color32, Color32)>();
        private static readonly Dictionary<int, float> strokeAmount = new Dictionary<int, float>();
        private static RectInt strokeRect;
        private static bool strokeHasRect;

        /// <summary>
        /// Paints a round dab of a texture (1 to 8; 0 rubs textures out) at a point on the land (x, z): opacity is how much
        /// of it (1 = only it), softness how soft its edge is (0 = hard, 1 = soft all the way in). Within a stroke, dabs
        /// don't build up on each other.
        /// </summary>
        internal static void Stamp(Vector2 centre, float radius, int layer, float opacity, float softness)
        {
            if (levels == null) return;
            int w = levelWidth[0], h = levelHeight[0];
            float texelX = area.width / w, texelZ = area.height / h;
            float texel = Mathf.Max(texelX, texelZ);
            float soft = Mathf.Max(radius * Mathf.Clamp01(softness), texel);
            float inner = Mathf.Max(radius - soft, 0f);
            int x0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - radius - area.x) / texelX));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt((centre.x + radius - area.x) / texelX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((centre.y - radius - area.y) / texelZ));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt((centre.y + radius - area.y) / texelZ));
            if (x0 > x1 || y0 > y1) return;

            Color32[] first = levels[0][0], second = levels[1][0];
            var weights = new float[Count];
            for (int y = y0; y <= y1; y++)
            {
                float dz = area.y + (y + 0.5f) * texelZ - centre.y;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = area.x + (x + 0.5f) * texelX - centre.x;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    float t = distance <= inner ? 1f : Mathf.Clamp01((radius - distance) / Mathf.Max(radius - inner, 1e-4f));
                    t = t * t * (3f - 2f * t);
                    float amount = t * Mathf.Clamp01(opacity);
                    if (amount <= 0f) continue;
                    int i = y * w + x;
                    if (strokeAmount.TryGetValue(i, out float had) && had >= amount) continue;
                    if (!strokeBefore.TryGetValue(i, out (Color32 a, Color32 b) before))
                    {
                        before = (first[i], second[i]);
                        strokeBefore[i] = before;
                    }
                    strokeAmount[i] = amount;

                    Read(before.a, before.b, weights);
                    for (int k = 0; k < Count; k++)
                    {
                        weights[k] = k == layer - 1 ? weights[k] + (1f - weights[k]) * amount : weights[k] * (1f - amount);
                    }
                    first[i] = new Color32(Byte(weights[0]), Byte(weights[1]), Byte(weights[2]), Byte(weights[3]));
                    second[i] = new Color32(Byte(weights[4]), Byte(weights[5]), Byte(weights[6]), Byte(weights[7]));
                }
            }
            var rect = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            strokeRect = strokeHasRect ? Union(strokeRect, rect) : rect;
            strokeHasRect = true;
            MarkDirty(rect);
        }

        private static void Read(Color32 a, Color32 b, float[] weights)
        {
            weights[0] = a.r / 255f;
            weights[1] = a.g / 255f;
            weights[2] = a.b / 255f;
            weights[3] = a.a / 255f;
            weights[4] = b.r / 255f;
            weights[5] = b.g / 255f;
            weights[6] = b.b / 255f;
            weights[7] = b.a / 255f;
        }

        private static byte Byte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

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

        /// <summary>Shows what's been painted (call after each lot of dabs).</summary>
        internal static void Flush()
        {
            if (!dirtyAny || levels == null) return;
            dirtyAny = false;
            for (int m = 0; m < 2; m++)
            {
                if (maps[m] == null) maps[m] = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPaths[m]);
                if (maps[m] == null) return;
            }
            RectInt rect = dirty;
            for (int m = 0; m < 2; m++)
            {
                for (int level = 0; level < levels[m].Length; level++)
                {
                    int w = levelWidth[level], h = levelHeight[level];
                    int x0 = Mathf.Clamp(rect.xMin >> level, 0, w - 1), x1 = Mathf.Clamp((rect.xMax - 1) >> level, 0, w - 1);
                    int y0 = Mathf.Clamp(rect.yMin >> level, 0, h - 1), y1 = Mathf.Clamp((rect.yMax - 1) >> level, 0, h - 1);
                    if (level > 0) Shrink(m, level, x0, x1, y0, y1);
                    int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                    var block = new Color32[bw * bh];
                    Color32[] pixels = levels[m][level];
                    for (int y = 0; y < bh; y++) System.Array.Copy(pixels, (y0 + y) * w + x0, block, y * bw, bw);
                    maps[m].SetPixels32(x0, y0, bw, bh, block, level);
                }
                maps[m].Apply(false);
            }

            // (grass grows by the textures too)
            float texelX = area.width / levelWidth[0], texelZ = area.height / levelHeight[0];
            GrassField.AreaChanged(new Rect(area.x + rect.xMin * texelX, area.y + rect.yMin * texelZ, rect.width * texelX, rect.height * texelZ));
        }

        private static void Shrink(int m, int level, int x0, int x1, int y0, int y1)
        {
            Color32[] below = levels[m][level - 1], here = levels[m][level];
            int bw = levelWidth[level - 1], bh = levelHeight[level - 1], w = levelWidth[level];
            for (int y = y0; y <= y1; y++)
            {
                int sy0 = Mathf.Min(y * 2, bh - 1), sy1 = Mathf.Min(y * 2 + 1, bh - 1);
                for (int x = x0; x <= x1; x++)
                {
                    int sx0 = Mathf.Min(x * 2, bw - 1), sx1 = Mathf.Min(x * 2 + 1, bw - 1);
                    Color32 a = below[sy0 * bw + sx0], b = below[sy0 * bw + sx1], c = below[sy1 * bw + sx0], d = below[sy1 * bw + sx1];
                    here[y * w + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) / 4), (byte)((a.g + b.g + c.g + d.g + 2) / 4),
                                                  (byte)((a.b + b.b + c.b + d.b + 2) / 4), (byte)((a.a + b.a + c.a + d.a + 2) / 4));
                }
            }
        }

        /// <summary>Finishes a soft-brush stroke (one step for Ctrl+Z).</summary>
        internal static void EndStroke(string name)
        {
            Flush();
            strokeHasRect = false;
            if (strokeBefore.Count == 0 || levels == null)
            {
                strokeBefore.Clear();
                strokeAmount.Clear();
                return;
            }
            var step = new TextureStep(strokeRect, levelWidth[0], levelHeight[0]);
            foreach (KeyValuePair<int, (Color32 a, Color32 b)> pixel in strokeBefore)
            {
                step.Indices.Add(pixel.Key);
                step.Before.Add(pixel.Value);
                step.After.Add((levels[0][0][pixel.Key], levels[1][0][pixel.Key]));
            }
            strokeBefore.Clear();
            strokeAmount.Clear();
            unsaved = true;
            LandPaint.Commit(step, name);
        }

        // Ctrl+Z in the middle of a stroke: what it painted is put back
        private static void Cancel()
        {
            if (strokeBefore.Count > 0 && levels != null)
            {
                foreach (KeyValuePair<int, (Color32 a, Color32 b)> pixel in strokeBefore)
                {
                    levels[0][0][pixel.Key] = pixel.Value.a;
                    levels[1][0][pixel.Key] = pixel.Value.b;
                }
                MarkDirty(strokeRect);
                Flush();
            }
            strokeBefore.Clear();
            strokeAmount.Clear();
            strokeHasRect = false;
        }

        /// <summary>Takes every soft-painted texture off the land (one step for Ctrl+Z). Returns false if there were none.</summary>
        internal static bool ClearSoft()
        {
            if (Load() != null) return false;
            var step = new TextureStep(new RectInt(0, 0, levelWidth[0], levelHeight[0]), levelWidth[0], levelHeight[0]);
            Color32[] first = levels[0][0], second = levels[1][0];
            for (int i = 0; i < first.Length; i++)
            {
                if (IsEmpty(first[i]) && IsEmpty(second[i])) continue;
                step.Indices.Add(i);
                step.Before.Add((first[i], second[i]));
                step.After.Add((default, default));
            }
            if (step.Indices.Count == 0) return false;
            step.Redo();
            LandPaint.Commit(step, "Clear Painted Textures");
            return true;
        }

        private static bool IsEmpty(Color32 c) => c.r == 0 && c.g == 0 && c.b == 0 && c.a == 0;

        /// <summary>Which texture (1 to 8) is painted most at a point on the land (whole triangles first; 0 = none).</summary>
        internal static int PickAt(LandPaint.Hit hit)
        {
            int onTriangle = LandPaint.TriangleTexture(hit.Piece, hit.Triangle);
            if (onTriangle > 0) return onTriangle;
            if (levels == null && (!Exist || Load() != null)) return 0;
            int w = levelWidth[0], h = levelHeight[0];
            int x = Mathf.FloorToInt((hit.Local.x - area.x) / area.width * w);
            int y = Mathf.FloorToInt((hit.Local.z - area.y) / area.height * h);
            if (x < 0 || y < 0 || x >= w || y >= h) return 0;
            var weights = new float[Count];
            Read(levels[0][0][y * w + x], levels[1][0][y * w + x], weights);
            int best = 0;
            float most = 0.3f;
            for (int k = 0; k < Count; k++)
            {
                if (weights[k] <= most) continue;
                most = weights[k];
                best = k + 1;
            }
            return best;
        }

        private sealed class TextureStep : LandPaint.Step
        {
            public readonly List<int> Indices = new List<int>();
            public readonly List<(Color32 a, Color32 b)> Before = new List<(Color32, Color32)>();
            public readonly List<(Color32 a, Color32 b)> After = new List<(Color32, Color32)>();
            private readonly RectInt rect;
            private readonly int width, height;

            public TextureStep(RectInt rect, int width, int height)
            {
                this.rect = rect;
                this.width = width;
                this.height = height;
            }

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<(Color32 a, Color32 b)> values)
            {
                if (levels == null && Load() != null) return;
                if (levels == null || levelWidth[0] != width || levelHeight[0] != height) return;   // (resized since)
                Color32[] first = levels[0][0], second = levels[1][0];
                for (int k = 0; k < Indices.Count; k++)
                {
                    int i = Indices[k];
                    if (i >= first.Length) continue;
                    first[i] = values[k].a;
                    second[i] = values[k].b;
                }
                MarkDirty(rect);
                Flush();
                unsaved = true;
            }
        }

        // Saves the maps to their PNGs (when Paint Land saves)
        private static void Save()
        {
            if (!unsaved || levels == null) return;
            unsaved = false;
            try
            {
                for (int m = 0; m < 2; m++) Write(MapPaths[m], levels[m][0], levelWidth[0], levelHeight[0]);
            }
            catch (System.Exception e)
            {
                unsaved = true;
                Debug.LogWarning("Paint Land: couldn't save the painted textures (" + e.Message + "). It'll try again.");
                LandPaint.ScheduleSave();
            }
        }
    }

    /// <summary>
    /// Editor only. Keeps Land Textures 1-4.png and 5-8.png set up the way painting needs them (readable, exact amounts:
    /// not compressed, no colour conversion, clamped, with smaller versions for the distance), and lets the painter know when
    /// they've been reimported.
    /// </summary>
    internal sealed class LandTexturesImport : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (assetPath != LandTextures.MapPaths[0] && assetPath != LandTextures.MapPaths[1]) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
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
                if (path != LandTextures.MapPaths[0] && path != LandTextures.MapPaths[1]) continue;
                EditorApplication.delayCall -= LandTextures.AfterImported;
                EditorApplication.delayCall += LandTextures.AfterImported;
                return;
            }
        }
    }
}
#endif
