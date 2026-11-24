using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chameleon
{
    /// <summary>Which way a visible surface faces, in world terms.</summary>
    internal enum SurfaceOrientation
    {
        Up,
        SlopedUp,
        VerticalN,
        VerticalE,
        VerticalS,
        VerticalW,
        SlopedDown,
        Down,
    }

    internal enum CurveKind { Visible, Tangent, Hidden }

    /// <summary>One object (or block piece) that takes part in the drawing, in world coordinates.</summary>
    internal sealed class SourceItem
    {
        public int Index;
        public Guid ObjectId;
        public GeometryBase Geometry;
        public int LayerIndex;
        public BoundingBox RayBox;
        public Mesh RayMesh;          // null for curves
        public int[] FaceStarts;      // first mesh face of each brep face (+ sentinel); null for meshes

        public int BrepFaceFromMeshFace(int meshFace)
        {
            if (FaceStarts == null) return -1;
            int lo = 0, hi = FaceStarts.Length - 2;
            int found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (FaceStarts[mid] <= meshFace) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            // Skip brep faces that produced no mesh faces.
            while (found >= 0 && found + 1 < FaceStarts.Length && FaceStarts[found + 1] <= meshFace) found++;
            return found;
        }
    }

    internal sealed class DrawingCurve
    {
        public Curve Curve;
        public SourceItem Source;
        public CurveKind Kind;
    }

    internal sealed class DrawingFill
    {
        public Curve[] Boundaries;    // first = outer boundary, rest = holes
        public SourceItem Source;
        public int FaceIndex;         // brep face index, -1 for meshes
        public Vector3d Normal;       // facing the viewer
        public SurfaceOrientation Orientation;
        public double Depth;          // distance from camera along the view direction
        public int DepthBand = -1;    // 0 = nearest, -1 = not banded
    }

    /// <summary>
    /// Computes a Make2D-style hidden line drawing and figures out, for every closed region of the
    /// visible drawing, which 3D surface is seen there.
    /// <para>
    /// The region test works by taking points inside each 2D region, projecting them back into the
    /// model along the line of sight and finding the first surface they hit.
    /// </para>
    /// </summary>
    internal sealed class Make2DEngine
    {
        readonly RhinoDoc _doc;
        readonly RhinoViewport _vp;
        readonly ChameleonOptions _opt;
        readonly double _tol;

        public readonly List<SourceItem> Sources = new List<SourceItem>();
        public string Error { get; private set; }
        public readonly List<string> Warnings = new List<string>();

        HiddenLineDrawing _hld;
        Transform _hldToWorld;
        Vector3d _camDir;
        Point3d _camLoc;
        BoundingBox _worldBox = BoundingBox.Empty;
        double _zLo, _zHi;

        /// <summary>Drawing coordinates = HLD x-y coordinates + Offset (z = Elevation).</summary>
        public Vector2d Offset { get; private set; }
        public double Elevation { get; private set; }

        public Make2DEngine(RhinoDoc doc, RhinoViewport viewport, ChameleonOptions options)
        {
            _doc = doc;
            _vp = viewport;
            _opt = options;
            _tol = doc.ModelAbsoluteTolerance;
        }

        // ------------------------------------------------------------------ sources

        public void CollectSources(IEnumerable<RhinoObject> objects)
        {
            foreach (var obj in objects)
            {
                if (obj == null) continue;
                if (obj is InstanceObject io)
                {
                    io.Explode(true, out RhinoObject[] pieces, out ObjectAttributes[] attrs, out Transform[] xforms);
                    if (pieces == null) continue;
                    for (int i = 0; i < pieces.Length; i++)
                    {
                        if (pieces[i] == null || pieces[i] is InstanceObject) continue;
                        int layer = attrs != null && i < attrs.Length ? attrs[i].LayerIndex : obj.Attributes.LayerIndex;
                        AddSource(io.Id, pieces[i].Geometry, xforms != null && i < xforms.Length ? xforms[i] : Transform.Identity, layer);
                    }
                }
                else
                {
                    AddSource(obj.Id, obj.Geometry, Transform.Identity, obj.Attributes.LayerIndex);
                }
            }
        }

        void AddSource(Guid id, GeometryBase geometry, Transform xform, int layerIndex)
        {
            GeometryBase g = Normalize(geometry);
            if (g == null) return;
            if (!xform.IsIdentity && !g.Transform(xform)) return;
            if (layerIndex < 0 || layerIndex >= _doc.Layers.Count) layerIndex = _doc.Layers.CurrentLayerIndex;

            var s = new SourceItem { Index = Sources.Count, ObjectId = id, Geometry = g, LayerIndex = layerIndex };
            BuildRayMesh(s);
            _worldBox.Union(g.GetBoundingBox(true));
            Sources.Add(s);
        }

        static GeometryBase Normalize(GeometryBase g)
        {
            switch (g)
            {
                case Brep b: return b.DuplicateBrep();
                case Extrusion e: return e.ToBrep(true);
                case Surface s: return s.ToBrep();
                case SubD sd: return sd.ToBrep(SubDToBrepOptions.DefaultPacked);
                case Mesh m: return m.DuplicateMesh();
                case Curve c: return c.DuplicateCurve();
                default: return null;
            }
        }

        void BuildRayMesh(SourceItem s)
        {
            var mp = MeshingParameters.QualityRenderMesh;
            Mesh mesh = null;

            if (s.Geometry is Brep brep)
            {
                int n = brep.Faces.Count;
                var starts = new int[n + 1];
                mesh = new Mesh();
                Mesh[] perFace = Mesh.CreateFromBrep(brep, mp);
                bool isPerFace = perFace != null && perFace.Length == n;
                for (int i = 0; i < n; i++)
                {
                    starts[i] = mesh.Faces.Count;
                    if (isPerFace)
                    {
                        if (perFace[i] != null) mesh.Append(perFace[i]);
                    }
                    else
                    {
                        using (var faceBrep = brep.Faces[i].DuplicateFace(false))
                        {
                            var parts = faceBrep != null ? Mesh.CreateFromBrep(faceBrep, mp) : null;
                            if (parts != null) foreach (var p in parts) mesh.Append(p);
                        }
                    }
                }
                starts[n] = mesh.Faces.Count;
                s.FaceStarts = starts;
            }
            else if (s.Geometry is Mesh m)
            {
                mesh = m;
            }

            if (mesh == null || mesh.Faces.Count == 0) return;
            mesh.FaceNormals.ComputeFaceNormals();
            s.RayMesh = mesh;
            var box = mesh.GetBoundingBox(true);
            box.Inflate(Math.Max(_tol * 10, box.Diagonal.Length * 1e-6));
            s.RayBox = box;
        }

        // ------------------------------------------------------------------ hidden lines

        public bool ComputeHiddenLines(bool includeHidden)
        {
            if (Sources.Count == 0) { Error = "Nothing usable was selected."; return false; }

            var hp = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = _tol,
                IncludeTangentEdges = _opt.TangentEdges,
                IncludeTangentSeams = false,   // seams split one surface into two fills
                IncludeHiddenCurves = includeHidden,
                Flatten = true,
            };
            hp.SetViewport(_vp);
            foreach (var s in Sources)
                hp.AddGeometry(s.Geometry, Transform.Identity, s);

            _hld = HiddenLineDrawing.Compute(hp, true);
            if (_hld == null) { Error = "The hidden line calculation failed."; return false; }

            if (!_hld.WorldToHiddenLine.TryGetInverse(out _hldToWorld))
            {
                Error = "This view's projection cannot be inverted; use a parallel or perspective model view.";
                return false;
            }

            _camDir = _vp.CameraDirection;
            _camDir.Unitize();
            _camLoc = _vp.CameraLocation;

            // Depth range of the model in HLD coordinates, used to build lines of sight.
            var w2h = _hld.WorldToHiddenLine;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var c in _worldBox.GetCorners())
            {
                var p = c;
                p.Transform(w2h);
                lo = Math.Min(lo, p.Z);
                hi = Math.Max(hi, p.Z);
            }
            double pad = Math.Max((hi - lo) * 0.05, _tol * 100);
            _zLo = lo - pad;
            _zHi = hi + pad;
            return true;
        }

        /// <summary>Hidden line curves in flattened HLD coordinates (before Offset is applied).</summary>
        public List<DrawingCurve> ExtractCurves(bool includeHidden)
        {
            var result = new List<DrawingCurve>();
            var flatten = Transform.PlanarProjection(Plane.WorldXY);
            foreach (var seg in _hld.Segments)
            {
                if (seg?.ParentCurve == null) continue;
                var st = seg.ParentCurve.SilhouetteType;
                if (st == SilhouetteType.None) continue;

                CurveKind kind;
                switch (seg.SegmentVisibility)
                {
                    case HiddenLineDrawingSegment.Visibility.Visible:
                        kind = (st == SilhouetteType.NonSilhouetteTangent || st == SilhouetteType.NonSilhouetteSeam)
                            ? CurveKind.Tangent : CurveKind.Visible;
                        break;
                    case HiddenLineDrawingSegment.Visibility.Hidden:
                        if (!includeHidden) continue;
                        kind = CurveKind.Hidden;
                        break;
                    default:
                        continue;
                }

                var crv = seg.CurveGeometry?.DuplicateCurve();
                if (crv == null) continue;
                crv.Transform(flatten);
                result.Add(new DrawingCurve
                {
                    Curve = crv,
                    Kind = kind,
                    Source = seg.ParentCurve.SourceObject?.Tag as SourceItem,
                });
            }
            return result;
        }

        /// <summary>
        /// Default placement: plan views drawn from a Top-style camera keep their world XY
        /// coordinates (the drawing lands on top of the model); every other view is placed with
        /// its lower-left corner at the world origin.
        /// </summary>
        public Vector2d DefaultOffset(BoundingBox hldCurvesBox)
        {
            var dir = _vp.CameraDirection; dir.Unitize();
            var cx = _vp.CameraX; cx.Unitize();
            if (_vp.IsParallelProjection && dir * -Vector3d.ZAxis > 0.99999 && cx * Vector3d.XAxis > 0.99999)
            {
                var c = _worldBox.Center;
                var q = c;
                q.Transform(_hld.WorldToHiddenLine);
                return new Vector2d(c.X - q.X, c.Y - q.Y);
            }
            return new Vector2d(-hldCurvesBox.Min.X, -hldCurvesBox.Min.Y);
        }

        public void SetPlacement(Vector2d offset, double elevation)
        {
            Offset = offset;
            Elevation = elevation;
        }

        public Transform PlacementTransform => Transform.Translation(Offset.X, Offset.Y, Elevation);

        // ------------------------------------------------------------------ fills

        sealed class Vote
        {
            public SourceItem Source;
            public int Face;
            public double Weight;
            public Vector3d Normal;
            public double Depth;
        }

        /// <summary>
        /// Splits the plane into the closed regions bounded by <paramref name="drawingCurves"/>
        /// (in drawing coordinates) and identifies the surface visible in each.
        /// </summary>
        public List<DrawingFill> BuildFills(IList<Curve> drawingCurves, Func<bool> cancelled)
        {
            var fills = new List<DrawingFill>();
            var plane = new Plane(new Point3d(0, 0, Elevation), Vector3d.ZAxis);

            CurveBooleanRegions regions = Curve.CreateBooleanRegions(drawingCurves, plane, false, _tol);
            if (regions == null || regions.RegionCount == 0)
            {
                Warnings.Add("No closed regions were found in the drawing curves.");
                return fills;
            }

            int count = regions.RegionCount;
            int missed = 0;
            bool meter = Rhino.UI.StatusBar.ShowProgressMeter(0, count, "Chameleon fills", true, true) != 0;
            try
            {
                for (int r = 0; r < count; r++)
                {
                    if (r % 25 == 0)
                    {
                        if (meter) Rhino.UI.StatusBar.UpdateProgressMeter(r, true);
                        RhinoApp.Wait();   // lets Rhino process the Escape key
                    }
                    if (cancelled != null && cancelled()) { Warnings.Add("Cancelled; fills are incomplete."); break; }

                    Curve[] boundaries = regions.RegionCurves(r);
                    if (boundaries == null || boundaries.Length == 0) continue;

                    var fill = ClassifyRegion(boundaries, plane);
                    if (fill == null) { missed++; continue; }
                    fills.Add(fill);
                }
            }
            finally
            {
                if (meter) Rhino.UI.StatusBar.HideProgressMeter();
                regions.Dispose();
            }

            if (missed > 0)
                Warnings.Add($"{missed} of {count} region(s) showed background (no surface) and were left unfilled.");
            return fills;
        }

        DrawingFill ClassifyRegion(Curve[] boundaries, Plane plane)
        {
            var samples = SampleInterior(boundaries, plane);
            if (samples.Count == 0) return null;

            var votes = new Dictionary<long, Vote>();
            double missWeight = 0, hitWeight = 0;
            foreach (var (pt, w) in samples)
            {
                if (!CastRay(pt.X, pt.Y, out SourceItem src, out int face, out Vector3d n, out double depth))
                {
                    missWeight += w;
                    continue;
                }
                hitWeight += w;
                long key = ((long)src.Index << 32) | (uint)face;
                if (!votes.TryGetValue(key, out Vote v))
                    votes[key] = v = new Vote { Source = src, Face = face };
                v.Weight += w;
                v.Normal += n * w;
                v.Depth += depth * w;
            }
            if (votes.Count == 0 || missWeight > hitWeight) return null;

            Vote best = votes.Values.OrderByDescending(v => v.Weight).First();
            var normal = best.Normal;
            if (!normal.Unitize()) normal = -_camDir;

            return new DrawingFill
            {
                Boundaries = boundaries,
                Source = best.Source,
                FaceIndex = best.Face,
                Normal = normal,
                Orientation = Classify(normal),
                Depth = best.Depth / best.Weight,
            };
        }

        /// <summary>Points strictly inside a region, weighted by the area they represent.</summary>
        List<(Point3d, double)> SampleInterior(Curve[] boundaries, Plane plane)
        {
            const int maxSamples = 7;
            var result = new List<(Point3d, double)>();
            var tris = new List<(Point3d, double)>();

            Brep[] breps = Brep.CreatePlanarBreps(boundaries, _tol);
            if (breps != null)
            {
                foreach (var b in breps)
                {
                    var meshes = Mesh.CreateFromBrep(b, MeshingParameters.FastRenderMesh);
                    if (meshes == null) continue;
                    foreach (var m in meshes)
                    {
                        foreach (var f in m.Faces)
                        {
                            Point3d a = m.Vertices[f.A], bb = m.Vertices[f.B], c = m.Vertices[f.C];
                            AddTri(tris, a, bb, c);
                            if (f.IsQuad) AddTri(tris, a, c, m.Vertices[f.D]);
                        }
                    }
                }
            }

            foreach (var (pt, area) in tris.OrderByDescending(t => t.Item2))
            {
                if (result.Count >= maxSamples) break;
                if (IsInside(pt, boundaries, plane)) result.Add((pt, area));
            }

            if (result.Count == 0)
            {
                var amp = AreaMassProperties.Compute(boundaries[0]);
                if (amp != null && IsInside(amp.Centroid, boundaries, plane))
                    result.Add((amp.Centroid, amp.Area));
            }
            return result;
        }

        static void AddTri(List<(Point3d, double)> tris, Point3d a, Point3d b, Point3d c)
        {
            double area = 0.5 * Vector3d.CrossProduct(b - a, c - a).Length;
            if (area <= 0) return;
            tris.Add((new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3), area));
        }

        bool IsInside(Point3d pt, Curve[] boundaries, Plane plane)
        {
            if (boundaries[0].Contains(pt, plane, _tol) != PointContainment.Inside) return false;
            for (int i = 1; i < boundaries.Length; i++)
                if (boundaries[i].Contains(pt, plane, _tol) != PointContainment.Outside) return false;
            return true;
        }

        double DepthOf(Point3d p) => (p - _camLoc) * _camDir;

        /// <summary>Line of sight through a drawing point; returns the first surface hit.</summary>
        bool CastRay(double x, double y, out SourceItem hitSource, out int brepFace, out Vector3d normal, out double depth)
        {
            hitSource = null; brepFace = -1; normal = Vector3d.Unset; depth = 0;

            var a = new Point3d(x - Offset.X, y - Offset.Y, _zLo);
            var b = new Point3d(x - Offset.X, y - Offset.Y, _zHi);
            a.Transform(_hldToWorld);
            b.Transform(_hldToWorld);
            if (!a.IsValid || !b.IsValid) return false;
            if (DepthOf(a) > DepthOf(b)) { var t = a; a = b; b = t; }

            var dir = b - a;
            if (!dir.Unitize()) return false;
            var line = new Line(a, b);
            var ray = new Ray3d(a, dir);

            double bestT = double.MaxValue;
            int bestFace = -1;
            SourceItem best = null;
            foreach (var s in Sources)
            {
                if (s.RayMesh == null) continue;
                if (!Intersection.LineBox(line, s.RayBox, 0.0, out Interval _)) continue;
                double t = Intersection.MeshRay(s.RayMesh, ray, out int[] faceIds);
                if (t >= 0 && t < bestT && faceIds != null && faceIds.Length > 0)
                {
                    bestT = t; best = s; bestFace = faceIds[0];
                }
            }
            if (best == null) return false;

            hitSource = best;
            brepFace = best.BrepFaceFromMeshFace(bestFace);
            normal = new Vector3d(best.RayMesh.FaceNormals[bestFace]);
            if (normal * dir > 0) normal = -normal;    // the side that faces the viewer
            depth = DepthOf(ray.PointAt(bestT));
            return true;
        }

        SurfaceOrientation Classify(Vector3d n)
        {
            double ang = RhinoMath.ToRadians(Math.Max(0.0, Math.Min(45.0, _opt.OrientationAngle)));
            if (n.Z >= Math.Cos(ang)) return SurfaceOrientation.Up;
            if (n.Z <= -Math.Cos(ang)) return SurfaceOrientation.Down;
            if (Math.Abs(n.Z) <= Math.Sin(ang))
            {
                double deg = RhinoMath.ToDegrees(Math.Atan2(n.Y, n.X));
                if (deg < 0) deg += 360;
                if (deg < 45 || deg >= 315) return SurfaceOrientation.VerticalE;
                if (deg < 135) return SurfaceOrientation.VerticalN;
                if (deg < 225) return SurfaceOrientation.VerticalW;
                return SurfaceOrientation.VerticalS;
            }
            return n.Z > 0 ? SurfaceOrientation.SlopedUp : SurfaceOrientation.SlopedDown;
        }

        /// <summary>Assigns DepthBand 0 (nearest) .. bands-1 (farthest) by equal depth intervals.</summary>
        public static void AssignDepthBands(List<DrawingFill> fills, int bands)
        {
            if (bands <= 0 || fills.Count == 0) return;
            double lo = fills.Min(f => f.Depth), hi = fills.Max(f => f.Depth);
            double range = hi - lo;
            foreach (var f in fills)
            {
                int band = range > 0 ? (int)Math.Floor((f.Depth - lo) / range * bands) : 0;
                f.DepthBand = Math.Max(0, Math.Min(bands - 1, band));
            }
        }
    }
}
