using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ZDepth
{
    /// <summary>
    /// In viewports using the Z Depth display mode: suppresses Rhino's own object drawing
    /// and redraws each object's render meshes with unlit vertex colours, grey by distance
    /// along the camera direction. Also records, during a probe capture, what a view draws.
    /// </summary>
    /// <remarks>
    /// Depth is affine in position and the GPU interpolates vertex colours
    /// perspective-correctly, so a vertex-coloured triangle shows its exact depth, as long
    /// as no vertex has to be clamped to white or black. Triangles that cross the near or
    /// far end of the range are therefore cut there each frame, and the pieces beyond the
    /// range drawn flat.
    /// </remarks>
    internal class ZDepthConduit : DisplayConduit
    {
        readonly Guid _modeId;
        readonly List<RhinoObject> _pending = new List<RhinoObject>();
        readonly List<(Entry entry, Transform xform)> _items = new List<(Entry, Transform)>();
        // One mesh copy per object, and per occurrence for block parts (each instance needs its own colours).
        readonly Dictionary<(uint serial, int occurrence), Entry> _entries = new Dictionary<(uint, int), Entry>();
        readonly Dictionary<uint, int> _occurrences = new Dictionary<uint, int>();
        ClipVertex[] _polyA = new ClipVertex[16], _polyB = new ClipVertex[16];
        int[] _outside = new int[0];

        static readonly Color[] Greys = Enumerable.Range(0, 256).Select(g => Color.FromArgb(255, g, g, g)).ToArray();

        class Entry
        {
            public Mesh Mesh;          // triangles only
            public float[] Vertices;   // x, y, z
            public int[] Triangles;
            public float[] X, Y, Depth;   // camera space, this frame
            public Color[] Colors;
            public readonly List<int> Hidden = new List<int>();   // faces cut this frame
            public Mesh Band;          // the cut pieces, this frame
        }

        public ZDepthConduit(Guid modeId)
        {
            _modeId = modeId;
        }

        /// <summary>Near and far (model units) of the most recent Z Depth frame.</summary>
        public Interval LastRange { get; private set; }

        /// <summary>While set, frames of that viewport draw nothing and record objects and frustum.</summary>
        public DepthProbe Probe { get; set; }

        bool IsActive(RhinoViewport viewport) => viewport.DisplayMode?.Id == _modeId;

        bool IsProbing(RhinoViewport viewport) => Probe != null && viewport.Id == Probe.ViewportId;

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (IsProbing(e.Viewport) || !IsActive(e.Viewport))
                return;
            _pending.Clear();
            // Background = far. Rhino does not reliably apply the mode's solid fill in every view.
            var background = ZDepthSettings.NearIsWhite ? Color.Black : Color.White;
            var b = e.Viewport.Bounds;
            e.Display.Draw2dRectangle(new Rectangle(-2, -2, b.Width + 4, b.Height + 4), background, 0, background);
        }

        protected override void PreDrawObject(DrawObjectEventArgs e)
        {
            var obj = e.RhinoObject;
            if (IsProbing(e.Viewport))
            {
                e.DrawObject = false;
                if (obj != null && !obj.IsInstanceDefinitionGeometry)
                    Probe.Add(obj);
                return;
            }
            if (!IsActive(e.Viewport))
                return;
            e.DrawObject = false;
            // Rhino also calls this for the parts of each block instance; the instance draws them.
            if (obj != null && !obj.IsInstanceDefinitionGeometry)
                _pending.Add(obj);
        }

        // Drawn here rather than in PreDrawObject, as in Clown ID: there the pipeline state
        // of the first object sticks to all of them.
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (IsProbing(e.Viewport))
            {
                Probe.Passes.Add(DepthCamera.FromViewport(e.Viewport, Probe.Width, Probe.Height));
                return;
            }
            if (!IsActive(e.Viewport))
                return;

            foreach (var entry in _entries.Values)
                Restore(entry);
            _items.Clear();
            _occurrences.Clear();
            foreach (var obj in _pending)
                Collect(obj, Transform.Identity);
            _pending.Clear();

            var camera = DepthCamera.FromViewport(e.Viewport, e.Viewport.Size.Width, e.Viewport.Size.Height);
            var worldToCamera = camera.WorldToCamera;
            foreach (var (entry, xform) in _items)
                ToCamera(entry, worldToCamera * xform);

            Interval range;
            if (ZDepthSettings.TryGetFixedRange(e.RhinoDoc, out var near, out var far))
                range = new Interval(near, far);
            else
                range = GeometryRange(camera.FrustumPlanes().ToArray());
            LastRange = range;
            Colorize(range, ZDepthSettings.NearIsWhite);

            foreach (var (entry, xform) in _items)
            {
                bool identity = xform.IsIdentity;
                if (!identity)
                    e.Display.PushModelTransform(xform);
                e.Display.DrawMeshFalseColors(entry.Mesh);
                if (entry.Band != null)
                    e.Display.DrawMeshFalseColors(entry.Band);
                if (!identity)
                    e.Display.PopModelTransform();
            }
        }

        static void ToCamera(Entry entry, Transform m)
        {
            var v = entry.Vertices;
            for (int i = 0, k = 0; i < entry.Depth.Length; i++, k += 3)
            {
                double x = v[k], y = v[k + 1], z = v[k + 2];
                entry.X[i] = (float)(m.M00 * x + m.M01 * y + m.M02 * z + m.M03);
                entry.Y[i] = (float)(m.M10 * x + m.M11 * y + m.M12 * z + m.M13);
                entry.Depth[i] = (float)(m.M20 * x + m.M21 * y + m.M22 * z + m.M23);
            }
        }

        /// <summary>
        /// Nearest and farthest geometry inside the view frustum (hidden or not), with
        /// triangles clipped at the frustum's sides, as <see cref="DepthRaster"/> does.
        /// </summary>
        Interval GeometryRange(ClipPlane[] planes)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var (entry, _) in _items)
            {
                var t = entry.Triangles;
                // Outside bits per vertex: bit p set when outside plane p.
                if (_outside.Length < entry.Depth.Length)
                    _outside = new int[entry.Depth.Length];
                var outside = _outside;
                for (int i = 0; i < entry.Depth.Length; i++)
                {
                    var v = Vertex(entry, i);
                    int bits = 0;
                    for (int p = 0; p < planes.Length; p++)
                        if (planes[p].Eval(v) < 0)
                            bits |= 1 << p;
                    outside[i] = bits;
                }
                for (int k = 0; k < t.Length; k += 3)
                {
                    int o0 = outside[t[k]], o1 = outside[t[k + 1]], o2 = outside[t[k + 2]];
                    if ((o0 & o1 & o2) != 0)
                        continue;
                    if ((o0 | o1 | o2) == 0)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            double d = entry.Depth[t[k + j]];
                            if (d < lo)
                                lo = d;
                            if (d > hi)
                                hi = d;
                        }
                        continue;
                    }
                    var poly = _polyA;
                    var scratch = _polyB;
                    for (int j = 0; j < 3; j++)
                        poly[j] = Vertex(entry, t[k + j]);
                    int n = 3;
                    for (int p = 0; p < planes.Length && n >= 3; p++)
                    {
                        if (((o0 | o1 | o2) & (1 << p)) == 0)
                            continue;
                        n = ClipVertex.Clip(poly, n, planes[p], scratch);
                        var swap = poly;
                        poly = scratch;
                        scratch = swap;
                    }
                    for (int j = 0; j < n && n >= 3; j++)
                    {
                        if (poly[j].Depth < lo)
                            lo = poly[j].Depth;
                        if (poly[j].Depth > hi)
                            hi = poly[j].Depth;
                    }
                }
            }
            if (lo > hi)
                return new Interval(0, 1);
            if (hi - lo < 1e-9)
                hi = lo + 1e-9;
            return new Interval(lo, hi);
        }

        void Colorize(Interval range, bool nearIsWhite)
        {
            double lo = range.T0, hi = range.T1;
            // grey = a * depth + b: near maps to 255 (or 0), far to 0 (or 255).
            double a = 255.0 / (hi - lo), b = -lo * a;
            if (nearIsWhite)
            {
                a = -a;
                b = 255 - b;
            }
            var nearGrey = Greys[nearIsWhite ? 255 : 0];
            var farGrey = Greys[nearIsWhite ? 0 : 255];
            var keepFar = new ClipPlane(0, 0, 1, -lo);   // depth >= lo
            var keepNear = new ClipPlane(0, 0, -1, lo);  // depth <= lo
            var keepBeforeHi = new ClipPlane(0, 0, -1, hi);
            var keepAfterHi = new ClipPlane(0, 0, 1, -hi);

            foreach (var (entry, _) in _items)
            {
                var d = entry.Depth;
                var colors = entry.Colors;
                for (int i = 0; i < d.Length; i++)
                    colors[i] = Grey(a * d[i] + b);
                entry.Mesh.VertexColors.SetColors(colors);

                // Cut the triangles that cross near or far; hide them and draw the pieces instead.
                entry.Band?.Dispose();
                entry.Band = null;
                var t = entry.Triangles;
                for (int f = 0, k = 0; k < t.Length; f++, k += 3)
                {
                    float d0 = d[t[k]], d1 = d[t[k + 1]], d2 = d[t[k + 2]];
                    float min = Math.Min(d0, Math.Min(d1, d2)), max = Math.Max(d0, Math.Max(d1, d2));
                    if (!(min < lo && max > lo) && !(min < hi && max > hi))
                        continue;
                    if (entry.Band == null)
                        entry.Band = new Mesh();
                    var triangle = new ClipVertex[3];
                    for (int j = 0; j < 3; j++)
                        triangle[j] = Vertex(entry, t[k + j]);
                    var nearPart = ClipOnce(triangle, 3, keepNear);
                    var rest = ClipOnce(triangle, 3, keepFar);
                    var middle = ClipOnce(rest, rest.Length, keepBeforeHi);
                    var farPart = ClipOnce(rest, rest.Length, keepAfterHi);
                    AddFan(entry.Band, nearPart, _ => nearGrey);
                    AddFan(entry.Band, middle, v => Grey(a * v.Depth + b));
                    AddFan(entry.Band, farPart, _ => farGrey);
                    entry.Mesh.Faces.SetFace(f, t[k], t[k], t[k]);
                    entry.Hidden.Add(f);
                }
            }
        }

        static Color Grey(double g)
        {
            g += 0.5;
            return Greys[g <= 0 ? 0 : g >= 255 ? 255 : (int)g];
        }

        static ClipVertex[] ClipOnce(ClipVertex[] poly, int n, ClipPlane plane)
        {
            var result = new ClipVertex[n + 1];
            int count = ClipVertex.Clip(poly, n, plane, result);
            Array.Resize(ref result, count);
            return result;
        }

        static void AddFan(Mesh band, ClipVertex[] poly, Func<ClipVertex, Color> color)
        {
            if (poly.Length < 3)
                return;
            int start = band.Vertices.Count;
            foreach (var p in poly)
            {
                band.Vertices.Add(p.LX, p.LY, p.LZ);
                band.VertexColors.Add(color(p));
            }
            for (int i = 1; i + 1 < poly.Length; i++)
                band.Faces.AddFace(start, start + i, start + i + 1);
        }

        static ClipVertex Vertex(Entry entry, int i)
        {
            var v = entry.Vertices;
            return new ClipVertex
            {
                X = entry.X[i],
                Y = entry.Y[i],
                Depth = entry.Depth[i],
                LX = v[3 * i],
                LY = v[3 * i + 1],
                LZ = v[3 * i + 2],
            };
        }

        static void Restore(Entry entry)
        {
            var t = entry.Triangles;
            foreach (var f in entry.Hidden)
                entry.Mesh.Faces.SetFace(f, t[3 * f], t[3 * f + 1], t[3 * f + 2]);
            entry.Hidden.Clear();
        }

        /// <summary>Render meshes of an object; block instances recurse with their transforms.</summary>
        void Collect(RhinoObject obj, Transform xform)
        {
            if (obj is InstanceObject instance)
            {
                var definition = instance.InstanceDefinition;
                if (definition == null)
                    return;
                var t = xform * instance.InstanceXform;
                foreach (var part in definition.GetObjects())
                    Collect(part, t);
                return;
            }

            uint serial = obj.RuntimeSerialNumber;
            int occurrence = _occurrences.TryGetValue(serial, out var n) ? n : 0;
            _occurrences[serial] = occurrence + 1;
            if (!_entries.TryGetValue((serial, occurrence), out var entry))
            {
                var meshes = obj.GetMeshes(MeshType.Render);
                if (meshes == null || meshes.Length == 0)
                    return;   // not meshed yet; Rhino builds render meshes for the mode and redraws
                var joined = new Mesh();
                joined.Append(meshes);
                joined.Faces.ConvertQuadsToTriangles();
                int count = joined.Vertices.Count;
                entry = new Entry
                {
                    Mesh = joined,
                    Vertices = joined.Vertices.ToFloatArray(),
                    Triangles = joined.Faces.ToIntArray(true),
                    X = new float[count],
                    Y = new float[count],
                    Depth = new float[count],
                    Colors = new Color[count],
                };
                _entries[(serial, occurrence)] = entry;
            }
            _items.Add((entry, xform));
        }

        /// <summary>Drops the mesh copies of a deleted or replaced object.</summary>
        public void Forget(uint serial)
        {
            foreach (var key in _entries.Keys.Where(k => k.serial == serial).ToList())
            {
                _entries[key].Mesh.Dispose();
                _entries[key].Band?.Dispose();
                _entries.Remove(key);
            }
        }

        /// <summary>Drops all mesh copies (document closed, mesh settings changed).</summary>
        public void ForgetAll()
        {
            foreach (var entry in _entries.Values)
            {
                entry.Mesh.Dispose();
                entry.Band?.Dispose();
            }
            _entries.Clear();
        }
    }

    /// <summary>What a view draws in one capture: its objects and the frustum of each pass.</summary>
    internal sealed class DepthProbe
    {
        readonly HashSet<uint> _seen = new HashSet<uint>();

        public Guid ViewportId;
        public int Width, Height;
        public readonly List<RhinoObject> Objects = new List<RhinoObject>();
        public readonly List<DepthCamera> Passes = new List<DepthCamera>();

        public void Add(RhinoObject obj)
        {
            if (_seen.Add(obj.RuntimeSerialNumber))
                Objects.Add(obj);
        }
    }
}
