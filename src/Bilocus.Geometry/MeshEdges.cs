// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;

namespace Bilocus.Geometry
{
    // Feature edges of a mesh, used to draw the preview outlines: open
    // borders and edges between faces meeting at an angle above a threshold.
    // Diagonals of quads, triangulation edges inside flat n-gons and the
    // shallow edges of smooth surfaces stay out, so the drawing reads like the
    // object and not like its triangulation.
    //
    // Blender sends vertices split per corner (different normals), so the
    // adjacency is rebuilt by welding positions. Face normals are computed
    // from the triangles and compared in absolute value: their sign depends
    // on the vertex order, which is not guaranteed to be consistent.
    public static class MeshEdges
    {
        // Meters: well below any modeling detail, well above float noise.
        private const double WeldTolerance = 1e-5;

        public const double DefaultAngleDegrees = 20.0;

        // Returns pairs of chunk-local vertex indices, two per line.
        public static int[] FeatureLines(float[] positions, int[] indices, double minAngleDegrees)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (indices == null) throw new ArgumentNullException("indices");

            int vertexCount = positions.Length / 3;
            int[] welded = new int[vertexCount];
            List<int> representative = new List<int>();
            Dictionary<WeldKey, int> weld = new Dictionary<WeldKey, int>();
            for (int v = 0; v < vertexCount; v++)
            {
                WeldKey key = new WeldKey(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
                int id;
                if (!weld.TryGetValue(key, out id))
                {
                    id = representative.Count;
                    weld[key] = id;
                    representative.Add(v);
                }
                welded[v] = id;
            }

            double cosLimit = Math.Cos(minAngleDegrees * Math.PI / 180.0);
            Dictionary<long, Edge> edges = new Dictionary<long, Edge>();
            List<long> order = new List<long>();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                double nx, ny, nz;
                if (!UnitNormal(positions, indices[t], indices[t + 1], indices[t + 2], out nx, out ny, out nz))
                {
                    // Degenerate triangle: it has no direction to compare.
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    int a = welded[indices[t + k]];
                    int b = welded[indices[t + (k + 1) % 3]];
                    if (a == b)
                    {
                        continue;
                    }
                    long key = (long)Math.Min(a, b) * representative.Count + Math.Max(a, b);
                    Edge edge;
                    if (!edges.TryGetValue(key, out edge))
                    {
                        edge = new Edge
                        {
                            A = representative[a],
                            B = representative[b],
                            Nx = nx,
                            Ny = ny,
                            Nz = nz,
                        };
                        edges[key] = edge;
                        order.Add(key);
                    }
                    else if (Math.Abs(edge.Nx * nx + edge.Ny * ny + edge.Nz * nz) < cosLimit)
                    {
                        edge.Sharp = true;
                    }
                    edge.Count++;
                }
            }

            List<int> lines = new List<int>();
            foreach (long key in order)
            {
                Edge edge = edges[key];
                if (edge.Count == 1 || edge.Sharp)
                {
                    lines.Add(edge.A);
                    lines.Add(edge.B);
                }
            }
            return lines.ToArray();
        }

        private static bool UnitNormal(float[] p, int i0, int i1, int i2,
                                       out double nx, out double ny, out double nz)
        {
            double ax = p[i1 * 3] - p[i0 * 3];
            double ay = p[i1 * 3 + 1] - p[i0 * 3 + 1];
            double az = p[i1 * 3 + 2] - p[i0 * 3 + 2];
            double bx = p[i2 * 3] - p[i0 * 3];
            double by = p[i2 * 3 + 1] - p[i0 * 3 + 1];
            double bz = p[i2 * 3 + 2] - p[i0 * 3 + 2];
            nx = ay * bz - az * by;
            ny = az * bx - ax * bz;
            nz = ax * by - ay * bx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12)
            {
                return false;
            }
            nx /= length;
            ny /= length;
            nz /= length;
            return true;
        }

        private sealed class Edge
        {
            public int A;
            public int B;
            public double Nx;
            public double Ny;
            public double Nz;
            public int Count;
            public bool Sharp;
        }

        private struct WeldKey : IEquatable<WeldKey>
        {
            private readonly long _x;
            private readonly long _y;
            private readonly long _z;

            public WeldKey(double x, double y, double z)
            {
                _x = (long)Math.Round(x / WeldTolerance);
                _y = (long)Math.Round(y / WeldTolerance);
                _z = (long)Math.Round(z / WeldTolerance);
            }

            public bool Equals(WeldKey other)
            {
                return _x == other._x && _y == other._y && _z == other._z;
            }

            public override bool Equals(object obj)
            {
                return obj is WeldKey && Equals((WeldKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (int)(_x * 73856093L ^ _y * 19349663L ^ _z * 83492791L);
                }
            }
        }
    }
}
