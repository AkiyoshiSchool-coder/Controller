#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. When a level is built or played, each Scatter Renderer takes what's scattered into itself and the
    /// scattered things lose their renderers and LOD Groups; the ones with nothing else on them (flowers, mushrooms,
    /// bushes: no colliders) go altogether, so the level loads faster and has tens of thousands fewer objects. Only the
    /// copy being built or played is changed, never the scene saved in the project.
    ///
    /// Also Tools > Paint Land > Optimize Scatter: puts a Scatter Renderer on the open scene's Land Scatter (Paint
    /// Land does this by itself when scattering) and turns on GPU instancing on the scattered things' materials, which
    /// drawing them this way needs.
    /// </summary>
    internal sealed class ScatterSceneProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (ScatterRenderer renderer in root.GetComponentsInChildren<ScatterRenderer>(true))
                {
                    if (renderer.isActiveAndEnabled) renderer.Bake();
                }
            }
        }

        [MenuItem("Tools/Paint Land/Optimize Scatter", priority = 1403)]
        private static void Optimize()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Optimize Scatter", "Stop Play mode first.", "OK");
                return;
            }
            ScatterRenderer renderer = Ready(SceneManager.GetActiveScene());
            if (renderer == null)
            {
                EditorUtility.DisplayDialog("Optimize Scatter", "There's no Land Scatter in the open scene (scatter something with Paint Land first).", "OK");
                return;
            }
            Selection.activeObject = renderer;
            Debug.Log($"Scatter Renderer: drawing {renderer.Things:N0} scattered things instanced. Its draw distances are on the Land Scatter object.", renderer);
        }

        /// <summary>The Scatter Renderer on the scene's Land Scatter (added if missing), with instancing on its materials.
        /// Null if the scene has no Land Scatter.</summary>
        internal static ScatterRenderer Ready(Scene scene)
        {
            GameObject container = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == LandScatter.ContainerName) container = root;
            }
            if (container == null) return null;
            int changed = EnableInstancing(container);
            if (!container.TryGetComponent(out ScatterRenderer renderer))
            {
                renderer = Undo.AddComponent<ScatterRenderer>(container);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            else if (changed > 0)
            {
                renderer.Refresh();
            }
            return renderer;
        }

        /// <summary>Turns on GPU instancing on every material the things under the object use. Returns how many changed.</summary>
        internal static int EnableInstancing(GameObject under)
        {
            var seen = new HashSet<Material>();
            int changed = 0;
            foreach (Renderer renderer in under.GetComponentsInChildren<MeshRenderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || !seen.Add(material) || material.enableInstancing) continue;
                    if (!material.shader.isSupported || !AssetDatabase.Contains(material)) continue;
                    material.enableInstancing = true;
                    EditorUtility.SetDirty(material);
                    changed++;
                }
            }
            if (changed > 0) AssetDatabase.SaveAssets();
            return changed;
        }
    }
}
#endif
