using System.Drawing;
using Rhino.Display;

namespace ZDepth
{
    /// <summary>Creates and maintains the "Z Depth" display mode.</summary>
    internal static class ZDepthDisplayMode
    {
        public const string Name = "Z Depth";

        public static DisplayModeDescription Find() => DisplayModeDescription.FindByName(Name);

        /// <summary>
        /// Finds or creates the mode (as a copy of Shaded, so Rhino keeps building render
        /// meshes for it) and strips everything that is not a surface.
        /// </summary>
        public static DisplayModeDescription Ensure()
        {
            var mode = Find();
            if (mode == null)
            {
                var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.ShadedId, Name);
                mode = DisplayModeDescription.GetDisplayMode(id);
                if (mode == null)
                    return null;
            }

            var a = mode.DisplayAttributes;
            // The conduit paints the background itself; this keeps the mode consistent where Rhino honours it.
            a.FillMode = DisplayPipelineAttributes.FrameBufferFillMode.SolidColor;
            a.SetFill(Color.Black);
            a.ViewSpecificAttributes.DrawGrid = false;
            a.ViewSpecificAttributes.DrawGridAxes = false;
            a.ViewSpecificAttributes.DrawWorldAxes = false;
            a.ShowCurves = false;
            a.ShowPoints = false;
            a.ShowPointClouds = false;
            a.ShowIsoCurves = false;
            a.ShowSurfaceEdges = false;
            a.ShowTangentEdges = false;
            a.ShowTangentSeams = false;
            a.ShowClipIntersectionEdges = false;
            a.MeshSpecificAttributes.ShowMeshWires = false;
            a.ShowAnnotations = false;
            a.ShowText = false;
            a.ShowLights = false;
            a.ShowClippingPlanes = false;
            a.ShowGrips = false;
            a.CastShadows = false;
            // Newer than the RhinoCommon we compile against; set them when the running Rhino has them.
            foreach (var name in new[] { "PostProcessGamma", "ShowSubDEdges", "ShowSubDBoundary", "ShowSubDCreases", "ShowSubDNonmanifoldEdges", "ShowClippingEdges" })
            {
                var p = typeof(DisplayPipelineAttributes).GetProperty(name);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                    p.SetValue(a, false);
            }

            DisplayModeDescription.UpdateDisplayMode(mode);
            return Find();
        }
    }
}
