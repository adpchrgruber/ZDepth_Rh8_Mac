using System;
using System.Collections.Generic;
using Rhino.Display;
using Rhino.Geometry;

namespace ZDepth
{
    /// <summary>
    /// A camera, its frustum and an image size. Camera space is (x, y, depth): x to the
    /// right, y up, depth the distance from the camera along the view direction.
    /// </summary>
    internal sealed class DepthCamera
    {
        public Point3d Location;
        public Vector3d X, Y, Direction;   // unit vectors
        public bool Parallel;
        public double Left, Right, Bottom, Top, Near, Far;   // frustum on the near plane
        public int Width, Height;

        public static DepthCamera FromViewport(RhinoViewport viewport, int width, int height)
        {
            viewport.GetFrustum(out var left, out var right, out var bottom, out var top, out var near, out var far);
            var direction = viewport.CameraDirection;
            direction.Unitize();
            return new DepthCamera
            {
                Location = viewport.CameraLocation,
                X = viewport.CameraX,
                Y = viewport.CameraY,
                Direction = direction,
                Parallel = viewport.IsParallelProjection,
                Left = left,
                Right = right,
                Bottom = bottom,
                Top = top,
                Near = near,
                Far = far,
                Width = width,
                Height = height,
            };
        }

        /// <summary>World to camera space; rows 0, 1, 2 give x, y and depth.</summary>
        public Transform WorldToCamera
        {
            get
            {
                var t = Transform.Identity;
                t.M00 = X.X; t.M01 = X.Y; t.M02 = X.Z; t.M03 = -(X * (Vector3d)Location);
                t.M10 = Y.X; t.M11 = Y.Y; t.M12 = Y.Z; t.M13 = -(Y * (Vector3d)Location);
                t.M20 = Direction.X; t.M21 = Direction.Y; t.M22 = Direction.Z; t.M23 = -(Direction * (Vector3d)Location);
                return t;
            }
        }

        /// <summary>The frustum's six planes in camera space, inside where non-negative.</summary>
        public List<ClipPlane> FrustumPlanes()
        {
            double n = Near, l = Left, r = Right, b = Bottom, t = Top;
            var planes = new List<ClipPlane>
            {
                new ClipPlane(0, 0, 1, -n),    // near: depth >= n
                new ClipPlane(0, 0, -1, Far),  // far: depth <= far
            };
            if (Parallel)
            {
                planes.Add(new ClipPlane(1, 0, 0, -l));
                planes.Add(new ClipPlane(-1, 0, 0, r));
                planes.Add(new ClipPlane(0, 1, 0, -b));
                planes.Add(new ClipPlane(0, -1, 0, t));
            }
            else
            {
                // x / depth between l / n and r / n, likewise y
                planes.Add(new ClipPlane(n, 0, -l, 0));
                planes.Add(new ClipPlane(-n, 0, r, 0));
                planes.Add(new ClipPlane(0, n, -b, 0));
                planes.Add(new ClipPlane(0, -n, t, 0));
            }
            return planes;
        }

        /// <summary>A clipping plane in camera space: keeps the side its normal points to, as Rhino does.</summary>
        public ClipPlane CameraPlane(Plane world)
        {
            var n = world.Normal;
            // keep (P - O) . N >= 0, with P = Location + x X + y Y + d Direction
            return new ClipPlane(X * n, Y * n, Direction * n, (Location - world.Origin) * n);
        }

        /// <summary>Pixel position (origin top left) of a camera-space point inside the frustum.</summary>
        public void ToPixel(double x, double y, double depth, out double px, out double py)
        {
            if (!Parallel)
            {
                x *= Near / depth;
                y *= Near / depth;
            }
            px = (x - Left) / (Right - Left) * Width;
            py = (Top - y) / (Top - Bottom) * Height;
        }
    }

    /// <summary>A plane in camera space: A x + B y + C depth + E, inside where non-negative.</summary>
    internal readonly struct ClipPlane
    {
        public readonly double A, B, C, E;

        public ClipPlane(double a, double b, double c, double e)
        {
            A = a;
            B = b;
            C = c;
            E = e;
        }

        public double Eval(in ClipVertex v) => A * v.X + B * v.Y + C * v.Depth + E;
    }

    /// <summary>A polygon vertex: camera space plus the local position it came from.</summary>
    internal struct ClipVertex
    {
        public double X, Y, Depth;
        public double LX, LY, LZ;

        public static ClipVertex Lerp(in ClipVertex p, in ClipVertex q, double s) => new ClipVertex
        {
            X = p.X + s * (q.X - p.X),
            Y = p.Y + s * (q.Y - p.Y),
            Depth = p.Depth + s * (q.Depth - p.Depth),
            LX = p.LX + s * (q.LX - p.LX),
            LY = p.LY + s * (q.LY - p.LY),
            LZ = p.LZ + s * (q.LZ - p.LZ),
        };

        /// <summary>
        /// Sutherland–Hodgman: clips the polygon src[0..count) to the plane's inside, into dst.
        /// Everything is affine in position, so interpolating along edges is exact.
        /// </summary>
        public static int Clip(ClipVertex[] src, int count, in ClipPlane plane, ClipVertex[] dst)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref src[i];
                ref readonly var q = ref src[i + 1 == count ? 0 : i + 1];
                double fp = plane.Eval(p), fq = plane.Eval(q);
                if (fp >= 0)
                    dst[n++] = p;
                if ((fp >= 0) != (fq >= 0))
                    dst[n++] = Lerp(p, q, fp / (fp - fq));
            }
            return n;
        }
    }
}
