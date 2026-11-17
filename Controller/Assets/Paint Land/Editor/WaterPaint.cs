#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace YaraJ.PaintLand
{
    /// <summary>
    /// Editor only. Paint Land > Water's brush: paints lakes. Where you start a stroke sets the water's level (or, starting
    /// on a lake painted before, the stroke adds to that lake at its level); the water shows where it's painted, wherever
    /// the land is lower than that level. As you paint, it can shape the land to hold the water (LandShape.HoldWater: the
    /// bed dug out under it, and a flat shore just under the water's level round it, so the edge you see is the smooth
    /// edge you painted, not the land's triangles crossing the water), and delete the plants, trees and rocks under it.
    /// A lake grows as you paint past its edge. Shift rubs water out. Ctrl+Z undoes a stroke (the water, the shape and
    /// anything deleted together). Smooth The Shore (TidyLake) tidies a lake painted before.
    /// </summary>
    internal static class WaterPaint
    {
        /// <summary>How the brush paints.</summary>
        internal struct Brush
        {
            public float Radius;        // metres
            public float Softness;      // 0 = hard edge, 1 = soft all the way in
            public bool Shape;          // shape the land to hold the water
            public float Depth;         // how deep it digs in the middle (metres)
            public bool Clear;          // delete the plants, trees and rocks under it
            public float Spot;          // metres per spot of a new lake's map
            public bool Recolour;       // the ground's colours worked out again where it's shaped
            public bool MoveThings;     // trees, rocks and plants on the shore go up or down with it
        }

        private const int MaxSpots = 4096;          // a lake's map is at most this many spots across
        private static LandWater lake;
        private static Brush brush;
        private static bool erasing, clearing, shaping;
        private static int originX, originZ;        // the lake's first spot, counted from where the stroke started
        private static readonly Dictionary<long, float> reached = new Dictionary<long, float>();
        private static readonly Dictionary<long, byte> before = new Dictionary<long, byte>();
        private static byte[] strokeData;           // the lake's map being painted (to tell if Ctrl+Z swapped it)
        private static bool changed;
        private static double lastGrass, lastMips;
        private static Rect grassArea;              // where it's painted since the grass last heard (x, z in the world)
        private static bool grassPending;
        private static bool hooked;

        /// <summary>The lake being painted (null between strokes).</summary>
        internal static LandWater Lake => lake;

        /// <summary>
        /// Starts a stroke at a point on the land (in the world). Returns null if it's started, "" if there's nothing to do
        /// (rubbing out where there's no water), or what's wrong.
        /// </summary>
        internal static string BeginStroke(Vector3 start, Brush with, bool erase, LandWater chosen)
        {
            if (!hooked)
            {
                hooked = true;
                LandPaint.Cancelling += Cancel;
            }
            brush = with;
            erasing = erase;
            reached.Clear();
            before.Clear();
            originX = originZ = 0;
            changed = false;
            shaping = clearing = false;
            lake = FindLake(start, Mathf.Max(with.Radius, 1f), chosen);
            if (lake == null && erase) return string.Empty;
            string trouble = LandShape.Prepare();   // (for the land's height under the water)
            if (trouble != null)
            {
                lake = null;
                return trouble;
            }
            if (lake == null)
            {
                // A new lake: its surface a little above the ground where the stroke starts
                float spot = Mathf.Clamp(with.Spot, 0.25f, 8f);
                int spots = Mathf.Clamp(Mathf.CeilToInt((with.Radius * 2f + 16f) / spot), 8, MaxSpots);
                lake = WaterSetup.AddPainted(start + Vector3.up * 0.2f, spot, spots, spots);
                if (lake == null) return "Couldn't add the water (is the water shader in Assets/Paint Land/Shaders?).";
            }
            strokeData = lake.MaskData;
            shaping = brush.Shape && !erasing;
            if (shaping) LandShape.BeginStroke("Paint Water", brush.MoveThings);
            clearing = brush.Clear && !erasing && LandScatter.BeginClearing(LandBuilder.GetSettings(), true);
            return null;
        }

        // A lake painted before that the stroke starts on (or that's painted within the brush of it); the chosen one first
        private static LandWater FindLake(Vector3 start, float radius, LandWater chosen)
        {
            LandWater found = null;
            foreach (LandWater water in WaterSetup.All())
            {
                if (water == null || !water.Painted || !water.isActiveAndEnabled || !Near(water, start, radius)) continue;
                if (water == chosen) return water;
                if (found == null) found = water;
            }
            return found;
        }

        private static bool Near(LandWater water, Vector3 point, float radius)
        {
            if (water.MaskAt(point) >= 0.5f) return true;
            for (int k = 0; k < 8; k++)
            {
                float angle = k * Mathf.PI * 0.25f;
                if (water.MaskAt(point + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius) >= 0.5f) return true;
            }
            return false;
        }

        /// <summary>One dab of the brush at a point in the world.</summary>
        internal static void Dab(Vector3 centre)
        {
            if (lake == null || !lake.Painted) return;
            Resync();
            float radius = Mathf.Max(brush.Radius, 0.5f);
            Vector3 local = lake.transform.InverseTransformPoint(centre);
            var at = new Vector2(local.x, local.z);
            if (!erasing)
            {
                Grow(at, radius + 2f);
                local = lake.transform.InverseTransformPoint(centre);   // (it may have moved)
                at = new Vector2(local.x, local.z);
            }

            byte[] data = lake.MaskData;
            int w = lake.MaskWidth, h = lake.MaskHeight;
            float spot = lake.size.x / w, spotZ = lake.size.y / h;
            float left = -lake.size.x * 0.5f, back = -lake.size.y * 0.5f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((at.x - radius - left) / spot)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((at.x + radius - left) / spot));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((at.y - radius - back) / spotZ)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((at.y + radius - back) / spotZ));
            if (x0 > x1 || y0 > y1) return;
            var dabArea = new Rect(centre.x - radius, centre.z - radius, radius * 2f, radius * 2f);
            grassArea = grassPending ? Rect.MinMaxRect(Mathf.Min(grassArea.xMin, dabArea.xMin), Mathf.Min(grassArea.yMin, dabArea.yMin),
                                                       Mathf.Max(grassArea.xMax, dabArea.xMax), Mathf.Max(grassArea.yMax, dabArea.yMax))
                                     : dabArea;
            grassPending = true;

            // The water itself (each spot only ever as much as the brush has reached it this stroke, so it doesn't pile up)
            float soft = Mathf.Max(radius * Mathf.Clamp01(brush.Softness), spot);
            float inner = Mathf.Max(radius - soft, 0f);
            for (int y = y0; y <= y1; y++)
            {
                float dz = back + (y + 0.5f) * spotZ - at.y;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = left + (x + 0.5f) * spot - at.x;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distance >= radius) continue;
                    float t = distance <= inner ? 1f : Mathf.Clamp01((radius - distance) / Mathf.Max(radius - inner, 1e-4f));
                    t = t * t * (3f - 2f * t);
                    int i = y * w + x;
                    long key = Key(x, y);
                    if (reached.TryGetValue(key, out float had) && had >= t) continue;
                    reached[key] = t;
                    if (!before.TryGetValue(key, out byte was))
                    {
                        was = data[i];
                        before[key] = was;
                    }
                    float now = erasing ? was * (1f - t) : was + (255f - was) * t;
                    byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(now), 0, 255);
                    if (data[i] == value) continue;
                    data[i] = value;
                    changed = true;
                }
            }
            if (erasing) return;

            // The land under it and round its edge
            if (shaping)
            {
                Transform land = LandPaint.Land != null ? LandPaint.Land.transform : null;
                float level = land != null ? land.InverseTransformPoint(lake.transform.position).y : lake.Level;
                Vector3 landCentre = land != null ? land.InverseTransformPoint(centre) : centre;
                LandShape.HoldWater(new Vector2(landCentre.x, landCentre.z), radius + LandShape.Spacing * 0.5f, level, brush.Depth, WaterAtLand, brush.Recolour);
            }

            if (clearing) LandScatter.ClearAt(centre, radius + spot, InWater);
        }

        // Ctrl+Z in the middle of a stroke put the lake back as it was (its map swapped): what this stroke knew of it is
        // forgotten, and it carries on from there
        private static void Resync()
        {
            if (lake.MaskData == strokeData) return;
            before.Clear();
            reached.Clear();
            originX = originZ = 0;
            strokeData = lake.MaskData;
        }

        // Is something standing here (in the world: where it stood when the stroke started) in the lake being painted? (also
        // when the ground under it has been dug down below the water since)
        private static bool InWater(Vector3 standing)
        {
            if (lake == null || lake.MaskAt(standing) < 0.4f) return false;
            if (standing.y < lake.Level + 0.05f) return true;
            Transform land = LandPaint.Land != null ? LandPaint.Land.transform : null;
            if (land == null) return false;
            Vector3 onLand = land.InverseTransformPoint(standing);
            return LandShape.HeightAt(new Vector2(onLand.x, onLand.z)) < land.InverseTransformPoint(lake.transform.position).y + 0.05f;
        }

        // How much painted water there is at a point on the land (x, z in the land's space)
        private static float WaterAtLand(Vector2 onLand)
        {
            if (lake == null) return 0f;
            Vector3 point = new Vector3(onLand.x, 0f, onLand.y);
            if (LandPaint.Land != null) point = LandPaint.Land.transform.TransformPoint(point);
            return lake.MaskAt(point);
        }

        // A spot, counted from where the stroke started (so it stays the same spot when the lake grows)
        private static long Key(int x, int y) => ((long)(x + originX) << 32) ^ (uint)(y + originZ);

        // The spot a key is now (x, y on the lake's map as it is now)
        private static void FromKey(long key, out int x, out int y)
        {
            x = (int)(key >> 32) - originX;
            y = unchecked((int)(key & 0xFFFFFFFFL)) - originZ;
        }

        // The lake's map made bigger when the brush reaches past its edge (keeping what's painted, in the same place)
        private static void Grow(Vector2 at, float reach)
        {
            int w = lake.MaskWidth, h = lake.MaskHeight;
            float spot = lake.size.x / w, spotZ = lake.size.y / h;
            float left = -lake.size.x * 0.5f, back = -lake.size.y * 0.5f;
            int needX0 = Mathf.FloorToInt((at.x - reach - left) / spot), needX1 = Mathf.CeilToInt((at.x + reach - left) / spot);
            int needY0 = Mathf.FloorToInt((at.y - reach - back) / spotZ), needY1 = Mathf.CeilToInt((at.y + reach - back) / spotZ);
            if (needX0 >= 0 && needY0 >= 0 && needX1 < w && needY1 < h) return;

            // (a little more than needed, so it doesn't grow every dab)
            int margin = Mathf.CeilToInt(Mathf.Max(16f, reach) / spot);
            int addLeft = needX0 < 0 ? -needX0 + margin : 0, addRight = needX1 >= w ? needX1 - w + 1 + margin : 0;
            int addBack = needY0 < 0 ? -needY0 + margin : 0, addFront = needY1 >= h ? needY1 - h + 1 + margin : 0;
            int newW = Mathf.Min(w + addLeft + addRight, MaxSpots), newH = Mathf.Min(h + addBack + addFront, MaxSpots);
            addLeft = Mathf.Min(addLeft, newW - w);
            addBack = Mathf.Min(addBack, newH - h);
            if (newW == w && newH == h) return;   // (as big as it gets)

            // (Ctrl+Z puts its size and place back; what's painted is put back by the stroke's own step)
            Undo.RecordObjects(new Object[] { lake, lake.transform }, erasing ? "Rub Out Water" : "Paint Water");
            byte[] old = lake.MaskData;
            var grown = new byte[newW * newH];
            for (int y = 0; y < h; y++) System.Array.Copy(old, y * w, grown, (y + addBack) * newW + addLeft, w);

            // Its middle moves so the spots painted stay where they were
            float newLeft = left - addLeft * spot, newBack = back - addBack * spotZ;
            var middle = new Vector3(newLeft + newW * spot * 0.5f, 0f, newBack + newH * spotZ * 0.5f);
            lake.transform.position = lake.transform.TransformPoint(middle);
            lake.size = new Vector2(newW * spot, newH * spotZ);
            lake.SetMask(grown, newW, newH);
            lake.Refresh(false);   // (the new part has no water yet: the grass needn't be worked out again)
            strokeData = grown;
            originX -= addLeft;
            originZ -= addBack;
        }

        /// <summary>Shows what's been painted (call after each lot of dabs).</summary>
        internal static void Flush()
        {
            if (lake == null) return;
            Resync();
            if (changed)
            {
                changed = false;
                // (the far-off look of the map, its mipmaps, is worked out a few times a second, not every time)
                double now = EditorApplication.timeSinceStartup;
                bool mips = now - lastMips > 0.3;
                if (mips) lastMips = now;
                lake.ApplyMask(mips);
                if (now - lastGrass > 0.25)
                {
                    lastGrass = now;
                    TellGrass();
                }
            }
            if (shaping) LandShape.Flush();
        }

        // No grass under the water (where it's been painted since the grass last heard)
        private static void TellGrass()
        {
            if (!grassPending) return;
            grassPending = false;
            GrassOver(grassArea);
        }

        // The grass in an area (x, z in the world) is worked out again
        private static void GrassOver(Rect world)
        {
            Transform land = LandPaint.Land != null ? LandPaint.Land.transform : null;
            Vector3 a = new Vector3(world.xMin, 0f, world.yMin), b = new Vector3(world.xMax, 0f, world.yMax);
            if (land != null)
            {
                a = land.InverseTransformPoint(a);
                b = land.InverseTransformPoint(b);
            }
            GrassField.AreaChanged(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z)));
        }

        /// <summary>Finishes a stroke: the water and the land's shape changed for it are one step for Ctrl+Z.</summary>
        internal static void EndStroke()
        {
            LandPaint.Step waterStep = null;
            if (lake != null)
            {
                lake.ApplyMask();
                TellGrass();
                if (before.Count > 0)
                {
                    EditorUtility.SetDirty(lake);
                    EditorSceneManager.MarkSceneDirty(lake.gameObject.scene);
                }
                waterStep = MakeStep();
            }
            LandPaint.Step shapeStep = shaping ? LandShape.FinishStroke() : null;
            if (clearing) LandScatter.EndClearing();
            var step = new LandPaint.MultiStep(waterStep, shapeStep);
            if (step.Empty) LandPaint.CloseUndoGroup();
            else LandPaint.Commit(step, erasing ? "Rub Out Water" : "Paint Water");
            clearing = shaping = false;
            lake = null;
            strokeData = null;
            reached.Clear();
            before.Clear();
        }

        // The stroke's step for Ctrl+Z: each spot it changed (where it is in the world, so it's found again however the
        // lake grows later), with what it was and what it is now
        private static LandPaint.Step MakeStep()
        {
            if (before.Count == 0 || !lake.Painted || lake.MaskData != strokeData) return null;
            byte[] data = lake.MaskData;
            int w = lake.MaskWidth, h = lake.MaskHeight;
            float spot = lake.size.x / w, spotZ = lake.size.y / h;
            float left = -lake.size.x * 0.5f, back = -lake.size.y * 0.5f;
            var step = new WaterStep(lake);
            foreach (KeyValuePair<long, byte> entry in before)
            {
                FromKey(entry.Key, out int x, out int y);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                byte now = data[y * w + x];
                if (now == entry.Value) continue;
                step.Add(lake.transform.TransformPoint(new Vector3(left + (x + 0.5f) * spot, 0f, back + (y + 0.5f) * spotZ)), entry.Value, now);
            }
            return step.Count > 0 ? step : null;
        }

        // Ctrl+Z in the middle of a stroke: the water painted in it is put back
        private static void Cancel()
        {
            if (lake == null) return;
            if (lake.Painted && lake.MaskData == strokeData)
            {
                byte[] data = lake.MaskData;
                int w = lake.MaskWidth, h = lake.MaskHeight;
                foreach (KeyValuePair<long, byte> entry in before)
                {
                    FromKey(entry.Key, out int x, out int y);
                    if (x >= 0 && y >= 0 && x < w && y < h) data[y * w + x] = entry.Value;
                }
                lake.ApplyMask();
            }
            before.Clear();
            reached.Clear();
            changed = false;
        }

        // ================================================================ Smooth The Shore

        /// <summary>
        /// Tidies a painted lake (one step for Ctrl+Z): bits of water cut off from it are rubbed out, its edge is smoothed,
        /// and (shape) the land is shaped to it as the brush does now: the bed under it, a flat shore just under the water
        /// round it. Returns null if it's done, "" if there was nothing to change, or what's wrong.
        /// </summary>
        internal static string TidyLake(LandWater water, bool shape, float depth, bool recolour, bool moveThings)
        {
            if (water == null || !water.Painted) return "Choose a painted lake first.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Stop playing first to smooth the shore.";
            if (lake != null) return string.Empty;   // (in the middle of a stroke)
            string trouble = shape ? LandShape.Prepare() : null;
            if (trouble != null) return trouble;

            byte[] data = water.MaskData;
            int w = water.MaskWidth, h = water.MaskHeight;
            float spot = water.size.x / w, spotZ = water.size.y / h;
            float left = -water.size.x * 0.5f, back = -water.size.y * 0.5f;
            var old = (byte[])data.Clone();
            RemoveStrays(data, w, h);
            Smooth(data, w, h, Mathf.Clamp(Mathf.RoundToInt(1.25f / Mathf.Max(spot, 0.05f)), 1, 8));

            LandPaint.OpenUndoGroup("Smooth The Shore");
            var waterStep = new WaterStep(water);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (old[i] == data[i]) continue;
                    waterStep.Add(water.transform.TransformPoint(new Vector3(left + (x + 0.5f) * spot, 0f, back + (y + 0.5f) * spotZ)), old[i], data[i]);
                }
            }
            if (waterStep.Count > 0)
            {
                water.ApplyMask();
                EditorUtility.SetDirty(water);
                EditorSceneManager.MarkSceneDirty(water.gameObject.scene);
            }

            LandPaint.Step shapeStep = null;
            if (shape)
            {
                Transform land = LandPaint.Land != null ? LandPaint.Land.transform : null;
                float level = land != null ? land.InverseTransformPoint(water.transform.position).y : water.Level;
                Vector3 middle = land != null ? land.InverseTransformPoint(water.transform.position) : water.transform.position;
                LandShape.BeginStroke("Smooth The Shore", moveThings);
                lake = water;   // (for WaterAtLand)
                try
                {
                    var centre = new Vector2(middle.x, middle.z);
                    float reach = water.size.magnitude * 0.5f;
                    LandShape.HoldWater(centre, reach, level, depth, WaterAtLand, recolour);
                    // (where water was rubbed out, the hole dug for it isn't left dry and deep)
                    LandShape.FillDry(centre, reach, level, onLand => OldWaterAt(water, old, onLand), WaterAtLand, recolour);
                    shapeStep = LandShape.FinishStroke();
                }
                finally
                {
                    lake = null;
                }
            }

            var step = new LandPaint.MultiStep(waterStep.Count > 0 ? waterStep : null, shapeStep);
            if (step.Empty)
            {
                LandPaint.CloseUndoGroup();
                return string.Empty;
            }
            LandPaint.Commit(step, "Smooth The Shore");
            Vector3 c = water.transform.position;
            GrassOver(new Rect(c.x - water.size.x * 0.5f, c.z - water.size.y * 0.5f, water.size.x, water.size.y));
            return null;
        }

        // How much water there was at a point on the land (x, z in its space), on a lake's map from before it was changed
        // (the same size and place as its map now)
        private static float OldWaterAt(LandWater water, byte[] old, Vector2 onLand)
        {
            Vector3 world = new Vector3(onLand.x, 0f, onLand.y);
            if (LandPaint.Land != null) world = LandPaint.Land.transform.TransformPoint(world);
            Vector3 local = water.transform.InverseTransformPoint(world);
            int w = water.MaskWidth, h = water.MaskHeight;
            int x = Mathf.FloorToInt((local.x / water.size.x + 0.5f) * w), y = Mathf.FloorToInt((local.z / water.size.y + 0.5f) * h);
            if (x < 0 || y < 0 || x >= w || y >= h || old.Length != w * h) return 0f;
            return old[y * w + x] / 255f;
        }

        // Stray water (not joined to anything painted in full with the brush, like the patches the brush used to fill in
        // round its edge): rubbed out
        private static void RemoveStrays(byte[] data, int w, int h)
        {
            var joined = new bool[data.Length];
            var stack = new Stack<int>();
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] < 200 || joined[i]) continue;
                joined[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int j = stack.Pop();
                    int x = j % w, y = j / w;
                    if (x > 0) Join(j - 1);
                    if (x < w - 1) Join(j + 1);
                    if (y > 0) Join(j - w);
                    if (y < h - 1) Join(j + w);
                }
            }
            for (int i = 0; i < data.Length; i++)
            {
                if (!joined[i] && data[i] >= 128) data[i] = 0;
            }

            void Join(int k)
            {
                if (joined[k] || data[k] < 128) return;
                joined[k] = true;
                stack.Push(k);
            }
        }

        // The edge smoothed: the map blurred (twice, radius spots), then sharpened back up round the middle of its edge
        private static void Smooth(byte[] data, int w, int h, int radius)
        {
            // Only round where there's water
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (data[y * w + x] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return;
            int pad = radius * 2 + 1;
            minX = Mathf.Max(minX - pad, 0);
            minY = Mathf.Max(minY - pad, 0);
            maxX = Mathf.Min(maxX + pad, w - 1);
            maxY = Mathf.Min(maxY + pad, h - 1);
            int bw = maxX - minX + 1, bh = maxY - minY + 1;

            // (what's clearly water counts as all water, so a lake filled to its shore isn't thinned)
            var a = new float[bw * bh];
            var b = new float[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++) a[y * bw + x] = Mathf.Clamp01((data[(y + minY) * w + x + minX] / 255f - 0.5f) * 4f + 0.5f);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlur(a, b, bw, bh, radius, true);
                BoxBlur(b, a, bw, bh, radius, false);
            }
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    float v = Mathf.Clamp01((a[y * bw + x] - 0.5f) * 2f + 0.5f);
                    data[(y + minY) * w + x + minX] = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                }
            }
        }

        // One pass of a box blur, along rows (across) or columns, with nothing past the edges
        private static void BoxBlur(float[] from, float[] to, int w, int h, int radius, bool across)
        {
            int lines = across ? h : w, length = across ? w : h;
            int stride = across ? 1 : w;
            float scale = 1f / (radius * 2 + 1);
            for (int line = 0; line < lines; line++)
            {
                int start = across ? line * w : line;
                float sum = 0f;
                for (int k = 0; k <= radius && k < length; k++) sum += from[start + k * stride];
                for (int k = 0; k < length; k++)
                {
                    to[start + k * stride] = sum * scale;
                    int add = k + radius + 1, drop = k - radius;
                    if (add < length) sum += from[start + add * stride];
                    if (drop >= 0) sum -= from[start + drop * stride];
                }
            }
        }

        // ================================================================ Ctrl+Z

        // The spots a stroke changed on a lake, by where they are in the world (the lake may have grown since)
        private sealed class WaterStep : LandPaint.Step
        {
            private readonly int lakeId;
            private readonly List<Vector3> spots = new List<Vector3>();
            private readonly List<byte> was = new List<byte>(), now = new List<byte>();

            public WaterStep(LandWater water) => lakeId = water.GetInstanceID();

            public int Count => spots.Count;

            public void Add(Vector3 world, byte before, byte after)
            {
                spots.Add(world);
                was.Add(before);
                now.Add(after);
            }

            public override void Undo() => Put(was);
            public override void Redo() => Put(now);

            private void Put(List<byte> values)
            {
                var water = EditorUtility.InstanceIDToObject(lakeId) as LandWater;
                if (water == null || !water.Painted) return;   // (deleted, or its adding undone)
                byte[] data = water.MaskData;
                int w = water.MaskWidth, h = water.MaskHeight;
                Vector2 size = water.size;
                Transform t = water.transform;
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                for (int k = 0; k < spots.Count; k++)
                {
                    Vector3 local = t.InverseTransformPoint(spots[k]);
                    int x = Mathf.FloorToInt((local.x / size.x + 0.5f) * w), y = Mathf.FloorToInt((local.z / size.y + 0.5f) * h);
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    data[y * w + x] = values[k];
                    Vector3 p = spots[k];
                    minX = Mathf.Min(minX, p.x);
                    maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.z);
                    maxZ = Mathf.Max(maxZ, p.z);
                }
                water.ApplyMask();
                EditorUtility.SetDirty(water);
                if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(water.gameObject.scene);
                if (maxX >= minX) GrassOver(Rect.MinMaxRect(minX - 1f, minZ - 1f, maxX + 1f, maxZ + 1f));
            }
        }
    }
}
#endif
