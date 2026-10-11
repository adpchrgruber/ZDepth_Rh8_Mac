using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Geometry;

namespace ZDepth
{
    /// <summary>Depth per pixel, rows top to bottom; NaN where no surface is.</summary>
    internal sealed class DepthImage
    {
        public int Width, Height;
        public float[] Depth;
        public int Hits;

        /// <summary>Nearest and farthest geometry inside the frustum, hidden or not.</summary>
        public Interval GeometryRange = Interval.Unset;

        /// <summary>Nearest and farthest visible surface.</summary>
        public Interval VisibleRange = Interval.Unset;
    }

    /// <summary>
    /// Renders depth on the CPU: clips each triangle to the frustum (and to clipping
    /// planes), then rasterizes it with a z-buffer. Independent of the display pipeline,
    /// so it is exact, at any size, and the same on Mac and Windows.
    /// </summary>
    internal static class DepthRaster
    {
        public static DepthImage Render(DepthCamera camera, IList<DepthItem> items, IList<ClipPlane> extraPlanes = null)
        {
            int width = camera.Width, height = camera.Height;
            var planes = camera.FrustumPlanes();
            if (extraPlanes != null)
                planes.AddRange(extraPlanes);
            var planeArray = planes.ToArray();

            // Depth as order-preserving integer keys, so threads can keep the minimum with CompareExchange.
            var keys = new int[width * height];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = int.MaxValue;

            var worldToCamera = camera.WorldToCamera;
            double lo = double.MaxValue, hi = double.MinValue;
            var rangeLock = new object();

            foreach (var item in items)
            {
                var m = worldToCamera * item.Xform;
                var v = item.Vertices;
                var t = item.Triangles;
                int vertexCount = v.Length / 3;
                var cam = new ClipVertex[vertexCount];
                for (int i = 0, k = 0; i < vertexCount; i++, k += 3)
                {
                    double x = v[k], y = v[k + 1], z = v[k + 2];
                    cam[i] = new ClipVertex
                    {
                        X = m.M00 * x + m.M01 * y + m.M02 * z + m.M03,
                        Y = m.M10 * x + m.M11 * y + m.M12 * z + m.M13,
                        Depth = m.M20 * x + m.M21 * y + m.M22 * z + m.M23,
                    };
                }

                int triangleCount = t.Length / 3;
                Parallel.For(0, (triangleCount + 1023) / 1024,
                    () => (lo: double.MaxValue, hi: double.MinValue, a: new ClipVertex[3 + planeArray.Length], b: new ClipVertex[3 + planeArray.Length], screen: new double[3 * (3 + planeArray.Length)]),
                    (chunk, _, local) =>
                    {
                        int end = Math.Min(triangleCount, (chunk + 1) * 1024);
                        for (int f = chunk * 1024; f < end; f++)
                        {
                            var poly = local.a;
                            poly[0] = cam[t[3 * f]];
                            poly[1] = cam[t[3 * f + 1]];
                            poly[2] = cam[t[3 * f + 2]];
                            int n = ClipToPlanes(ref poly, ref local.b, 3, planeArray);
                            local.a = poly;
                            if (n < 3)
                                continue;
                            for (int i = 0; i < n; i++)
                            {
                                if (poly[i].Depth < local.lo)
                                    local.lo = poly[i].Depth;
                                if (poly[i].Depth > local.hi)
                                    local.hi = poly[i].Depth;
                            }
                            RasterizeFan(camera, poly, n, keys, local.screen);
                        }
                        return local;
                    },
                    local =>
                    {
                        lock (rangeLock)
                        {
                            lo = Math.Min(lo, local.lo);
                            hi = Math.Max(hi, local.hi);
                        }
                    });
            }

            var depth = new float[keys.Length];
            int hits = 0;
            float vlo = float.MaxValue, vhi = float.MinValue;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == int.MaxValue)
                {
                    depth[i] = float.NaN;
                    continue;
                }
                float d = FromKey(keys[i]);
                depth[i] = d;
                hits++;
                if (d < vlo)
                    vlo = d;
                if (d > vhi)
                    vhi = d;
            }
            return new DepthImage
            {
                Width = width,
                Height = height,
                Depth = depth,
                Hits = hits,
                GeometryRange = lo <= hi ? new Interval(lo, hi) : Interval.Unset,
                VisibleRange = hits > 0 ? new Interval(vlo, vhi) : Interval.Unset,
            };
        }

        /// <summary>Clips a polygon to all planes, ping-ponging between the two buffers.</summary>
        static int ClipToPlanes(ref ClipVertex[] poly, ref ClipVertex[] scratch, int n, ClipPlane[] planes)
        {
            foreach (var plane in planes)
            {
                // Skip the work when the polygon is entirely inside this plane.
                bool inside = true;
                for (int i = 0; i < n && inside; i++)
                    inside = plane.Eval(poly[i]) >= 0;
                if (inside)
                    continue;
                n = ClipVertex.Clip(poly, n, plane, scratch);
                var swap = poly;
                poly = scratch;
                scratch = swap;
                if (n < 3)
                    return n;
            }
            return n;
        }

        static void RasterizeFan(DepthCamera camera, ClipVertex[] poly, int n, int[] keys, double[] s)
        {
            // Per vertex: screen x, y, and the attribute that is linear on screen
            // (1/depth in perspective, depth itself in parallel projection).
            for (int i = 0; i < n; i++)
            {
                camera.ToPixel(poly[i].X, poly[i].Y, poly[i].Depth, out s[3 * i], out s[3 * i + 1]);
                s[3 * i + 2] = camera.Parallel ? poly[i].Depth : 1.0 / poly[i].Depth;
            }
            for (int i = 1; i + 1 < n; i++)
                RasterizeTriangle(camera, s[0], s[1], s[2], s[3 * i], s[3 * i + 1], s[3 * i + 2], s[3 * i + 3], s[3 * i + 4], s[3 * i + 5], keys);
        }

        static void RasterizeTriangle(DepthCamera camera, double x0, double y0, double q0, double x1, double y1, double q1,
            double x2, double y2, double q2, int[] keys)
        {
            double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area) < 1e-12)
                return;
            int width = camera.Width, height = camera.Height;
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) - 0.5));
            int maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2)) - 0.5));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) - 0.5));
            int maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2)) - 0.5));
            double inv = 1.0 / area;
            bool parallel = camera.Parallel;
            for (int y = minY; y <= maxY; y++)
            {
                double cy = y + 0.5;
                for (int x = minX; x <= maxX; x++)
                {
                    double cx = x + 0.5;
                    // Barycentric weights; pixels on an edge count for both triangles (no cracks).
                    double w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) * inv;
                    double w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) * inv;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9)
                        continue;
                    double qq = w0 * q0 + w1 * q1 + w2 * q2;
                    float d = (float)(parallel ? qq : 1.0 / qq);
                    int key = ToKey(d);
                    int i = y * width + x;
                    int current = Volatile.Read(ref keys[i]);
                    while (key < current)
                    {
                        int seen = Interlocked.CompareExchange(ref keys[i], key, current);
                        if (seen == current)
                            break;
                        current = seen;
                    }
                }
            }
        }

        // Float to an int with the same order (negative floats included).
        static int ToKey(float f)
        {
            int bits = new FloatBits { Float = f }.Int;
            return bits >= 0 ? bits : bits ^ 0x7FFFFFFF;
        }

        static float FromKey(int key) => new FloatBits { Int = key >= 0 ? key : key ^ 0x7FFFFFFF }.Float;

        // BitConverter.SingleToInt32Bits is missing from .NET Framework 4.8.
        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public int Int;
        }
    }
}
