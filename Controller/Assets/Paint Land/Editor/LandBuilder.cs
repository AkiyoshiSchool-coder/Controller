#if UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Tools > Paint Land > Rebuild Land: rebuilds the game's land from the land model (any mesh or model)
    ///, as set in Land Settings: evenly sized triangles over the model's shape (its hills rounded off), with
    /// Astroneer-style colours in its vertex colours, split into pieces with colliders, using the Land material (vertex
    /// colours, plus a texture if you give it one). The land model itself is left as it is: in the scene it's switched off
    /// and the new Land put in its place, and the trees, rocks and plants from the nature pack that stood on it are moved
    /// up or down onto the new ground. Rebuilding again updates the same Land, keeping what's been painted on it with
    /// Tools > Paint Land (the triangles coloured in stay where they were; the paths are kept separately).
    /// Each triangle has its own three vertices, so it can be coloured in on its own, with that colour in a second vertex
    /// stream (TEXCOORD2) that the Land material lays over the ground's colours, and a texture painted on the whole triangle
    /// in a third (TEXCOORD3). The land's shape changed in Paint Land > Shape is kept too (in Land Shape.bytes, as how far
    /// each point was raised or lowered), and put back on the new land.
    /// It runs by itself once, the first time Unity loads it (and again once to update a land built by an older version
    /// of this, quietly). Nothing is saved in the scene until you save it.
    /// </summary>
    [InitializeOnLoad]
    internal static class LandBuilder
    {
        private const string SourcePath = "Assets/Paint Land/Sample/Sample Land.prefab";
        internal const string Folder = "Assets/Paint Land/Data/Land";
        internal const string SettingsPath = Folder + "/Land Settings.asset";
        private const string OldMeshesPath = Folder + "/Land Meshes.asset";   // where the pieces' meshes used to be, all in one file
        internal const string PiecesFolder = Folder + "/Land Pieces";         // a file per piece, so painting one saves quickly
        internal const string MaterialPath = Folder + "/Land.mat";
        internal const string PrefabPath = Folder + "/Land.prefab";
        private const string ShaderPath = "Assets/Paint Land/Shaders/LandVertexColours.shader";
        private const string PropsFolder = "Assets/Paint Land/Sample/";
        private const string DoneKey = "PaintLand.Builder.Done.";

        // The pieces' vertices: where, which way the ground faces (smoothly), and the ground's colour; then, in a stream of
        // their own (so painting can update it alone), the colour painted on the vertex's triangle (alpha 0 = none); then,
        // in a third, the texture painted on its triangle (x = which, 1 to 8, 0 = none; y = how much); then, in a fourth,
        // the ground's colour painted by hand at the vertex's corner (Paint Ground), its colour times how much (so it blends
        // smoothly across a triangle), and how much (alpha) - kept apart from the ground's own colour, so the grass still
        // grows by the land's own colours but takes on the painted ones
        internal const int PaintStream = 1;
        internal const int TextureStream = 2;
        internal const int GroundPaintStream = 3;
        internal static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.UNorm8, 4, PaintStream),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm8, 4, TextureStream),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UNorm8, 4, GroundPaintStream),
        };

        /// <summary>A vertex of a piece, as kept in its first stream.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct LandVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Color32 Colour;
        }

        /// <summary>Is this mesh one of the land's own pieces (not a tree or plant placed under the Land)?</summary>
        internal static bool IsPiece(Mesh mesh)
        {
            return mesh != null && AssetDatabase.GetAssetPath(mesh).StartsWith(Folder + "/", System.StringComparison.Ordinal);
        }

        /// <summary>Is this one of the Land's own pieces (a child of it named like "Land 2_3"), not a plant placed under it?</summary>
        internal static bool IsPieceObject(GameObject land, Transform t)
        {
            if (land == null || t == null || t.parent != land.transform) return false;
            string name = t.name;
            if (name == "Land Meshes") return true;     // (the first piece, as an older version of this named it)
            if (!name.StartsWith("Land ", System.StringComparison.Ordinal)) return false;
            int split = name.IndexOf('_', 5);
            return split > 5 && int.TryParse(name.Substring(5, split - 5), out _) && int.TryParse(name.Substring(split + 1), out _);
        }

        /// <summary>A piece's own mesh (in Land Pieces), by the piece's name.</summary>
        internal static Mesh PieceMesh(string name) => AssetDatabase.LoadAssetAtPath<Mesh>($"{PiecesFolder}/{name}.asset");

        /// <summary>Is this a Polybrush component (Polybrush gives each mesh it touches its own copy of it)?</summary>
        internal static bool IsPolybrush(System.Type type)
        {
            string space = type.Namespace ?? string.Empty;
            return space.StartsWith("UnityEngine.Polybrush", System.StringComparison.Ordinal) ||
                   space.StartsWith("UnityEditor.Polybrush", System.StringComparison.Ordinal) || type.Name == "PolybrushMesh";
        }

        /// <summary>
        /// Puts the Land's pieces back on their own meshes, where something (Polybrush) swapped in its own copy, so they can
        /// be painted and rebuilt. Keep colours: the colours painted on the copies are copied onto the pieces' own meshes.
        /// Returns how many pieces it put back.
        /// </summary>
        internal static int TakeBackPieces(GameObject land, bool keepColours)
        {
            int taken = 0;
            foreach (MeshFilter filter in land.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!IsPieceObject(land, filter.transform)) continue;
                Mesh piece = PieceMesh(filter.gameObject.name);
                if (piece == null) continue;
                GameObject go = filter.gameObject;
                go.TryGetComponent(out MeshRenderer renderer);
                bool polybrush = false;
                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component != null && IsPolybrush(component.GetType())) polybrush = true;
                }
                bool streams = renderer != null && renderer.additionalVertexStreams != null;
                if (filter.sharedMesh == piece && !polybrush && !streams) continue;

                if (keepColours)
                {
                    if (filter.sharedMesh != null && filter.sharedMesh != piece) CopyColours(filter.sharedMesh, piece);
                    if (streams) CopyColours(renderer.additionalVertexStreams, piece);
                }
                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component != null && IsPolybrush(component.GetType())) Undo.DestroyObjectImmediate(component);
                }
                Undo.RecordObject(filter, "Take Back Land Piece");
                filter.sharedMesh = piece;
                if (renderer != null && renderer.additionalVertexStreams != null)
                {
                    Undo.RecordObject(renderer, "Take Back Land Piece");
                    renderer.additionalVertexStreams = null;
                }
                if (go.TryGetComponent(out MeshCollider collider))
                {
                    Undo.RecordObject(collider, "Take Back Land Piece");
                    collider.sharedMesh = null;
                    collider.sharedMesh = piece;
                }
                taken++;
            }
            if (taken > 0) EditorSceneManager.MarkSceneDirty(land.scene);
            return taken;
        }

        // Copies the colours of a copy of a piece onto the piece's own mesh (matching vertices by where they are)
        private static void CopyColours(Mesh from, Mesh piece)
        {
            if (from == null || !from.isReadable || !CanPaint(piece)) return;
            Color32[] colours = from.colors32;
            if (colours == null || colours.Length == 0) return;
            Vector3[] points = from.vertices;
            LandVertex[] vertices;
            using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(piece))
            {
                vertices = data[0].GetVertexData<LandVertex>(0).ToArray();
            }
            bool changed = false;
            if (points.Length == colours.Length)
            {
                var byPlace = new Dictionary<Vector3Int, Color32>();
                for (int i = 0; i < points.Length; i++) byPlace[Vector3Int.RoundToInt(points[i] * 100f)] = colours[i];
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!byPlace.TryGetValue(Vector3Int.RoundToInt(vertices[i].Position * 100f), out Color32 colour)) continue;
                    changed |= Differs(vertices[i].Colour, colour);
                    vertices[i].Colour = colour;
                }
            }
            else if (colours.Length == vertices.Length)
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    changed |= Differs(vertices[i].Colour, colours[i]);
                    vertices[i].Colour = colours[i];
                }
            }
            if (!changed) return;
            piece.SetVertexBufferData(vertices, 0, 0, vertices.Length, 0,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds);
            EditorUtility.SetDirty(piece);
            AssetDatabase.SaveAssetIfDirty(piece);
        }

        private static bool Differs(Color32 a, Color32 b) => a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a;

        /// <summary>Why a mesh can't be painted (for messages).</summary>
        internal static string WhyNotPaintable(Mesh mesh)
        {
            if (mesh == null) return "it has no mesh";
            string copy = AssetDatabase.Contains(mesh) ? string.Empty : ", a copy that isn't saved anywhere";
            if (!mesh.isReadable) return $"its mesh '{mesh.name}'{copy} isn't readable";
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord2)) return $"its mesh '{mesh.name}'{copy} has no paint channel";
            return $"its mesh '{mesh.name}'{copy} has its paint channel as {mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord2)} " +
                   $"x{mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord2)} in stream {mesh.GetVertexAttributeStream(VertexAttribute.TexCoord2)}";
        }

        /// <summary>Does this piece's mesh have the stream the triangles' painted textures go in?</summary>
        internal static bool CanTexture(Mesh mesh)
        {
            return CanPaint(mesh) && mesh.HasVertexAttribute(VertexAttribute.TexCoord3)
                   && mesh.GetVertexAttributeStream(VertexAttribute.TexCoord3) == TextureStream
                   && mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord3) == VertexAttributeFormat.UNorm8
                   && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord3) == 4;
        }

        /// <summary>Does this piece's mesh have the stream the ground's hand-painted colours go in?</summary>
        internal static bool CanPaintGround(Mesh mesh)
        {
            return CanTexture(mesh) && mesh.HasVertexAttribute(VertexAttribute.TexCoord4)
                   && mesh.GetVertexAttributeStream(VertexAttribute.TexCoord4) == GroundPaintStream
                   && mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord4) == VertexAttributeFormat.UNorm8
                   && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord4) == 4;
        }

        /// <summary>
        /// Gives a piece's mesh, built before textures (or the ground's colours) could be painted on it, the streams they go
        /// in (keeping everything else). Returns true if it changed it.
        /// </summary>
        internal static bool AddStreams(Mesh mesh)
        {
            if (!CanPaint(mesh) || CanPaintGround(mesh)) return false;
            if (mesh.GetVertexBufferStride(0) != Marshal.SizeOf<LandVertex>()) return false;
            LandVertex[] vertices;
            Color32[] paint, texture = null;
            using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                vertices = data[0].GetVertexData<LandVertex>(0).ToArray();
                paint = data[0].GetVertexData<Color32>(PaintStream).ToArray();
                if (CanTexture(mesh)) texture = data[0].GetVertexData<Color32>(TextureStream).ToArray();
            }
            int count = vertices.Length;
            if (paint.Length != count || (texture != null && texture.Length != count)) return false;
            SubMeshDescriptor sub = mesh.GetSubMesh(0);
            Bounds bounds = mesh.bounds;
            const MeshUpdateFlags quick = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers |
                                          MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds;
            mesh.SetVertexBufferParams(count, Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, count, 0, quick);
            mesh.SetVertexBufferData(paint, 0, 0, count, PaintStream, quick);
            mesh.SetVertexBufferData(texture ?? new Color32[count], 0, 0, count, TextureStream, quick);
            mesh.SetVertexBufferData(new Color32[count], 0, 0, count, GroundPaintStream, quick);
            mesh.SetSubMesh(0, sub, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.bounds = bounds;
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssetIfDirty(mesh);
            return true;
        }

        /// <summary>A colour painted on the ground (alpha = how much) as kept in the fourth stream: its colour times how much.</summary>
        internal static Color32 PremultipliedPaint(Color32 paint)
        {
            if (paint.a == 0) return default;
            return new Color32((byte)((paint.r * paint.a + 127) / 255), (byte)((paint.g * paint.a + 127) / 255),
                               (byte)((paint.b * paint.a + 127) / 255), paint.a);
        }

        /// <summary>Does this piece's mesh have the stream the triangles' painted colours go in?</summary>
        internal static bool CanPaint(Mesh mesh)
        {
            return mesh != null && mesh.isReadable && mesh.HasVertexAttribute(VertexAttribute.TexCoord2)
                   && mesh.GetVertexAttributeStream(VertexAttribute.TexCoord2) == PaintStream
                   && mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord2) == VertexAttributeFormat.UNorm8
                   && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord2) == 4;
        }
        private const float PropReach = 2f;      // props standing this close to the old ground (metres) are moved with it
        private const float PatchSize = 180f;    // how wide the lighter patches of grass are (metres)

        static LandBuilder()
        {
            EditorApplication.delayCall += RunOnce;
            EditorApplication.delayCall += UpgradePieces;
        }

        private static int upgradeWaits;

        // Pieces built before textures and the ground's colours could be painted on them get the streams they go in, so
        // the Land material (which reads them) shows them right
        private static void UpgradePieces()
        {
            if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (++upgradeWaits < 5000) EditorApplication.delayCall += UpgradePieces;
                return;
            }
            if (!AssetDatabase.IsValidFolder(PiecesFolder)) return;
            int upgraded = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { PiecesFolder }))
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                if (mesh != null && AddStreams(mesh)) upgraded++;
            }
            if (upgraded > 0) Debug.Log($"Land: {upgraded} pieces of the land can now have textures and ground colours painted on them.");
        }

        private static int waits;

        // The first time Unity loads this (in this project), once it has finished importing
        private static void RunOnce()
        {
            string key = DoneKey + Application.dataPath;
            if (Application.isBatchMode || EditorPrefs.GetBool(key, false)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.playModeStateChanged -= AfterPlayMode;
                EditorApplication.playModeStateChanged += AfterPlayMode;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) == null)
            {
                if (++waits < 5000) EditorApplication.delayCall += RunOnce;
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath) == null) return; // (no land model yet)
            EditorPrefs.SetBool(key, true);

            // A land built before only needs updating (so it can be painted) if it was built by an older version of this
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab != null)
            {
                MeshFilter filter = prefab.GetComponentInChildren<MeshFilter>(true);
                if (filter != null && CanPaint(filter.sharedMesh)) return;
            }
            Build(GetSettings(), quiet: prefab != null);
        }

        private static void AfterPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= AfterPlayMode;
            EditorApplication.delayCall += RunOnce;
        }

        [MenuItem("Tools/Paint Land/Rebuild Land", priority = 1400)]
        public static void RebuildFromMenu() => Build(GetSettings());

        [MenuItem("Tools/Paint Land/Land Settings", priority = 1401)]
        public static void SelectSettings()
        {
            LandSettings settings = GetSettings();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        internal static LandSettings GetSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<LandSettings>(SettingsPath);
            if (settings != null) return settings;
            EnsureFolder(Folder);
            settings = ScriptableObject.CreateInstance<LandSettings>();
            settings.source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        // ---------------------------------------------------------------- Building

        /// <summary>Rebuilds the land. Quiet: no message at the end (just in the Console).</summary>
        public static void Build(LandSettings settings, bool quiet = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Rebuild Land", "Stop play mode first.", "OK");
                return;
            }
            if (settings == null || settings.source == null)
            {
                EditorUtility.DisplayDialog("Rebuild Land", "Set the land model in Land Settings (Tools > Paint Land > Land Settings) first.", "OK");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Rebuild Land", "Reading the land model...", 0.1f);
                string sourcePath = AssetDatabase.GetAssetPath(settings.source);
                GameObject sourceInScene = FindInstance(sourcePath);
                GameObject landInScene = FindInstance(PrefabPath);

                // The model's shape, where it is in the scene (or where it would be put)
                Matrix4x4 place = sourceInScene != null
                    ? sourceInScene.transform.localToWorldMatrix * settings.source.transform.worldToLocalMatrix
                    : Matrix4x4.identity;
                if (!ReadModel(settings.source, place, out List<Vector3> points, out List<int> triangles, out List<Color> colours))
                {
                    EditorUtility.DisplayDialog("Rebuild Land", $"Couldn't read a mesh from '{sourcePath}'.", "OK");
                    return;
                }
                var model = new Ground(points, triangles, colours);

                // The ground the props stand on now: the land built before, or the model
                Ground before = landInScene != null ? GroundOf(landInScene) : sourceInScene != null ? model : null;

                // Heights over a grid of evenly sized triangles, then bumps and colours
                EditorUtility.DisplayProgressBar("Rebuild Land", "Shaping the land...", 0.3f);
                Bounds area = model.Area;
                float size = Mathf.Max(1f, settings.triangleSize);
                int columns = Mathf.Max(1, Mathf.RoundToInt(area.size.x / size));
                int rows = Mathf.Max(1, Mathf.RoundToInt(area.size.z / size));
                float stepX = area.size.x / columns, stepZ = area.size.z / rows;
                int width = columns + 1, depth = rows + 1;
                var heights = new float[width * depth];
                var modelColours = new Color[width * depth];
                const float inset = 0.001f;
                for (int z = 0; z < depth; z++)
                {
                    float wz = Mathf.Clamp(area.min.z + z * stepZ, area.min.z + inset, area.max.z - inset);
                    for (int x = 0; x < width; x++)
                    {
                        float wx = Mathf.Clamp(area.min.x + x * stepX, area.min.x + inset, area.max.x - inset);
                        heights[z * width + x] = model.Height(wx, wz, settings.roundness, out modelColours[z * width + x]);
                    }
                }
                FillGaps(heights, width, depth);

                Vector2 noiseOffset = new Vector2(1000f + settings.seed * 37.1f, 1000f + settings.seed * 53.7f);
                if (settings.bumps > 0f)
                {
                    float bumpSize = Mathf.Max(1f, settings.bumpSize);
                    for (int z = 0; z < depth; z++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            float wx = area.min.x + x * stepX + noiseOffset.x, wz = area.min.z + z * stepZ + noiseOffset.y;
                            float bump = (Mathf.PerlinNoise(wx / bumpSize, wz / bumpSize) * 2f - 1f)
                                       + 0.5f * (Mathf.PerlinNoise(wx / (bumpSize * 0.43f) + 17.3f, wz / (bumpSize * 0.43f) + 4.1f) * 2f - 1f);
                            heights[z * width + x] += settings.bumps * bump / 1.5f;
                        }
                    }
                }

                // The colours go from low to high by the land's built height (before it's reshaped in Paint Land, so a hill
                // raised there doesn't change the colours everywhere else); then the shape changed in Paint Land goes back on
                float low = float.MaxValue, high = float.MinValue;
                foreach (float h in heights)
                {
                    low = Mathf.Min(low, h);
                    high = Mathf.Max(high, h);
                }
                bool reshaped = LandShape.AddTo(heights, width, depth, area.min.x, area.min.z, stepX, stepZ, out Color32[] groundPaint);
                settings.colourLow = low;
                settings.colourHigh = high;

                Color[] landColours = settings.paintColours
                    ? Paint(settings, heights, width, depth, stepX, stepZ, area, low, high)
                    : model.HasColours ? modelColours : Fill(Color.white, heights.Length);

                // The pieces' meshes (the painter lets go of the old ones first, saving what it has)
                LandPaint.Forget();
                EditorUtility.DisplayProgressBar("Rebuild Land", "Making the meshes...", 0.6f);
                List<Mesh> meshes = MakeMeshes(settings, heights, landColours, groundPaint, width, depth, stepX, stepZ, area,
                                               out int triangleCount, out int carried);
                Material material = GetMaterial();
                LandPaint.AfterRebuild(settings, material, new Rect(area.min.x, area.min.z, area.size.x, area.size.z));

                // The Land prefab: a piece per mesh, with a collider
                EditorUtility.DisplayProgressBar("Rebuild Land", "Putting it in the scene...", 0.8f);
                GameObject like = sourceInScene != null ? sourceInScene : settings.source;
                GameObject prefab = SavePrefab(meshes, material, like.layer, GameObjectUtility.GetStaticEditorFlags(like));

                // In the scene: the new Land in place of the model (switched off, not deleted)
                var report = new StringBuilder();
                report.AppendLine($"Built the land: {triangleCount:N0} triangles about {size:0.#} m across, in {meshes.Count} pieces, " +
                                  $"with {(settings.paintColours ? "Astroneer-style" : model.HasColours ? "the model's own" : "white")} vertex colours.");
                if (carried > 0) report.AppendLine($"Kept the {carried:N0} triangles coloured in (or textured) with Paint Land.");
                if (reshaped) report.AppendLine("Kept the shape changed with Paint Land > Shape.");
                if (groundPaint != null) report.AppendLine("Kept the ground's colours painted with Paint Land.");
                if (landInScene == null && sourceInScene != null)
                {
                    landInScene = (GameObject)PrefabUtility.InstantiatePrefab(prefab, sourceInScene.scene);
                    Undo.RegisterCreatedObjectUndo(landInScene, "Rebuild Land");
                    if (sourceInScene.transform.parent != null) landInScene.transform.SetParent(sourceInScene.transform.parent, true);
                    landInScene.transform.SetSiblingIndex(sourceInScene.transform.GetSiblingIndex() + 1);

                    // Anything added under the old land (plants painted onto it, say) goes with the new one
                    var added = new List<Transform>();
                    foreach (Transform child in sourceInScene.transform)
                    {
                        if (PrefabUtility.IsAddedGameObjectOverride(child.gameObject)) added.Add(child);
                    }
                    foreach (Transform child in added) Undo.SetTransformParent(child, landInScene.transform, "Rebuild Land");

                    Undo.RecordObject(sourceInScene, "Rebuild Land");
                    sourceInScene.SetActive(false);
                    report.AppendLine($"Put it in the scene in place of '{sourceInScene.name}', which is switched off (delete it when you're happy)" +
                                      (added.Count > 0 ? $", and moved the {added.Count} things placed under it onto the new land." : "."));
                }
                else if (landInScene != null)
                {
                    // The pieces use the new meshes (any Polybrush made its own copies of too), and the colliders take the
                    // new shape
                    TakeBackPieces(landInScene, false);
                    foreach (MeshCollider collider in landInScene.GetComponentsInChildren<MeshCollider>(true))
                    {
                        if (!IsPieceObject(landInScene, collider.transform)) continue;
                        Mesh mesh = collider.sharedMesh;
                        collider.sharedMesh = null;
                        collider.sharedMesh = mesh;
                    }
                }
                else
                {
                    report.AppendLine($"Drag {PrefabPath} into your scene to use it.");
                }

                // Props from the nature pack that stood on the old ground, onto the new
                if (landInScene != null && before != null)
                {
                    Ground after = GroundOf(landInScene);
                    int moved = MoveProps(landInScene.scene, before, after);
                    if (moved > 0) report.AppendLine($"Moved {moved} trees, rocks and plants onto the new ground.");
                }
                if (landInScene != null) EditorSceneManager.MarkSceneDirty(landInScene.scene);
                GrassField.LandChanged();   // (the grass grows on the new ground)

                // The meshes all in one file, from before: nothing uses them now
                if (AssetDatabase.LoadAssetAtPath<Object>(OldMeshesPath) != null) AssetDatabase.DeleteAsset(OldMeshesPath);

                settings.builtTriangles = triangleCount;
                settings.builtPieces = meshes.Count;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();

                report.AppendLine();
                report.AppendLine("Change the shape and colours in Land Settings (Tools > Paint Land > Land Settings) and press Rebuild Land. " +
                                  "Paint paths, colours and textures, and raise or lower the land, with Tools > Paint Land. " +
                                  "Land.mat (next to it) can take a texture, such as grass, over the colours. Save the scene to keep the change.");
                EditorUtility.ClearProgressBar();
                Debug.Log("Rebuild Land:\n" + report);
                if (!quiet) EditorUtility.DisplayDialog("Rebuild Land", report.ToString(), "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // The model's triangles, in the world (and its vertex colours, if it has any)
        private static bool ReadModel(GameObject model, Matrix4x4 place, out List<Vector3> points, out List<int> triangles, out List<Color> colours)
        {
            points = new List<Vector3>();
            triangles = new List<int>();
            colours = new List<Color>();
            bool anyColours = false;
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Matrix4x4 toWorld = place * filter.transform.localToWorldMatrix;
                int first = points.Count;
                using (Mesh.MeshDataArray dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData data = dataArray[0];
                    using (var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
                    {
                        data.GetVertices(vertices);
                        foreach (Vector3 vertex in vertices) points.Add(toWorld.MultiplyPoint3x4(vertex));
                    }
                    if (data.HasVertexAttribute(VertexAttribute.Color))
                    {
                        anyColours = true;
                        using (var vertexColours = new NativeArray<Color>(data.vertexCount, Allocator.Temp))
                        {
                            data.GetColors(vertexColours);
                            foreach (Color colour in vertexColours) colours.Add(colour);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < data.vertexCount; i++) colours.Add(Color.white);
                    }
                    for (int sub = 0; sub < data.subMeshCount; sub++)
                    {
                        SubMeshDescriptor descriptor = data.GetSubMesh(sub);
                        if (descriptor.topology != MeshTopology.Triangles) continue;
                        using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                        {
                            data.GetIndices(indices, sub);
                            foreach (int index in indices) triangles.Add(first + index);
                        }
                    }
                }
            }
            if (!anyColours) colours = null;
            return triangles.Count >= 3;
        }

        // The ground of a land built before (its pieces' meshes, in the world)
        private static Ground GroundOf(GameObject land)
        {
            var points = new List<Vector3>();
            var triangles = new List<int>();
            foreach (MeshFilter filter in land.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (!IsPieceObject(land, filter.transform) && !IsPiece(mesh)) continue;   // (not the plants placed under it)
                int first = points.Count;
                Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in mesh.vertices) points.Add(toWorld.MultiplyPoint3x4(vertex));
                foreach (int index in mesh.triangles) triangles.Add(first + index);
            }
            return triangles.Count >= 3 ? new Ground(points, triangles, null) : null;
        }

        // Fills any heights the model didn't cover (holes, the very edge) from their neighbours
        private static void FillGaps(float[] heights, int width, int depth)
        {
            for (int pass = 0; pass < 200; pass++)
            {
                bool missing = false;
                var next = (float[])heights.Clone();
                for (int z = 0; z < depth; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = z * width + x;
                        if (!float.IsNaN(heights[i])) continue;
                        float sum = 0f;
                        int count = 0;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                                float h = heights[nz * width + nx];
                                if (float.IsNaN(h)) continue;
                                sum += h;
                                count++;
                            }
                        }
                        if (count > 0) next[i] = sum / count;
                        else missing = true;
                    }
                }
                System.Array.Copy(next, heights, heights.Length);
                if (!missing) return;
            }
            for (int i = 0; i < heights.Length; i++)
            {
                if (float.IsNaN(heights[i])) heights[i] = 0f;
            }
        }

        // Astroneer-style colours: richer grass in the hollows and drier up high, lighter patches here and there, soil on
        // the slopes, sand at the very bottom and bare ground on the highest tops, the hollows a little darker
        private static Color[] Paint(LandSettings s, float[] heights, int width, int depth, float stepX, float stepZ, Bounds area, float low, float high)
        {
            float[] soft = Blur(heights, width, depth, 2);
            var colours = new Color[heights.Length];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    colours[z * width + x] = GroundColour(s, heights, width, depth, x, z, stepX, stepZ, area.min.x, area.min.z, low, high, soft[z * width + x]);
                }
            }
            return colours;
        }

        /// <summary>
        /// The colour Rebuild Land gives a corner of the grid of heights (x, z: which corner; origin: where the first one is;
        /// low, high: the land's lowest and highest built height; soft: the average height round it, from SoftHeight).
        /// </summary>
        internal static Color GroundColour(LandSettings s, float[] heights, int width, int depth, int x, int z, float stepX, float stepZ,
            float originX, float originZ, float low, float high, float soft)
        {
            int i = z * width + x;
            float h = heights[i];
            float up = (h - low) / Mathf.Max(high - low, 0.01f);
            int left = Mathf.Max(x - 1, 0), right = Mathf.Min(x + 1, width - 1);
            int back = Mathf.Max(z - 1, 0), front = Mathf.Min(z + 1, depth - 1);
            float dx = right > left ? (heights[z * width + right] - heights[z * width + left]) / ((right - left) * stepX) : 0f;
            float dz = front > back ? (heights[front * width + x] - heights[back * width + x]) / ((front - back) * stepZ) : 0f;
            float slope = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
            var noiseOffset = new Vector2(1000f + s.seed * 37.1f, 1000f + s.seed * 53.7f);
            float patch = Mathf.PerlinNoise((originX + x * stepX + noiseOffset.y) / PatchSize, (originZ + z * stepZ + noiseOffset.x) / PatchSize);

            Color c = Color.Lerp(s.lowGrass, s.grass, Smooth(0.05f, 0.35f, up));
            c = Color.Lerp(c, s.lightGrass, Smooth(0.45f, 0.75f, patch) * 0.8f);
            c = Color.Lerp(c, s.highGrass, Smooth(0.55f, 0.85f, up));
            c = Color.Lerp(c, s.sand, Smooth(0.03f, 0f, up));
            c = Color.Lerp(c, s.soil, Smooth(s.soilFrom, Mathf.Max(s.soilFull, s.soilFrom + 0.1f), slope));
            c = Color.Lerp(c, s.rock, Smooth(0.85f, 0.97f, up));
            float hollow = Mathf.Max(soft - h, 0f);
            c *= 1f - Mathf.Min(hollow * 0.06f, s.hollowShade);
            c.a = 1f;
            return c;
        }

        /// <summary>The average height round a corner of the grid (the 5 x 5 corners round it), for GroundColour.</summary>
        internal static float SoftHeight(float[] heights, int width, int depth, int x, int z)
        {
            float sum = 0f;
            int count = 0;
            for (int nz = Mathf.Max(z - 2, 0); nz <= Mathf.Min(z + 2, depth - 1); nz++)
            {
                for (int nx = Mathf.Max(x - 2, 0); nx <= Mathf.Min(x + 2, width - 1); nx++)
                {
                    sum += heights[nz * width + nx];
                    count++;
                }
            }
            return count > 0 ? sum / count : heights[z * width + x];
        }

        /// <summary>Which way the ground faces (smoothly) at a corner of the grid of heights.</summary>
        internal static Vector3 CornerNormal(float[] heights, int width, int depth, int x, int z, float stepX, float stepZ)
        {
            int left = Mathf.Max(x - 1, 0), right = Mathf.Min(x + 1, width - 1);
            int back = Mathf.Max(z - 1, 0), front = Mathf.Min(z + 1, depth - 1);
            float dx = right > left ? (heights[z * width + right] - heights[z * width + left]) / ((right - left) * stepX) : 0f;
            float dz = front > back ? (heights[front * width + x] - heights[back * width + x]) / ((front - back) * stepZ) : 0f;
            return new Vector3(-dx, 1f, -dz).normalized;
        }

        private static float Smooth(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static float[] Blur(float[] values, int width, int depth, int radius)
        {
            var across = new float[values.Length];
            var result = new float[values.Length];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int d = -radius; d <= radius; d++)
                    {
                        int nx = x + d;
                        if (nx < 0 || nx >= width) continue;
                        sum += values[z * width + nx];
                        count++;
                    }
                    across[z * width + x] = sum / count;
                }
            }
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int d = -radius; d <= radius; d++)
                    {
                        int nz = z + d;
                        if (nz < 0 || nz >= depth) continue;
                        sum += across[nz * width + x];
                        count++;
                    }
                    result[z * width + x] = sum / count;
                }
            }
            return result;
        }

        private static Color[] Fill(Color colour, int count)
        {
            var colours = new Color[count];
            for (int i = 0; i < count; i++) colours[i] = colour;
            return colours;
        }

        // The pieces' meshes (updating the ones built before, so everything using them keeps them), each in its own file.
        // Every triangle has its own three vertices, so it can be coloured in on its own; the triangles coloured in before
        // are coloured the same again (whichever new triangle has its middle in a coloured one).
        private static List<Mesh> MakeMeshes(LandSettings settings, float[] heights, Color[] colours, Color32[] groundPaint, int width, int depth,
            float stepX, float stepZ, Bounds area, out int triangleCount, out int carried)
        {
            EnsureFolder(PiecesFolder);
            var existing = new Dictionary<string, Mesh>();
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { PiecesFolder }))
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                if (mesh != null) existing[mesh.name] = mesh;
            }
            var paint = new PaintCarry();
            foreach (Mesh old in existing.Values) paint.Add(old);
            paint.Finish();

            // Smooth normals over the whole grid (so the pieces meet without a seam)
            var normals = new Vector3[heights.Length];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++) normals[z * width + x] = CornerNormal(heights, width, depth, x, z, stepX, stepZ);
            }

            const MeshUpdateFlags quick = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers |
                                          MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds;
            int cells = Mathf.Clamp(settings.pieceSize, 8, 250);
            int columns = width - 1, rows = depth - 1;
            int piecesX = (columns + cells - 1) / cells, piecesZ = (rows + cells - 1) / cells;
            var meshes = new List<Mesh>();
            var used = new HashSet<Mesh>();
            triangleCount = 0;
            int coloured = 0;
            for (int pz = 0; pz < piecesZ; pz++)
            {
                for (int px = 0; px < piecesX; px++)
                {
                    int x0 = px * cells, x1 = Mathf.Min(x0 + cells, columns);
                    int z0 = pz * cells, z1 = Mathf.Min(z0 + cells, rows);
                    int w = x1 - x0 + 1, d = z1 - z0 + 1;

                    // The grid's corners in this piece (and the ground's colour painted by hand at each)
                    var corners = new LandVertex[w * d];
                    var cornerPaint = new Color32[w * d];
                    for (int z = 0; z < d; z++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int i = (z0 + z) * width + x0 + x;
                            corners[z * w + x] = new LandVertex
                            {
                                Position = new Vector3(area.min.x + (x0 + x) * stepX, heights[i], area.min.z + (z0 + z) * stepZ),
                                Normal = normals[i],
                                Colour = colours[i],
                            };
                            if (groundPaint != null && i < groundPaint.Length) cornerPaint[z * w + x] = PremultipliedPaint(groundPaint[i]);
                        }
                    }

                    // Two triangles a square, split along whichever diagonal follows the ground better, each with its own
                    // three vertices
                    int vertexCount = (w - 1) * (d - 1) * 6;
                    var vertices = new LandVertex[vertexCount];
                    var painted = new Color32[vertexCount];
                    var textured = new Color32[vertexCount];
                    var groundPainted = new Color32[vertexCount];
                    int n = 0;
                    void Triangle(int i0, int i1, int i2)
                    {
                        vertices[n] = corners[i0];
                        vertices[n + 1] = corners[i1];
                        vertices[n + 2] = corners[i2];
                        groundPainted[n] = cornerPaint[i0];
                        groundPainted[n + 1] = cornerPaint[i1];
                        groundPainted[n + 2] = cornerPaint[i2];
                        Vector3 middle = (corners[i0].Position + corners[i1].Position + corners[i2].Position) / 3f;
                        if (paint.TryGet(new Vector2(middle.x, middle.z), out Color32 colour, out Color32 texture))
                        {
                            painted[n] = painted[n + 1] = painted[n + 2] = colour;
                            textured[n] = textured[n + 1] = textured[n + 2] = texture;
                            coloured++;
                        }
                        n += 3;
                    }
                    for (int z = 0; z < d - 1; z++)
                    {
                        for (int x = 0; x < w - 1; x++)
                        {
                            int a = z * w + x, b = a + 1, c = a + w, e = c + 1;  // a: back left, b: back right, c: front left, e: front right
                            if (Mathf.Abs(corners[a].Position.y - corners[e].Position.y) <= Mathf.Abs(corners[b].Position.y - corners[c].Position.y))
                            {
                                Triangle(a, c, e);
                                Triangle(a, e, b);
                            }
                            else
                            {
                                Triangle(a, c, b);
                                Triangle(b, c, e);
                            }
                        }
                    }
                    triangleCount += vertexCount / 3;

                    string name = $"Land {px}_{pz}";
                    if (!existing.TryGetValue(name, out Mesh mesh)) mesh = new Mesh { name = name };
                    mesh.Clear();
                    mesh.SetVertexBufferParams(vertexCount, Layout);
                    mesh.SetVertexBufferData(vertices, 0, 0, vertexCount, 0, quick);
                    mesh.SetVertexBufferData(painted, 0, 0, vertexCount, PaintStream, quick);
                    mesh.SetVertexBufferData(textured, 0, 0, vertexCount, TextureStream, quick);
                    mesh.SetVertexBufferData(groundPainted, 0, 0, vertexCount, GroundPaintStream, quick);
                    if (vertexCount > 65535)
                    {
                        var indices = new int[vertexCount];
                        for (int i = 0; i < vertexCount; i++) indices[i] = i;
                        mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);
                        mesh.SetIndexBufferData(indices, 0, 0, vertexCount, quick);
                    }
                    else
                    {
                        var indices = new ushort[vertexCount];
                        for (int i = 0; i < vertexCount; i++) indices[i] = (ushort)i;
                        mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt16);
                        mesh.SetIndexBufferData(indices, 0, 0, vertexCount, quick);
                    }
                    mesh.subMeshCount = 1;
                    mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCount, MeshTopology.Triangles));
                    mesh.RecalculateBounds();
                    if (AssetDatabase.Contains(mesh)) EditorUtility.SetDirty(mesh);
                    else AssetDatabase.CreateAsset(mesh, $"{PiecesFolder}/{name}.asset");
                    meshes.Add(mesh);
                    used.Add(mesh);
                }
            }

            // Pieces there are no more of
            foreach (Mesh old in existing.Values)
            {
                if (!used.Contains(old)) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(old));
            }
            AssetDatabase.SaveAssets();
            carried = coloured;
            return meshes;
        }

        // The triangles coloured in (or textured) on the land built before (from above), to paint the new land the same
        private sealed class PaintCarry
        {
            private readonly List<Vector2> corners = new List<Vector2>();   // three a triangle (x, z)
            private readonly List<Color32> colours = new List<Color32>();
            private readonly List<Color32> textures = new List<Color32>();
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            private float cellSize = 1f;

            public void Add(Mesh mesh)
            {
                if (!CanPaint(mesh)) return;
                using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData data = dataArray[0];
                    using (var points = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
                    {
                        data.GetVertices(points);
                        NativeArray<Color32> paint = data.GetVertexData<Color32>(PaintStream);
                        NativeArray<Color32> texture = CanTexture(mesh) ? data.GetVertexData<Color32>(TextureStream) : default;
                        var indices = new List<int>();
                        mesh.GetTriangles(indices, 0);
                        for (int t = 0; t + 2 < indices.Count; t += 3)
                        {
                            Color32 colour = paint[indices[t]];
                            Color32 textureHere = texture.IsCreated ? texture[indices[t]] : default;
                            bool textured = textureHere.r > 0 && textureHere.g > 0;
                            if (colour.a == 0 && !textured) continue;
                            Vector3 p0 = points[indices[t]], p1 = points[indices[t + 1]], p2 = points[indices[t + 2]];
                            corners.Add(new Vector2(p0.x, p0.z));
                            corners.Add(new Vector2(p1.x, p1.z));
                            corners.Add(new Vector2(p2.x, p2.z));
                            colours.Add(colour.a > 0 ? colour : default);
                            textures.Add(textured ? textureHere : default);
                            float across = Mathf.Max(Mathf.Max(p0.x, p1.x, p2.x) - Mathf.Min(p0.x, p1.x, p2.x),
                                                     Mathf.Max(p0.z, p1.z, p2.z) - Mathf.Min(p0.z, p1.z, p2.z));
                            cellSize = Mathf.Max(cellSize, across);
                        }
                    }
                }
            }

            public void Finish()
            {
                for (int t = 0; t < colours.Count; t++)
                {
                    Vector2 middle = (corners[t * 3] + corners[t * 3 + 1] + corners[t * 3 + 2]) / 3f;
                    long key = Key(Mathf.FloorToInt(middle.x / cellSize), Mathf.FloorToInt(middle.y / cellSize));
                    if (!cells.TryGetValue(key, out List<int> cell)) cells[key] = cell = new List<int>();
                    cell.Add(t);
                }
            }

            public bool TryGet(Vector2 point, out Color32 colour, out Color32 texture)
            {
                colour = default;
                texture = default;
                if (colours.Count == 0) return false;
                int cx = Mathf.FloorToInt(point.x / cellSize), cz = Mathf.FloorToInt(point.y / cellSize);
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!cells.TryGetValue(Key(cx + dx, cz + dz), out List<int> cell)) continue;
                        foreach (int t in cell)
                        {
                            if (!Inside(point, corners[t * 3], corners[t * 3 + 1], corners[t * 3 + 2])) continue;
                            colour = colours[t];
                            texture = textures[t];
                            return true;
                        }
                    }
                }
                return false;
            }

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                float det = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(det) < 1e-9f) return false;
                float w0 = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / det;
                float w1 = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / det;
                const float edge = -1e-4f;
                return w0 >= edge && w1 >= edge && 1f - w0 - w1 >= edge;
            }
        }

        // The Land prefab: a piece per mesh, each with a collider (an existing prefab is updated, so the Land in the
        // scene keeps everything added to it)
        private static GameObject SavePrefab(List<Mesh> meshes, Material material, int layer, StaticEditorFlags flags)
        {
            bool exists = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
            GameObject root = exists ? PrefabUtility.LoadPrefabContents(PrefabPath) : new GameObject("Land");
            try
            {
                root.layer = layer;
                GameObjectUtility.SetStaticEditorFlags(root, flags);
                var wanted = new HashSet<string>();
                foreach (Mesh mesh in meshes)
                {
                    wanted.Add(mesh.name);
                    Transform piece = root.transform.Find(mesh.name);
                    if (piece == null)
                    {
                        piece = new GameObject(mesh.name).transform;
                        piece.SetParent(root.transform, false);
                    }
                    GameObject go = piece.gameObject;
                    go.layer = layer;
                    GameObjectUtility.SetStaticEditorFlags(go, flags);
                    if (!go.TryGetComponent(out MeshFilter filter)) filter = go.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    if (!go.TryGetComponent(out MeshRenderer renderer)) renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    if (!go.TryGetComponent(out MeshCollider collider)) collider = go.AddComponent<MeshCollider>();
                    collider.sharedMesh = null;
                    collider.sharedMesh = mesh;
                }
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    GameObject child = root.transform.GetChild(i).gameObject;
                    if (!wanted.Contains(child.name)) Object.DestroyImmediate(child);
                }
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                if (exists) PrefabUtility.UnloadPrefabContents(root);
                else Object.DestroyImmediate(root);
            }
        }

        private static Material GetMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) return material;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            material = new Material(shader) { name = "Land", enableInstancing = true };
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        // Moves the nature pack's props (trees, rocks, plants) that stood on the old ground up or down onto the new
        private static int MoveProps(Scene scene, Ground before, Ground after)
        {
            int moved = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!IsProp(t)) continue;
                    Vector3 position = t.position;
                    float old = before.Height(position.x, position.z, 0f, out _);
                    float now = after.Height(position.x, position.z, 0f, out _);
                    if (float.IsNaN(old) || float.IsNaN(now) || Mathf.Abs(position.y - old) > PropReach || Mathf.Abs(now - old) < 0.001f) continue;
                    Undo.RecordObject(t, "Rebuild Land");
                    t.position = new Vector3(position.x, position.y + (now - old), position.z);
                    moved++;
                }
            }
            return moved;
        }

        /// <summary>Is this a tree, rock or plant standing on the land (from the nature pack, or scattered with Paint Land)?</summary>
        internal static bool IsProp(Transform t)
        {
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) return false;
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
            return path != null && (path.StartsWith(PropsFolder, System.StringComparison.Ordinal) || LandScatter.IsScattered(t));
        }

        /// <summary>The Land in the open scenes (null if it isn't in one).</summary>
        internal static GameObject FindLand() => FindInstance(PrefabPath);

        private static GameObject FindInstance(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
                        if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) == assetPath) return t.gameObject;
                    }
                }
            }
            return null;
        }

        internal static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        // ---------------------------------------------------------------- The ground, from above

        // The height of a surface made of triangles at any point, looked up from above, rounded off between its corners
        // if asked (each corner's slope carried across the triangle, so the hills curve instead of being flat-sided)
        private sealed class Ground
        {
            private readonly Vector3[] points;
            private readonly int[] triangles;
            private readonly Vector2[] slopes;      // how the height changes across x and z at each corner
            private readonly Color[] colours;
            private readonly List<int>[] cells;
            private readonly int cellsX, cellsZ;
            private readonly float cellSize;
            public readonly Bounds Area;
            public bool HasColours => colours != null;

            public Ground(List<Vector3> rawPoints, List<int> rawTriangles, List<Color> rawColours)
            {
                // Corners in the same place made one (a model's faces each have their own), so slopes are shared
                var index = new Dictionary<Vector3Int, int>();
                var welded = new List<Vector3>();
                var weldedColours = rawColours != null ? new List<Color>() : null;
                var remap = new int[rawPoints.Count];
                for (int i = 0; i < rawPoints.Count; i++)
                {
                    Vector3Int key = Vector3Int.RoundToInt(rawPoints[i] * 100f);
                    if (!index.TryGetValue(key, out int w))
                    {
                        w = welded.Count;
                        index[key] = w;
                        welded.Add(rawPoints[i]);
                        weldedColours?.Add(rawColours[i]);
                    }
                    remap[i] = w;
                }
                var kept = new List<int>(rawTriangles.Count);
                for (int t = 0; t + 2 < rawTriangles.Count; t += 3)
                {
                    int a = remap[rawTriangles[t]], b = remap[rawTriangles[t + 1]], c = remap[rawTriangles[t + 2]];
                    if (a == b || b == c || a == c) continue;
                    kept.Add(a);
                    kept.Add(b);
                    kept.Add(c);
                }
                points = welded.ToArray();
                triangles = kept.ToArray();
                colours = weldedColours?.ToArray();

                // Each corner's slope, from the faces round it (bigger faces counting more)
                var normals = new Vector3[points.Length];
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vector3 p0 = points[triangles[t]], p1 = points[triangles[t + 1]], p2 = points[triangles[t + 2]];
                    Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                    if (n.y < 0f) n = -n;
                    normals[triangles[t]] += n;
                    normals[triangles[t + 1]] += n;
                    normals[triangles[t + 2]] += n;
                }
                slopes = new Vector2[points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 n = normals[i];
                    if (n.y <= 1e-6f) continue;
                    slopes[i] = Vector2.ClampMagnitude(new Vector2(-n.x / n.y, -n.z / n.y), 4f);
                }

                // A grid of buckets over it, each listing the triangles that reach into it
                var bounds = new Bounds(points.Length > 0 ? points[0] : Vector3.zero, Vector3.zero);
                foreach (Vector3 p in points) bounds.Encapsulate(p);
                Area = bounds;
                cellSize = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.z) / 64f, 0.01f);
                cellsX = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / cellSize) + 1);
                cellsZ = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / cellSize) + 1);
                cells = new List<int>[cellsX * cellsZ];
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vector3 p0 = points[triangles[t]], p1 = points[triangles[t + 1]], p2 = points[triangles[t + 2]];
                    int minX = CellX(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))), maxX = CellX(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)));
                    int minZ = CellZ(Mathf.Min(p0.z, Mathf.Min(p1.z, p2.z))), maxZ = CellZ(Mathf.Max(p0.z, Mathf.Max(p1.z, p2.z)));
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            List<int> cell = cells[z * cellsX + x] ?? (cells[z * cellsX + x] = new List<int>());
                            cell.Add(t);
                        }
                    }
                }
            }

            private int CellX(float x) => Mathf.Clamp(Mathf.FloorToInt((x - Area.min.x) / cellSize), 0, cellsX - 1);
            private int CellZ(float z) => Mathf.Clamp(Mathf.FloorToInt((z - Area.min.z) / cellSize), 0, cellsZ - 1);

            // The height at (x, z) (NaN off the surface), and the colour there
            public float Height(float x, float z, float roundness, out Color colour)
            {
                colour = Color.white;
                if (x < Area.min.x - 0.01f || x > Area.max.x + 0.01f || z < Area.min.z - 0.01f || z > Area.max.z + 0.01f) return float.NaN;
                List<int> cell = cells[CellZ(z) * cellsX + CellX(x)];
                if (cell == null) return float.NaN;
                foreach (int t in cell)
                {
                    int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                    Vector3 p0 = points[i0], p1 = points[i1], p2 = points[i2];
                    float det = (p1.z - p2.z) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.z - p2.z);
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    float w0 = ((p1.z - p2.z) * (x - p2.x) + (p2.x - p1.x) * (z - p2.z)) / det;
                    float w1 = ((p2.z - p0.z) * (x - p2.x) + (p0.x - p2.x) * (z - p2.z)) / det;
                    float w2 = 1f - w0 - w1;
                    const float edge = -1e-5f;
                    if (w0 < edge || w1 < edge || w2 < edge) continue;

                    float flat = w0 * p0.y + w1 * p1.y + w2 * p2.y;
                    float height = flat;
                    if (roundness > 0f)
                    {
                        float round = w0 * (p0.y + slopes[i0].x * (x - p0.x) + slopes[i0].y * (z - p0.z))
                                    + w1 * (p1.y + slopes[i1].x * (x - p1.x) + slopes[i1].y * (z - p1.z))
                                    + w2 * (p2.y + slopes[i2].x * (x - p2.x) + slopes[i2].y * (z - p2.z));
                        height = Mathf.Lerp(flat, round, roundness);
                    }
                    if (colours != null) colour = colours[i0] * w0 + colours[i1] * w1 + colours[i2] * w2;
                    return height;
                }
                return float.NaN;
            }
        }
    }
}
#endif
