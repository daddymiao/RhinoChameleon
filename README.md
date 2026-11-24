# Chameleon: Make2D with fills for Rhino 8

Rhino's Make2D only gives you lines. Chameleon also makes a **solid hatch for every visible surface**.
Each hatch goes on a layer that mirrors the layer the surface came from in the 3D model. Export the
result to Illustrator and you get filled shapes grouped by layer, so you can restyle a whole layer
at once (all glazing, all walls, all roofs and so on).

```
Make2D
├─ Visible
│  ├─ Curves    ── <your layer tree>      (line work, same as Make2D)
│  ├─ Tangents  ── <your layer tree>
│  └─ Fills     ── <your layer tree> [── Up / Vertical-S / ...] [── Depth 01 ...]
└─ Hidden
   └─ Curves    ── <your layer tree>      (only with HiddenLines=Yes)
```

## Commands

### `ChameleonMake2D`: use this instead of Make2D
1. Set up the view you want to draw (any parallel or perspective model view, or an active detail).
2. Run `ChameleonMake2D` and select the objects. You can pick polysurfaces, surfaces, extrusions,
   meshes, SubDs, block instances (including nested blocks) and curves.
3. Press Enter. The line work and the fills are created together, so they always line up.

Placement: if the view is a plan (Top-style camera), the drawing keeps its world XY coordinates and
sits on top of the model. Any other view is placed with its lower-left corner at the world origin.

### `ChameleonFill`: add fills to a drawing you already made with Make2D
1. Keep the **same view and camera** you used for the Make2D.
2. Run `ChameleonFill` and select the same 3D objects, then press Enter.
3. Select the existing Make2D curves (the whole visible drawing; curves on `Hidden` layers are
   skipped automatically) and press Enter.

The fills are built from *your* curves, so they match your line work exactly, including any edits
you made afterwards. The top-level layer name is taken from your drawing. If you press Enter
without selecting curves, the fills are placed where `ChameleonMake2D` would put them.

If the selected curves don't match what the current view sees, the command stops and tells you.
That usually means the camera has moved since the Make2D.

## Options

| Option | Default | What it does |
|---|---|---|
| `HiddenLines` | No | Also output hidden lines (`ChameleonMake2D` only) |
| `TangentEdges` | Yes | Draw tangent edges on `Visible::Tangents` (`ChameleonMake2D` only) |
| `Fills` | Yes | Create the fills (`ChameleonMake2D` only) |
| `JoinCurves` | Yes | Join line segments from the same object into longer curves (`ChameleonMake2D` only) |
| `SplitByOrientation` | No | Adds a sub-layer for which way each surface faces: `Up`, `Down`, `Sloped-Up`, `Sloped-Down`, `Vertical-N/E/S/W` (world +Y = north). Each one gets a different shade, so axonometrics come out shaded |
| `DepthBands` | 0 | Splits fills into N bands by distance from the camera (`Depth 01` is nearest). Useful for atmospheric depth in elevations |
| `MergeFragments` | Yes | Joins touching pieces of the same surface into one hatch |
| `RootLayer` | Make2D | Name of the top-level layer |

Your option choices are kept between sessions. Each hatch also stores user text (`Chameleon.SourceObject`,
`Chameleon.SourceLayer`, `Chameleon.Face`, `Chameleon.Orientation`, `Chameleon.Depth`) that you can use with
`SelByUserText` or Grasshopper.

## Getting it into Illustrator
* Select the `Make2D` layer tree, then **File → Export Selected → Adobe Illustrator (\*.ai)**.
  Solid hatches come out as filled paths and Rhino layers become Illustrator layers. Or **Print →
  PDF** with layers turned on, then open the PDF in Illustrator.
* Fill colours are pale tints of the source layer colours. That's only a starting point, since the
  idea is to restyle in Illustrator by layer.
* Fills are drawn behind the line work (display order −1, and they are created first).

## How it works
1. Runs Rhino's own hidden-line engine (`HiddenLineDrawing`, the same one Make2D uses) on the
   current view.
2. Splits the drawing into every closed region its visible curves make (`Curve.CreateBooleanRegions`).
3. For each region, takes a few points inside it, projects them back into the model along the line
   of sight and finds the **first surface hit**. That's the surface you see in that region. The
   surface's object, layer, face, normal and depth decide which layer the fill goes on.
4. Writes a solid hatch per region (merging touching pieces of the same face) and the line work, on
   the layer tree described above.

## Installing and updating

The ready-to-use plug-in is always at **`dist/Chameleon.rhp`** in this repo (Rhino 8, Mac and
Windows). It is rebuilt and committed with every code change.

### Install once
1. Clone this repo somewhere permanent, e.g. `~/Documents/RhinoChameleon`. If you would rather not
   use git, download `dist/Chameleon.rhp` into a permanent folder instead.
2. Load the plug-in into Rhino **from that location**, once:
   * **Windows:** drag `dist/Chameleon.rhp` onto the Rhino window, or use
     **Tools → Options → Plug-ins → Install…**
   * **Mac:** dragging an `.rhp` onto Rhino for Mac opens the import prompt, so run this once
     instead. Type `ScriptEditor`, create a new **C#** script, paste the code below, click **Run** and
     pick `dist/Chameleon.rhp`. The command line should say `Chameleon install: Success`.
     ```csharp
     // #! csharp
     using System;
     using Rhino;

     var dialog = new Rhino.UI.OpenFileDialog { Title = "Choose Chameleon.rhp", Filter = "Rhino plug-in (*.rhp)|*.rhp" };
     if (dialog.ShowOpenDialog())
     {
         var result = Rhino.PlugIns.PlugIn.LoadPlugIn(dialog.FileName, out Guid id);
         RhinoApp.WriteLine($"Chameleon install: {result}");
     }
     ```

### Update to a new version (no script, no reinstall)
Rhino remembers *where* it loaded the plug-in from and reloads that file every time it starts. To
update, replace the file at that same location:
1. **Quit Rhino.**
2. Get the new file:
   * if you cloned the repo, run `git pull` in it (or click **Pull** in GitHub Desktop);
   * if you downloaded the file, download the new `dist/Chameleon.rhp` over the old one, with
     the same name in the same folder.
3. Start Rhino. The new version loads.

Don't move or rename the file after installing. If you do, run the one-time install again from the
new location.

## Building from source
```
dotnet build -c Release
```
This needs the .NET 8 SDK. It produces `bin/Release/net7.0/Chameleon.rhp` and
`bin/Release/net48/Chameleon.rhp`, and copies the `net7.0` build to `dist/Chameleon.rhp`. In VS
Code or Visual Studio, the "Rhino 8 - netcore" launch profile starts Rhino with the plug-in loaded.

## Known limits
* **Clipping planes / section cuts are not handled yet.** Fills inside a section cut would be taken
  from the geometry the clipping plane removes.
* A region is only filled if its visible curves close it. Gaps in the line work (for example from
  very low tolerances) leave that region unfilled. The command reports how many regions showed only
  background.
* A region crossed by a tangent edge with `TangentEdges=No` gets one fill, from whichever face
  covers most of it.
* Very large scenes are slower than Make2D, because each region needs a few ray casts.
