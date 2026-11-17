# Paint Land: LowPoly Landscape Painter

Paint Land turns a mesh into a low-poly landscape you paint by hand in the Scene view. You can shape hills, paint ground colours and textures, draw paths, dig lakes, grow GPU grass and scatter trees and rocks, all from one window. Every stroke can be undone.

## Requirements

- Unity 6 (6000.0) or newer.
- Universal Render Pipeline (URP). The water uses URP's Depth Texture, which Paint Land turns on for you when you add water.

## Quick start

1. Open **Assets/Paint Land/Sample/Paint Land Demo.unity**.
2. Open the painting window with **Tools > Paint Land > Open Paint Land**.
3. Pick a tab, click **Start Painting**, then drag over the land in the Scene view.

### Using your own land

1. Make your land as a model or mesh, for example in Blender.
2. Open **Tools > Paint Land > Land Settings** and set **Source** to your model.
3. Drag the model into your scene and click **Tools > Paint Land > Rebuild Land**. It's rebuilt as evenly sized triangles with soft vertex colours, and swapped into the scene in place of your model. Your model is switched off, not deleted.
4. Paint with **Tools > Paint Land > Open Paint Land**.

You can rebuild the land at any time, for example after changing the settings. Everything you painted stays.

## The tabs

| Tab | What it does |
| --- | --- |
| Shape | Raise, lower, smooth, flatten, roughen or restore the land, and make ramps. Colliders, colours, grass and plants follow the ground as it moves. |
| Ground | Paint the ground's colours with a soft brush, or colour whole triangles for a crisp low-poly look. Pick From Land takes a colour from the ground. |
| Textures | Paint up to 8 textures (rock, sand, mud and more), soft-edged or on whole triangles. Set the textures on the Land material. |
| Paths | Draw paths of any width with crisp edges. They can also clear away the trees and plants in their way. |
| Water | Paint lakes. The water level is wherever you start the stroke. The bed is dug out with a smooth shore, and anything in the water is removed. You can also add a sea over the whole land, and Smooth The Shore tidies a lake painted earlier. |
| Grass | Add the GPU Grass Field over the whole land, then paint where it grows thicker, thinner or not at all. |
| Scatter | Scatter trees, rocks, flowers or any prefabs in natural clumps and patches, with random colours, sizes and lean. Drop your own prefabs or folders on the window to make a new kind. Add Colliders gives each kind a matching collider. See **Scatter in detail** below. |

### Scatter in detail

Open a kind (click its name) to see its settings. Each prefab in it has its own box:

- **Tick box:** switch the prefab on or off. A prefab that's off isn't scattered, and the eraser leaves it alone.
- **Chance:** how often it's picked next to the others in the kind (2 = twice as often as 1).
- **Clumps:** how many grow together in each clump of it, from-to (for example 3-5; 1-1 = each on its own).
- **Part colours:** a row of colours for each part of the prefab, such as **Petals**, **Leaves**, **Trunk**, **Stems**, **Cap**, **Stalk** or **Rock**. Each clump picks one colour per part. Click **+** to add a colour, click a swatch to change it, and right-click a swatch to remove or copy it. Each row also has a ready-made set (Wildflowers, Autumn, Bark, Toadstools, Stone...) and **Own Colour** to go back to the prefab's own.
- **+** adds the same prefab again with the same settings, for example a white-petal copy of a red flower. **x** takes it out of the kind.

Parts are found from the prefab's material names (a material called "Petals", "Leaves", "Bark", "Mushroom Cap"...), so give each part its own material to colour it separately. A part with no colours of its own uses the kind's **Plant Colours**, and **Recolour The Ones Already Placed** applies the colours to everything already in the scene.

Each kind also has **Draw Distance**, **Detail Distance** and **Shadow Distance**. See Performance below.

## Controls

| Input | Action |
| --- | --- |
| Drag | Paint |
| Shift, or the Erase tick | Rub out |
| Ctrl + click | Pick up a colour, texture or height |
| [ and ] | Make the brush smaller or bigger |
| Esc | Stop painting |
| Ctrl + Z | Undo a stroke |

## Where things are kept

- Everything Paint Land saves goes in **Assets/Paint Land/Data**: the built land, its shape, the paint masks (PNG files for textures, paths and grass) and the materials. These are ordinary files, so they work with version control.
- Scattered objects are ordinary scene objects under a **Land Scatter** object, saved with the scene, so you can still select, move or delete them by hand.
- Lakes and the sea are **Land Water** objects in the scene.

## Performance

- **Grass Field:** the grass is worked out in 16 m chunks near each camera and drawn on the GPU, thousands of tufts at a time. Fewer tufts are drawn further away, and none cast shadows.
- **Scattered things:** a **Scatter Renderer** on the Land Scatter object draws everything you scatter with GPU instancing, thousands at a time, in 32 m squares. Each kind is drawn only out to its **Draw Distance**, switches to its simpler level of detail past its **Detail Distance**, and casts shadows only within its **Shadow Distance** (flowers and mushrooms don't cast any by default). Set these per kind in the Scatter tab. Paint Land adds the Scatter Renderer the first time you scatter; for a scene scattered with an older version, use **Tools > Paint Land > Optimize Scatter**.
- **When playing and in builds**, the scattered objects lose their renderers and LOD Groups, and the ones with nothing else on them (no colliders) are left out altogether, so scenes load faster and hold far fewer objects. This only changes the copy being played or built, never your saved scene.
- In the editor the scattered objects stay where they are with their own renderers switched off. Untick **In Editor** on the Scatter Renderer if you want to click them in the Scene view.
- Tinted materials are shared copies (one per colour), with GPU instancing on.

## Samples

**Tools > Paint Land > Samples** rebuilds the sample land and props, or the demo scene, if you've changed or deleted them. All sample models are generated by code and are free to use in your projects.

## Support

Contact YaraJ through the Asset Store publisher page.
