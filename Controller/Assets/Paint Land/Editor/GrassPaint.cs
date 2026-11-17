#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Sets up the Grass Field (Paint Land > Grass > Add Grass Field), and paints where it grows by hand: Add
    /// (more grass, even off the green ground), Remove (none), Automatic (back to what the land decides). That's kept in
    /// Land Grass.png (next to Land Settings), a texture laid over the land from above: red = how much grass, alpha = how much
    /// the painting counts over the land's own choice. Each stroke is a step for Ctrl+Z (with Paint Land's other strokes),
    /// and it saves by itself a moment after you stop.
    /// </summary>
    [InitializeOnLoad]
    internal static class GrassPaint
    {
        internal const string MaskPath = LandBuilder.Folder + "/Land Grass.png";
        internal const string MaterialPath = "Assets/Paint Land/Data/Materials/Grass Field.mat";
        private const string ShaderPath = "Assets/Paint Land/Shaders/GrassField.shader";
        private const string TuftPrefab = "Assets/Paint Land/Sample/Prefabs/Grass Tuft.prefab";
        private const float MaskDetail = 2f;   // metres per pixel of the mask

        internal enum Brush { Add, Remove, Automatic }

        static GrassPaint()
        {
            LandPaint.Saving += Save;
        }

        internal static GrassField Find() => Object.FindAnyObjectByType<GrassField>();

        // ================================================================ Setting up

        /// <summary>Puts a Grass Field in the scene for the Land (or returns the one there is).</summary>
        internal static GrassField Create()
        {
            GrassField existing = Find();
            if (existing != null) return existing;
            GameObject land = LandBuilder.FindLand();
            if (land == null)
            {
                EditorUtility.DisplayDialog("Grass Field", "There's no Land in the open scene. Build it with Tools > Paint Land > Rebuild Land first.", "OK");
                return null;
            }
            Material material = GetMaterial();
            if (material == null)
            {
                EditorUtility.DisplayDialog("Grass Field", $"Couldn't find the grass shader ({ShaderPath}).", "OK");
                return null;
            }

            var go = new GameObject("Grass Field");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, land.scene);
            if (land.transform.parent == null) go.transform.SetSiblingIndex(land.transform.GetSiblingIndex() + 1);
            var field = go.AddComponent<GrassField>();
            field.land = land;
            field.material = material;
            TuftMeshes(out field.nearMesh, out field.farMesh);
            field.maskArea = LandArea(land);
            Undo.RegisterCreatedObjectUndo(go, "Add Grass Field");
            EditorSceneManager.MarkSceneDirty(land.scene);
            GrassField.LandChanged();
            return field;
        }

        // The Grass Field material: the grass shader, with the nature pack's grass blades for its shape
        private static Material GetMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) return material;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) shader = Shader.Find("Paint Land/Grass Field");
            if (shader == null) return null;
            material = new Material(shader) { name = "Grass Field", enableInstancing = true };
            Material source = TuftMaterial();
            if (source != null)
            {
                Texture blades = source.HasProperty("_BaseTexture") ? source.GetTexture("_BaseTexture")
                               : source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap")
                               : source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
                if (blades != null) material.SetTexture("_BaseMap", blades);
                if (source.HasProperty("_LeavesThickness")) material.SetFloat("_Cutoff", 1f - source.GetFloat("_LeavesThickness"));
            }
            LandBuilder.EnsureFolder("Assets/Paint Land/Data/Materials");
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        private static Material TuftMaterial()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TuftPrefab);
            Renderer renderer = prefab != null ? prefab.GetComponentInChildren<Renderer>(true) : null;
            return renderer != null ? renderer.sharedMaterial : null;
        }

        // The tuft close up and further away: the nature pack grass's first two levels of detail
        private static void TuftMeshes(out Mesh near, out Mesh far)
        {
            near = far = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TuftPrefab);
            if (prefab == null) return;
            if (prefab.TryGetComponent(out LODGroup group))
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length > 0) near = MeshOf(lods[0]);
                if (lods.Length > 1) far = MeshOf(lods[1]);
            }
            if (near == null)
            {
                MeshFilter filter = prefab.GetComponentInChildren<MeshFilter>(true);
                if (filter != null) near = filter.sharedMesh;
            }
        }

        private static Mesh MeshOf(LOD lod)
        {
            foreach (Renderer renderer in lod.renderers)
            {
                if (renderer != null && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) return filter.sharedMesh;
            }
            return null;
        }

        // Where the land lies (x, z in its own space), for the mask
        private static Rect LandArea(GameObject land)
        {
            Rect area = LandBuilder.GetSettings().pathArea;
            if (area.width > 0f && area.height > 0f) return area;
            bool any = false;
            Bounds all = default;
            foreach (MeshFilter filter in land.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!LandBuilder.IsPieceObject(land, filter.transform) || filter.sharedMesh == null) continue;
                if (!any) all = filter.sharedMesh.bounds;
                else all.Encapsulate(filter.sharedMesh.bounds);
                any = true;
            }
            return any ? new Rect(all.min.x, all.min.z, all.size.x, all.size.z) : new Rect(-1000f, -1000f, 2000f, 2000f);
        }

        // ================================================================ The mask

        /// <summary>Gets the mask ready to paint (making Land Grass.png the first time). Null if ready, or what's wrong.</summary>
        internal static string PrepareMask(GrassField field)
        {
            if (field.mask != null && field.MaskPixels != null) return null;
            Rect area = field.maskArea.width > 0f && field.maskArea.height > 0f ? field.maskArea
                      : field.land != null ? LandArea(field.land) : LandBuilder.GetSettings().pathArea;
            if (area.width <= 0f || area.height <= 0f) return "Couldn't tell where the land is. Rebuild it first.";

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(MaskPath);
            if (texture == null)
            {
                int width = Mathf.Clamp(Mathf.CeilToInt(area.width / MaskDetail), 16, 2048);
                int height = Mathf.Clamp(Mathf.CeilToInt(area.height / MaskDetail), 16, 2048);
                LandBuilder.EnsureFolder(LandBuilder.Folder);
                File.WriteAllBytes(FullPath(MaskPath), ImageConversion.EncodeArrayToPNG(new Color32[width * height],
                    GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height));
                AssetDatabase.ImportAsset(MaskPath, ImportAssetOptions.ForceUpdate);
            }
            SetImport();
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(MaskPath);
            if (texture == null || !texture.isReadable) return "Couldn't make Land Grass.png readable.";

            Undo.RecordObject(field, "Grass Mask");
            field.SetMask(texture, texture.GetPixels32(), area);
            EditorUtility.SetDirty(field);
            EditorSceneManager.MarkSceneDirty(field.gameObject.scene);
            return null;
        }

        // Readable, exact (not compressed, no colour conversion), no smaller versions
        private static void SetImport()
        {
            if (!(AssetImporter.GetAtPath(MaskPath) is TextureImporter importer)) return;
            bool right = importer.isReadable && !importer.sRGBTexture && !importer.mipmapEnabled &&
                         importer.textureCompression == TextureImporterCompression.Uncompressed && importer.npotScale == TextureImporterNPOTScale.None;
            if (right) return;
            importer.textureType = TextureImporterType.Default;
            importer.isReadable = true;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        private static string FullPath(string assetPath) => Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);

        // ================================================================ Painting

        private static readonly Dictionary<int, Color32> before = new Dictionary<int, Color32>();
        private static readonly Dictionary<int, float> reached = new Dictionary<int, float>();
        private static bool unsaved;

        /// <summary>
        /// Paints the mask in a soft round dab at a point on the land (x, z). Add: amount (0 to 1) of grass. Within a stroke,
        /// dabs don't build up on each other.
        /// </summary>
        internal static void Stamp(GrassField field, Vector2 centre, float radius, Brush brush, float amount)
        {
            Color32[] pixels = field.MaskPixels;
            if (pixels == null) return;
            int width = field.MaskWidth, height = field.MaskHeight;
            Rect area = field.maskArea;
            float texelX = area.width / width, texelZ = area.height / height;
            float soft = Mathf.Max(radius * 0.3f, Mathf.Max(texelX, texelZ));
            int x0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - radius - area.x) / texelX));
            int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((centre.x + radius - area.x) / texelX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((centre.y - radius - area.y) / texelZ));
            int y1 = Mathf.Min(height - 1, Mathf.CeilToInt((centre.y + radius - area.y) / texelZ));
            if (x0 > x1 || y0 > y1) return;
            byte target = (byte)Mathf.RoundToInt(Mathf.Clamp01(amount) * 255f);

            for (int y = y0; y <= y1; y++)
            {
                float dz = area.y + (y + 0.5f) * texelZ - centre.y;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = area.x + (x + 0.5f) * texelX - centre.x;
                    float t = Mathf.Clamp01((radius - Mathf.Sqrt(dx * dx + dz * dz)) / soft);
                    if (t <= 0f) continue;
                    int i = y * width + x;
                    if (reached.TryGetValue(i, out float had) && had >= t) continue;
                    if (!before.TryGetValue(i, out Color32 was))
                    {
                        was = pixels[i];
                        before[i] = was;
                    }
                    reached[i] = t;
                    pixels[i] = Painted(was, brush, target, t);
                }
            }
            GrassField.AreaChanged(new Rect(area.x + x0 * texelX, area.y + y0 * texelZ, (x1 - x0 + 1) * texelX, (y1 - y0 + 1) * texelZ));
        }

        private static Color32 Painted(Color32 was, Brush brush, byte target, float t)
        {
            byte sure = (byte)Mathf.Max(was.a, Mathf.RoundToInt(t * 255f));
            switch (brush)
            {
                case Brush.Add:
                {
                    float from = was.a > 0 ? was.r : target;
                    return new Color32((byte)Mathf.RoundToInt(Mathf.Lerp(from, target, t)), 0, 0, sure);
                }
                case Brush.Remove:
                {
                    float from = was.a > 0 ? was.r : 0f;
                    return new Color32((byte)Mathf.RoundToInt(from * (1f - t)), 0, 0, sure);
                }
                default:
                    return new Color32(was.r, 0, 0, (byte)Mathf.RoundToInt(was.a * (1f - t)));
            }
        }

        /// <summary>Finishes a stroke (one step for Ctrl+Z).</summary>
        internal static void EndStroke(string name)
        {
            if (before.Count == 0) return;
            GrassField field = Find();
            if (field == null || field.MaskPixels == null)
            {
                before.Clear();
                reached.Clear();
                return;
            }
            var step = new MaskStep();
            foreach (KeyValuePair<int, Color32> pixel in before)
            {
                step.Indices.Add(pixel.Key);
                step.Before.Add(pixel.Value);
                step.After.Add(field.MaskPixels[pixel.Key]);
            }
            before.Clear();
            reached.Clear();
            unsaved = true;
            KeepInTexture(field);
            LandPaint.Commit(step, name);
        }

        // The mask texture keeps what's painted too (so the Grass Field reading it again, after a script reload or a
        // setting changed, gets it), until it's saved to Land Grass.png
        private static void KeepInTexture(GrassField field)
        {
            if (field.mask == null || field.MaskPixels == null || !field.mask.isReadable) return;
            if (field.mask.width != field.MaskWidth || field.mask.height != field.MaskHeight) return;
            field.mask.SetPixels32(field.MaskPixels);
            field.mask.Apply(false);
        }

        private sealed class MaskStep : LandPaint.Step
        {
            public readonly List<int> Indices = new List<int>();
            public readonly List<Color32> Before = new List<Color32>();
            public readonly List<Color32> After = new List<Color32>();

            public override void Undo() => Put(Before);
            public override void Redo() => Put(After);

            private void Put(List<Color32> colours)
            {
                GrassField field = Find();
                if (field == null || field.MaskPixels == null) return;
                Color32[] pixels = field.MaskPixels;
                for (int k = 0; k < Indices.Count; k++)
                {
                    if (Indices[k] < pixels.Length) pixels[Indices[k]] = colours[k];
                }
                GrassField.AreaChanged(field.maskArea);
                KeepInTexture(field);
                unsaved = true;
            }
        }

        // Saves the mask to Land Grass.png (when Paint Land saves)
        private static void Save()
        {
            if (!unsaved) return;
            GrassField field = Find();
            if (field == null || field.MaskPixels == null || field.mask == null) return;
            unsaved = false;
            try
            {
                File.WriteAllBytes(FullPath(MaskPath), ImageConversion.EncodeArrayToPNG(field.MaskPixels, GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)field.MaskWidth, (uint)field.MaskHeight));
            }
            catch (System.Exception e)
            {
                unsaved = true;
                Debug.LogWarning("Paint Land: couldn't save the grass (" + e.Message + "). It'll try again.");
            }
        }

        // ================================================================ The grass objects scattered before

        /// <summary>The grass tufts in the scene as objects (scattered, or placed with Polybrush): the Grass Field replaces them.</summary>
        internal static List<GameObject> GrassObjects()
        {
            var prefabs = new HashSet<GameObject>();
            var tuft = AssetDatabase.LoadAssetAtPath<GameObject>(TuftPrefab);
            if (tuft != null) prefabs.Add(tuft);
            foreach (LandScatterGroup kind in LandBuilder.GetSettings().scatter)
            {
                if (kind == null || kind.name != "Grass") continue;
                foreach (GameObject prefab in kind.prefabs)
                {
                    if (prefab != null) prefabs.Add(prefab);
                }
            }
            var found = new List<GameObject>();
            GameObject land = LandBuilder.FindLand();
            if (land == null) return found;
            foreach (GameObject root in land.scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                    if (!prefabs.Contains(PrefabUtility.GetCorrespondingObjectFromSource(go))) continue;
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(go) || PrefabUtility.IsAddedGameObjectOverride(go)) found.Add(go);
                }
            }
            return found;
        }

        /// <summary>Deletes those grass objects (one step for Ctrl+Z).</summary>
        internal static void RemoveGrassObjects(List<GameObject> objects)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            foreach (GameObject go in objects)
            {
                if (go != null) Undo.DestroyObjectImmediate(go);
            }
            Undo.SetCurrentGroupName("Remove Grass Objects");
            Undo.CollapseUndoOperations(group);
        }
    }
}
#endif
