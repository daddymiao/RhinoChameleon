using Rhino;

namespace Chameleon
{
    ///<summary>
    /// Chameleon: Make2D with a solid fill for every visible surface, placed on layers that
    /// mirror the source layers so the drawing can be restyled by layer in Illustrator.
    /// Commands: ChameleonMake2D, ChameleonFill.
    ///</summary>
    public class ChameleonPlugin : Rhino.PlugIns.PlugIn
    {
        public ChameleonPlugin()
        {
            Instance = this;
        }

        ///<summary>Gets the only instance of the ChameleonPlugin plug-in.</summary>
        public static ChameleonPlugin Instance { get; private set; }
    }
}
