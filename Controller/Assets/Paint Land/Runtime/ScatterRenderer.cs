using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Draws everything Paint Land scattered (the trees, bushes, rocks, flowers, mushrooms... under the Land Scatter
    /// object) fast: thousands at a time on the GPU (instancing), in 32 m squares, each kind only as far away as it's
    /// worth seeing (Draw Distance), with its simpler look past Detail Distance, and casting shadows only near the
    /// camera (Shadow Distance) - flowers and mushrooms not at all. Set per kind here, or in Paint Land > Scatter.
    ///
    /// The scattered things stay ordinary objects in the scene, so Paint Land can still paint, rub out, recolour and give
    /// them colliders; in the editor their own renderers are just switched off while this draws them (and a thing just
    /// painted is drawn by its own renderer until the stroke is done). When playing, and in a build, their renderers are
    /// taken off altogether, and the ones with no colliders (flowers, mushrooms, bushes) aren't there at all: only this
    /// draws them (ScatterSceneProcessor). Added to Land Scatter by Paint Land.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("World/Scatter Renderer")]
    public class ScatterRenderer : MonoBehaviour
    {
        [Serializable]
        public class Kind
        {
            [Tooltip("The kind (the name of its object under Land Scatter).")]
            public string name;
            [Tooltip("Not drawn further from the camera than this (metres).")]
            [Min(1f)] public float drawDistance = 250f;
            [Tooltip("Its most detailed look within this; its simpler looks further away (metres).")]
            [Min(1f)] public float detailDistance = 30f;
            [Tooltip("Casts shadows within this (metres). 0 = never (small plants: their shadows hardly show).")]
            [Min(0f)] public float shadowDistance = 40f;
        }

        [Tooltip("How far each kind is drawn, and its detail and shadows.")]
        public List<Kind> kinds = new List<Kind>();
        [Tooltip("Draw them this way in the Scene view too (off: the editor draws each one itself, slower but clickable).")]
        public bool inEditor = true;

        // ---------------------------------------------------------------- What's drawn

        [Serializable]
        private class Part
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            public int lod;               // -1: drawn at every level of detail
            public bool shadows;
            public Matrix4x4 local;       // from the thing's own space
        }

        [Serializable]
        private class Model
        {
            public int kind;
            public int lodCount = 1;
            public int layer;
            public Bounds bounds;         // in the thing's own space
            public List<Part> parts = new List<Part>();
        }

        [Serializable]
        private struct Thing
        {
            public int model;
            public Matrix4x4 matrix;
        }

        // Filled for play and builds by ScatterSceneProcessor (the things themselves are gone by then); in the editor
        // what's drawn is read from the scene instead and not saved
        [SerializeField, HideInInspector] private List<Model> bakedModels = new List<Model>();
        [SerializeField, HideInInspector] private List<Thing> bakedThings = new List<Thing>();

        private List<Model> models = new List<Model>();
        private List<Thing> things = new List<Thing>();
        private readonly List<Renderer> hidden = new List<Renderer>();

        private const float ChunkSize = 32f;
        private const int MaxPerDraw = 1023;

        private sealed class ChunkModel
        {
            public int Model;
            public Vector3[] Positions;
            public Matrix4x4[][] Parts;   // [part][thing]
        }

        private sealed class Chunk
        {
            public Bounds Bounds;
            public readonly List<ChunkModel> Models = new List<ChunkModel>();
        }

        private sealed class Batch
        {
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
            public Bounds Bounds;
        }

        private readonly List<Chunk> chunks = new List<Chunk>();
        private Batch[] batches = new Batch[0];   // [part index]: what doesn't cast shadows, gathered from every square
        private int[] partStart = new int[0];     // each model's first part index
        private readonly Plane[] planes = new Plane[6];
        private float furthest;

        /// <summary>How many scattered things it draws from.</summary>
        public int Things => things.Count;
        /// <summary>How many it drew for the last camera.</summary>
        public int Drawn { get; private set; }
        /// <summary>How many draw calls that took.</summary>
        public int Draws { get; private set; }
        /// <summary>How long working out what to draw took for the last camera (ms, on the CPU).</summary>
        public float DrawTime { get; private set; }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += Draw;
            if (bakedThings.Count > 0)
            {
                models = bakedModels;
                things = bakedThings;
                BuildChunks();
            }
            else
            {
                Refresh();
            }
#if UNITY_EDITOR
            EditorHooks(true);
#endif
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
            ShowOriginals();
#if UNITY_EDITOR
            EditorHooks(false);
#endif
        }

        private void OnValidate()
        {
            foreach (Kind kind in kinds)
            {
                if (kind == null) continue;
                kind.detailDistance = Mathf.Min(kind.detailDistance, kind.drawDistance);
                kind.shadowDistance = Mathf.Min(kind.shadowDistance, kind.drawDistance);
            }
            Furthest();
        }

        // ================================================================ Reading what's scattered

        /// <summary>Reads everything under this again (after things are painted, rubbed out or recoloured).</summary>
        public void Refresh()
        {
            ShowOriginals();
            Read(models = new List<Model>(), things = new List<Thing>(), hidden);
            if (!Application.isPlaying && !inEditor) ShowOriginals();
            else foreach (Renderer r in hidden) r.forceRenderingOff = true;
            BuildChunks();
        }

        /// <summary>For play and builds: reads everything into what's saved with the scene, then takes the things'
        /// renderers off (and the things with nothing else on them, like colliders, away altogether).</summary>
        public void Bake()
        {
            ShowOriginals();
            bakedModels = new List<Model>();
            bakedThings = new List<Thing>();
            var drawn = new List<Renderer>();
            Read(bakedModels, bakedThings, drawn);
            models = bakedModels;
            things = bakedThings;

            // Switched off first (that always works), then taken away where Unity allows it
            foreach (Renderer r in drawn)
            {
                if (r != null) r.enabled = false;
            }
            foreach (Transform kind in transform)
            {
                foreach (Transform thing in kind)
                {
                    if (thing.TryGetComponent(out LODGroup group)) group.enabled = false;
                }
            }
            try
            {
                foreach (Renderer r in drawn)
                {
                    if (r == null) continue;
                    if (r.TryGetComponent(out MeshFilter filter)) DestroyImmediate(filter);
                    DestroyImmediate(r);
                }
                foreach (Transform kind in transform)
                {
                    for (int i = kind.childCount - 1; i >= 0; i--)
                    {
                        Transform thing = kind.GetChild(i);
                        if (thing.TryGetComponent(out LODGroup group)) DestroyImmediate(group);
                        RemoveEmpty(thing);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Scatter Renderer: couldn't take the scattered things' renderers away ({e.Message}); they're switched off instead.", this);
            }
            BuildChunks();
        }

        // Takes away objects left with nothing on them (no components, no children) once their renderers are gone
        private static bool RemoveEmpty(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) RemoveEmpty(t.GetChild(i));
            if (t.childCount > 0 || t.GetComponents<Component>().Length > 1) return false;
            DestroyImmediate(t.gameObject);
            return true;
        }

        // Every thing under each kind's object: its parts (by level of detail if it has a LOD Group), grouped into
        // models (the same prefab with the same materials), and where it is
        private void Read(List<Model> intoModels, List<Thing> intoThings, List<Renderer> drawn)
        {
            drawn.Clear();
            var known = new Dictionary<string, int>();
            var key = new System.Text.StringBuilder();
            var parts = new List<Part>();
            var partRenderers = new List<Renderer>();
            foreach (Transform kindObject in transform)
            {
                int kindIndex = KindIndex(kindObject.name);
                foreach (Transform thing in kindObject)
                {
                    if (!thing.gameObject.activeInHierarchy) continue;
                    parts.Clear();
                    partRenderers.Clear();
                    int lodCount = 1;
                    Matrix4x4 toThing = thing.worldToLocalMatrix;
                    if (thing.TryGetComponent(out LODGroup group) && group.enabled)
                    {
                        LOD[] lods = group.GetLODs();
                        lodCount = Mathf.Max(1, lods.Length);
                        for (int l = 0; l < lods.Length; l++)
                        {
                            foreach (Renderer r in lods[l].renderers) AddParts(r, l, toThing, parts, partRenderers);
                        }
                    }
                    foreach (MeshRenderer r in thing.GetComponentsInChildren<MeshRenderer>())
                    {
                        if (!partRenderers.Contains(r)) AddParts(r, -1, toThing, parts, partRenderers);
                    }
                    if (parts.Count == 0) continue;

                    key.Clear();
                    key.Append(kindIndex).Append('|').Append(lodCount).Append('|').Append(thing.gameObject.layer);
                    foreach (Part p in parts)
                    {
                        key.Append('|').Append(p.mesh.GetInstanceID()).Append(':').Append(p.submesh).Append(':')
                           .Append(p.material.GetInstanceID()).Append(':').Append(p.lod).Append(':').Append(p.shadows ? 1 : 0);
                        for (int m = 0; m < 16; m++) key.Append(':').Append(Mathf.RoundToInt(p.local[m] * 1000f));
                    }
                    string id = key.ToString();
                    if (!known.TryGetValue(id, out int model))
                    {
                        model = intoModels.Count;
                        known[id] = model;
                        var made = new Model { kind = kindIndex, lodCount = lodCount, layer = thing.gameObject.layer, parts = new List<Part>(parts) };
                        made.bounds = PartsBounds(made.parts);
                        intoModels.Add(made);
                    }
                    intoThings.Add(new Thing { model = model, matrix = thing.localToWorldMatrix });
                    drawn.AddRange(partRenderers);
                }
            }
        }

        // A renderer's meshes, if they can be drawn this way (instancing on its materials)
        private static void AddParts(Renderer renderer, int lod, Matrix4x4 toThing, List<Part> parts, List<Renderer> renderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderers.Contains(renderer)) return;
            if (!(renderer is MeshRenderer) || !renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) return;
            Material[] materials = renderer.sharedMaterials;
            Mesh mesh = filter.sharedMesh;
            foreach (Material material in materials)
            {
                if (material == null || !material.enableInstancing) return;   // (left for its own renderer to draw)
            }
            Matrix4x4 local = toThing * renderer.localToWorldMatrix;
            bool shadows = renderer.shadowCastingMode != ShadowCastingMode.Off;
            for (int s = 0; s < Mathf.Min(materials.Length, mesh.subMeshCount); s++)
            {
                parts.Add(new Part { mesh = mesh, submesh = s, material = materials[s], lod = lod, shadows = shadows, local = local });
            }
            renderers.Add(renderer);
        }

        private static Bounds PartsBounds(List<Part> parts)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (Part p in parts)
            {
                Bounds b = p.mesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 point = p.local.MultiplyPoint3x4(corner);
                    if (!any) bounds = new Bounds(point, Vector3.zero);
                    else bounds.Encapsulate(point);
                    any = true;
                }
            }
            return bounds;
        }

        private void ShowOriginals()
        {
            foreach (Renderer r in hidden)
            {
                if (r != null) r.forceRenderingOff = false;
            }
            hidden.Clear();
        }

        /// <summary>A kind's draw settings, by its name (made with sensible ones for its name the first time).</summary>
        public Kind KindSettings(string name) => kinds[KindIndex(name)];

        private int KindIndex(string name)
        {
            for (int i = 0; i < kinds.Count; i++)
            {
                if (kinds[i] != null && kinds[i].name == name) return i;
            }
            kinds.Add(DefaultKind(name));
            Furthest();
            return kinds.Count - 1;
        }

        /// <summary>How far a kind is drawn to start with, going by its name: plants near, trees far.</summary>
        public static Kind DefaultKind(string name)
        {
            string n = (name ?? string.Empty).ToLowerInvariant();
            if (n.Contains("flower") || n.Contains("grass") || n.Contains("plant") || n.Contains("weed"))
                return new Kind { name = name, drawDistance = 80f, detailDistance = 15f, shadowDistance = 0f };
            if (n.Contains("mushroom"))
                return new Kind { name = name, drawDistance = 60f, detailDistance = 12f, shadowDistance = 0f };
            if (n.Contains("bush") || n.Contains("shrub"))
                return new Kind { name = name, drawDistance = 180f, detailDistance = 25f, shadowDistance = 30f };
            if (n.Contains("stump") || n.Contains("log"))
                return new Kind { name = name, drawDistance = 150f, detailDistance = 20f, shadowDistance = 30f };
            if (n.Contains("rock") || n.Contains("stone"))
                return new Kind { name = name, drawDistance = 300f, detailDistance = 30f, shadowDistance = 50f };
            if (n.Contains("tree"))
                return new Kind { name = name, drawDistance = 1000f, detailDistance = 45f, shadowDistance = 80f };
            return new Kind { name = name, drawDistance = 250f, detailDistance = 30f, shadowDistance = 40f };
        }

        private void Furthest()
        {
            furthest = 0f;
            foreach (Kind kind in kinds)
            {
                if (kind != null) furthest = Mathf.Max(furthest, kind.drawDistance);
            }
        }

        // Sorts the things into 32 m squares, each thing's parts already placed in the world
        private void BuildChunks()
        {
            chunks.Clear();
            Furthest();
            partStart = new int[models.Count];
            int partCount = 0;
            for (int m = 0; m < models.Count; m++)
            {
                partStart[m] = partCount;
                partCount += models[m].parts.Count;
            }
            batches = new Batch[partCount];
            for (int i = 0; i < batches.Length; i++) batches[i] = new Batch();

            var byCell = new Dictionary<Vector2Int, Dictionary<int, List<Matrix4x4>>>();
            foreach (Thing thing in things)
            {
                if (thing.model < 0 || thing.model >= models.Count) continue;
                Vector3 at = thing.matrix.GetColumn(3);
                var cell = new Vector2Int(Mathf.FloorToInt(at.x / ChunkSize), Mathf.FloorToInt(at.z / ChunkSize));
                if (!byCell.TryGetValue(cell, out var inCell)) byCell[cell] = inCell = new Dictionary<int, List<Matrix4x4>>();
                if (!inCell.TryGetValue(thing.model, out var list)) inCell[thing.model] = list = new List<Matrix4x4>();
                list.Add(thing.matrix);
            }
            foreach (var pair in byCell)
            {
                var chunk = new Chunk();
                bool first = true;
                foreach (var entry in pair.Value)
                {
                    Model model = models[entry.Key];
                    List<Matrix4x4> roots = entry.Value;
                    var chunkModel = new ChunkModel
                    {
                        Model = entry.Key,
                        Positions = new Vector3[roots.Count],
                        Parts = new Matrix4x4[model.parts.Count][],
                    };
                    for (int p = 0; p < model.parts.Count; p++) chunkModel.Parts[p] = new Matrix4x4[roots.Count];
                    for (int i = 0; i < roots.Count; i++)
                    {
                        Matrix4x4 root = roots[i];
                        chunkModel.Positions[i] = root.GetColumn(3);
                        for (int p = 0; p < model.parts.Count; p++) chunkModel.Parts[p][i] = root * model.parts[p].local;
                        Bounds b = TransformBounds(model.bounds, root);
                        if (first) chunk.Bounds = b;
                        else chunk.Bounds.Encapsulate(b);
                        first = false;
                    }
                    chunk.Models.Add(chunkModel);
                }
                chunks.Add(chunk);
            }
        }

        private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            Vector3 centre = matrix.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            Vector3 x = matrix.MultiplyVector(new Vector3(e.x, 0f, 0f)), y = matrix.MultiplyVector(new Vector3(0f, e.y, 0f)),
                    z = matrix.MultiplyVector(new Vector3(0f, 0f, e.z));
            var extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                                      Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(centre, extents * 2f);
        }

        // ================================================================ Drawing

        private void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (!isActiveAndEnabled || camera == null || chunks.Count == 0) return;
            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection) return;
            if (!Application.isPlaying && !inEditor) return;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            Vector3 eye = camera.transform.position;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            float reach = Mathf.Min(furthest, camera.farClipPlane);
            foreach (Batch batch in batches)
            {
                batch.Matrices.Clear();
            }

            // No point casting shadows past where the pipeline draws them (a little over, for long shadows reaching in)
            var pipeline = GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            float shadowLimit = pipeline != null ? pipeline.shadowDistance + ChunkSize * 0.5f : float.MaxValue;
            int drawn = 0, draws = 0;
            foreach (Chunk chunk in chunks)
            {
                float near = Mathf.Sqrt(chunk.Bounds.SqrDistance(eye));
                if (near > reach || !GeometryUtility.TestPlanesAABB(planes, chunk.Bounds)) continue;
                Vector3 far = Farthest(chunk.Bounds, eye);
                float farSqr = (far - eye).sqrMagnitude;

                foreach (ChunkModel entry in chunk.Models)
                {
                    Model model = models[entry.Model];
                    Kind kind = model.kind < kinds.Count ? kinds[model.kind] : null;
                    float distance = kind != null ? kind.drawDistance : 250f;
                    if (near > distance) continue;
                    int lod = Lod(model.lodCount, near, kind != null ? kind.detailDistance : 30f);
                    bool shadows = kind != null && near < Mathf.Min(kind.shadowDistance, shadowLimit);
                    bool allIn = farSqr <= distance * distance;
                    float distanceSqr = distance * distance;
                    int first = partStart[entry.Model];
                    for (int p = 0; p < model.parts.Count; p++)
                    {
                        Part part = model.parts[p];
                        if (part.lod >= 0 && part.lod != lod) continue;
                        if (shadows && part.shadows)
                        {
                            // Shadow casters are drawn a square at a time, so each shadow cascade only takes the squares
                            // it reaches (all in one, every caster would be drawn into every cascade)
                            var casting = new RenderParams(part.material)
                            {
                                camera = camera,
                                worldBounds = chunk.Bounds,
                                layer = model.layer,
                                shadowCastingMode = ShadowCastingMode.On,
                                receiveShadows = true,
                            };
                            Matrix4x4[] all = entry.Parts[p];
                            for (int start = 0; start < all.Length; start += MaxPerDraw)
                            {
                                Graphics.RenderMeshInstanced(casting, part.mesh, part.submesh, all, Mathf.Min(MaxPerDraw, all.Length - start), start);
                                draws++;
                            }
                            continue;
                        }
                        Batch batch = batches[first + p];
                        if (batch.Matrices.Count == 0) batch.Bounds = chunk.Bounds;
                        else batch.Bounds.Encapsulate(chunk.Bounds);
                        Matrix4x4[] placed = entry.Parts[p];
                        if (allIn)
                        {
                            batch.Matrices.AddRange(placed);
                        }
                        else
                        {
                            for (int i = 0; i < placed.Length; i++)
                            {
                                if ((entry.Positions[i] - eye).sqrMagnitude <= distanceSqr) batch.Matrices.Add(placed[i]);
                            }
                        }
                    }
                    drawn += entry.Positions.Length;
                }
            }

            // Everything else, each part of each model in as few draws as it takes
            for (int m = 0; m < models.Count; m++)
            {
                Model model = models[m];
                for (int p = 0; p < model.parts.Count; p++)
                {
                    Batch batch = batches[partStart[m] + p];
                    int count = batch.Matrices.Count;
                    if (count == 0) continue;
                    Part part = model.parts[p];
                    var settings = new RenderParams(part.material)
                    {
                        camera = camera,
                        worldBounds = batch.Bounds,
                        layer = model.layer,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = true,
                    };
                    for (int start = 0; start < count; start += MaxPerDraw)
                    {
                        Graphics.RenderMeshInstanced(settings, part.mesh, part.submesh, batch.Matrices, Mathf.Min(MaxPerDraw, count - start), start);
                        draws++;
                    }
                    batch.Matrices.Clear();
                }
            }
            Drawn = drawn;
            DrawTime = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Draws = draws;
        }

        // Its most detailed look near, its simplest beyond three times as far, the one between in between
        private static int Lod(int count, float distance, float detail)
        {
            if (count <= 1 || distance < detail) return 0;
            if (count == 2 || distance >= detail * 3f) return count - 1;
            return 1;
        }

        private static Vector3 Farthest(Bounds b, Vector3 from)
        {
            return new Vector3(from.x < b.center.x ? b.max.x : b.min.x, from.y < b.center.y ? b.max.y : b.min.y,
                               from.z < b.center.z ? b.max.z : b.min.z);
        }

#if UNITY_EDITOR
        // ================================================================ Keeping up with Paint Land (editor)

        private bool changed;
        private double changedAt;

        private void EditorHooks(bool on)
        {
            UnityEditor.ObjectChangeEvents.changesPublished -= OnChanges;
            UnityEditor.Undo.undoRedoPerformed -= MarkChanged;
            UnityEditor.EditorApplication.update -= EditorUpdate;
            if (!on || Application.isPlaying) return;
            UnityEditor.ObjectChangeEvents.changesPublished += OnChanges;
            UnityEditor.Undo.undoRedoPerformed += MarkChanged;
            UnityEditor.EditorApplication.update += EditorUpdate;
        }

        private void MarkChanged()
        {
            changed = true;
            changedAt = UnityEditor.EditorApplication.timeSinceStartup;
        }

        // Anything made, removed, moved or recoloured under this: read again once the painting stops for a moment
        private void OnChanges(ref UnityEditor.ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                int id;
                switch (stream.GetEventType(i))
                {
                    case UnityEditor.ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        id = properties.instanceId;
                        break;
                    case UnityEditor.ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                        id = created.instanceId;
                        break;
                    case UnityEditor.ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy);
                        id = hierarchy.instanceId;
                        break;
                    case UnityEditor.ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure);
                        id = structure.instanceId;
                        break;
                    case UnityEditor.ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        if (Under(parent.previousParentInstanceId) || Under(parent.newParentInstanceId)) MarkChanged();
                        continue;
                    case UnityEditor.ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        id = destroyed.parentInstanceId;
                        break;
                    case UnityEditor.ObjectChangeKind.UpdatePrefabInstances:
                    case UnityEditor.ObjectChangeKind.ChangeScene:
                        MarkChanged();
                        continue;
                    default:
                        continue;
                }
                if (Under(id)) MarkChanged();
            }
        }

        private bool Under(int instanceId)
        {
            UnityEngine.Object found = UnityEditor.EditorUtility.InstanceIDToObject(instanceId);
            Transform t = found is GameObject go ? go.transform : found is Component c ? c.transform : null;
            return t != null && t != transform && t.IsChildOf(transform);
        }

        private void EditorUpdate()
        {
            if (!changed || UnityEditor.EditorApplication.timeSinceStartup - changedAt < 0.35) return;
            if (GUIUtility.hotControl != 0) return;   // (still painting)
            changed = false;
            if (this != null && isActiveAndEnabled) Refresh();
        }
#endif
    }
}
