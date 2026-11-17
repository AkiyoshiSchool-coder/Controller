#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Adds water (World/Water, LandWater) for Paint Land > Water: over the whole land, or a lake where you
    /// click. All the water shares one material (Assets/Paint Land/Data/Materials/Water.mat, the Paint Land/Water shader), and the URP
    /// asset's Depth Texture is turned on so the water can tell how deep it is (for its colour and the foam at the shore).
    /// </summary>
    internal static class WaterSetup
    {
        internal const string MaterialPath = "Assets/Paint Land/Data/Materials/Water.mat";
        private const string ShaderPath = "Assets/Paint Land/Shaders/Water.shader";

        internal static Material GetMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) return material;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) shader = Shader.Find("Paint Land/Water");
            if (shader == null) return null;
            material = new Material(shader) { name = "Water", enableInstancing = true };
            LandBuilder.EnsureFolder("Assets/Paint Land/Data/Materials");
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        /// <summary>The water in the open scenes.</summary>
        internal static List<LandWater> All()
        {
            var found = new List<LandWater>(Object.FindObjectsByType<LandWater>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            found.RemoveAll(w => w == null || EditorUtility.IsPersistent(w));
            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return found;
        }

        /// <summary>Adds water: its middle at a point in the world (its height is the water level), size metres across.</summary>
        internal static LandWater Add(Vector3 middle, Vector2 size, string name)
        {
            Material material = GetMaterial();
            if (material == null)
            {
                EditorUtility.DisplayDialog("Water", $"Couldn't find the water shader ({ShaderPath}).", "OK");
                return null;
            }
            GameObject land = LandBuilder.FindLand();
            var go = new GameObject(name);
            if (land != null)
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, land.scene);
                if (land.transform.parent == null) go.transform.SetSiblingIndex(land.transform.GetSiblingIndex() + 1);
            }
            go.transform.position = middle;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            var water = go.AddComponent<LandWater>();
            water.size = new Vector2(Mathf.Max(size.x, 1f), Mathf.Max(size.y, 1f));
            Undo.RegisterCreatedObjectUndo(go, "Add Water");
            EditorSceneManager.MarkSceneDirty(go.scene);
            int turnedOn = TurnOnDepthTexture();
            if (turnedOn > 0) Debug.Log($"Water: turned on Depth Texture on {turnedOn} URP asset(s), so the water can tell how deep it is.");
            return water;
        }

        /// <summary>
        /// Adds painted water (shown only where it's painted, nowhere yet): its middle at a point in the world (its height is
        /// the water level), width x height spots of spot metres each.
        /// </summary>
        internal static LandWater AddPainted(Vector3 middle, float spot, int width, int height)
        {
            LandWater water = Add(middle, new Vector2(width * spot, height * spot), "Lake");
            if (water == null) return null;
            water.SetMask(new byte[width * height], width, height);
            EditorUtility.SetDirty(water);
            return water;
        }

        /// <summary>
        /// Deletes the trees, rocks and plants (from the nature pack, or scattered) standing in any water (one step for
        /// Ctrl+Z). Returns how many.
        /// </summary>
        internal static int ClearUnderWater()
        {
            GameObject land = LandBuilder.FindLand();
            if (land == null || LandWater.All.Count == 0) return 0;
            var doomed = new List<GameObject>();
            foreach (GameObject root in land.scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!LandBuilder.IsProp(t) || !LandWater.UnderWater(t.position, 0.02f)) continue;
                    // (only whole things: not a part inside another prefab, which can't be deleted on its own)
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject) && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject)) continue;
                    doomed.Add(t.gameObject);
                }
            }
            if (doomed.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            int deleted = 0;
            foreach (GameObject go in doomed)
            {
                if (go == null) continue;   // (inside one deleted already)
                Undo.DestroyObjectImmediate(go);
                deleted++;
            }
            Undo.SetCurrentGroupName("Delete Things In Water");
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(land.scene);
            return deleted;
        }

        /// <summary>Where the land lies (from above, in the world) and how high it goes; false if there's no Land.</summary>
        internal static bool LandArea(out Bounds bounds)
        {
            bounds = default;
            GameObject land = LandBuilder.FindLand();
            if (land == null) return false;
            bool any = false;
            foreach (MeshRenderer renderer in land.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!LandBuilder.IsPieceObject(land, renderer.transform)) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any;
        }

        /// <summary>A good first water level for the whole land: a little above its lowest ground (where the sand is).</summary>
        internal static float StartingLevel(Bounds land)
        {
            LandSettings settings = LandBuilder.GetSettings();
            float low = settings.colourHigh > settings.colourLow ? settings.colourLow : land.min.y;
            float high = settings.colourHigh > settings.colourLow ? settings.colourHigh : land.max.y;
            Transform space = LandBuilder.FindLand() != null ? LandBuilder.FindLand().transform : null;
            float level = low + (high - low) * 0.05f;
            return space != null ? space.TransformPoint(new Vector3(0f, level, 0f)).y : level;
        }

        // ---------------------------------------------------------------- The depth texture

        /// <summary>Is Depth Texture on in every URP asset in use (so the water can tell how deep it is)?</summary>
        internal static bool DepthTextureOn()
        {
            foreach (RenderPipelineAsset asset in Assets())
            {
                SerializedProperty depth = new SerializedObject(asset).FindProperty("m_RequireDepthTexture");
                if (depth != null && !depth.boolValue) return false;
            }
            return true;
        }

        /// <summary>Turns Depth Texture on in the URP assets in use. Returns how many it changed.</summary>
        internal static int TurnOnDepthTexture()
        {
            int changed = 0;
            foreach (RenderPipelineAsset asset in Assets())
            {
                var serialized = new SerializedObject(asset);
                SerializedProperty depth = serialized.FindProperty("m_RequireDepthTexture");   // (the URP asset's Depth Texture)
                if (depth == null || depth.boolValue) continue;
                depth.boolValue = true;
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                changed++;
            }
            if (changed > 0) AssetDatabase.SaveAssets();
            return changed;
        }

        // The render pipeline assets in use: the project's, and the current quality level's
        private static HashSet<RenderPipelineAsset> Assets()
        {
            var assets = new HashSet<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline != null) assets.Add(GraphicsSettings.defaultRenderPipeline);
            RenderPipelineAsset quality = QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel());
            if (quality != null) assets.Add(quality);
            return assets;
        }
    }
}
#endif
