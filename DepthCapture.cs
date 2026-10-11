using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;

namespace ZDepth
{
    /// <summary>
    /// Depth of a view at any size, on the CPU. A probe capture tells which objects the
    /// view draws and its exact framing at that size; the rasterizer does the rest.
    /// </summary>
    internal static class DepthCapture
    {
        public static DepthImage Render(ZDepthConduit conduit, RhinoDoc doc, RhinoView view, int width, int height, out DepthCamera camera)
        {
            camera = Probe(conduit, view, width, height, out var objects);
            if (camera == null)
                return null;
            var items = DepthScene.Collect(doc, objects);
            return DepthRaster.Render(camera, items, ClippingPlanes(doc, view.ActiveViewport, camera));
        }

        /// <summary>
        /// Captures the view at the size with all object drawing suppressed, recording the
        /// objects Rhino draws and the frustum of each pass. Large captures are drawn in tiles
        /// after a first overview pass; the tiles together are the frustum.
        /// </summary>
        static DepthCamera Probe(ZDepthConduit conduit, RhinoView view, int width, int height, out List<RhinoObject> objects)
        {
            var probe = new DepthProbe { ViewportId = view.ActiveViewport.Id, Width = width, Height = height };
            conduit.Probe = probe;
            try
            {
                using (view.CaptureToBitmap(new Size(width, height), false, false, false)) { }
            }
            finally
            {
                conduit.Probe = null;
            }
            objects = probe.Objects;
            if (probe.Passes.Count == 0)
                return null;
            var passes = probe.Passes.Count > 1 ? probe.Passes.Skip(1).ToList() : probe.Passes;
            var camera = passes[0];
            camera.Left = passes.Min(p => p.Left);
            camera.Right = passes.Max(p => p.Right);
            camera.Bottom = passes.Min(p => p.Bottom);
            camera.Top = passes.Max(p => p.Top);
            camera.Width = width;
            camera.Height = height;
            return camera;
        }

        /// <summary>The document's clipping planes that cut this viewport, in camera space.</summary>
        static List<ClipPlane> ClippingPlanes(RhinoDoc doc, RhinoViewport viewport, DepthCamera camera)
        {
            var planes = new List<ClipPlane>();
            var settings = new ObjectEnumeratorSettings { ObjectTypeFilter = ObjectType.ClipPlane, HiddenObjects = false };
            foreach (var obj in doc.Objects.GetObjectList(settings).OfType<ClippingPlaneObject>())
            {
                var surface = obj.ClippingPlaneGeometry;
                if (surface != null && surface.ViewportIds().Contains(viewport.Id))
                    planes.Add(camera.CameraPlane(surface.Plane));
            }
            return planes;
        }
    }
}
