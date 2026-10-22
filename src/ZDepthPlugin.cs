using Rhino;
using Rhino.PlugIns;

namespace ZDepth
{
    ///<summary>
    /// ZDepth: a depth (z-buffer) view and capture for Rhino 8. Rhino's own ShowZBuffer draws
    /// nothing on the Mac (RH-82309); this does the same job on Mac and Windows.
    ///</summary>
    public class ZDepthPlugin : PlugIn
    {
        public ZDepthPlugin()
        {
            Instance = this;
        }

        ///<summary>Gets the only instance of the ZDepthPlugin plug-in.</summary>
        public static ZDepthPlugin Instance { get; private set; }

        internal ZDepthConduit Conduit { get; private set; }

        // Load with Rhino so the display mode is never selected without the conduit behind it.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            var mode = ZDepthDisplayMode.Ensure();
            if (mode == null)
            {
                errorMessage = "Could not create the \"" + ZDepthDisplayMode.Name + "\" display mode.";
                return LoadReturnCode.ErrorShowDialog;
            }
            Conduit = new ZDepthConduit(mode.Id) { Enabled = true };
            RhinoDoc.DeleteRhinoObject += (s, e) => Conduit.Forget(e.TheObject.RuntimeSerialNumber);
            RhinoDoc.CloseDocument += (s, e) => Conduit.ForgetAll();
            RhinoDoc.ModifyObjectAttributes += (s, e) => Conduit.Forget(e.RhinoObject.RuntimeSerialNumber); // per-object mesh settings
            RhinoDoc.InstanceDefinitionTableEvent += (s, e) => Conduit.ForgetAll();     // block edits change instances
            RhinoDoc.DocumentPropertiesChanged += (s, e) => Conduit.ForgetAll();        // render mesh quality
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            if (Conduit != null)
                Conduit.Enabled = false;
        }
    }
}
