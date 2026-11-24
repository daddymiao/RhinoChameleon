using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Chameleon
{
    /// <summary>
    /// Writes drawing curves and fills into the document on a layer tree that mirrors the source
    /// layers, the same way Rhino's Make2D does:
    /// <code>
    /// Make2D
    ///   Visible
    ///     Curves   :: &lt;source layer path&gt;
    ///     Tangents :: &lt;source layer path&gt;
    ///     Fills    :: &lt;source layer path&gt; [:: orientation] [:: Depth NN]
    ///   Hidden
    ///     Curves   :: &lt;source layer path&gt;
    /// </code>
    /// </summary>
    internal sealed class DrawingWriter
    {
        readonly RhinoDoc _doc;
        readonly ChameleonOptions _opt;
        readonly string _root;
        readonly double _tol;
        readonly Dictionary<string, int> _layerCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int _solidPattern = -1;

        public DrawingWriter(RhinoDoc doc, ChameleonOptions options, string rootLayer)
        {
            _doc = doc;
            _opt = options;
            _root = string.IsNullOrWhiteSpace(rootLayer) ? "Make2D" : rootLayer.Trim();
            _tol = doc.ModelAbsoluteTolerance;
        }

        public static string OrientationLayerName(SurfaceOrientation o)
        {
            switch (o)
            {
                case SurfaceOrientation.Up: return "Up";
                case SurfaceOrientation.SlopedUp: return "Sloped-Up";
                case SurfaceOrientation.VerticalN: return "Vertical-N";
                case SurfaceOrientation.VerticalE: return "Vertical-E";
                case SurfaceOrientation.VerticalS: return "Vertical-S";
                case SurfaceOrientation.VerticalW: return "Vertical-W";
                case SurfaceOrientation.SlopedDown: return "Sloped-Down";
                default: return "Down";
            }
        }

        // ------------------------------------------------------------------ layers

        sealed class LayerSpec
        {
            public string Name;
            public Color Color = Color.Black;
            public Layer Style;           // copy plot weight / linetype from this layer when creating
            public int Linetype = -1;
        }

        int EnsureLayer(List<LayerSpec> specs)
        {
            Guid parent = Guid.Empty;
            string full = null;
            int index = -1;
            foreach (var spec in specs)
            {
                full = full == null ? spec.Name : full + "::" + spec.Name;
                if (!_layerCache.TryGetValue(full, out index))
                {
                    index = _doc.Layers.FindByFullPath(full, -1);
                    if (index < 0)
                    {
                        var layer = new Layer { Name = spec.Name, ParentLayerId = parent, Color = spec.Color, PlotColor = spec.Color };
                        if (spec.Style != null)
                        {
                            layer.PlotWeight = spec.Style.PlotWeight;
                            layer.LinetypeIndex = spec.Style.LinetypeIndex;
                        }
                        if (spec.Linetype >= 0) layer.LinetypeIndex = spec.Linetype;
                        index = _doc.Layers.Add(layer);
                        if (index < 0) throw new InvalidOperationException($"Could not create layer \"{full}\".");
                    }
                    _layerCache[full] = index;
                }
                parent = _doc.Layers[index].Id;
            }
            return index;
        }

        /// <summary>Layer specs for each level of the source object's layer path.</summary>
        List<LayerSpec> SourcePath(SourceItem src, Func<Color, Color> colorize, bool copyStyle)
        {
            var specs = new List<LayerSpec>();
            if (src == null) return specs;
            var layer = _doc.Layers.FindIndex(src.LayerIndex);
            if (layer == null) return specs;

            var chain = new List<Layer>();
            for (var l = layer; l != null; l = l.ParentLayerId == Guid.Empty ? null : _doc.Layers.FindId(l.ParentLayerId))
                chain.Insert(0, l);
            foreach (var l in chain)
                specs.Add(new LayerSpec { Name = l.Name, Color = colorize(l.Color), Style = copyStyle ? l : null });
            return specs;
        }

        static Color Tint(Color c, double towardWhite)
        {
            towardWhite = Math.Max(0, Math.Min(1, towardWhite));
            int f(int v) => (int)Math.Round(v + (255 - v) * towardWhite);
            return Color.FromArgb(255, f(c.R), f(c.G), f(c.B));
        }

        static double OrientationTint(SurfaceOrientation o)
        {
            // Light from above / south-east, so an axonometric reads as shaded out of the box.
            switch (o)
            {
                case SurfaceOrientation.Up: return 0.80;
                case SurfaceOrientation.SlopedUp: return 0.70;
                case SurfaceOrientation.VerticalS: return 0.60;
                case SurfaceOrientation.VerticalE: return 0.50;
                case SurfaceOrientation.VerticalW: return 0.40;
                case SurfaceOrientation.VerticalN: return 0.30;
                case SurfaceOrientation.SlopedDown: return 0.25;
                default: return 0.20;
            }
        }

        int CurveLayer(SourceItem src, CurveKind kind)
        {
            int hiddenLt = -1;
            if (kind == CurveKind.Hidden)
            {
                var lt = _doc.Linetypes.FindName("Hidden") ?? _doc.Linetypes.FindName("Dashed");
                if (lt != null) hiddenLt = lt.Index;
            }
            var specs = new List<LayerSpec>
            {
                new LayerSpec { Name = _root },
                new LayerSpec { Name = kind == CurveKind.Hidden ? "Hidden" : "Visible" },
                new LayerSpec { Name = kind == CurveKind.Tangent ? "Tangents" : "Curves", Linetype = hiddenLt },
            };
            var src_ = SourcePath(src, c => c, true);
            if (hiddenLt >= 0) foreach (var s in src_) s.Linetype = hiddenLt;
            specs.AddRange(src_);
            return EnsureLayer(specs);
        }

        int FillLayer(DrawingFill fill)
        {
            double tint = _opt.SplitByOrientation ? OrientationTint(fill.Orientation) : 0.55;
            int bands = Math.Max(1, _opt.DepthBands);
            if (fill.DepthBand >= 0) tint += (1 - tint) * 0.6 * fill.DepthBand / Math.Max(1, bands - 1);

            var specs = new List<LayerSpec>
            {
                new LayerSpec { Name = _root },
                new LayerSpec { Name = "Visible" },
                new LayerSpec { Name = "Fills" },
            };
            var srcPath = SourcePath(fill.Source, c => Tint(c, 0.55), false);
            if (srcPath.Count == 0) srcPath.Add(new LayerSpec { Name = "Unknown", Color = Color.LightGray });
            specs.AddRange(srcPath);

            Color baseColor = _doc.Layers.FindIndex(fill.Source?.LayerIndex ?? -1)?.Color ?? Color.Gray;
            if (_opt.SplitByOrientation)
                specs.Add(new LayerSpec { Name = OrientationLayerName(fill.Orientation), Color = Tint(baseColor, OrientationTint(fill.Orientation)) });
            if (fill.DepthBand >= 0)
                specs.Add(new LayerSpec { Name = $"Depth {fill.DepthBand + 1:00}", Color = Tint(baseColor, tint) });

            // Leaf layer gets the final tint even when it is the source layer itself.
            specs[specs.Count - 1].Color = Tint(baseColor, tint);
            return EnsureLayer(specs);
        }

        int SolidPattern()
        {
            if (_solidPattern >= 0) return _solidPattern;
            var p = _doc.HatchPatterns.FindName("Solid");
            _solidPattern = p != null ? p.Index : _doc.HatchPatterns.Add(HatchPattern.Defaults.Solid);
            return _solidPattern;
        }

        // ------------------------------------------------------------------ objects

        public int AddFills(List<DrawingFill> fills)
        {
            int pattern = SolidPattern();
            if (pattern < 0) throw new InvalidOperationException("Could not find or create the Solid hatch pattern.");

            int added = 0;
            var groups = fills.GroupBy(f => (Layer: FillLayer(f), Src: f.Source?.Index ?? -1, f.FaceIndex));
            foreach (var g in groups)
            {
                var members = g.ToList();
                var loopSets = new List<IEnumerable<Curve>>();

                if (_opt.MergeFragments && members.Count > 1 && members.All(f => f.Boundaries.Length == 1))
                {
                    var merged = TryUnion(members.Select(f => f.Boundaries[0]).ToList());
                    if (merged != null) loopSets.Add(merged);
                }
                if (loopSets.Count == 0)
                    foreach (var f in members) loopSets.Add(f.Boundaries);

                var first = members[0];
                var attrs = FillAttributes(g.Key.Layer, first);
                foreach (var loops in loopSets)
                {
                    var hatches = Hatch.Create(loops, pattern, 0.0, 1.0, _tol);
                    if (hatches == null) continue;
                    foreach (var h in hatches)
                        if (_doc.Objects.AddHatch(h, attrs) != Guid.Empty) added++;
                }
            }
            return added;
        }

        /// <summary>Union of touching outer loops; null if the result does not preserve the area.</summary>
        Curve[] TryUnion(List<Curve> loops)
        {
            var union = Curve.CreateBooleanUnion(loops, _tol);
            if (union == null || union.Length == 0 || union.Length >= loops.Count) return null;

            double before = loops.Sum(c => AreaMassProperties.Compute(c)?.Area ?? 0);
            var breps = Brep.CreatePlanarBreps(union, _tol);
            if (breps == null) return null;
            double after = breps.Sum(b => b.GetArea());
            if (before <= 0 || Math.Abs(after - before) > before * 0.005) return null;
            return union;
        }

        ObjectAttributes FillAttributes(int layer, DrawingFill f)
        {
            var a = new ObjectAttributes
            {
                LayerIndex = layer,
                ColorSource = ObjectColorSource.ColorFromLayer,
                PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer,
                DisplayOrder = -1,   // behind the line work
            };
            if (f.Source != null)
            {
                a.SetUserString("Chameleon.SourceObject", f.Source.ObjectId.ToString());
                a.SetUserString("Chameleon.SourceLayer", _doc.Layers.FindIndex(f.Source.LayerIndex)?.FullPath ?? "");
            }
            a.SetUserString("Chameleon.Face", f.FaceIndex.ToString());
            a.SetUserString("Chameleon.Orientation", OrientationLayerName(f.Orientation));
            a.SetUserString("Chameleon.Depth", f.Depth.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            return a;
        }

        public int AddCurves(List<DrawingCurve> curves)
        {
            int added = 0;
            var groups = curves.GroupBy(c => (Layer: CurveLayer(c.Source, c.Kind), Src: c.Source?.Index ?? -1));
            foreach (var g in groups)
            {
                IEnumerable<Curve> output = g.Select(c => c.Curve);
                if (_opt.JoinCurves)
                {
                    var joined = Curve.JoinCurves(output, _tol * 2, false);
                    if (joined != null && joined.Length > 0) output = joined;
                }
                var attrs = new ObjectAttributes
                {
                    LayerIndex = g.Key.Layer,
                    ColorSource = ObjectColorSource.ColorFromLayer,
                    PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer,
                    PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer,
                    LinetypeSource = ObjectLinetypeSource.LinetypeFromLayer,
                };
                foreach (var c in output)
                    if (_doc.Objects.AddCurve(c, attrs) != Guid.Empty) added++;
            }
            return added;
        }
    }
}
