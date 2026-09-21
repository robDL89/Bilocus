// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // The sketch-plane normals for proxies, chosen so that beams traced with
    // Pick Lines do not roll.
    //
    // A beam taken from a ModelCurve inherits the section's orientation
    // from the curve's sketch plane. Measured in the field on 2026-09-16: a
    // nearly flat arc on a downhill stretch had its plane tilted by 40
    // degrees and the beam rotated along with it; six arcs of an S had the
    // normal pointing down and the others pointing up, depending on the
    // direction of travel. Hence two rules:
    //
    // 1. A non-vertical line sits on the "plan" plane: the one containing
    //    the line and the perpendicular horizontal. It is only tilted as
    //    much as the line's slope, never rolled sideways. PerpendicularVector
    //    used to be used, which for a steep line picks an X or Y axis and
    //    gives a rotated plane; it stays as a fallback for vertical lines,
    //    which have no plan plane.
    // 2. Any normal is oriented with the same convention, so the direction
    //    of travel does not matter: upward; if the plane is vertical,
    //    positive X, and on a tie positive Y.
    public static class ProxyPlaneNormal
    {
        // Below this horizontal component the direction is treated as
        // vertical: the plan plane is undefined.
        private const double MinHorizontal = 1e-6;

        // Below this Z component the normal is treated as horizontal, i.e.
        // the plane as vertical. Wide on purpose: points arrive in float32,
        // and an arc in an elevation view can have a normal with a Z on the
        // order of 1e-5 instead of zero.
        private const double VerticalPlaneTolerance = 1e-3;

        // The plan plane's normal for a line of the given direction, already
        // oriented upward. Null if the line is vertical.
        public static double[] ForLine(double[] direction)
        {
            if (direction == null) throw new ArgumentNullException("direction");

            double length = Math.Sqrt(direction[0] * direction[0]
                + direction[1] * direction[1] + direction[2] * direction[2]);
            if (length < 1e-12)
            {
                throw new ArgumentException("null direction: no plane defined");
            }

            double dx = direction[0] / length;
            double dy = direction[1] / length;
            double dz = direction[2] / length;

            if (Math.Sqrt(dx * dx + dy * dy) < MinHorizontal) { return null; }

            // Z minus its projection onto the direction: perpendicular to
            // the line, in the vertical plane that contains it, with
            // positive Z.
            double nx = -dx * dz;
            double ny = -dy * dz;
            double nz = 1.0 - dz * dz;

            double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return new double[] { nx / normalLength, ny / normalLength, nz / normalLength };
        }

        // The same normal with the convention's sign. Does not normalize.
        public static double[] Orient(double[] normal)
        {
            if (normal == null) throw new ArgumentNullException("normal");

            bool flip;
            if (Math.Abs(normal[2]) > VerticalPlaneTolerance)
            {
                flip = normal[2] < 0;
            }
            else if (Math.Abs(normal[0]) > VerticalPlaneTolerance)
            {
                flip = normal[0] < 0;
            }
            else
            {
                flip = normal[1] < 0;
            }

            return flip
                ? new double[] { -normal[0], -normal[1], -normal[2] }
                : new double[] { normal[0], normal[1], normal[2] };
        }
    }
}
