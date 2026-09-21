// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // How far a polygon strays from its own plane.
    //
    // Used by the bake to decide, polygon by polygon, whether a Blender face
    // can become a whole TessellatedFace ("a planar face bounded by a
    // polyline", from the API documentation) or must fall back to
    // triangles. Only the measurement lives here: Revit decides the
    // threshold, with its own tolerance.
    //
    // Newell's normal, not the cross product of two edges. That depends on
    // WHICH edges are chosen, and on a concave quad or one with three nearly
    // collinear vertices it gives a wrong or null normal. Newell sums the
    // contribution of every edge: it is the polygon's average normal,
    // stable even on concave ones.
    public static class PolygonPlanarity
    {
        // Below this length of Newell's normal (which equals twice the
        // projected area) the polygon has no defined plane.
        public const double DegenerateNormalLength = 1e-12;

        // points: flat xyz coordinates. The polygon is the count indices of
        // faceVertices starting at start.
        //
        // The distance is measured from the plane through the vertices'
        // centroid, with Newell's normal.
        //
        // Degenerate polygon: degenerate = true and the returned value is
        // infinite, on purpose. Whoever compares against the tolerance and
        // forgets to check degenerate still gets "not planar", i.e. the
        // fallback to triangles, rather than a whole face with no area.
        public static double MaxDeviation(double[] points, int[] faceVertices, int start, int count, out bool degenerate)
        {
            if (points == null) throw new ArgumentNullException("points");
            if (faceVertices == null) throw new ArgumentNullException("faceVertices");
            if (points.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "points must have a length that is a multiple of 3, has " + points.Length);
            }
            if (count < 3)
            {
                throw new ArgumentException("a polygon has at least 3 vertices, here " + count);
            }
            if (start < 0 || start > faceVertices.Length - count)
            {
                throw new ArgumentException(string.Format(
                    "polygon of {0} vertices starting at {1} is out of faceVertices, which is {2} long",
                    count, start, faceVertices.Length));
            }

            int pointCount = points.Length / 3;
            for (int k = 0; k < count; k++)
            {
                int v = faceVertices[start + k];
                if (v < 0 || v >= pointCount)
                {
                    throw new ArgumentException(string.Format(
                        "vertex index {0} is out of range 0..{1}", v, pointCount - 1));
                }
            }

            double cx = 0, cy = 0, cz = 0;
            for (int k = 0; k < count; k++)
            {
                int at = faceVertices[start + k] * 3;
                cx += points[at];
                cy += points[at + 1];
                cz += points[at + 2];
            }
            cx /= count;
            cy /= count;
            cz /= count;

            // Newell on the vertices MINUS the centroid. Mathematically the
            // translation changes nothing; numerically it does: with world
            // coordinates a kilometer from the origin the (zi + zj) terms
            // are large, and the differences that matter end up in the last
            // digits.
            double nx = 0, ny = 0, nz = 0;
            for (int k = 0; k < count; k++)
            {
                int a = faceVertices[start + k] * 3;
                int b = faceVertices[start + (k + 1) % count] * 3;

                double ax = points[a] - cx, ay = points[a + 1] - cy, az = points[a + 2] - cz;
                double bx = points[b] - cx, by = points[b + 1] - cy, bz = points[b + 2] - cz;

                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
            }

            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < DegenerateNormalLength)
            {
                degenerate = true;
                return double.PositiveInfinity;
            }
            degenerate = false;

            nx /= length;
            ny /= length;
            nz /= length;

            double max = 0;
            for (int k = 0; k < count; k++)
            {
                int at = faceVertices[start + k] * 3;
                double distance = Math.Abs(
                    (points[at] - cx) * nx + (points[at + 1] - cy) * ny + (points[at + 2] - cz) * nz);
                if (distance > max) { max = distance; }
            }
            return max;
        }
    }
}
