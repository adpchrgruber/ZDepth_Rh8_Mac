using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ZDepth
{
    /// <summary>Triangles of one object (render meshes joined), placed by a transform.</summary>
    internal sealed class DepthItem
    {
        public float[] Vertices;   // x, y, z
        public int[] Triangles;    // three vertex indices each
        public Transform Xform;
    }

    /// <summary>Collects the render meshes of the objects a view draws, for the CPU rasterizer.</summary>
    internal static class DepthScene
    {
        public static List<DepthItem> Collect(RhinoDoc doc, IEnumerable<RhinoObject> objects)
        {
            var parameters = doc.GetMeshingParameters(doc.MeshingParameterStyle);
            var meshes = new Dictionary<uint, (float[] vertices, int[] triangles)>();
            var items = new List<DepthItem>();
            foreach (var obj in objects)
                Collect(obj, Transform.Identity, parameters, meshes, items);
            return items;
        }

        static void Collect(RhinoObject obj, Transform xform, MeshingParameters parameters,
            Dictionary<uint, (float[], int[])> meshes, List<DepthItem> items)
        {
            if (obj is InstanceObject instance)
            {
                var definition = instance.InstanceDefinition;
                if (definition == null)
                    return;
                var t = xform * instance.InstanceXform;
                foreach (var part in definition.GetObjects())
                    Collect(part, t, parameters, meshes, items);
                return;
            }
            if (!meshes.TryGetValue(obj.RuntimeSerialNumber, out var mesh))
            {
                mesh = Triangulate(obj, parameters);
                meshes[obj.RuntimeSerialNumber] = mesh;
            }
            if (mesh.Item1 != null)
                items.Add(new DepthItem { Vertices = mesh.Item1, Triangles = mesh.Item2, Xform = xform });
        }

        /// <summary>The object's render meshes as one triangle list; meshes it first if Rhino has not yet.</summary>
        static (float[], int[]) Triangulate(RhinoObject obj, MeshingParameters parameters)
        {
            var parts = obj.GetMeshes(MeshType.Render);
            if ((parts == null || parts.Length == 0) && obj.IsMeshable(MeshType.Render))
            {
                obj.CreateMeshes(MeshType.Render, parameters, false);
                parts = obj.GetMeshes(MeshType.Render);
            }
            if (parts == null || parts.Length == 0)
                return (null, null);
            var vertices = new List<float>();
            var triangles = new List<int>();
            foreach (var part in parts.Where(m => m != null))
            {
                int offset = vertices.Count / 3;
                vertices.AddRange(part.Vertices.ToFloatArray());
                triangles.AddRange(part.Faces.ToIntArray(true).Select(i => i + offset));
            }
            return (vertices.ToArray(), triangles.ToArray());
        }
    }
}
