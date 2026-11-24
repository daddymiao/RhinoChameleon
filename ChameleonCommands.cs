using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chameleon
{
    /// <summary>Shared plumbing for the Chameleon commands.</summary>
    internal static class ChameleonCommon
    {
        public const ObjectType ModelFilter =
            ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh |
            ObjectType.SubD | ObjectType.InstanceReference | ObjectType.Curve;

        public static RhinoViewport GetViewport(RhinoDoc doc, out string error)
        {
            error = null;
            var view = doc.Views.ActiveView;
            var vp = view?.ActiveViewport;
            if (vp == null) { error = "There is no active view."; return null; }
            if (view is RhinoPageView page && page.PageIsActive)
            {
                error = "Activate a detail (or a model view) first; Chameleon draws what that view's camera sees.";
                return null;
            }
            return vp;
        }

        /// <summary>Object selection with the command options on the same prompt.</summary>
        public static Result SelectModelObjects(string prompt, ChameleonOptions o, bool make2DOptions, out List<RhinoObject> objects)
        {
            objects = null;
            using (var go = new GetObject())
            {
                go.SetCommandPrompt(prompt);
                go.GeometryFilter = ModelFilter;
                go.GroupSelect = true;
                go.SubObjectSelect = false;
                go.EnableClearObjectsOnEntry(false);
                go.EnableUnselectObjectsOnExit(false);
                go.DeselectAllBeforePostSelect = false;

                while (true)
                {
                    go.ClearCommandOptions();
                    var optHidden = new OptionToggle(o.HiddenLines, "No", "Yes");
                    var optTangent = new OptionToggle(o.TangentEdges, "No", "Yes");
                    var optFills = new OptionToggle(o.Fills, "No", "Yes");
                    var optOrient = new OptionToggle(o.SplitByOrientation, "No", "Yes");
                    var optBands = new OptionInteger(o.DepthBands, 0, 20);
                    var optMerge = new OptionToggle(o.MergeFragments, "No", "Yes");
                    var optJoin = new OptionToggle(o.JoinCurves, "No", "Yes");

                    if (make2DOptions)
                    {
                        go.AddOptionToggle("HiddenLines", ref optHidden);
                        go.AddOptionToggle("TangentEdges", ref optTangent);
                        go.AddOptionToggle("Fills", ref optFills);
                        go.AddOptionToggle("JoinCurves", ref optJoin);
                    }
                    go.AddOptionToggle("SplitByOrientation", ref optOrient);
                    go.AddOptionInteger("DepthBands", ref optBands);
                    go.AddOptionToggle("MergeFragments", ref optMerge);
                    int optRoot = go.AddOption("RootLayer", SafeOptionValue(o.RootLayer));

                    var res = go.GetMultiple(1, 0);

                    o.HiddenLines = optHidden.CurrentValue;
                    o.TangentEdges = optTangent.CurrentValue;
                    o.Fills = optFills.CurrentValue;
                    o.SplitByOrientation = optOrient.CurrentValue;
                    o.DepthBands = optBands.CurrentValue;
                    o.MergeFragments = optMerge.CurrentValue;
                    o.JoinCurves = optJoin.CurrentValue;

                    if (res == GetResult.Option)
                    {
                        if (go.OptionIndex() == optRoot)
                        {
                            var gs = new GetString();
                            gs.SetCommandPrompt("Name of the top-level drawing layer");
                            gs.SetDefaultString(o.RootLayer);
                            if (gs.Get() == GetResult.String && ModelComponent.IsValidComponentName(gs.StringResult().Trim()))
                                o.RootLayer = gs.StringResult().Trim();
                            else
                                RhinoApp.WriteLine("Root layer name unchanged.");
                        }
                        go.EnablePreSelect(false, true);
                        continue;
                    }
                    if (res != GetResult.Object) return go.CommandResult();
                    break;
                }

                objects = go.Objects().Select(r => r.Object()).Where(x => x != null).ToList();
            }
            return objects.Count > 0 ? Result.Success : Result.Nothing;
        }

        static string SafeOptionValue(string s)
        {
            var chars = (s ?? "").Where(char.IsLetterOrDigit).ToArray();
            return chars.Length > 0 ? new string(chars) : "Make2D";
        }

        public static void Report(Make2DEngine engine)
        {
            foreach (var w in engine.Warnings) RhinoApp.WriteLine("Chameleon: " + w);
        }

        /// <summary>Tracks the Escape key while a long computation runs.</summary>
        public sealed class EscapeWatcher : IDisposable
        {
            public bool Pressed { get; private set; }
            public EscapeWatcher() { RhinoApp.EscapeKeyPressed += OnEsc; }
            void OnEsc(object sender, EventArgs e) { Pressed = true; }
            public void Dispose() { RhinoApp.EscapeKeyPressed -= OnEsc; }
        }
    }

    /// <summary>
    /// ChameleonMake2D: a Make2D that also creates solid hatches for every visible surface, on layers
    /// that mirror the source layers (Make2D::Visible::Fills::&lt;layer&gt;).
    /// </summary>
    public class ChameleonMake2DCommand : Command
    {
        public ChameleonMake2DCommand() { Instance = this; }
        public static ChameleonMake2DCommand Instance { get; private set; }
        public override string EnglishName => "ChameleonMake2D";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var vp = ChameleonCommon.GetViewport(doc, out string err);
            if (vp == null) { RhinoApp.WriteLine(err); return Result.Failure; }

            var o = ChameleonOptions.Load();
            var sel = ChameleonCommon.SelectModelObjects("Select objects to draw (Make2D with fills)", o, true, out var objects);
            if (sel != Result.Success) return sel;
            o.Save();

            RhinoApp.WriteLine($"Chameleon: computing hidden lines for {objects.Count} object(s) in \"{vp.Name}\"...");
            var engine = new Make2DEngine(doc, vp, o);
            engine.CollectSources(objects);
            if (!engine.ComputeHiddenLines(o.HiddenLines)) { RhinoApp.WriteLine("Chameleon: " + engine.Error); return Result.Failure; }

            var curves = engine.ExtractCurves(o.HiddenLines);
            if (curves.Count == 0) { RhinoApp.WriteLine("Chameleon: the drawing is empty."); return Result.Nothing; }

            var box = BoundingBox.Empty;
            foreach (var c in curves) box.Union(c.Curve.GetBoundingBox(true));
            engine.SetPlacement(engine.DefaultOffset(box), 0.0);
            var place = engine.PlacementTransform;
            foreach (var c in curves) c.Curve.Transform(place);

            List<DrawingFill> fills = new List<DrawingFill>();
            if (o.Fills)
            {
                RhinoApp.WriteLine("Chameleon: finding visible surfaces (Esc to stop)...");
                var boundary = curves.Where(c => c.Kind != CurveKind.Hidden).Select(c => c.Curve).ToList();
                using (var esc = new ChameleonCommon.EscapeWatcher())
                    fills = engine.BuildFills(boundary, () => esc.Pressed);
                Make2DEngine.AssignDepthBands(fills, o.DepthBands);
            }

            var writer = new DrawingWriter(doc, o, o.RootLayer);
            int nFills = o.Fills ? writer.AddFills(fills) : 0;
            int nCurves = writer.AddCurves(curves);
            doc.Views.Redraw();

            ChameleonCommon.Report(engine);
            RhinoApp.WriteLine($"Chameleon: added {nCurves} curve(s) and {nFills} fill(s) under layer \"{o.RootLayer}\".");
            return Result.Success;
        }
    }

    /// <summary>
    /// ChameleonFill: adds the fills to a drawing you already made with Rhino's Make2D. Select the 3D
    /// objects, then the existing Make2D curves; the fills are aligned to, and bounded by, those curves.
    /// Run it from the same view (camera unchanged) that the Make2D was made from.
    /// </summary>
    public class ChameleonFillCommand : Command
    {
        public ChameleonFillCommand() { Instance = this; }
        public static ChameleonFillCommand Instance { get; private set; }
        public override string EnglishName => "ChameleonFill";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var vp = ChameleonCommon.GetViewport(doc, out string err);
            if (vp == null) { RhinoApp.WriteLine(err); return Result.Failure; }

            var o = ChameleonOptions.Load();
            var sel = ChameleonCommon.SelectModelObjects("Select the 3D objects the Make2D drawing was made from", o, false, out var objects);
            if (sel != Result.Success) return sel;
            o.Save();

            // The existing Make2D drawing.
            List<Curve> drawing = new List<Curve>();
            string rootLayer = o.RootLayer;
            using (var gc = new GetObject())
            {
                gc.SetCommandPrompt("Select the existing Make2D curves (Enter to create fills at the default Make2D position)");
                gc.GeometryFilter = ObjectType.Curve;
                gc.GroupSelect = true;
                gc.AcceptNothing(true);
                gc.EnablePreSelect(false, true);
                gc.DeselectAllBeforePostSelect = false;
                var res = gc.GetMultiple(1, 0);
                if (res == GetResult.Object)
                {
                    var ids = new HashSet<Guid>(objects.Select(x => x.Id));
                    foreach (var r in gc.Objects())
                    {
                        var ro = r.Object();
                        if (ro == null || ids.Contains(ro.Id)) continue;
                        var layer = doc.Layers.FindIndex(ro.Attributes.LayerIndex);
                        var path = layer?.FullPath ?? "";
                        var parts = path.Split(new[] { "::" }, StringSplitOptions.None);
                        if (parts.Any(p => p.Equals("Hidden", StringComparison.OrdinalIgnoreCase))) continue;
                        if (drawing.Count == 0 && parts.Length > 1) rootLayer = parts[0];
                        var c = r.Curve()?.DuplicateCurve();
                        if (c != null) drawing.Add(c);
                    }
                    if (drawing.Count == 0) { RhinoApp.WriteLine("Chameleon: no visible drawing curves were selected."); return Result.Nothing; }
                }
                else if (res != GetResult.Nothing) return gc.CommandResult();
            }

            RhinoApp.WriteLine($"Chameleon: computing hidden lines for {objects.Count} object(s) in \"{vp.Name}\"...");
            var engine = new Make2DEngine(doc, vp, o);
            engine.CollectSources(objects);
            if (!engine.ComputeHiddenLines(false)) { RhinoApp.WriteLine("Chameleon: " + engine.Error); return Result.Failure; }

            var ours = engine.ExtractCurves(false);
            if (ours.Count == 0) { RhinoApp.WriteLine("Chameleon: nothing is visible from this view."); return Result.Nothing; }
            var ourBox = BoundingBox.Empty;
            foreach (var c in ours) ourBox.Union(c.Curve.GetBoundingBox(true));

            List<Curve> boundary;
            if (drawing.Count > 0)
            {
                var theirBox = BoundingBox.Empty;
                foreach (var c in drawing) theirBox.Union(c.GetBoundingBox(true));
                double tol = doc.ModelAbsoluteTolerance;
                if (theirBox.Max.Z - theirBox.Min.Z > tol * 10)
                {
                    RhinoApp.WriteLine("Chameleon: the selected drawing is not flat on a plane parallel to world XY.");
                    return Result.Failure;
                }
                double dx = Math.Abs((theirBox.Max.X - theirBox.Min.X) - (ourBox.Max.X - ourBox.Min.X));
                double dy = Math.Abs((theirBox.Max.Y - theirBox.Min.Y) - (ourBox.Max.Y - ourBox.Min.Y));
                double size = Math.Max(ourBox.Diagonal.Length, tol);
                if (dx > size * 0.01 + tol * 10 || dy > size * 0.01 + tol * 10)
                {
                    RhinoApp.WriteLine(
                        "Chameleon: the selected curves do not match what this view sees from these objects " +
                        $"(size differs by {dx:0.###} x {dy:0.###}). Use the same view/camera and the same objects " +
                        "as the original Make2D, select the whole visible drawing, or use ChameleonMake2D instead.");
                    return Result.Failure;
                }
                engine.SetPlacement(new Vector2d(theirBox.Min.X - ourBox.Min.X, theirBox.Min.Y - ourBox.Min.Y), theirBox.Min.Z);
                boundary = drawing;
            }
            else
            {
                engine.SetPlacement(engine.DefaultOffset(ourBox), 0.0);
                var place = engine.PlacementTransform;
                boundary = ours.Select(c => { c.Curve.Transform(place); return c.Curve; }).ToList();
            }

            RhinoApp.WriteLine("Chameleon: finding visible surfaces (Esc to stop)...");
            List<DrawingFill> fills;
            using (var esc = new ChameleonCommon.EscapeWatcher())
                fills = engine.BuildFills(boundary, () => esc.Pressed);
            Make2DEngine.AssignDepthBands(fills, o.DepthBands);

            var writer = new DrawingWriter(doc, o, rootLayer);
            int n = writer.AddFills(fills);
            doc.Views.Redraw();

            ChameleonCommon.Report(engine);
            RhinoApp.WriteLine($"Chameleon: added {n} fill(s) under \"{rootLayer}::Visible::Fills\".");
            return Result.Success;
        }
    }
}
