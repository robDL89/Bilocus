// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // Computes a normal perpendicular to the given direction, to build the
    // SketchPlane a ModelCurve is hosted on.
    //
    // The naive approach, cross(direction, Zaxis), produces the null vector
    // when the direction is parallel to Z: this happens for every vertical
    // edge, which in architecture are half of all the edges that exist. A
    // plane with a null normal makes Plane.CreateByNormalAndOrigin fail.
    //
    // The solution: pick as the reference axis the one the direction is
    // LEAST aligned with, i.e. the smallest component (in absolute value)
    // of the normalized direction. The cross product between the direction
    // and that axis can never collapse to zero, because the two vectors are
    // never parallel.
    //
    // The internal computation is in double for numerical stability in the
    // normalization; input and output stay float because the rest of the
    // project's wire format is float32 and the normal ends up in a sketch
    // plane anyway, not in a critical measurement.
    public static class PerpendicularVector
    {
        private const double MinLength = 1e-9;

        public static float[] Compute(float[] direction)
        {
            if (direction == null) throw new ArgumentNullException("direction");

            double dx = direction[0];
            double dy = direction[1];
            double dz = direction[2];

            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < MinLength)
            {
                throw new ArgumentException("null direction: it has no defined perpendicular normal");
            }

            dx /= length;
            dy /= length;
            dz /= length;

            double refX, refY, refZ;
            ChooseReferenceAxis(dx, dy, dz, out refX, out refY, out refZ);

            // Cross product direction x reference.
            double nx = dy * refZ - dz * refY;
            double ny = dz * refX - dx * refZ;
            double nz = dx * refY - dy * refX;

            double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            nx /= normalLength;
            ny /= normalLength;
            nz /= normalLength;

            return new float[] { (float)nx, (float)ny, (float)nz };
        }

        // Picks the cartesian axis the (normalized) direction is least
        // aligned with: the one whose component has the smallest absolute
        // value. On a tie, the order X, Y, Z wins.
        private static void ChooseReferenceAxis(double dx, double dy, double dz,
            out double refX, out double refY, out double refZ)
        {
            double ax = Math.Abs(dx);
            double ay = Math.Abs(dy);
            double az = Math.Abs(dz);

            if (ax <= ay && ax <= az)
            {
                refX = 1.0; refY = 0.0; refZ = 0.0;
            }
            else if (ay <= ax && ay <= az)
            {
                refX = 0.0; refY = 1.0; refZ = 0.0;
            }
            else
            {
                refX = 0.0; refY = 0.0; refZ = 1.0;
            }
        }
    }
}
