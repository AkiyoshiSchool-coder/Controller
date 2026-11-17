#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Tools > Paint Land: brushes for changing the land in the Scene view, a tab each:
    ///   Shape:     raise, lower, smooth, flatten, ramp, roughen or restore the land; its colliders, colours and the trees
    ///              and plants standing on it follow (LandShape).
    ///   Ground:    paint the ground's colours, smoothly (kept by LandShape; the grass takes the colour on) or a whole
    ///              triangle at a time (LandPaint).
    ///   Textures:  paint up to 8 textures (rock, sand, mud...: kept in the Land material), soft or on whole triangles
    ///              (LandTextures).
    ///   Paths:     paint paths, any width, crisp edges; they can delete the things under them (LandPaint, LandScatter).
    ///   Water:     paint lakes, the land dug out to hold them and things in them deleted (WaterPaint, LandWater).
    ///   Grass:     paint where the Grass Field grows more or less (GrassPaint).
    ///   Scatter:   scatter the ticked kinds of things (trees, rocks, flowers...) in natural clumps (LandScatter).
    /// Shift (or the Erase tick) rubs out, Ctrl+click picks up a colour, texture or height, [ and ] change the brush size,
    /// Esc stops, Ctrl+Z undoes a stroke. The less used things are under More. Everything saves by itself and stays when
    /// the land is rebuilt.
    /// </summary>
    internal sealed class LandPainter : EditorWindow
    {
        private enum Mode { Shape, Ground, Textures, Paths, Water, Grass, Scatter }

        private const string Prefs = "PaintLand.Painter.";
        private const float MaxPathWidth = 40f, MaxTriangleBrush = 60f, MaxScatterBrush = 80f, MaxGrassBrush = 80f;
        private const float MaxTextureBrush = 120f, MaxShapeBrush = 400f, MaxGroundBrush = 300f;
        private static readonly int ControlHint = "LandPainter".GetHashCode();
        private static readonly Vector3 Lift = Vector3.up * 0.05f;

        private Mode mode;
        private float pathWidth = 3f;
        private float triangleBrush;
        private float scatterBrush = 10f;
        private float grassBrush = 12f;
        private GrassPaint.Brush grassKind;
        private float grassAmount = 1f;
        private int textureLayer = 1;               // the texture painted (1 to 8)
        private bool textureTriangles;              // on whole triangles (sharp edges), not the soft brush
        private float textureBrush = 8f;
        private float textureOpacity = 1f;
        private float textureSoftness = 0.5f;
        private float textureTriangleBrush;
        private bool groundMode = true;             // Ground tab: painting the ground's colours (smooth), not whole triangles
        private float groundBrush = 30f;
        private float groundStrength = 0.6f;
        private float groundSoftness = 0.6f;
        [System.NonSerialized] private bool pickingColour;   // the next click on the land picks its colour
        [System.NonSerialized] private LandWater selectedWater;
        private float waterBrush = 24f;
        private float waterSoftness = 0.4f;
        private bool waterShape = true;             // painting water shapes the land to hold it
        private float waterDepth = 3f;
        private bool waterClear = true;             // painting water deletes the plants, trees and rocks under it
        private float waterSpot = 1f;               // metres per spot of a new painted lake
        private Vector3 lastWaterDab;
        private LandShape.Tool shapeTool;
        private float shapeBrush = 40f;
        private float shapeStrength = 0.3f;
        private float shapeSoftness = 0.6f;
        private bool flattenAtStart = true;         // flatten to the height where each stroke starts
        private float flattenHeight;
        private float noiseSize = 24f;
        private bool moveThings = true;             // the trees, rocks and plants move with the ground
        private bool recolour = true;               // the ground's colours worked out again as it changes
        private bool scatterColliders = true;       // scattered things get their kind's collider
        private Color colour = new Color(0.62f, 0.42f, 0.27f);
        private bool eraser;
        private bool painting;
        [System.NonSerialized] private string problem;
        [System.NonSerialized] private int pickingFor = -1;   // the scatter kind a colour is being picked from the land for
        private bool showMore;
        private Vector2 scroll;

        // The brush in the Scene view
        private bool overLand;
        private LandPaint.Hit hit;
        private Vector2 lastMouse = new Vector2(float.NaN, float.NaN);
        private Matrix4x4 lastCamera;
        private bool stroking;
        private SceneView strokeView;       // the Scene view the stroke is being painted in
        private int strokeControl;
        private bool strokeErases;
        private Mode strokeMode;
        private Vector3 lastDab;            // in the world
        private Vector2 lastPathDab;        // on the land (x, z)
        private Vector3 lastScatterDab;     // in the world
        private Vector2 lastGrassDab;       // on the land (x, z)
        private bool clearingThings;        // this path stroke deletes the plants etc. under it
        private GrassPaint.Brush strokeGrass;
        [System.NonSerialized] private GrassField strokeField;
        private int strokeLayer;                    // the texture this stroke paints (0 = rubs out)
        private bool strokeGround;                  // this Ground stroke paints the ground's colours (smoothly)
        private Vector2 lastGroundDab;
        private bool strokeTriangles;
        private LandShape.Tool strokeShape;
        private float strokeTarget;                 // Flatten: the height
        private Vector2 shapeCentre;                // where the shape brush is (x, z on the land)
        private Vector2 rampFrom, rampTo;
        private double lastTick;
        private Vector2 lastTextureDab;
        private readonly List<(LandPaint.Piece piece, int triangle)> near = new List<(LandPaint.Piece piece, int triangle)>();
        private readonly List<Vector3> outline = new List<Vector3>();
        [System.NonSerialized] private GUIStyle statusStyle;

        [MenuItem("Tools/Paint Land/Open Paint Land", priority = 1402)]
        public static void Open()
        {
            var window = GetWindow<LandPainter>();
            window.titleContent = new GUIContent("Paint Land");
            window.minSize = new Vector2(260f, 380f);
            window.Show();
            window.StartPainting();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Paint Land");
            LoadPrefs();
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= Repaint;
            StopPainting();
            SavePrefs();
            LandPaint.SaveNow();
        }

        private void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) StopPainting();
        }

        private void StartPainting()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                problem = "Stop Play mode to paint the land.";
                return;
            }
            problem = LandPaint.Load();
            painting = problem == null;
            if (painting)
            {
                Tools.hidden = true;
                if (LandPaint.PathsExist) problem = LandPaint.LoadPaths();
                // (ground colours painted before they were kept apart from the land's own get moved apart now)
                if (problem == null && LandShape.GroundPainted()) LandShape.Prepare();
            }
            SceneView.RepaintAll();
            Repaint();
        }

        private void StopPainting()
        {
            if (stroking) EndStroke();
            if (painting) Tools.hidden = false;
            painting = false;
            overLand = false;
            SceneView.RepaintAll();
            Repaint();
        }

        // ================================================================ The window

        private static readonly GUIContent[] Tabs =
        {
            new GUIContent("Shape", "Raise, lower, smooth and flatten the land."),
            new GUIContent("Ground", "Paint the ground's colours: smoothly, or whole triangles."),
            new GUIContent("Textures", "Paint textures such as rock, sand and mud."),
            new GUIContent("Paths", "Paint paths."),
            new GUIContent("Water", "Paint lakes."),
            new GUIContent("Grass", "Paint where the grass grows."),
            new GUIContent("Scatter", "Scatter trees, rocks, flowers and other things."),
        };

        private void OnGUI()
        {
            LandSettings settings = LandBuilder.GetSettings();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawStartStop(settings);

            // The tabs
            EditorGUILayout.Space(6f);
            EditorGUI.BeginChangeCheck();
            mode = (Mode)GUILayout.SelectionGrid((int)mode, Tabs, 4, GUILayout.Height(46));
            if (EditorGUI.EndChangeCheck())
            {
                pickingColour = false;
                pickingFor = -1;
                SavePrefs();
                SceneView.RepaintAll();
            }
            EditorGUILayout.Space(4f);

            switch (mode)
            {
                case Mode.Shape: DrawShape(settings); break;
                case Mode.Ground: DrawGround(settings); break;
                case Mode.Textures: DrawTextures(settings); break;
                case Mode.Paths: DrawPaths(settings); break;
                case Mode.Water: DrawWater(); break;
                case Mode.Grass: DrawGrass(); break;
                default: DrawScatterTab(settings); break;
            }

            // How to use it (one line), then the less used things
            EditorGUILayout.Space(8f);
            Hint(Keys());
            EditorGUILayout.Space(2f);
            showMore = EditorGUILayout.Foldout(showMore, "More", true);
            if (showMore)
            {
                EditorGUI.indentLevel++;
                DrawMore(settings);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();
            if (AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        // The big Start / Stop button, and anything stopping painting
        private void DrawStartStop(LandSettings settings)
        {
            Color background = GUI.backgroundColor;
            if (painting) GUI.backgroundColor = new Color(0.5f, 1f, 0.55f);
            var startStop = painting
                ? new GUIContent("Painting  (click to stop)", "Drag on the land in the Scene view. Click here or press Esc to stop.")
                : new GUIContent("Start Painting", "Paint on the land in the Scene view.");
            if (GUILayout.Button(startStop, GUILayout.Height(32)))
            {
                if (painting) StopPainting();
                else StartPainting();
            }
            GUI.backgroundColor = background;
            string warning = !string.IsNullOrEmpty(problem) ? problem : painting ? LandPaint.Skipped : null;
            if (string.IsNullOrEmpty(warning)) return;
            EditorGUILayout.HelpBox(warning, MessageType.Warning);
            if (warning.Contains("rebuil", System.StringComparison.OrdinalIgnoreCase) && GUILayout.Button("Rebuild Land"))
            {
                LandBuilder.Build(settings);
                StartPainting();
                GUIUtility.ExitGUI();
            }
        }

        // The keys for the tab, in one line
        private string Keys()
        {
            switch (mode)
            {
                case Mode.Shape:
                    return shapeTool == LandShape.Tool.Ramp
                        ? "Drag from one end of the ramp to the other  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo"
                        : "Hold to shape  ·  Shift " + (shapeTool == LandShape.Tool.Raise ? "lowers" : shapeTool == LandShape.Tool.Lower ? "raises" : "smooths") +
                          "  ·  Ctrl+click picks a flatten height  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo";
                case Mode.Water:
                    return "Drag to paint water (its surface is where you start)  ·  Shift rubs out  ·  Ctrl+click sets the chosen water's level  ·  [ ] size  ·  Ctrl+Z undo";
                case Mode.Grass:
                    return "Drag to paint  ·  Shift does the opposite  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo";
                case Mode.Scatter:
                    return "Drag to scatter the ticked things  ·  Shift takes them away  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo";
                case Mode.Textures:
                    return "Drag to paint  ·  Shift rubs out  ·  Ctrl+click picks the texture  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo";
                default:
                    return "Drag to paint  ·  Shift rubs out  ·  Ctrl+click picks a colour  ·  [ ] size  ·  Esc stop  ·  Ctrl+Z undo";
            }
        }

        // ---------------------------------------------------------------- Little helpers for the layout

        private static GUIStyle hintStyle, headerStyle;
        private static readonly Dictionary<string, bool> open = new Dictionary<string, bool>();

        private static void Header(string text, string tooltip = null)
        {
            headerStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(new GUIContent(text, tooltip), headerStyle);
        }

        private static void Hint(string text)
        {
            hintStyle ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            Color before = GUI.contentColor;
            GUI.contentColor = new Color(before.r, before.g, before.b, 0.7f);
            EditorGUILayout.LabelField(text, hintStyle);
            GUI.contentColor = before;
        }

        // A section that can be folded away (remembered between sessions)
        private static bool Section(string key, string title, bool startOpen = true)
        {
            if (!open.TryGetValue(key, out bool isOpen)) isOpen = EditorPrefs.GetBool(Prefs + "Open." + key, startOpen);
            EditorGUILayout.Space(6f);
            bool now = EditorGUILayout.Foldout(isOpen, title, true, FoldoutBold);
            if (now != isOpen) EditorPrefs.SetBool(Prefs + "Open." + key, now);
            open[key] = now;
            return now;
        }

        private static GUIStyle foldoutBold;
        private static GUIStyle FoldoutBold => foldoutBold ??= new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };

        private static float Slider(string label, string tooltip, float value, float min, float max) =>
            EditorGUILayout.Slider(new GUIContent(label, tooltip), value, min, max);

        // "Erase (or hold Shift)" for the tabs that rub out
        private void EraseToggle(string tooltip)
        {
            eraser = EditorGUILayout.ToggleLeft(new GUIContent("Erase  (or hold Shift)", tooltip), eraser);
        }

        // Saves the window's settings and redraws the brush, if anything above changed
        private void Changed()
        {
            SavePrefs();
            SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- More (the less used things, for each tab)

        private void DrawMore(LandSettings settings)
        {
            switch (mode)
            {
                case Mode.Shape:
                    if (GUILayout.Button(new GUIContent("Restore The Whole Land...", "Puts the whole land back to the shape Rebuild Land makes. Ctrl+Z undoes it.")) &&
                        EnsureLoaded() &&
                        EditorUtility.DisplayDialog("Paint Land", "Put the whole land back to the shape Rebuild Land makes? (Ctrl+Z undoes it.)", "Restore", "Cancel"))
                    {
                        string trouble = LandShape.Prepare();
                        if (trouble != null) problem = trouble;
                        else if (!LandShape.RestoreAll(recolour, moveThings)) ShowNotification(new GUIContent("The land hasn't been reshaped"));
                        SceneView.RepaintAll();
                    }
                    break;

                case Mode.Ground:
                    if (GUILayout.Button(new GUIContent("Clear Painted Ground Colours...", "Back to the colours Rebuild Land gives. Ctrl+Z undoes it.")) &&
                        EnsureLoaded() &&
                        EditorUtility.DisplayDialog("Paint Land", "Take every ground colour painted with the smooth brush off? (Ctrl+Z undoes it.)", "Clear", "Cancel"))
                    {
                        string trouble = LandShape.Prepare();
                        if (trouble != null) problem = trouble;
                        else if (!LandShape.ClearGroundPaint()) ShowNotification(new GUIContent("No ground colours painted yet"));
                        SceneView.RepaintAll();
                    }
                    if (GUILayout.Button(new GUIContent("Clear Triangle Colours...", "Takes the colour off every triangle. Ctrl+Z undoes it.")) &&
                        EnsureLoaded() &&
                        EditorUtility.DisplayDialog("Paint Land", "Take the colour off every triangle? (Ctrl+Z undoes it.)", "Clear", "Cancel"))
                    {
                        LandPaint.ClearTriangles();
                    }
                    break;

                case Mode.Textures:
                    DrawTexturesMore(settings);
                    break;

                case Mode.Paths:
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(LandBuilder.MaterialPath);
                    if (material != null && material.HasProperty("_PathEdge"))
                    {
                        EditorGUI.BeginChangeCheck();
                        float edge = Slider("Path Edges", "0 = crisp, 1 = soft.", material.GetFloat("_PathEdge"), 0f, 1f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Path Edges");
                            material.SetFloat("_PathEdge", edge);
                            EditorUtility.SetDirty(material);
                        }
                    }
                    EditorGUI.BeginChangeCheck();
                    float detail = EditorGUILayout.DelayedFloatField(new GUIContent("Path Detail (m)",
                        "How fine paths are (metres per pixel, 0.25 to 4). Smaller is sharper but bigger. Changing it resizes the paths already painted."),
                        settings.pathDetail);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(settings, "Path Detail");
                        settings.pathDetail = Mathf.Clamp(detail, 0.25f, 4f);
                        EditorUtility.SetDirty(settings);
                        LandPaint.ResizePaths();
                        if (painting && LandPaint.PathsExist) problem = LandPaint.LoadPaths();
                    }
                    if (GUILayout.Button(new GUIContent("Clear All Paths...", "Takes every path off the land. Ctrl+Z undoes it.")) &&
                        LandPaint.PathsExist && EnsureLoaded() &&
                        EditorUtility.DisplayDialog("Paint Land", "Take every path off the land? (Ctrl+Z undoes it.)", "Clear Paths", "Cancel"))
                    {
                        LandPaint.ClearPaths();
                    }
                    break;
                }

                case Mode.Water:
                    if (GUILayout.Button(new GUIContent("Water Over The Whole Land", "A sea: water over all of the land, a little above its lowest ground. Change its level after.")))
                    {
                        AddSea();
                    }
                    if (!WaterSetup.DepthTextureOn() && GUILayout.Button(new GUIContent("Turn On Depth Texture",
                            "The URP setting the water needs to tell how deep it is (for its shallow colour and the foam at the shore).")))
                    {
                        WaterSetup.TurnOnDepthTexture();
                    }
                    break;

                case Mode.Grass:
                {
                    GrassField field = GrassPaint.Find();
                    if (field == null) break;
                    if (GUILayout.Button(new GUIContent("Select Grass Field", "Shows the Grass Field object (its settings are in the Inspector too).")))
                    {
                        Selection.activeObject = field.gameObject;
                    }
                    if (GUILayout.Button(new GUIContent("Remove Grass Objects...",
                            "Deletes grass tufts placed as objects (scattered before): the Grass Field draws grass much faster. Ctrl+Z undoes it.")))
                    {
                        List<GameObject> objects = GrassPaint.GrassObjects();
                        if (objects.Count == 0)
                        {
                            ShowNotification(new GUIContent("No grass objects in the scene"));
                        }
                        else if (EditorUtility.DisplayDialog("Remove Grass Objects",
                                     $"Delete the {objects.Count:N0} grass tufts placed as objects? The Grass Field draws the grass instead. Ctrl+Z undoes it.",
                                     "Delete", "Cancel"))
                        {
                            GrassPaint.RemoveGrassObjects(objects);
                        }
                    }
                    break;
                }
            }
            if (GUILayout.Button(new GUIContent("Land Settings", "The land's shape and colours (Rebuild Land), and the colour palette."))) LandBuilder.SelectSettings();
        }

        // ---------------------------------------------------------------- Shape

        private static readonly GUIContent[] ShapeTools =
        {
            new GUIContent("Raise", "Hold to raise the land."),
            new GUIContent("Lower", "Hold to lower the land."),
            new GUIContent("Smooth", "Rounds off bumps and edges."),
            new GUIContent("Flatten", "Levels the land to one height."),
            new GUIContent("Ramp", "A straight slope from where you start dragging to where you let go."),
            new GUIContent("Noise", "Roughens the land with bumps."),
            new GUIContent("Restore", "Back to the shape Rebuild Land makes."),
        };

        private static readonly string[] ShapeToolHelp =
        {
            "Hold the mouse down to raise the land.",
            "Hold the mouse down to lower the land.",
            "Rounds off bumps and sharp edges.",
            "Levels the land to one height.",
            "Drag from one end to the other: a straight slope between them.",
            "Roughens the land with bumps.",
            "Puts the land back to the shape Rebuild Land makes.",
        };

        private void DrawShape(LandSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            shapeTool = (LandShape.Tool)GUILayout.SelectionGrid((int)shapeTool, ShapeTools, 4, GUILayout.Height(44));
            Hint(ShapeToolHelp[(int)shapeTool]);

            Header("Brush");
            shapeBrush = Slider(shapeTool == LandShape.Tool.Ramp ? "Ramp Width (m)" : "Size (m)", "How wide the brush is.", shapeBrush, 2f, MaxShapeBrush);
            shapeStrength = Slider(shapeTool == LandShape.Tool.Ramp ? "Strength" : "Speed",
                shapeTool == LandShape.Tool.Ramp ? "How fully the ramp is made (1 = exactly)." : "How fast it changes the land.", shapeStrength, 0.01f, 1f);
            shapeSoftness = Slider("Soft Edge", "0 = hard edge, 1 = soft all the way in.", shapeSoftness, 0f, 1f);
            if (shapeTool == LandShape.Tool.Flatten)
            {
                flattenAtStart = EditorGUILayout.ToggleLeft(new GUIContent("Flatten to where each stroke starts", "Off: to the height below (Ctrl+click the land to pick it)."),
                                                            flattenAtStart);
                if (!flattenAtStart) flattenHeight = EditorGUILayout.FloatField(new GUIContent("Height (m)", "Ctrl+click the land to pick it."), flattenHeight);
            }
            if (shapeTool == LandShape.Tool.Noise) noiseSize = Slider("Bump Size (m)", "How wide the bumps are.", noiseSize, 4f, 200f);

            if (Section("ShapeOptions", "Options"))
            {
                moveThings = EditorGUILayout.ToggleLeft(new GUIContent("Move trees, rocks and plants with the ground",
                    "Things standing where the land changes go up or down with it."), moveThings);
                using (new EditorGUI.DisabledScope(!settings.paintColours))
                {
                    recolour = EditorGUILayout.ToggleLeft(new GUIContent("Recolour the ground to fit",
                        "Soil on new slopes, rock up high, sand down low, as Rebuild Land colours it."), recolour);
                }
            }
            if (EditorGUI.EndChangeCheck()) Changed();

            float spacing = LandShape.Spacing;
            if (spacing <= 0f && painting && LandShape.Prepare() == null) spacing = LandShape.Spacing;
            if (spacing > 0f) Hint($"The land bends at its triangles' corners, {spacing:0.#} m apart.");
        }

        // ---------------------------------------------------------------- Ground

        private void DrawGround(LandSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            groundMode = GUILayout.Toolbar(groundMode ? 0 : 1, new[]
            {
                new GUIContent("Smooth", "A soft brush: the colour blends smoothly across the ground."),
                new GUIContent("Whole Triangles", "Each triangle one flat colour (the low-poly look)."),
            }, GUILayout.Height(24)) == 0;

            DrawColourPicker(settings);

            Header("Brush");
            if (groundMode)
            {
                groundBrush = Slider("Size (m)", "How wide the brush is.", groundBrush, 2f, MaxGroundBrush);
                groundStrength = Slider("Strength", "How much of the colour each stroke paints.", groundStrength, 0.05f, 1f);
                groundSoftness = Slider("Soft Edge", "0 = hard edge, 1 = soft all the way in.", groundSoftness, 0f, 1f);
            }
            else
            {
                triangleBrush = Slider("Size (m)", "0 colours one triangle at a time.", triangleBrush, 0f, MaxTriangleBrush);
            }
            EraseToggle(groundMode ? "Back to the land's own colours." : "Take the colour off triangles.");
            if (EditorGUI.EndChangeCheck()) Changed();
            if (groundMode) Hint("Grass keeps growing here and takes on the colour.");
        }

        // The colour to paint with: the field, Pick From Land (the land's real colour, not the lit colour on screen) and the palette
        private void DrawColourPicker(LandSettings settings)
        {
            Header("Colour");
            using (new EditorGUILayout.HorizontalScope())
            {
                colour = EditorGUILayout.ColorField(GUIContent.none, colour, false, false, false, GUILayout.Height(20f));
                colour.a = 1f;
                Color pickBackground = GUI.backgroundColor;
                if (pickingColour) GUI.backgroundColor = new Color(0.5f, 1f, 0.55f);
                var pick = new GUIContent(pickingColour ? " Click the land" : " Pick From Land", EditorGUIUtility.IconContent("EyeDropper.Large").image,
                                          "Click the land to take its real colour (Ctrl+click it while painting does the same).");
                if (GUILayout.Button(pick, GUILayout.Width(124f), GUILayout.Height(20f)))
                {
                    if (pickingColour)
                    {
                        pickingColour = false;
                    }
                    else
                    {
                        if (!painting) StartPainting();
                        pickingColour = painting;
                        pickingFor = -1;
                        if (painting) ShowNotification(new GUIContent("Click the land in the Scene view"));
                    }
                    SceneView.RepaintAll();
                }
                GUI.backgroundColor = pickBackground;
            }
            DrawPalette(settings);
        }

        // ---------------------------------------------------------------- Paths

        private void DrawPaths(LandSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            DrawColourPicker(settings);
            Header("Brush");
            pathWidth = Slider("Width (m)", "How wide the path is.", pathWidth, 0.5f, MaxPathWidth);
            EraseToggle("Rub paths out.");
            if (EditorGUI.EndChangeCheck()) Changed();

            if (Section("PathClearing", "Clear Under New Paths", false)) DrawPathClearing(settings);
        }

        // What new paths clear: the Grass Field's grass, and the ticked kinds of things scattered
        private void DrawPathClearing(LandSettings settings)
        {
            LandScatter.SetUpKinds(settings);
            GrassField field = GrassPaint.Find();
            if (field != null)
            {
                var serialized = new SerializedObject(field);
                serialized.Update();
                SerializedProperty grass = serialized.FindProperty("pathsClearGrass");
                if (grass != null)
                {
                    EditorGUI.BeginChangeCheck();
                    bool on = EditorGUILayout.ToggleLeft(new GUIContent("Grass", "No grass grows on paths."), grass.boolValue);
                    if (EditorGUI.EndChangeCheck()) grass.boolValue = on;
                }
                if (serialized.ApplyModifiedProperties()) SceneView.RepaintAll();
            }
            foreach (LandScatterGroup kind in settings.scatter)
            {
                if (kind == null) continue;
                EditorGUI.BeginChangeCheck();
                bool on = EditorGUILayout.ToggleLeft(new GUIContent(kind.name, $"Painting a path deletes the {kind.name} under it (Ctrl+Z brings them back)."), kind.clearOnPaths);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(settings, "Clear Under Paths");
                    kind.clearOnPaths = on;
                    EditorUtility.SetDirty(settings);
                    SceneView.RepaintAll();
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All", EditorStyles.miniButtonLeft)) SetClearing(settings, true);
                if (GUILayout.Button("None", EditorStyles.miniButtonRight)) SetClearing(settings, false);
            }
        }

        private static void SetClearing(LandSettings settings, bool on)
        {
            Undo.RecordObject(settings, "Clear Under Paths");
            foreach (LandScatterGroup kind in settings.scatter)
            {
                if (kind != null) kind.clearOnPaths = on;
            }
            EditorUtility.SetDirty(settings);
            SceneView.RepaintAll();
        }

        private static string ClearedNames()
        {
            var names = new List<string>();
            foreach (LandScatterGroup kind in LandBuilder.GetSettings().scatter)
            {
                if (kind != null && kind.clearOnPaths) names.Add(kind.name);
            }
            if (names.Count == 0) return string.Empty;
            return ", clearing " + (names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.GetRange(0, 3)) + $" and {names.Count - 3} more");
        }

        // ---------------------------------------------------------------- Textures

        private void DrawTextures(LandSettings settings)
        {
            Material material = LandTextures.Material;
            if (!LandTextures.Supported(material))
            {
                EditorGUILayout.HelpBox(material == null
                    ? "Land.mat is missing. Rebuild the land (Tools > Paint Land > Rebuild Land) first."
                    : "The Land material's shader is out of date (Assets/Paint Land/Shaders/LandVertexColours.shader).", MessageType.Warning);
                return;
            }
            LandTextures.SetUp(settings);

            // The 8 textures: click one to paint with it, drop a texture or terrain layer on one to change it
            Header("Texture", "Click one to paint with it. Drop a texture or terrain layer on one to change it; right-click to empty it.");
            const float tile = 58f, gap = 4f;
            float width = Mathf.Max(position.width - 24f, tile);
            int perRow = Mathf.Clamp(Mathf.FloorToInt((width + gap) / (tile + gap)), 1, LandTextures.Count);
            int rows = (LandTextures.Count + perRow - 1) / perRow;
            Rect area = GUILayoutUtility.GetRect(width, rows * (tile + 16f + gap));
            Event e = Event.current;
            for (int k = 0; k < LandTextures.Count; k++)
            {
                int layer = k + 1;
                var r = new Rect(area.x + (k % perRow) * (tile + gap), area.y + (k / perRow) * (tile + 16f + gap), tile, tile);
                Texture texture = LandTextures.LayerTexture(material, layer);
                EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.25f));
                if (texture != null)
                {
                    GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), texture, ScaleMode.ScaleAndCrop);
                    Color tint = material.GetColor(LandTextures.Property(layer, "Tint"));
                    if (tint != Color.white) EditorGUI.DrawRect(new Rect(r.x + 2f, r.yMax - 8f, r.width - 4f, 6f), tint);
                }
                else
                {
                    GUI.Label(r, "drop a\ntexture", CentredMini);
                }
                if (layer == textureLayer) Outline(r, new Color(0.5f, 1f, 0.55f), 3f);
                else Outline(r, new Color(0f, 0f, 0f, 0.5f), 1f);
                GUI.Label(new Rect(r.x - 2f, r.yMax, r.width + 4f, 16f), LandTextures.Name(settings, material, layer), CentredMini);

                if (!r.Contains(e.mousePosition)) continue;
                if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
                {
                    Object dropped = null;
                    foreach (Object o in DragAndDrop.objectReferences)
                    {
                        if (o is Texture2D || o is TerrainLayer) dropped = o;
                    }
                    if (dropped == null) continue;
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        LandTextures.SetLayer(settings, material, layer, dropped);
                        textureLayer = layer;
                        SavePrefs();
                        SceneView.RepaintAll();
                    }
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 0)
                {
                    textureLayer = layer;
                    SavePrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 1)
                {
                    int which = layer;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Empty this slot"), false, () =>
                    {
                        Undo.RecordObjects(new Object[] { material, settings }, "Empty Land Texture");
                        material.SetTexture(LandTextures.Property(which), null);
                        LandTextures.SetName(settings, which, string.Empty);
                        EditorUtility.SetDirty(material);
                        EditorUtility.SetDirty(settings);
                        Repaint();
                    });
                    menu.ShowAsContext();
                    e.Use();
                }
            }

            // The chosen texture's settings (kept in the Land material)
            textureLayer = Mathf.Clamp(textureLayer, 1, LandTextures.Count);
            if (LandTextures.LayerTexture(material, textureLayer) == null)
            {
                Hint("This slot is empty: drop a texture or terrain layer on it.");
            }
            if (Section("TextureSettings", $"{LandTextures.Name(settings, material, textureLayer)}: Settings", false))
            {
                int layer = textureLayer;
                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.TextField("Name", LandTextures.Name(settings, material, layer));
                var texture = (Texture)EditorGUILayout.ObjectField(new GUIContent("Texture", "Any texture, or drop a terrain layer on the slot above."),
                                                                   LandTextures.LayerTexture(material, layer), typeof(Texture2D), false);
                float size = Slider("Size (m)", "How big the texture is on the land.", material.GetFloat(LandTextures.Property(layer, "Size")), 0.5f, 50f);
                Color tint = EditorGUILayout.ColorField(new GUIContent("Tint", "Multiplies its colours."), material.GetColor(LandTextures.Property(layer, "Tint")), true, false, false);
                float land = Slider("Land Colour", "0 = the texture's own colours; 1 = the land's colours with the texture's detail (fits the low-poly look).",
                                    material.GetFloat(LandTextures.Property(layer, "Land")), 0f, 1f);
                float grass = Slider("Grass On It", "How much grass grows on it (0 = none).", material.GetFloat(LandTextures.Property(layer, "Grass")), 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { material, settings }, "Change Land Texture");
                    LandTextures.SetName(settings, layer, name);
                    float grassBefore = material.GetFloat(LandTextures.Property(layer, "Grass"));
                    material.SetTexture(LandTextures.Property(layer), texture);
                    material.SetFloat(LandTextures.Property(layer, "Size"), size);
                    material.SetColor(LandTextures.Property(layer, "Tint"), tint);
                    material.SetFloat(LandTextures.Property(layer, "Land"), land);
                    material.SetFloat(LandTextures.Property(layer, "Grass"), grass);
                    EditorUtility.SetDirty(material);
                    EditorUtility.SetDirty(settings);
                    if (!Mathf.Approximately(grassBefore, grass)) GrassField.LandChanged();
                    SceneView.RepaintAll();
                }
            }

            // The brush
            Header("Brush");
            EditorGUI.BeginChangeCheck();
            textureTriangles = GUILayout.Toolbar(textureTriangles ? 1 : 0, new[]
            {
                new GUIContent("Soft", "Paints anywhere, blended in at the edge."),
                new GUIContent("Whole Triangles", "Paints whole triangles, with sharp edges along them."),
            }, GUILayout.Height(22)) == 1;
            if (textureTriangles)
            {
                textureTriangleBrush = Slider("Size (m)", "0 paints one triangle at a time.", textureTriangleBrush, 0f, MaxTriangleBrush);
            }
            else
            {
                textureBrush = Slider("Size (m)", "How wide the brush is.", textureBrush, 1f, MaxTextureBrush);
                textureOpacity = Slider("Strength", "How much of the texture a stroke paints (1 = only it).", textureOpacity, 0.05f, 1f);
                textureSoftness = Slider("Soft Edge", "0 = hard edge, 1 = soft all the way in.", textureSoftness, 0f, 1f);
            }
            EraseToggle("Rub textures out.");
            if (EditorGUI.EndChangeCheck()) Changed();
        }

        private void DrawTexturesMore(LandSettings settings)
        {
            Material material = LandTextures.Material;
            if (LandTextures.Supported(material) && material.HasProperty("_TextureEdge"))
            {
                EditorGUI.BeginChangeCheck();
                float edge = Slider("Texture Edges", "Soft-brushed textures: 0 = blended, 1 = crisp edges.", material.GetFloat("_TextureEdge"), 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(material, "Texture Edges");
                    material.SetFloat("_TextureEdge", edge);
                    EditorUtility.SetDirty(material);
                }
            }
            EditorGUI.BeginChangeCheck();
            float detail = EditorGUILayout.DelayedFloatField(new GUIContent("Texture Detail (m)",
                "How fine the soft brush is (metres per pixel, 0.5 to 4). Changing it resizes what's painted."), settings.textureDetail);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(settings, "Texture Detail");
                settings.textureDetail = Mathf.Clamp(detail, 0.5f, 4f);
                EditorUtility.SetDirty(settings);
                LandTextures.Resize();
            }
            if (GUILayout.Button(new GUIContent("Clear Soft-Painted Textures...", "Ctrl+Z undoes it.")) &&
                LandTextures.Exist && EnsureLoaded() &&
                EditorUtility.DisplayDialog("Paint Land", "Take every soft-painted texture off the land? (Ctrl+Z undoes it.)", "Clear", "Cancel"))
            {
                LandTextures.ClearSoft();
            }
            if (GUILayout.Button(new GUIContent("Clear Triangle Textures...", "Ctrl+Z undoes it.")) &&
                EnsureLoaded() &&
                EditorUtility.DisplayDialog("Paint Land", "Take the texture off every triangle? (Ctrl+Z undoes it.)", "Clear", "Cancel"))
            {
                LandPaint.ClearTriangleTextures();
            }
            if (GUILayout.Button(new GUIContent("Select Land Material", "The textures' settings are in the Land material too.")))
            {
                Selection.activeObject = LandTextures.Material;
            }
        }

        private static GUIStyle centredMini;
        private static GUIStyle CentredMini => centredMini ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };

        // ---------------------------------------------------------------- Water

        private void DrawWater()
        {
            List<LandWater> waters = WaterSetup.All();
            if (selectedWater == null || !waters.Contains(selectedWater)) selectedWater = waters.Count > 0 ? waters[0] : null;
            if (!WaterSetup.DepthTextureOn())
            {
                EditorGUILayout.HelpBox("Depth Texture is off in your URP settings, so the water can't show its shallow colour or shore foam.", MessageType.Warning);
                if (GUILayout.Button("Turn It On")) WaterSetup.TurnOnDepthTexture();
            }

            // The brush
            Header("Brush");
            EditorGUI.BeginChangeCheck();
            waterBrush = Slider("Size (m)", "How wide the brush is.", waterBrush, 2f, 200f);
            waterSoftness = Slider("Soft Edge", "0 = hard edge, 1 = soft (a wobblier shore).", waterSoftness, 0f, 1f);
            EraseToggle("Rub water out.");
            if (Section("WaterOptions", "When Painting Water"))
            {
                waterShape = EditorGUILayout.ToggleLeft(new GUIContent("Dig the lake bed",
                    "Lowers the land under the water, and makes a flat shore just under the water round it, so its edge is the smooth one you paint."), waterShape);
                if (waterShape) waterDepth = Slider("Depth (m)", "How deep it digs in the middle.", waterDepth, 0.5f, 30f);
                waterClear = EditorGUILayout.ToggleLeft(new GUIContent("Delete trees, rocks and plants in the water",
                    "Things standing under the water are deleted (Ctrl+Z brings them back)."), waterClear);
                waterSpot = Slider("Edge Detail (m)", "How fine a new lake's edge is. Smaller is smoother.", waterSpot, 0.25f, 4f);
            }
            if (EditorGUI.EndChangeCheck()) Changed();

            // The water in the scene
            Header("Water In The Scene");
            if (waters.Count == 0)
            {
                Hint("None yet: drag on the land to paint a lake.");
                return;
            }
            foreach (LandWater water in waters)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool chosen = water == selectedWater;
                    if (GUILayout.Toggle(chosen, $"{water.name}  ·  level {water.Level:0.#} m", "Button") && !chosen)
                    {
                        selectedWater = water;
                        SceneView.RepaintAll();
                    }
                    if (GUILayout.Button(new GUIContent("x", "Delete this water (Ctrl+Z brings it back)."), GUILayout.Width(22f)))
                    {
                        if (selectedWater == water) selectedWater = null;
                        Undo.DestroyObjectImmediate(water.gameObject);
                        SceneView.RepaintAll();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            // The chosen water
            LandWater selected = selectedWater;
            if (selected != null)
            {
                bool hasLand = WaterSetup.LandArea(out Bounds land);
                float low = hasLand ? land.min.y - 10f : selected.Level - 100f, high = hasLand ? land.max.y : selected.Level + 100f;
                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.TextField("Name", selected.name);
                float level = Slider("Level (m)", "How high the water comes (Ctrl+click the land to set it).", selected.Level,
                                     Mathf.Min(low, selected.Level), Mathf.Max(high, selected.Level));
                Vector2 size = selected.size;
                if (!selected.Painted)
                {
                    size = EditorGUILayout.Vector2Field(new GUIContent("Size (m)", "How far it reaches. It shows only where the land is lower than its level."), selected.size);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { selected.gameObject, selected.transform, selected }, "Change Water");
                    selected.name = name;
                    Vector3 p = selected.transform.position;
                    selected.transform.position = new Vector3(p.x, level, p.z);
                    selected.size = new Vector2(Mathf.Max(size.x, 1f), Mathf.Max(size.y, 1f));
                    selected.Refresh();
                    EditorUtility.SetDirty(selected);
                    SceneView.RepaintAll();
                }

                // A lake painted before (with bits cut off, or a rough edge): tidied up
                bool tidy;
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    tidy = selected.Painted && GUILayout.Button(new GUIContent("Smooth The Shore",
                        "Rubs out stray patches of water (not joined to anything you painted with the brush), smooths the edge, and (with " +
                        "Dig the lake bed on) shapes the land to it: the bed under it and a flat shore just under the water round it. " +
                        "Press again to smooth more. Ctrl+Z undoes it."));
                }
                if (tidy)
                {
                    string result = WaterPaint.TidyLake(selected, waterShape, waterDepth, recolour, moveThings);
                    if (result == null) ShowNotification(new GUIContent("Shore smoothed"));
                    else if (result.Length == 0) ShowNotification(new GUIContent("Nothing to smooth"));
                    else problem = result;
                    SceneView.RepaintAll();
                }
            }
            if (GUILayout.Button(new GUIContent("Delete Trees, Rocks And Plants In The Water",
                    "Deletes everything from the nature pack (and anything scattered) standing under any water. Ctrl+Z brings them back.")))
            {
                int deleted = WaterSetup.ClearUnderWater();
                ShowNotification(new GUIContent(deleted > 0 ? $"Deleted {deleted:N0} things in the water" : "Nothing in the water"));
            }

            // How all the water looks (the Water material)
            Material material = WaterSetup.GetMaterial();
            if (material == null || !Section("WaterLook", "Look", false)) return;
            EditorGUI.BeginChangeCheck();
            Color shallow = EditorGUILayout.ColorField(new GUIContent("Shallow Colour", "Its alpha is how see-through it is."), material.GetColor("_ShallowColor"), true, true, false);
            Color deep = EditorGUILayout.ColorField(new GUIContent("Deep Colour", "Its alpha is how see-through it is."), material.GetColor("_DeepColor"), true, true, false);
            float depth = Slider("Deep From (m)", "How deep it is before it's all the deep colour.", material.GetFloat("_Depth"), 0.1f, 50f);
            Color foam = EditorGUILayout.ColorField(new GUIContent("Foam", "Its alpha is how much foam."), material.GetColor("_FoamColor"), true, true, false);
            float foamWidth = Slider("Foam Width (m)", null, material.GetFloat("_FoamWidth"), 0f, 4f);
            float waves = Slider("Waves", null, material.GetFloat("_WaveStrength"), 0f, 1f);
            float waveSize = Slider("Wave Size (m)", null, material.GetFloat("_WaveSize"), 0.5f, 50f);
            float waveSpeed = Slider("Wave Speed", null, material.GetFloat("_WaveSpeed"), 0f, 4f);
            float shine = Slider("Shine", "How much the sun glints on it.", material.GetFloat("_Shine"), 0f, 1f);
            float reflection = Slider("Sky Reflection", null, material.GetFloat("_Reflection"), 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(material, "Water Look");
                material.SetColor("_ShallowColor", shallow);
                material.SetColor("_DeepColor", deep);
                material.SetFloat("_Depth", depth);
                material.SetColor("_FoamColor", foam);
                material.SetFloat("_FoamWidth", foamWidth);
                material.SetFloat("_WaveStrength", waves);
                material.SetFloat("_WaveSize", waveSize);
                material.SetFloat("_WaveSpeed", waveSpeed);
                material.SetFloat("_Shine", shine);
                material.SetFloat("_Reflection", reflection);
                EditorUtility.SetDirty(material);
                SceneView.RepaintAll();
            }
        }

        // Water over the whole land (a sea), a little above its lowest ground; and nothing left standing in it
        private void AddSea()
        {
            if (!WaterSetup.LandArea(out Bounds land))
            {
                problem = "There's no Land in the open scene. Build it with Tools > Paint Land > Rebuild Land first.";
                return;
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var middle = new Vector3(land.center.x, WaterSetup.StartingLevel(land), land.center.z);
            LandWater added = WaterSetup.Add(middle, new Vector2(land.size.x + 400f, land.size.z + 400f), "Sea");
            if (added == null) return;
            selectedWater = added;
            int deleted = waterClear ? WaterSetup.ClearUnderWater() : 0;
            Undo.CollapseUndoOperations(group);
            Undo.SetCurrentGroupName("Add Sea");
            if (deleted > 0) ShowNotification(new GUIContent($"Deleted {deleted:N0} things in the water"));
            SceneView.RepaintAll();
        }

        // The chosen water's edge, at its level, in the Scene view
        private void DrawWaterOutline()
        {
            LandWater water = selectedWater;
            if (water == null || water.Painted) return;
            Transform t = water.transform;
            Vector2 half = water.size * 0.5f;
            Vector3[] corners =
            {
                t.TransformPoint(new Vector3(-half.x, 0f, -half.y)), t.TransformPoint(new Vector3(half.x, 0f, -half.y)),
                t.TransformPoint(new Vector3(half.x, 0f, half.y)), t.TransformPoint(new Vector3(-half.x, 0f, half.y)),
                t.TransformPoint(new Vector3(-half.x, 0f, -half.y)),
            };
            Handles.color = new Color(0f, 0f, 0f, 0.5f);
            Handles.DrawAAPolyLine(5f, corners);
            Handles.color = new Color(0.4f, 0.8f, 1f);
            Handles.DrawAAPolyLine(2.5f, corners);
        }

        // ---------------------------------------------------------------- Grass

        // Shift (or Erase) swaps adding and removing; with Automatic it removes
        private GrassPaint.Brush GrassBrush(bool erasing)
        {
            if (!erasing) return grassKind;
            return grassKind == GrassPaint.Brush.Remove ? GrassPaint.Brush.Add : GrassPaint.Brush.Remove;
        }

        private void DrawGrass()
        {
            GrassField field = GrassPaint.Find();
            if (field == null)
            {
                Hint("Grass over the whole land, drawn by the GPU near the camera (no objects, so it stays fast). It grows on green ground by itself.");
                if (GUILayout.Button("Add Grass", GUILayout.Height(30)))
                {
                    GrassField made = GrassPaint.Create();
                    if (made != null) ShowNotification(new GUIContent("Grass added"));
                    SceneView.RepaintAll();
                }
                return;
            }

            Header("Brush");
            EditorGUI.BeginChangeCheck();
            grassKind = (GrassPaint.Brush)GUILayout.Toolbar((int)grassKind, new[]
            {
                new GUIContent("Add", "More grass (even off the green ground)."),
                new GUIContent("Remove", "No grass."),
                new GUIContent("Automatic", "Back to what the land decides."),
            }, GUILayout.Height(24));
            grassBrush = Slider("Size (m)", "How wide the brush is.", grassBrush, 1f, MaxGrassBrush);
            if (grassKind == GrassPaint.Brush.Add) grassAmount = Slider("Thickness", "How thick the grass Add paints.", grassAmount, 0.05f, 1f);
            EraseToggle("Does the opposite: removes grass (or adds it, with Remove).");
            if (EditorGUI.EndChangeCheck()) Changed();
            Hint($"Grass grows by itself on green ground, not on paths, water or steep slopes. Drawing {field.Drawn:N0} tufts now.");

            // The Grass Field's settings
            if (Section("GrassSettings", "Grass Settings", false))
            {
                var serialized = new SerializedObject(field);
                serialized.Update();
                foreach (string property in new[]
                         {
                             "density", "maxSlope", "patchiness", "patchSize", "size", "heightVariation", "followSlope", "randomLean",
                             "groundColour", "colours", "shadeVariation", "distance", "detailDistance", "farDensity", "showInSceneView", "seed",
                         })
                {
                    SerializedProperty found = serialized.FindProperty(property);
                    if (found != null) EditorGUILayout.PropertyField(found, true);
                }
                if (serialized.ApplyModifiedProperties()) SceneView.RepaintAll();
            }

            // How it looks (the Grass Field material)
            Material material = field.material;
            if (material == null || !Section("GrassLook", "Look", false)) return;
            EditorGUI.BeginChangeCheck();
            float wind = Slider("Wind Strength", null, material.GetFloat("_WindStrength"), 0f, 1f);
            float windSpeed = Slider("Wind Speed", null, material.GetFloat("_WindSpeed"), 0f, 5f);
            float tips = Slider("Tip Brightness", "How much lighter the tips are.", material.GetFloat("_TipBrightness"), 0.5f, 2f);
            float bottom = Slider("Darker At Bottom", "How much darker the roots are.", material.GetFloat("_BottomShade"), 0f, 1f);
            float cutoff = Slider("Blade Thinness", "Higher = thinner blades.", material.GetFloat("_Cutoff"), 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(material, "Grass Look");
                material.SetFloat("_WindStrength", wind);
                material.SetFloat("_WindSpeed", windSpeed);
                material.SetFloat("_TipBrightness", tips);
                material.SetFloat("_BottomShade", bottom);
                material.SetFloat("_Cutoff", cutoff);
                EditorUtility.SetDirty(material);
                SceneView.RepaintAll();
            }
        }

        // ---------------------------------------------------------------- Scatter

        private void DrawScatterTab(LandSettings settings)
        {
            Header("Brush");
            EditorGUI.BeginChangeCheck();
            scatterBrush = Slider("Size (m)", "How wide the brush is.", scatterBrush, 1f, MaxScatterBrush);
            EraseToggle("Take the ticked things away.");
            scatterColliders = EditorGUILayout.ToggleLeft(new GUIContent("Add colliders (so you can't walk through them)",
                "Each one painted gets its kind's collider (set in the kind's settings below): round the trunk for trees, the " +
                "exact shape for rocks, stumps and logs, none for grass and flowers."), scatterColliders);
            if (EditorGUI.EndChangeCheck()) Changed();
            if (GUILayout.Button(new GUIContent("Give The Ones Already Placed Colliders Too",
                    "Gives everything of every kind already in the scene (scattered, or placed by hand) its kind's collider, as set " +
                    "below (a kind set to None loses the ones this added). Ctrl+Z undoes it.")))
            {
                int count = LandScatter.UpdateColliders(settings.scatter);
                ShowNotification(new GUIContent(count > 0 ? $"Updated the colliders on {count:N0} things" : "Nothing to change"));
            }
            Hint("Going over a spot again only fills it up to each kind's density. Nothing is put in water.");
            ScatterRenderer drawer = UnityEngine.Object.FindFirstObjectByType<ScatterRenderer>();
            if (drawer != null)
            {
                Hint($"Drawn fast (GPU instancing): {drawer.Things:N0} things, {drawer.Drawn:N0} near the last camera in {drawer.Draws:N0} " +
                     "draw calls. How far each kind is drawn, and its shadows, are in its settings below.");
            }
            DrawScatter(settings);
        }

        // ---------------------------------------------------------------- The Scatter tab

        private void DrawScatter(LandSettings settings)
        {
            LandScatter.SetUpKinds(settings);
            Header("What To Scatter", "Tick the kinds to scatter. Click a name to open its settings.");
            for (int i = 0; i < settings.scatter.Count; i++)
            {
                LandScatterGroup kind = settings.scatter[i];
                if (kind == null || !DrawKind(settings, kind)) continue;
                Undo.RecordObject(settings, "Remove Scatter Kind");
                settings.scatter.RemoveAt(i);
                EditorUtility.SetDirty(settings);
                GUIUtility.ExitGUI();
            }

            // A new kind, from prefabs (or folders of them) dropped here, or empty
            Rect drop = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop prefabs or a folder here for a new kind", DropStyle);
            List<GameObject> dropped = Dropped(drop, out string droppedName);
            if (dropped != null)
            {
                Undo.RecordObject(settings, "Add Scatter Kind");
                settings.scatter.Add(new LandScatterGroup
                {
                    name = droppedName, entries = dropped.ConvertAll(p => new ScatterPrefab(p)), open = true,
                    collider = LandScatter.GuessCollider(droppedName, dropped),
                });
                settings.scatter[settings.scatter.Count - 1].SyncPrefabs();
                EditorUtility.SetDirty(settings);
            }
            if (GUILayout.Button(new GUIContent("New Kind", "An empty kind to drop prefabs into.")))
            {
                Undo.RecordObject(settings, "Add Scatter Kind");
                settings.scatter.Add(new LandScatterGroup { open = true });
                EditorUtility.SetDirty(settings);
            }
        }

        // One kind: its tick, name and pictures, and (opened) its settings. Returns true if it's to be removed.
        private bool DrawKind(LandSettings settings, LandScatterGroup kind)
        {
            bool remove = false;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    bool on = EditorGUILayout.Toggle(kind.on, GUILayout.Width(16f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(settings, on ? "Tick Scatter Kind" : "Untick Scatter Kind");
                        kind.on = on;
                        EditorUtility.SetDirty(settings);
                        SceneView.RepaintAll();
                    }
                    kind.open = EditorGUILayout.Foldout(kind.open, kind.name, true);
                    int shown = 0;
                    foreach (GameObject prefab in kind.prefabs)
                    {
                        if (prefab == null) continue;
                        if (++shown > 4) break;
                        Texture picture = AssetPreview.GetAssetPreview(prefab);
                        if (picture == null) picture = AssetPreview.GetMiniThumbnail(prefab);
                        GUILayout.Label(new GUIContent(picture, prefab.name), GUILayout.Width(26f), GUILayout.Height(26f));
                    }
                }
                if (!kind.open) return false;

                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.TextField("Name", kind.name);
                float density = EditorGUILayout.Slider(new GUIContent("Density", "How many in each 10 x 10 m of land."), kind.density, 0.05f, 60f);
                float smallest = Mathf.Min(kind.size.x, kind.size.y), biggest = Mathf.Max(kind.size.x, kind.size.y);
                EditorGUILayout.MinMaxSlider(new GUIContent($"Size ({smallest:0.##} to {biggest:0.##})", "Random size, from-to (1 = the prefab's own size)."),
                                             ref smallest, ref biggest, 0.1f, 3f);
                float lean = EditorGUILayout.Slider(new GUIContent("Lean With Slope", "0 = straight up, 1 = square to the ground."), kind.followSlope, 0f, 1f);
                float steepest = EditorGUILayout.Slider(new GUIContent("Steepest Ground (°)", "Not on ground steeper than this."), kind.maxSlope, 0f, 90f);
                float sink = EditorGUILayout.Slider(new GUIContent("Sink Into Ground (m)", "So they don't float on slopes."), kind.sink, 0f, 1f);
                float patchiness = EditorGUILayout.Slider(new GUIContent("Patchiness", "0 = spread evenly, 1 = thick patches with bare ground between."),
                                                          kind.patchiness, 0f, 1f);
                float patchSize = EditorGUILayout.Slider(new GUIContent("Patch Size (m)", "How wide the patches are."), kind.patchSize, 1f, 80f);
                float randomLean = EditorGUILayout.Slider(new GUIContent("Random Lean (°)", "So they don't all stand the same."), kind.randomLean, 0f, 45f);
                float heightVariation = EditorGUILayout.Slider(new GUIContent("Height Variation", "How much taller or shorter than wide each one can be."),
                                                               kind.heightVariation, 0f, 0.8f);
                var solid = (ScatterCollider)EditorGUILayout.Popup(new GUIContent("Collider",
                    "What each one gets when it's painted (with Add Colliders on), so you can't walk through it. A prefab with " +
                    "colliders of its own keeps those."), (int)kind.collider, ColliderNames);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(settings, "Change Scatter Kind");
                    kind.name = name;
                    kind.density = density;
                    kind.size = new Vector2(smallest, biggest);
                    kind.followSlope = lean;
                    kind.maxSlope = steepest;
                    kind.sink = sink;
                    kind.patchiness = patchiness;
                    kind.patchSize = patchSize;
                    kind.randomLean = randomLean;
                    kind.heightVariation = heightVariation;
                    kind.collider = solid;
                    EditorUtility.SetDirty(settings);
                }
                if (GUILayout.Button(new GUIContent("Give The Ones Already Placed This Collider",
                        $"Gives every {kind.name} already in the scene (scattered, or placed by hand) the collider above. Ctrl+Z undoes it."),
                        EditorStyles.miniButton))
                {
                    int count = LandScatter.UpdateColliders(new[] { kind });
                    ShowNotification(new GUIContent(count > 0 ? $"Updated the colliders on {count:N0} {kind.name}" : $"No {kind.name} to change"));
                }

                DrawPrefabs(settings, kind);
                DrawColours(settings, kind);
                DrawDrawing(kind);

                EditorGUILayout.Space(4f);
                if (GUILayout.Button("Remove This Kind")) remove = true;
            }
            return remove;
        }

        // How far a kind is drawn, when it changes to its simpler look and how far it casts shadows (kept on the Scatter
        // Renderer on Land Scatter, which draws them)
        private static void DrawDrawing(LandScatterGroup kind)
        {
            ScatterRenderer drawer = UnityEngine.Object.FindFirstObjectByType<ScatterRenderer>();
            if (drawer == null || string.IsNullOrEmpty(kind.name)) return;
            ScatterRenderer.Kind draw = drawer.KindSettings(kind.name);
            EditorGUILayout.Space(2f);
            EditorGUI.BeginChangeCheck();
            float distance = EditorGUILayout.Slider(new GUIContent("Draw Distance (m)", "Not drawn further from the camera than this."),
                                                    draw.drawDistance, 10f, 1000f);
            float detail = EditorGUILayout.Slider(new GUIContent("Detail Distance (m)", "Its most detailed look within this, its simpler " +
                                                  "looks further away."), draw.detailDistance, 5f, 200f);
            float shadows = EditorGUILayout.Slider(new GUIContent("Shadow Distance (m)", "Casts shadows within this. 0 = never (small " +
                                                   "plants: their shadows hardly show, and they cost the most)."), draw.shadowDistance, 0f, 150f);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(drawer, "Change Scatter Drawing");
            draw.drawDistance = distance;
            draw.detailDistance = Mathf.Min(detail, distance);
            draw.shadowDistance = Mathf.Min(shadows, distance);
            EditorUtility.SetDirty(drawer);
            SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- A kind's prefabs and colours

        // The kind's prefabs, each in its own box: ticked on or off, how often it's picked, its petal colours (flowers);
        // copy one to have the same prefab again (say with other petals), or take it out. Dropping prefabs adds them,
        // even ones already there.
        private void DrawPrefabs(LandSettings settings, LandScatterGroup kind)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(new GUIContent("Prefabs", "Picked from at random, as often as each one's Chance. Untick " +
                "one to leave it out (it's not scattered or rubbed out). The same prefab can be here more than once."),
                EditorStyles.boldLabel);
            for (int k = 0; k < kind.entries.Count; k++)
            {
                ScatterPrefab entry = kind.entries[k];
                if (entry == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    Color before = GUI.color;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        bool on = EditorGUILayout.Toggle(new GUIContent(string.Empty, "Use this one. Off: it's not scattered or rubbed out."),
                                                         entry.on, GUILayout.Width(16f));
                        if (!on) GUI.color = new Color(1f, 1f, 1f, 0.45f);
                        Texture picture = entry.prefab != null ? AssetPreview.GetAssetPreview(entry.prefab) : null;
                        if (picture == null && entry.prefab != null) picture = AssetPreview.GetMiniThumbnail(entry.prefab);
                        GUILayout.Label(new GUIContent(picture, entry.prefab != null ? entry.prefab.name : "No prefab"),
                                        GUILayout.Width(38f), GUILayout.Height(38f));
                        GameObject prefab;
                        float chance, fewest, most;
                        using (new EditorGUILayout.VerticalScope())
                        {
                            prefab = (GameObject)EditorGUILayout.ObjectField(entry.prefab, typeof(GameObject), false);
                            float labelWidth = EditorGUIUtility.labelWidth;
                            EditorGUIUtility.labelWidth = 84f;
                            chance = EditorGUILayout.Slider(new GUIContent("Chance", "How often this one is picked next to the others " +
                                                            "(2 = twice as often as a 1)."), entry.chance, 0.05f, 5f);
                            fewest = Mathf.Max(1, entry.clump.x);
                            most = Mathf.Max(fewest, entry.clump.y);
                            string range = Mathf.RoundToInt(fewest) == Mathf.RoundToInt(most) ? $"{fewest:0}" : $"{fewest:0}-{most:0}";
                            EditorGUILayout.MinMaxSlider(new GUIContent($"Clumps ({range})", "How many grow together in each clump of this " +
                                                         "one, from-to (1 = each on its own)."), ref fewest, ref most, 1f, 12f);
                            EditorGUIUtility.labelWidth = labelWidth;
                        }
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(settings, "Change Scatter Prefab");
                            entry.on = on;
                            entry.prefab = prefab;
                            entry.chance = chance;
                            entry.clump = new Vector2Int(Mathf.RoundToInt(fewest), Mathf.Max(Mathf.RoundToInt(fewest), Mathf.RoundToInt(most)));
                            kind.SyncPrefabs();
                            EditorUtility.SetDirty(settings);
                        }
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(24f)))
                        {
                            if (GUILayout.Button(new GUIContent("+", "Add this prefab again (with the same settings), say to give it other petal colours."),
                                    EditorStyles.miniButton, GUILayout.Width(24f)))
                            {
                                Undo.RecordObject(settings, "Copy Scatter Prefab");
                                kind.entries.Insert(k + 1, entry.Copy());
                                EditorUtility.SetDirty(settings);
                                GUIUtility.ExitGUI();
                            }
                            if (GUILayout.Button(new GUIContent("x", "Take this prefab out of the kind."), EditorStyles.miniButton, GUILayout.Width(24f)))
                            {
                                Undo.RecordObject(settings, "Remove Scatter Prefab");
                                kind.entries.RemoveAt(k);
                                kind.SyncPrefabs();
                                EditorUtility.SetDirty(settings);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                    foreach (string part in LandScatter.PartsOf(entry.prefab)) DrawPartColours(settings, kind, entry, part);
                    GUI.color = before;
                }
            }

            Rect drop = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop prefabs or a folder here to add them", DropStyle);
            List<GameObject> dropped = Dropped(drop, out _);
            if (dropped != null)
            {
                Undo.RecordObject(settings, "Add Scatter Prefabs");
                foreach (GameObject prefab in dropped) kind.entries.Add(new ScatterPrefab(prefab));
                kind.SyncPrefabs();
                EditorUtility.SetDirty(settings);
            }
        }

        // One part of a prefab (its Petals, Leaves, Trunk...): the colours it can be, one picked for each clump; a button
        // for a ready-made set (Wildflowers, Autumn, Bark...) and one to go back to its own (or the kind's Plant Colours)
        private void DrawPartColours(LandSettings settings, LandScatterGroup kind, ScatterPrefab entry, string part)
        {
            List<Color> colours = entry.ColoursFor(part);
            PartPreset preset = PresetFor(part, settings);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(part, PartTooltip(part)), EditorStyles.miniBoldLabel, GUILayout.Width(48f));
                using (new EditorGUILayout.VerticalScope())
                {
                    ColourStrip(settings, colours, $"Change {part} Colours", NextColour(colours, preset.Colours), 100f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (colours.Count == 0)
                        {
                            GUILayout.Label(kind.colours.Count > 0 && part != LandScatter.Petals ? "(the kind's Plant Colours)" : "(its own colour)",
                                            EditorStyles.miniLabel);
                        }
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent(preset.Name, preset.Tooltip), EditorStyles.miniButtonLeft))
                        {
                            Undo.RecordObject(settings, $"Change {part} Colours");
                            entry.SetColours(part, new List<Color>(preset.Colours));
                            EditorUtility.SetDirty(settings);
                        }
                        using (new EditorGUI.DisabledScope(colours.Count == 0))
                        {
                            if (GUILayout.Button(new GUIContent("Own Colour", $"No {part.ToLowerInvariant()} colours: the prefab's own (or the kind's " +
                                    "Plant Colours, if it has some)."), EditorStyles.miniButtonRight))
                            {
                                Undo.RecordObject(settings, $"Change {part} Colours");
                                colours.Clear();
                                EditorUtility.SetDirty(settings);
                            }
                        }
                    }
                }
            }
        }

        private static string PartTooltip(string part)
        {
            switch (part)
            {
                case LandScatter.Petals: return "The colours its flower heads can be.";
                case "Leaves": return "The colours its leaves (or needles) can be.";
                case "Trunk": return "The colours its trunk and branches can be (fruit keeps its own colour).";
                case "Stems": return "The colours its stems and leaves can be.";
                case "Blades": return "The colours its blades of grass can be.";
                case "Rock": return "The colours the rock can be.";
                case "Cap": return "The colours its cap can be.";
                case "Stalk": return "The colours its stalk can be.";
                default: return "The colours it can be (fruit keeps its own colour).";
            }
        }

        private struct PartPreset
        {
            public string Name, Tooltip;
            public Color[] Colours;
        }

        private static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.magenta;

        // A ready-made set of colours for each part
        private static PartPreset PresetFor(string part, LandSettings settings)
        {
            switch (part)
            {
                case LandScatter.Petals:
                    return new PartPreset { Name = "Wildflowers", Tooltip = "Red, white, yellow, pink, purple and orange.", Colours = new[]
                        { Hex("#DB1F1A"), Hex("#F7F2E6"), Hex("#FFD126"), Hex("#F280B3"), Hex("#8C4DD9"), Hex("#FF801A") } };
                case "Leaves":
                    return new PartPreset { Name = "Autumn", Tooltip = "Golden yellow, orange, red and a last bit of green.", Colours = new[]
                        { Hex("#E8B321"), Hex("#E0761C"), Hex("#C2361B"), Hex("#8A9A2C") } };
                case "Trunk":
                    return new PartPreset { Name = "Bark", Tooltip = "Brown, dark brown, grey and birch white.", Colours = new[]
                        { Hex("#7A5A3A"), Hex("#4E3826"), Hex("#77736B"), Hex("#E3DED2") } };
                case "Cap":
                    return new PartPreset { Name = "Toadstools", Tooltip = "Red, orange, brown, cream and purple.", Colours = new[]
                        { Hex("#D9382E"), Hex("#E8862A"), Hex("#8A5A36"), Hex("#EDE3CF"), Hex("#7E4FC2") } };
                case "Stalk":
                    return new PartPreset { Name = "Pale", Tooltip = "Cream, white and light tan.", Colours = new[]
                        { Hex("#EDE6D6"), Hex("#F7F4EE"), Hex("#D8C3A0") } };
                case "Rock":
                    return new PartPreset { Name = "Stone", Tooltip = "Light grey, grey, warm sandstone and mossy grey.", Colours = new[]
                        { Hex("#C9C7C2"), Hex("#8E8C88"), Hex("#C2A47E"), Hex("#7F8A6E") } };
                case "Colour":
                    return new PartPreset { Name = "Earthy", Tooltip = "Browns and tans, cream and red.", Colours = new[]
                        { Hex("#8A6A45"), Hex("#C9A879"), Hex("#EDE3CF"), Hex("#B8402F") } };
                default:
                    return new PartPreset { Name = "Land Greens", Tooltip = "The land's own greens (from Land Settings), so it matches the ground.",
                        Colours = LandScatter.LandColours(settings).ToArray() };
            }
        }

        // The first of the ready-made colours not in the list yet (or the last one again)
        private static Color NextColour(List<Color> colours, Color[] presets)
        {
            foreach (Color preset in presets)
            {
                bool there = false;
                foreach (Color c in colours) there |= Same(c, preset);
                if (!there) return preset;
            }
            return colours.Count > 0 ? colours[colours.Count - 1] : presets.Length > 0 ? presets[0] : Color.white;
        }

        // A kind's colours (its stems and leaves): the ones it can be (picked at random, in patches), how much the shade
        // varies, how dark the bottom is; and a button to give the ones already in the scene the colours again
        private void DrawColours(LandSettings settings, LandScatterGroup kind)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(new GUIContent("Plant Colours", "The colours the whole kind can be, picked at random (any part of a prefab without colours of its own above) " +
                "(in patches). None: the prefab's own. Click one to change it, right-click to remove or copy it."), EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                ColourStrip(settings, kind.colours, "Change Scatter Colours", colour, 70f);
                if (kind.colours.Count == 0) EditorGUILayout.LabelField("(the prefab's own colours: + adds the painter's colour)", EditorStyles.miniLabel);

                int index = settings.scatter.IndexOf(kind);
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color background = GUI.backgroundColor;
                    if (pickingFor == index) GUI.backgroundColor = new Color(0.5f, 1f, 0.55f);
                    if (GUILayout.Button(new GUIContent(pickingFor == index ? "Click The Land..." : "Pick From Land",
                            "Click the land in the Scene view to add its real colour (the colour it's painted, not the lit colour on screen " +
                            "that the eyedropper copies)."), EditorStyles.miniButtonLeft))
                    {
                        if (pickingFor == index)
                        {
                            pickingFor = -1;
                        }
                        else
                        {
                            if (!painting) StartPainting();
                            pickingFor = painting ? index : -1;
                            if (painting) ShowNotification(new GUIContent("Click the land in the Scene view"));
                        }
                        SceneView.RepaintAll();
                    }
                    GUI.backgroundColor = background;
                    if (GUILayout.Button(new GUIContent("Land Greens", "The land's own greens (from Land Settings), so it matches the ground."),
                            EditorStyles.miniButtonMid))
                    {
                        Undo.RecordObject(settings, "Scatter Colours From Land");
                        kind.colours = LandScatter.LandColours(settings);
                        EditorUtility.SetDirty(settings);
                    }
                    using (new EditorGUI.DisabledScope(kind.colours.Count == 0))
                    {
                        if (GUILayout.Button(new GUIContent("Own Colours", "No colours: use the prefab's own."), EditorStyles.miniButtonRight))
                        {
                            Undo.RecordObject(settings, "Clear Scatter Colours");
                            kind.colours.Clear();
                            EditorUtility.SetDirty(settings);
                        }
                    }
                }
                if (kind.colours.Count > 0)
                {
                    EditorGUI.BeginChangeCheck();
                    float shade = EditorGUILayout.Slider(new GUIContent("Shade Variation", "How much each one's shade varies from the colour " +
                                                         "picked (petals vary half as much)."), kind.shadeVariation, 0f, 0.4f);
                    float bottom = EditorGUILayout.Slider(new GUIContent("Darker At Bottom", "How much darker they are at the roots (plants from the nature pack)."),
                                                          kind.bottomShade, 0f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(settings, "Change Scatter Colours");
                        kind.shadeVariation = shade;
                        kind.bottomShade = bottom;
                        EditorUtility.SetDirty(settings);
                    }
                }
            }
            if (GUILayout.Button(new GUIContent("Recolour The Ones Already Placed",
                    "Gives everything of this kind already in the scene (scattered, or placed by hand) these plant and petal colours. " +
                    "Ctrl+Z undoes it.")))
            {
                int count = LandScatter.Recolour(kind);
                ShowNotification(new GUIContent(count > 0 ? $"Recoloured {count:N0} {kind.name}" : $"No {kind.name} in the scene yet"));
            }
        }

        // A row (or rows) of colour swatches: click one to change it, right-click to remove or copy it; + adds 'added'.
        // 'margin': how much of the window's width is taken up beside it.
        private void ColourStrip(LandSettings settings, List<Color> list, string undoName, Color added, float margin)
        {
            const float size = 22f, gap = 3f;
            float width = Mathf.Max(position.width - margin, size);
            int perRow = Mathf.Max(1, Mathf.FloorToInt((width + gap) / (size + gap)));
            int count = list.Count + 1;
            int rows = (count + perRow - 1) / perRow;
            Rect area = GUILayoutUtility.GetRect(width, rows * (size + gap), GUILayout.ExpandWidth(true));
            Event e = Event.current;
            for (int i = 0; i < count; i++)
            {
                var r = new Rect(area.x + (i % perRow) * (size + gap), area.y + (i / perRow) * (size + gap), size, size);
                if (i == list.Count)
                {
                    if (GUI.Button(r, new GUIContent("+", "Add a colour (then click it to change it).")))
                    {
                        Undo.RecordObject(settings, undoName);
                        added.a = 1f;
                        list.Add(added);
                        EditorUtility.SetDirty(settings);
                    }
                    continue;
                }
                if (e.type == EventType.MouseDown && e.button == 1 && r.Contains(e.mousePosition))
                {
                    int at = i;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Remove"), false, () => EditList(settings, undoName, () => list.RemoveAt(at)));
                    menu.AddItem(new GUIContent("Copy"), false, () => EditList(settings, undoName, () => list.Insert(at + 1, list[at])));
                    menu.AddItem(new GUIContent("Use The Painter's Colour"), false, () => EditList(settings, undoName, () =>
                    {
                        Color painter = colour;
                        painter.a = 1f;
                        list[at] = painter;
                    }));
                    menu.ShowAsContext();
                    e.Use();
                    continue;
                }
                EditorGUI.BeginChangeCheck();
                Color picked = EditorGUI.ColorField(r, new GUIContent(string.Empty, "Click to change it, right-click to remove or copy it."),
                                                    list[i], true, false, false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(settings, undoName);
                    picked.a = 1f;
                    list[i] = picked;
                    EditorUtility.SetDirty(settings);
                }
            }
        }

        private void EditList(LandSettings settings, string undoName, System.Action edit)
        {
            Undo.RecordObject(settings, undoName);
            edit();
            EditorUtility.SetDirty(settings);
            Repaint();
        }

        private static readonly GUIContent[] ColliderNames =
        {
            new GUIContent("None (walk through)"), new GUIContent("Round The Trunk (trees)"),
            new GUIContent("Exact Shape (rocks, logs)"), new GUIContent("Box"),
        };

        private static GUIStyle dropStyle;
        private static GUIStyle DropStyle => dropStyle ??= new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter };

        // Prefabs (or folders of them) dragged from the Project window and let go over an area
        private static List<GameObject> Dropped(Rect area, out string name)
        {
            name = null;
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !area.Contains(e.mousePosition)) return null;
            List<GameObject> prefabs = LandScatter.PrefabsIn(DragAndDrop.objectReferences);
            if (prefabs.Count == 0) return null;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type != EventType.DragPerform)
            {
                e.Use();
                return null;
            }
            DragAndDrop.AcceptDrag();
            e.Use();
            string[] paths = DragAndDrop.paths;
            name = paths != null && paths.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(paths[0]) : prefabs[0].name;
            return prefabs;
        }

        private bool EnsureLoaded()
        {
            if (LandPaint.Ready) return true;
            problem = LandPaint.Load();
            return problem == null;
        }

        // The palette: click a swatch to paint with it, right-click to change or remove it, + adds the current colour
        private void DrawPalette(LandSettings settings)
        {
            const float size = 24f, gap = 3f;
            float width = Mathf.Max(position.width - 14f, size);
            int perRow = Mathf.Max(1, Mathf.FloorToInt((width + gap) / (size + gap)));
            int count = settings.palette.Count + 1;
            int rows = (count + perRow - 1) / perRow;
            Rect area = GUILayoutUtility.GetRect(width, rows * (size + gap));
            Event e = Event.current;
            for (int i = 0; i < count; i++)
            {
                var r = new Rect(area.x + (i % perRow) * (size + gap), area.y + (i / perRow) * (size + gap), size, size);
                if (i == settings.palette.Count)
                {
                    if (GUI.Button(r, new GUIContent("+", "Add the colour above to the palette.")))
                    {
                        Undo.RecordObject(settings, "Add Paint Colour");
                        settings.palette.Add(new LandPaintColour("#" + ColorUtility.ToHtmlStringRGB(colour), colour));
                        EditorUtility.SetDirty(settings);
                    }
                    continue;
                }

                LandPaintColour swatch = settings.palette[i];
                Color c = swatch.colour;
                c.a = 1f;
                EditorGUI.DrawRect(r, c);
                if (Same(c, colour))
                {
                    Outline(r, Color.white, 2f);
                    Outline(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), new Color(0f, 0f, 0f, 0.6f), 1f);
                }
                else
                {
                    Outline(r, new Color(0f, 0f, 0f, 0.45f), 1f);
                }
                GUI.Label(r, new GUIContent(string.Empty, swatch.name));
                if (e.type != EventType.MouseDown || !r.Contains(e.mousePosition)) continue;
                if (e.button == 0)
                {
                    colour = c;
                    GUI.changed = true;
                    SavePrefs();
                    SceneView.RepaintAll();
                    e.Use();
                }
                else if (e.button == 1)
                {
                    int index = i;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Use the current colour here"), false, () =>
                    {
                        Undo.RecordObject(settings, "Change Paint Colour");
                        settings.palette[index].colour = colour;
                        EditorUtility.SetDirty(settings);
                        Repaint();
                    });
                    menu.AddItem(new GUIContent("Remove"), false, () =>
                    {
                        Undo.RecordObject(settings, "Remove Paint Colour");
                        settings.palette.RemoveAt(index);
                        EditorUtility.SetDirty(settings);
                        Repaint();
                    });
                    menu.ShowAsContext();
                    e.Use();
                }
            }
        }

        private static void Outline(Rect r, Color c, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), c);
        }

        private static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.003f && Mathf.Abs(a.g - b.g) < 0.003f && Mathf.Abs(a.b - b.b) < 0.003f;

        // ================================================================ The Scene view

        private void OnSceneGUI(SceneView view)
        {
            if (!painting) return;
            if (!LandPaint.Ready)
            {
                problem = LandPaint.Load();
                if (problem != null)
                {
                    StopPainting();
                    return;
                }
            }

            Event e = Event.current;
            int id = GUIUtility.GetControlID(ControlHint, FocusType.Passive);
            bool erasing = (eraser && mode != Mode.Shape) || e.shift;   // (no Erase toggle on the Shape tab: Shift there lowers or smooths)
            switch (e.type)
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(id);
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape && (pickingFor >= 0 || pickingColour))
                    {
                        pickingFor = -1;
                        pickingColour = false;
                        Repaint();
                        view.Repaint();
                        e.Use();
                        return;
                    }
                    if (e.keyCode == KeyCode.Escape && !stroking)
                    {
                        StopPainting();
                        e.Use();
                        return;
                    }
                    if (e.keyCode == KeyCode.LeftBracket || e.keyCode == KeyCode.RightBracket)
                    {
                        Resize(e.keyCode == KeyCode.RightBracket);
                        e.Use();
                        view.Repaint();
                    }
                    break;

                case EventType.MouseMove:
                    Aim(view, e.mousePosition, true);
                    view.Repaint();
                    break;

                case EventType.MouseLeaveWindow:
                    overLand = false;
                    view.Repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    if (stroking) EndStroke();
                    Aim(view, e.mousePosition, true);
                    if (!overLand) break;
                    if (mode == Mode.Water && (e.control || e.command))
                    {
                        // The chosen water's level to the height clicked
                        if (selectedWater != null)
                        {
                            Undo.RecordObject(selectedWater.transform, "Water Level");
                            Vector3 p = selectedWater.transform.position;
                            selectedWater.transform.position = new Vector3(p.x, hit.Point.y + 0.05f, p.z);
                            selectedWater.Refresh();
                        }
                        Repaint();
                        view.Repaint();
                        e.Use();
                        break;
                    }
                    if (pickingColour)
                    {
                        // The land's real colour here (not the lit colour on screen)
                        colour = LandPaint.PickColour(hit);
                        colour.a = 1f;
                        pickingColour = false;
                        SavePrefs();
                        Repaint();
                        e.Use();
                        break;
                    }
                    if (pickingFor >= 0)
                    {
                        // The land's own colour here (not the lit colour on screen) for a scatter kind
                        LandSettings settings = LandBuilder.GetSettings();
                        Color picked = LandPaint.PickColour(hit);
                        picked.a = 1f;
                        if (pickingFor < settings.scatter.Count && settings.scatter[pickingFor] != null)
                        {
                            Undo.RecordObject(settings, "Pick Scatter Colour");
                            settings.scatter[pickingFor].colours.Add(picked);
                            EditorUtility.SetDirty(settings);
                        }
                        colour = picked;
                        pickingFor = -1;
                        SavePrefs();
                        Repaint();
                        e.Use();
                        break;
                    }
                    if (e.control || e.command)
                    {
                        if (mode == Mode.Textures)
                        {
                            int picked = LandTextures.PickAt(hit);
                            if (picked > 0) textureLayer = picked;
                            else ShowNotification(new GUIContent("No texture painted there"));
                        }
                        else if (mode == Mode.Shape)
                        {
                            flattenHeight = hit.Local.y;
                            flattenAtStart = false;
                            if (shapeTool != LandShape.Tool.Flatten) shapeTool = LandShape.Tool.Flatten;
                            ShowNotification(new GUIContent($"Flatten to {flattenHeight:0.##} m"));
                        }
                        else
                        {
                            colour = LandPaint.PickColour(hit);
                            colour.a = 1f;
                        }
                        SavePrefs();
                        Repaint();
                        e.Use();
                        break;
                    }
                    if (!BeginStroke(erasing)) break;
                    strokeView = view;
                    strokeControl = id;
                    GUIUtility.hotControl = id;
                    Dab(hit, true);
                    FlushAll();
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!stroking || view != strokeView || e.button != 0) break;
                    Aim(view, e.mousePosition, true);
                    if (overLand)
                    {
                        Dab(hit, false);
                        FlushAll();
                    }
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (!stroking || e.button != 0) break;
                    if (GUIUtility.hotControl == strokeControl) GUIUtility.hotControl = 0;
                    EndStroke();
                    e.Use();
                    break;

                case EventType.Repaint:
                    Aim(view, e.mousePosition, stroking && strokeMode == Mode.Shape);
                    if (overLand) DrawBrush(stroking ? strokeErases : erasing);
                    else if (mode == Mode.Water) DrawWaterOutline();
                    DrawStatus(view, stroking ? strokeErases : erasing);
                    break;
            }

            // The button let go somewhere this didn't hear about (outside the Scene view, say)
            if (stroking && e.rawType == EventType.MouseUp && e.button == 0) EndStroke();
        }

        // Shows what the stroke has done so far
        private void FlushAll()
        {
            LandPaint.Flush();
            if (strokeMode == Mode.Textures) LandTextures.Flush();
            if (strokeMode == Mode.Shape || (strokeMode == Mode.Ground && strokeGround)) LandShape.Flush();
            if (strokeMode == Mode.Water) WaterPaint.Flush();
        }

        // While the mouse is held down with the Shape brush, it keeps changing the land (all but Ramp, made when let go)
        private void Tick()
        {
            if (!stroking || strokeMode != Mode.Shape || strokeShape == LandShape.Tool.Ramp) return;
            double now = EditorApplication.timeSinceStartup;
            float seconds = Mathf.Clamp((float)(now - lastTick), 0f, 0.05f);
            lastTick = now;
            if (seconds <= 0f) return;
            LandShape.Dab(shapeCentre, ShapeBrushNow(), seconds);
            LandShape.Flush();
            SceneView.RepaintAll();
        }

        private WaterPaint.Brush WaterBrushNow()
        {
            return new WaterPaint.Brush
            {
                Radius = waterBrush * 0.5f,
                Softness = waterSoftness,
                Shape = waterShape,
                Depth = waterDepth,
                Clear = waterClear,
                Spot = waterSpot,
                Recolour = recolour,
                MoveThings = moveThings,
            };
        }

        private LandShape.Brush ShapeBrushNow()
        {
            return new LandShape.Brush
            {
                Tool = strokeShape,
                Radius = shapeBrush * 0.5f,
                Strength = shapeStrength,
                Softness = shapeSoftness,
                Target = strokeTarget,
                NoiseSize = noiseSize,
                Recolour = recolour,
            };
        }

        // Which shape tool a stroke uses: Shift swaps Raise and Lower, and smooths with the others
        private LandShape.Tool ShapeToolNow(bool shift)
        {
            if (!shift || shapeTool == LandShape.Tool.Ramp) return shapeTool;
            if (shapeTool == LandShape.Tool.Raise) return LandShape.Tool.Lower;
            if (shapeTool == LandShape.Tool.Lower) return LandShape.Tool.Raise;
            return LandShape.Tool.Smooth;
        }

        // Where the mouse points at the land (worked out again only if the mouse or the camera moved)
        private void Aim(SceneView view, Vector2 mouse, bool force)
        {
            Matrix4x4 camera = view.camera.transform.localToWorldMatrix;
            if (!force && mouse == lastMouse && camera == lastCamera) return;
            lastMouse = mouse;
            lastCamera = camera;
            overLand = LandPaint.Raycast(HandleUtility.GUIPointToWorldRay(mouse), out hit);
        }

        private bool BeginStroke(bool erasing)
        {
            strokeMode = mode;
            strokeErases = erasing;
            if (mode == Mode.Textures)
            {
                Material material = LandTextures.Material;
                if (!LandTextures.Supported(material))
                {
                    problem = "The Land material's shader is out of date (Assets/Paint Land/Shaders/LandVertexColours.shader).";
                    Repaint();
                    return false;
                }
                strokeTriangles = textureTriangles;
                strokeLayer = erasing ? 0 : Mathf.Clamp(textureLayer, 1, LandTextures.Count);
                if (!strokeTriangles)
                {
                    if (erasing && !LandTextures.Exist) return false;   // (nothing to rub out)
                    string trouble = LandTextures.Load();
                    if (trouble != null)
                    {
                        problem = trouble;
                        Repaint();
                        return false;
                    }
                }
                if (problem != null && problem.Contains("shader")) problem = null;
            }
            if (mode == Mode.Water)
            {
                LandPaint.OpenUndoGroup(erasing ? "Rub Out Water" : "Paint Water");
                string trouble = WaterPaint.BeginStroke(hit.Point, WaterBrushNow(), erasing, selectedWater);
                if (trouble != null)
                {
                    LandPaint.CloseUndoGroup();
                    if (trouble.Length > 0) problem = trouble;
                    Repaint();
                    return false;
                }
                selectedWater = WaterPaint.Lake;
                lastWaterDab = hit.Point;
            }
            strokeGround = mode == Mode.Ground && groundMode;
            if (strokeGround)
            {
                string trouble = LandShape.Prepare();
                if (trouble != null)
                {
                    problem = trouble;
                    Repaint();
                    return false;
                }
                LandShape.BeginStroke(erasing ? "Rub Out Ground Colour" : "Paint Ground", false);
            }
            if (mode == Mode.Shape)
            {
                string trouble = LandShape.Prepare();
                if (trouble != null)
                {
                    problem = trouble;
                    Repaint();
                    return false;
                }
                strokeShape = ShapeToolNow(Event.current != null && Event.current.shift);
                strokeTarget = flattenAtStart ? hit.Local.y : flattenHeight;
                string name = strokeShape switch
                {
                    LandShape.Tool.Raise => "Raise Land",
                    LandShape.Tool.Lower => "Lower Land",
                    LandShape.Tool.Smooth => "Smooth Land",
                    LandShape.Tool.Flatten => "Flatten Land",
                    LandShape.Tool.Ramp => "Ramp",
                    LandShape.Tool.Noise => "Roughen Land",
                    _ => "Restore Land",
                };
                LandPaint.OpenUndoGroup(name);
                LandShape.BeginStroke(name, moveThings);
                lastTick = EditorApplication.timeSinceStartup;
            }
            if (mode == Mode.Grass)
            {
                strokeField = GrassPaint.Find();
                string trouble = strokeField == null ? "Add a Grass Field first (the button in the Grass tab)." : GrassPaint.PrepareMask(strokeField);
                if (trouble != null)
                {
                    problem = trouble;
                    Repaint();
                    return false;
                }
                strokeGrass = GrassBrush(erasing);
            }
            if (mode == Mode.Scatter)
            {
                LandScatter.AddColliders = scatterColliders;
                string trouble = LandScatter.Begin(erasing);
                if (trouble != null)
                {
                    problem = trouble;
                    Repaint();
                    return false;
                }
                if (problem != null && problem.StartsWith("Tick")) problem = null;
            }
            if (mode == Mode.Paths)
            {
                if (erasing && !LandPaint.PathsExist) return false;   // (nothing to rub out)
                string trouble = LandPaint.LoadPaths();
                if (trouble != null)
                {
                    problem = trouble;
                    Repaint();
                    return false;
                }

                // A new path deletes the ticked kinds of things under it (one Ctrl+Z brings back the lot)
                clearingThings = !erasing && LandScatter.BeginClearing(LandBuilder.GetSettings());
                if (clearingThings) LandPaint.OpenUndoGroup("Paint Path");
            }
            stroking = true;
            LandPaint.Busy = true;
            return true;
        }

        private void EndStroke()
        {
            if (strokeMode == Mode.Scatter) LandScatter.End();
            if (strokeMode == Mode.Textures && stroking && !strokeTriangles)
            {
                LandTextures.EndStroke(strokeLayer == 0 ? "Rub Out Textures" : "Paint Texture");
            }
            if (strokeMode == Mode.Shape && stroking)
            {
                if (strokeShape == LandShape.Tool.Ramp) LandShape.Ramp(rampFrom, rampTo, ShapeBrushNow());
                LandShape.EndStroke();
            }
            if (strokeMode == Mode.Ground && strokeGround && stroking)
            {
                LandShape.EndStroke();
                strokeGround = false;
            }
            if (strokeMode == Mode.Water && stroking) WaterPaint.EndStroke();
            if (clearingThings)
            {
                LandScatter.EndClearing();
                clearingThings = false;
            }
            if (strokeMode == Mode.Grass && stroking)
            {
                GrassPaint.EndStroke(strokeGrass == GrassPaint.Brush.Add ? "Add Grass" : strokeGrass == GrassPaint.Brush.Remove ? "Remove Grass" : "Grass Back To Automatic");
            }
            stroking = false;
            strokeView = null;
            LandPaint.EndStroke();
            Repaint();
        }

        // Paints at the mouse, and along the way from where it last painted (unless that's a jump, like from one hill
        // across to another behind it)
        private void Dab(LandPaint.Hit at, bool first)
        {
            Color32 paint = colour;
            paint.a = 255;
            if (strokeMode == Mode.Shape)
            {
                // (the brush keeps working where it is while the mouse is held: see Tick)
                shapeCentre = new Vector2(at.Local.x, at.Local.z);
                if (first) rampFrom = shapeCentre;
                rampTo = shapeCentre;
                return;
            }
            if (strokeMode == Mode.Water)
            {
                float radius = waterBrush * 0.5f;
                float length = first ? 0f : Vector3.Distance(new Vector3(lastWaterDab.x, 0f, lastWaterDab.z), new Vector3(at.Point.x, 0f, at.Point.z));
                if (length <= 0f || length > radius * 8f + 10f)
                {
                    WaterPaint.Dab(at.Point);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.3f, 0.5f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 400);
                    for (int k = 1; k <= count; k++) WaterPaint.Dab(Vector3.Lerp(lastWaterDab, at.Point, k / (float)count));
                }
                lastWaterDab = at.Point;
                lastDab = at.Point;
                return;
            }
            if (strokeMode == Mode.Ground && strokeGround)
            {
                var point = new Vector2(at.Local.x, at.Local.z);
                float radius = groundBrush * 0.5f;
                float length = first ? 0f : Vector2.Distance(lastGroundDab, point);
                if (length <= 0f || length > radius * 8f + 10f)
                {
                    LandShape.PaintGround(point, radius, paint, groundStrength, groundSoftness, strokeErases);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.25f, 0.5f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++)
                    {
                        LandShape.PaintGround(Vector2.Lerp(lastGroundDab, point, k / (float)count), radius, paint, groundStrength, groundSoftness, strokeErases);
                    }
                }
                lastGroundDab = point;
                lastDab = at.Point;
                return;
            }
            if (strokeMode == Mode.Textures && strokeTriangles)
            {
                float radius = textureTriangleBrush * 0.5f;
                float length = first ? 0f : Vector3.Distance(lastDab, at.Point);
                if (length <= 0f || length > radius * 8f + at.Piece.CellSize * 3f)
                {
                    TextureNear(at.Point, radius);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.5f, at.Piece.CellSize * 0.25f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++) TextureNear(Vector3.Lerp(lastDab, at.Point, k / (float)count), radius);
                }
                lastDab = at.Point;
                return;
            }
            if (strokeMode == Mode.Textures)
            {
                var point = new Vector2(at.Local.x, at.Local.z);
                float radius = textureBrush * 0.5f;
                float length = first ? 0f : Vector2.Distance(lastTextureDab, point);
                if (length <= 0f || length > radius * 8f + 6f)
                {
                    LandTextures.Stamp(point, radius, strokeLayer, textureOpacity, textureSoftness);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.2f, 0.1f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++) LandTextures.Stamp(Vector2.Lerp(lastTextureDab, point, k / (float)count), radius, strokeLayer, textureOpacity, textureSoftness);
                }
                lastTextureDab = point;
                lastDab = at.Point;
                return;
            }
            if (strokeMode == Mode.Paths)
            {
                var point = new Vector2(at.Local.x, at.Local.z);
                float radius = pathWidth * 0.5f;
                float length = first ? 0f : Vector2.Distance(lastPathDab, point);
                if (length <= 0f || length > radius * 8f + 6f)
                {
                    LandPaint.StampPath(point, radius, paint, strokeErases);
                    if (clearingThings) LandScatter.ClearAt(OnLand(point), radius);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.25f, 0.05f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++)
                    {
                        Vector2 along = Vector2.Lerp(lastPathDab, point, k / (float)count);
                        LandPaint.StampPath(along, radius, paint, strokeErases);
                        if (clearingThings) LandScatter.ClearAt(OnLand(along), radius);
                    }
                }
                lastPathDab = point;
            }
            else if (strokeMode == Mode.Grass)
            {
                if (strokeField == null) return;
                var point = new Vector2(at.Local.x, at.Local.z);
                float radius = grassBrush * 0.5f;
                float length = first ? 0f : Vector2.Distance(lastGrassDab, point);
                if (length <= 0f || length > radius * 8f + 6f)
                {
                    GrassPaint.Stamp(strokeField, point, radius, strokeGrass, grassAmount);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.25f, 0.25f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++)
                    {
                        GrassPaint.Stamp(strokeField, Vector2.Lerp(lastGrassDab, point, k / (float)count), radius, strokeGrass, grassAmount);
                    }
                }
                lastGrassDab = point;
            }
            else if (strokeMode == Mode.Scatter)
            {
                // A dab every half a brush along the way, so each covers new ground
                float radius = scatterBrush * 0.5f;
                Vector3 flatFrom = new Vector3(lastScatterDab.x, 0f, lastScatterDab.z), flatTo = new Vector3(at.Point.x, 0f, at.Point.z);
                float length = first ? 0f : Vector3.Distance(flatFrom, flatTo);
                float step = Mathf.Max(radius * 0.5f, 0.25f);
                if (first || length > radius * 8f + 10f)
                {
                    LandScatter.Dab(at.Point, radius, strokeErases);
                    lastScatterDab = at.Point;
                }
                else
                {
                    for (int k = 0; length >= step && k < 400; k++)
                    {
                        lastScatterDab = Vector3.MoveTowards(lastScatterDab, at.Point, step);
                        LandScatter.Dab(lastScatterDab, radius, strokeErases);
                        length -= step;
                    }
                }
            }
            else
            {
                float radius = triangleBrush * 0.5f;
                Color32 fill = strokeErases ? default : paint;
                float length = first ? 0f : Vector3.Distance(lastDab, at.Point);
                if (length <= 0f || length > radius * 8f + at.Piece.CellSize * 3f)
                {
                    ColourNear(at.Point, radius, fill);
                }
                else
                {
                    float step = Mathf.Max(radius * 0.5f, at.Piece.CellSize * 0.25f);
                    int count = Mathf.Clamp(Mathf.CeilToInt(length / step), 1, 2000);
                    for (int k = 1; k <= count; k++) ColourNear(Vector3.Lerp(lastDab, at.Point, k / (float)count), radius, fill);
                }
            }
            lastDab = at.Point;
        }

        // A point on the land (x, z in its own space) in the world
        private static Vector3 OnLand(Vector2 local)
        {
            Vector3 point = new Vector3(local.x, 0f, local.y);
            return LandPaint.Land != null ? LandPaint.Land.transform.TransformPoint(point) : point;
        }

        private void ColourNear(Vector3 world, float radius, Color32 fill)
        {
            near.Clear();
            LandPaint.TrianglesNear(world, radius, near);
            foreach ((LandPaint.Piece piece, int triangle) in near) LandPaint.PaintTriangle(piece, triangle, fill);
        }

        private void TextureNear(Vector3 world, float radius)
        {
            near.Clear();
            LandPaint.TrianglesNear(world, radius, near);
            foreach ((LandPaint.Piece piece, int triangle) in near) LandPaint.PaintTriangleTexture(piece, triangle, strokeLayer);
        }

        private void Resize(bool bigger)
        {
            if (mode == Mode.Water)
            {
                waterBrush = Mathf.Clamp(waterBrush * (bigger ? 1.25f : 0.8f), 2f, 200f);
            }
            else if (mode == Mode.Shape)
            {
                shapeBrush = Mathf.Clamp(shapeBrush * (bigger ? 1.25f : 0.8f), 2f, MaxShapeBrush);
            }
            else if (mode == Mode.Ground && groundMode)
            {
                groundBrush = Mathf.Clamp(groundBrush * (bigger ? 1.25f : 0.8f), 2f, MaxGroundBrush);
            }
            else if (mode == Mode.Textures && !textureTriangles)
            {
                textureBrush = Mathf.Clamp(textureBrush * (bigger ? 1.25f : 0.8f), 1f, MaxTextureBrush);
            }
            else if (mode == Mode.Textures)
            {
                textureTriangleBrush = bigger
                    ? (textureTriangleBrush < 1f ? 4f : Mathf.Min(textureTriangleBrush * 1.25f, MaxTriangleBrush))
                    : (textureTriangleBrush <= 4f ? 0f : textureTriangleBrush * 0.8f);
            }
            else if (mode == Mode.Grass)
            {
                grassBrush = Mathf.Clamp(grassBrush * (bigger ? 1.25f : 0.8f), 1f, MaxGrassBrush);
            }
            else if (mode == Mode.Scatter)
            {
                scatterBrush = Mathf.Clamp(scatterBrush * (bigger ? 1.25f : 0.8f), 1f, MaxScatterBrush);
            }
            else if (mode == Mode.Paths)
            {
                pathWidth = Mathf.Clamp(pathWidth * (bigger ? 1.25f : 0.8f), 0.5f, MaxPathWidth);
            }
            else if (bigger)
            {
                triangleBrush = triangleBrush < 1f ? 4f : Mathf.Min(triangleBrush * 1.25f, MaxTriangleBrush);
            }
            else
            {
                triangleBrush = triangleBrush <= 4f ? 0f : triangleBrush * 0.8f;
            }
            SavePrefs();
            Repaint();
        }

        // The brush: a circle lying on the ground (filled with the colour), or the triangles it would colour in
        private void DrawBrush(bool erasing)
        {
            Color c = erasing ? new Color(1f, 0.35f, 0.3f) : colour;
            c.a = 1f;
            CompareFunction zTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;

            if (mode == Mode.Water)
            {
                DrawWaterOutline();
                Color ring = erasing ? new Color(1f, 0.35f, 0.3f) : new Color(0.35f, 0.7f, 1f);
                Circle(hit.Point, waterBrush * 0.5f);
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.15f);
                Vector3 middle = hit.Point + Lift;
                for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                DrawOutline(ring);
                if (waterSoftness > 0.05f && waterSoftness < 0.95f)
                {
                    Circle(hit.Point, waterBrush * 0.5f * (1f - waterSoftness));
                    Handles.color = new Color(ring.r, ring.g, ring.b, 0.6f);
                    Handles.DrawAAPolyLine(1.5f, outline.ToArray());
                }
            }
            else if (mode == Mode.Shape)
            {
                LandShape.Tool tool = stroking && strokeMode == Mode.Shape ? strokeShape : ShapeToolNow(Event.current != null && Event.current.shift);
                Color ring = tool == LandShape.Tool.Raise ? new Color(0.45f, 1f, 0.45f) : tool == LandShape.Tool.Lower ? new Color(1f, 0.45f, 0.35f)
                           : tool == LandShape.Tool.Smooth ? new Color(0.5f, 0.75f, 1f) : tool == LandShape.Tool.Flatten ? new Color(1f, 0.85f, 0.35f)
                           : tool == LandShape.Tool.Ramp ? new Color(1f, 0.6f, 0.2f) : tool == LandShape.Tool.Noise ? new Color(0.85f, 0.55f, 1f)
                           : new Color(0.85f, 0.85f, 0.85f);
                float radius = shapeBrush * 0.5f;
                if (tool == LandShape.Tool.Ramp && stroking && strokeMode == Mode.Shape)
                {
                    // The ramp so far: from where the drag started to the mouse, as wide as the brush
                    Vector3 from = OnGround(rampFrom), to = OnGround(rampTo);
                    Handles.color = new Color(0f, 0f, 0f, 0.55f);
                    Handles.DrawAAPolyLine(6f, from + Lift, to + Lift);
                    Handles.color = ring;
                    Handles.DrawAAPolyLine(3f, from + Lift, to + Lift);
                    Circle(from, radius);
                    DrawOutline(ring);
                    Circle(to, radius);
                    DrawOutline(ring);
                }
                else
                {
                    Circle(hit.Point, radius);
                    Handles.color = new Color(ring.r, ring.g, ring.b, 0.1f);
                    Vector3 middle = hit.Point + Lift;
                    for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                    DrawOutline(ring);
                    if (shapeSoftness > 0.05f && shapeSoftness < 0.95f)
                    {
                        Circle(hit.Point, radius * (1f - shapeSoftness));
                        Handles.color = new Color(ring.r, ring.g, ring.b, 0.6f);
                        Handles.DrawAAPolyLine(1.5f, outline.ToArray());
                    }
                }
            }
            else if (mode == Mode.Ground && groundMode)
            {
                Circle(hit.Point, groundBrush * 0.5f);
                if (!erasing)
                {
                    Handles.color = new Color(c.r, c.g, c.b, 0.3f);
                    Vector3 middle = hit.Point + Lift;
                    for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                }
                DrawOutline(c);
                if (groundSoftness > 0.05f && groundSoftness < 0.95f)
                {
                    Circle(hit.Point, groundBrush * 0.5f * (1f - groundSoftness));
                    Handles.color = new Color(c.r, c.g, c.b, 0.6f);
                    Handles.DrawAAPolyLine(1.5f, outline.ToArray());
                }
            }
            else if (mode == Mode.Textures && !textureTriangles)
            {
                Color ring = erasing ? new Color(1f, 0.35f, 0.3f) : new Color(0.95f, 0.95f, 0.95f);
                Circle(hit.Point, textureBrush * 0.5f);
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.12f);
                Vector3 middle = hit.Point + Lift;
                for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                DrawOutline(ring);
                if (textureSoftness > 0.05f && textureSoftness < 0.95f)
                {
                    Circle(hit.Point, textureBrush * 0.5f * (1f - textureSoftness));
                    Handles.color = new Color(ring.r, ring.g, ring.b, 0.6f);
                    Handles.DrawAAPolyLine(1.5f, outline.ToArray());
                }
            }
            else if (mode == Mode.Textures)
            {
                Color fill = erasing ? new Color(1f, 0.35f, 0.3f) : new Color(0.95f, 0.95f, 0.95f);
                near.Clear();
                LandPaint.TrianglesNear(hit.Point, textureTriangleBrush * 0.5f, near);
                int shown = Mathf.Min(near.Count, 800);
                Handles.color = new Color(fill.r, fill.g, fill.b, 0.35f);
                for (int i = 0; i < shown; i++)
                {
                    Corners(near[i], out Vector3 a, out Vector3 b, out Vector3 d);
                    Handles.DrawAAConvexPolygon(a, b, d);
                }
                Handles.color = new Color(0f, 0f, 0f, 0.6f);
                for (int i = 0; i < shown; i++)
                {
                    Corners(near[i], out Vector3 a, out Vector3 b, out Vector3 d);
                    Handles.DrawAAPolyLine(2f, a, b, d, a);
                }
                if (textureTriangleBrush > 0f)
                {
                    Circle(hit.Point, textureTriangleBrush * 0.5f);
                    DrawOutline(fill);
                }
            }
            else if (mode == Mode.Grass)
            {
                GrassPaint.Brush brush = GrassBrush(erasing);
                Color ring = brush == GrassPaint.Brush.Add ? new Color(0.45f, 1f, 0.45f)
                           : brush == GrassPaint.Brush.Remove ? new Color(1f, 0.35f, 0.3f) : new Color(0.5f, 0.75f, 1f);
                Circle(hit.Point, grassBrush * 0.5f);
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.12f);
                Vector3 middle = hit.Point + Lift;
                for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                DrawOutline(ring);
            }
            else if (mode == Mode.Scatter)
            {
                Color ring = erasing ? new Color(1f, 0.35f, 0.3f) : new Color(0.45f, 1f, 0.45f);
                Circle(hit.Point, scatterBrush * 0.5f);
                Handles.color = new Color(ring.r, ring.g, ring.b, 0.12f);
                Vector3 middle = hit.Point + Lift;
                for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                DrawOutline(ring);
            }
            else if (mode == Mode.Paths)
            {
                Circle(hit.Point, pathWidth * 0.5f);
                if (!erasing)
                {
                    Handles.color = new Color(c.r, c.g, c.b, 0.35f);
                    Vector3 middle = hit.Point + Lift;
                    for (int i = 0; i + 1 < outline.Count; i++) Handles.DrawAAConvexPolygon(middle, outline[i], outline[i + 1]);
                }
                DrawOutline(c);
            }
            else
            {
                near.Clear();
                LandPaint.TrianglesNear(hit.Point, triangleBrush * 0.5f, near);
                int shown = Mathf.Min(near.Count, 800);
                Handles.color = new Color(c.r, c.g, c.b, erasing ? 0.3f : 0.65f);
                for (int i = 0; i < shown; i++)
                {
                    Corners(near[i], out Vector3 a, out Vector3 b, out Vector3 d);
                    Handles.DrawAAConvexPolygon(a, b, d);
                }
                Handles.color = new Color(0f, 0f, 0f, 0.6f);
                for (int i = 0; i < shown; i++)
                {
                    Corners(near[i], out Vector3 a, out Vector3 b, out Vector3 d);
                    Handles.DrawAAPolyLine(2f, a, b, d, a);
                }
                if (triangleBrush > 0f)
                {
                    Circle(hit.Point, triangleBrush * 0.5f);
                    DrawOutline(c);
                }
            }
            Handles.zTest = zTest;
        }

        // A point on the land (x, z in its space) on the ground, in the world
        private static Vector3 OnGround(Vector2 local)
        {
            Vector3 world = OnLand(local);
            return LandPaint.GroundAt(world, out _, out _, out Vector3 ground) ? ground : world;
        }

        private static void Corners((LandPaint.Piece piece, int triangle) which, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            Transform t = which.piece.Transform;
            Vector3[] points = which.piece.Points;
            int v = which.triangle * 3;
            a = t.TransformPoint(points[v]) + Lift;
            b = t.TransformPoint(points[v + 1]) + Lift;
            c = t.TransformPoint(points[v + 2]) + Lift;
        }

        private void Circle(Vector3 centre, float radius)
        {
            const int segments = 48;
            outline.Clear();
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector3 point = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                if (LandPaint.GroundAt(point, out _, out _, out Vector3 ground)) point = ground;
                outline.Add(point + Lift);
            }
        }

        private void DrawOutline(Color c)
        {
            Vector3[] points = outline.ToArray();
            Handles.color = new Color(0f, 0f, 0f, 0.55f);
            Handles.DrawAAPolyLine(5f, points);
            Handles.color = c;
            Handles.DrawAAPolyLine(2.5f, points);
        }

        private void DrawStatus(SceneView view, bool erasing)
        {
            GrassPaint.Brush grassBrushNow = GrassBrush(erasing);
            LandShape.Tool toolNow = stroking && strokeMode == Mode.Shape ? strokeShape : ShapeToolNow(Event.current != null && Event.current.shift);
            string what = mode == Mode.Water
                ? (erasing ? "Rubbing out water" : "Painting water") + $" (brush {waterBrush:0.#} m)" + (waterShape && !erasing ? $", digging {waterDepth:0.#} m deep" : string.Empty)
                : mode == Mode.Shape
                ? (toolNow == LandShape.Tool.Ramp ? $"Ramp {shapeBrush:0.#} m wide (drag from one end to the other)"
                   : $"{ShapeTools[(int)toolNow].text} the land (brush {shapeBrush:0.#} m)") + (moveThings ? ", moving plants with it" : string.Empty)
                : mode == Mode.Textures
                ? (erasing ? "Rubbing out textures" : "Painting " + LandTextures.Name(LandBuilder.GetSettings(), LandTextures.Material, textureLayer))
                  + (textureTriangles ? (textureTriangleBrush > 0f ? $" on triangles, brush {textureTriangleBrush:0.#} m" : " on one triangle at a time")
                                      : $" (brush {textureBrush:0.#} m)")
                : mode == Mode.Ground && groundMode
                ? (erasing ? "Rubbing ground colours out" : "Painting the ground's colour") + $" (brush {groundBrush:0.#} m)"
                : mode == Mode.Grass
                ? (grassBrushNow == GrassPaint.Brush.Add ? "Adding grass" : grassBrushNow == GrassPaint.Brush.Remove ? "Removing grass" : "Grass back to automatic")
                  + $" (brush {grassBrush:0.#} m)"
                : mode == Mode.Scatter
                ? (erasing ? "Taking away " : "Scattering ") + TickedNames() + $" (brush {scatterBrush:0.#} m)"
                : mode == Mode.Paths
                ? (erasing ? "Rubbing out paths" : "Painting paths") + $" {pathWidth:0.#} m wide" + (erasing ? string.Empty : ClearedNames())
                : (erasing ? "Taking colour off triangles" : "Colouring triangles") + (triangleBrush > 0f ? $", brush {triangleBrush:0.#} m" : ", one at a time");
            string text = $"{what}    ·    Esc to stop";
            if (pickingColour) text = "Click the land to pick its colour   (Esc: cancel)";
            if (pickingFor >= 0)
            {
                List<LandScatterGroup> kinds = LandBuilder.GetSettings().scatter;
                string kind = pickingFor < kinds.Count && kinds[pickingFor] != null ? kinds[pickingFor].name : "it";
                text = $"Click the land to pick its colour for {kind}   (Esc: cancel)";
            }
            statusStyle ??= new GUIStyle(EditorStyles.helpBox) { wordWrap = false, alignment = TextAnchor.MiddleCenter };
            Handles.BeginGUI();
            var content = new GUIContent(text);
            Vector2 size = statusStyle.CalcSize(content);
            float width = view.position.width;
            var r = new Rect(Mathf.Max(4f, (width - size.x) * 0.5f), 6f, size.x + 8f, size.y + 4f);
            GUI.Box(r, content, statusStyle);
            Handles.EndGUI();
        }

        private static string TickedNames()
        {
            var names = new List<string>();
            foreach (LandScatterGroup kind in LandBuilder.GetSettings().scatter)
            {
                if (kind != null && kind.on) names.Add(kind.name);
            }
            if (names.Count == 0) return "nothing (tick something in Paint Land)";
            return names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.GetRange(0, 3)) + $" and {names.Count - 3} more";
        }

        // ================================================================ Remembered between sessions

        private void LoadPrefs()
        {
            mode = (Mode)Mathf.Clamp(EditorPrefs.GetInt(Prefs + "Tab2", 0), 0, 6);
            waterBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "WaterBrush", 24f), 2f, 200f);
            waterSoftness = Mathf.Clamp01(EditorPrefs.GetFloat(Prefs + "WaterSoftness", 0.4f));
            waterShape = EditorPrefs.GetBool(Prefs + "WaterShape", true);
            waterDepth = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "WaterDepth", 3f), 0.5f, 30f);
            waterClear = EditorPrefs.GetBool(Prefs + "WaterClear", true);
            waterSpot = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "WaterSpot", 1f), 0.25f, 4f);
            textureLayer = Mathf.Clamp(EditorPrefs.GetInt(Prefs + "TextureLayer", 1), 1, LandTextures.Count);
            textureTriangles = EditorPrefs.GetBool(Prefs + "TextureTriangles", false);
            textureBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "TextureBrush", 8f), 1f, MaxTextureBrush);
            textureOpacity = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "TextureOpacity", 1f), 0.05f, 1f);
            textureSoftness = Mathf.Clamp01(EditorPrefs.GetFloat(Prefs + "TextureSoftness", 0.5f));
            textureTriangleBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "TextureTriangleBrush", 0f), 0f, MaxTriangleBrush);
            shapeTool = (LandShape.Tool)Mathf.Clamp(EditorPrefs.GetInt(Prefs + "ShapeTool", 0), 0, 6);
            shapeBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "ShapeBrush", 40f), 2f, MaxShapeBrush);
            shapeStrength = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "ShapeStrength", 0.3f), 0.01f, 1f);
            shapeSoftness = Mathf.Clamp01(EditorPrefs.GetFloat(Prefs + "ShapeSoftness", 0.6f));
            flattenAtStart = EditorPrefs.GetBool(Prefs + "FlattenAtStart", true);
            flattenHeight = EditorPrefs.GetFloat(Prefs + "FlattenHeight", 0f);
            noiseSize = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "NoiseSize", 24f), 4f, 200f);
            moveThings = EditorPrefs.GetBool(Prefs + "MoveThings", true);
            groundMode = EditorPrefs.GetBool(Prefs + "GroundSmooth", true);
            groundBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "GroundBrush", 30f), 2f, MaxGroundBrush);
            groundStrength = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "GroundStrength", 0.6f), 0.05f, 1f);
            groundSoftness = Mathf.Clamp01(EditorPrefs.GetFloat(Prefs + "GroundSoftness", 0.6f));
            recolour = EditorPrefs.GetBool(Prefs + "Recolour", true);
            scatterColliders = EditorPrefs.GetBool(Prefs + "ScatterColliders", true);
            grassBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "GrassBrush", 12f), 1f, MaxGrassBrush);
            grassKind = (GrassPaint.Brush)Mathf.Clamp(EditorPrefs.GetInt(Prefs + "GrassKind", 0), 0, 2);
            grassAmount = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "GrassAmount", 1f), 0.05f, 1f);
            scatterBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "ScatterBrush", 10f), 1f, MaxScatterBrush);
            pathWidth = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "PathWidth", 3f), 0.5f, MaxPathWidth);
            triangleBrush = Mathf.Clamp(EditorPrefs.GetFloat(Prefs + "TriangleBrush", 0f), 0f, MaxTriangleBrush);
            eraser = EditorPrefs.GetBool(Prefs + "Eraser", false);
            if (ColorUtility.TryParseHtmlString("#" + EditorPrefs.GetString(Prefs + "Colour", "9E6B45"), out Color c)) colour = c;
        }

        private void SavePrefs()
        {
            EditorPrefs.SetInt(Prefs + "Tab2", (int)mode);
            EditorPrefs.SetFloat(Prefs + "WaterBrush", waterBrush);
            EditorPrefs.SetFloat(Prefs + "WaterSoftness", waterSoftness);
            EditorPrefs.SetBool(Prefs + "WaterShape", waterShape);
            EditorPrefs.SetFloat(Prefs + "WaterDepth", waterDepth);
            EditorPrefs.SetBool(Prefs + "WaterClear", waterClear);
            EditorPrefs.SetFloat(Prefs + "WaterSpot", waterSpot);
            EditorPrefs.SetInt(Prefs + "TextureLayer", textureLayer);
            EditorPrefs.SetBool(Prefs + "TextureTriangles", textureTriangles);
            EditorPrefs.SetFloat(Prefs + "TextureBrush", textureBrush);
            EditorPrefs.SetFloat(Prefs + "TextureOpacity", textureOpacity);
            EditorPrefs.SetFloat(Prefs + "TextureSoftness", textureSoftness);
            EditorPrefs.SetFloat(Prefs + "TextureTriangleBrush", textureTriangleBrush);
            EditorPrefs.SetInt(Prefs + "ShapeTool", (int)shapeTool);
            EditorPrefs.SetFloat(Prefs + "ShapeBrush", shapeBrush);
            EditorPrefs.SetFloat(Prefs + "ShapeStrength", shapeStrength);
            EditorPrefs.SetFloat(Prefs + "ShapeSoftness", shapeSoftness);
            EditorPrefs.SetBool(Prefs + "FlattenAtStart", flattenAtStart);
            EditorPrefs.SetFloat(Prefs + "FlattenHeight", flattenHeight);
            EditorPrefs.SetFloat(Prefs + "NoiseSize", noiseSize);
            EditorPrefs.SetBool(Prefs + "MoveThings", moveThings);
            EditorPrefs.SetBool(Prefs + "GroundSmooth", groundMode);
            EditorPrefs.SetFloat(Prefs + "GroundBrush", groundBrush);
            EditorPrefs.SetFloat(Prefs + "GroundStrength", groundStrength);
            EditorPrefs.SetFloat(Prefs + "GroundSoftness", groundSoftness);
            EditorPrefs.SetBool(Prefs + "Recolour", recolour);
            EditorPrefs.SetBool(Prefs + "ScatterColliders", scatterColliders);
            EditorPrefs.SetFloat(Prefs + "PathWidth", pathWidth);
            EditorPrefs.SetFloat(Prefs + "TriangleBrush", triangleBrush);
            EditorPrefs.SetFloat(Prefs + "ScatterBrush", scatterBrush);
            EditorPrefs.SetFloat(Prefs + "GrassBrush", grassBrush);
            EditorPrefs.SetInt(Prefs + "GrassKind", (int)grassKind);
            EditorPrefs.SetFloat(Prefs + "GrassAmount", grassAmount);
            EditorPrefs.SetBool(Prefs + "Eraser", eraser);
            EditorPrefs.SetString(Prefs + "Colour", ColorUtility.ToHtmlStringRGB(colour));
        }
    }
}
#endif
