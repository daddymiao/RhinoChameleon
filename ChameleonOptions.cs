using Rhino;

namespace Chameleon
{
    /// <summary>
    /// User options shared by the Chameleon commands. Values persist between Rhino
    /// sessions through the plug-in's settings.
    /// </summary>
    internal sealed class ChameleonOptions
    {
        /// <summary>Name of the top-level layer that receives the drawing (Rhino's Make2D uses "Make2D").</summary>
        public string RootLayer = "Make2D";

        /// <summary>Also output hidden curves (Make2D::Hidden::Curves::...).</summary>
        public bool HiddenLines = false;

        /// <summary>Include tangent edges (output to Make2D::Visible::Tangents::...).</summary>
        public bool TangentEdges = true;

        /// <summary>Create the solid hatch fills.</summary>
        public bool Fills = true;

        /// <summary>Add an orientation sub-layer under each fill layer (Up, Down, Vertical-N, ...).</summary>
        public bool SplitByOrientation = false;

        /// <summary>Number of depth bands (0 = off). Adds "Depth 01" (nearest) ... sub-layers.</summary>
        public int DepthBands = 0;

        /// <summary>Merge touching fill fragments that come from the same surface into one hatch.</summary>
        public bool MergeFragments = true;

        /// <summary>Join visible curve segments of the same object into longer curves.</summary>
        public bool JoinCurves = true;

        /// <summary>Angle (degrees) used to decide whether a surface is horizontal or vertical.</summary>
        public double OrientationAngle = 10.0;

        public static ChameleonOptions Load()
        {
            var o = new ChameleonOptions();
            var s = ChameleonPlugin.Instance?.Settings;
            if (s == null) return o;
            o.RootLayer = s.GetString("RootLayer", o.RootLayer);
            o.HiddenLines = s.GetBool("HiddenLines", o.HiddenLines);
            o.TangentEdges = s.GetBool("TangentEdges", o.TangentEdges);
            o.Fills = s.GetBool("Fills", o.Fills);
            o.SplitByOrientation = s.GetBool("SplitByOrientation", o.SplitByOrientation);
            o.DepthBands = s.GetInteger("DepthBands", o.DepthBands);
            o.MergeFragments = s.GetBool("MergeFragments", o.MergeFragments);
            o.JoinCurves = s.GetBool("JoinCurves", o.JoinCurves);
            o.OrientationAngle = s.GetDouble("OrientationAngle", o.OrientationAngle);
            if (string.IsNullOrWhiteSpace(o.RootLayer)) o.RootLayer = "Make2D";
            return o;
        }

        public void Save()
        {
            var s = ChameleonPlugin.Instance?.Settings;
            if (s == null) return;
            s.SetString("RootLayer", RootLayer);
            s.SetBool("HiddenLines", HiddenLines);
            s.SetBool("TangentEdges", TangentEdges);
            s.SetBool("Fills", Fills);
            s.SetBool("SplitByOrientation", SplitByOrientation);
            s.SetInteger("DepthBands", DepthBands);
            s.SetBool("MergeFragments", MergeFragments);
            s.SetBool("JoinCurves", JoinCurves);
            s.SetDouble("OrientationAngle", OrientationAngle);
        }
    }
}
