// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // The matrix of a Blender object split between the family instance and
    // the family geometry.
    //
    // A level-hosted instance can do two things: sit at a point and rotate
    // in plan. Everything else in the matrix (tilts about X and Y, scale,
    // mirroring) has nowhere to go except inside the family. Hence the
    // decomposition:
    //
    //   world = Rz(AngleRadians) * ToFamilyPoints(local) + OriginMeters
    //
    // which is the property asserted in every test. If it does not hold,
    // the instance in Revit is not where the preview is, and nobody flags
    // it.
    //
    // Row-major like RowMajorMatrix, meters, double. No Revit API: feet and
    // XYZ are FamilyBaker's job.
    public sealed class FamilyPlacement
    {
        // Below this length the plan-view projection of an axis has no
        // reliable direction: atan2 of two near-zeros returns an arbitrary
        // angle, decided by float32 noise.
        private const double DegenerateLength = 1e-9;

        private readonly double[] _origin;

        // Linear part, row-major, in double.
        private readonly double _m00, _m01, _m02;
        private readonly double _m10, _m11, _m12;
        private readonly double _m20, _m21, _m22;

        // Cosine and sine of -AngleRadians, computed once: ToFamilyPoints
        // loops over every vertex of the object.
        private readonly double _cos;
        private readonly double _sin;

        // Rotation about Z to give the instance, in radians, counterclockwise
        // as seen from above like in Revit. In (-pi, pi].
        public double AngleRadians { get; private set; }

        // Negative determinant of the 3x3 part: the geometry inside the
        // family is mirrored and the loops must be reversed, as in the
        // DirectShape bake. The plan rotation does not change the sign, so
        // it holds for both the world and the family points.
        public bool FlipWinding { get; private set; }

        // Translation of the matrix (elements 3, 7, 11), meters. The
        // instance's insertion point, absolute Z included: FamilyBaker moves
        // the instance onto it after creation. A fresh copy on every read, because it
        // is an array.
        public double[] OriginMeters
        {
            get { return (double[])_origin.Clone(); }
        }

        private FamilyPlacement(float[] matrix16)
        {
            _m00 = matrix16[0]; _m01 = matrix16[1]; _m02 = matrix16[2];
            _m10 = matrix16[4]; _m11 = matrix16[5]; _m12 = matrix16[6];
            _m20 = matrix16[8]; _m21 = matrix16[9]; _m22 = matrix16[10];

            _origin = new double[] { matrix16[3], matrix16[7], matrix16[11] };

            AngleRadians = PlanAngle();
            _cos = Math.Cos(-AngleRadians);
            _sin = Math.Sin(-AngleRadians);
        }

        // Raises ArgumentNullException / ArgumentException for a null
        // matrix, one of the wrong length, or with non-finite values: the
        // same checks as RowMajorMatrix, which already does them in the
        // determinant.
        public static FamilyPlacement Decompose(float[] matrix16)
        {
            double determinant = RowMajorMatrix.Determinant3x3(matrix16);

            FamilyPlacement placement = new FamilyPlacement(matrix16);
            placement.FlipWinding = determinant < 0;
            return placement;
        }

        // Family points, meters, double: the linear part applied to the
        // local points and then the plan rotation removed. WITHOUT
        // translation: the family origin coincides with the object's
        // origin, so the family stays close to zero even for an object
        // kilometers away.
        public double[] ToFamilyPoints(float[] localPositions)
        {
            if (localPositions == null) throw new ArgumentNullException("localPositions");
            if (localPositions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "localPositions must have a length that is a multiple of 3, has " + localPositions.Length);
            }

            double[] family = new double[localPositions.Length];
            for (int i = 0; i < localPositions.Length; i += 3)
            {
                double x = localPositions[i];
                double y = localPositions[i + 1];
                double z = localPositions[i + 2];

                double wx = _m00 * x + _m01 * y + _m02 * z;
                double wy = _m10 * x + _m11 * y + _m12 * z;
                double wz = _m20 * x + _m21 * y + _m22 * z;

                family[i] = _cos * wx - _sin * wy;
                family[i + 1] = _sin * wx + _cos * wy;
                family[i + 2] = wz;
            }
            return family;
        }

        // The plan angle is the direction of the local X axis once
        // transformed and projected onto XY. With a non-uniform scale the
        // linear part is not a rotation, but the X axis remains a good
        // stand-in for "which way the object faces", and it is what the
        // user sees in Blender.
        //
        // Vertical X axis (object lying on its side): its projection has no
        // direction. It falls back to the Y axis, minus a quarter turn, so
        // an object that in Blender has its Y axis pointing north gives
        // angle 0 the same way an X axis pointing east would have. Y
        // degenerate too: angle 0, and the whole linear part ends up in the
        // family.
        // Bake together: the points of ANOTHER object, in the family system
        // of this placement (the active object's). The member's own matrix
        // brings its local points into the world, then the family's origin
        // and plan rotation are taken away. For the placement's own matrix
        // it gives the same result as ToFamilyPoints.
        public double[] ToFamilyPointsOf(float[] memberMatrix16, float[] localPositions)
        {
            if (memberMatrix16 == null) throw new ArgumentNullException("memberMatrix16");
            if (memberMatrix16.Length != 16)
            {
                throw new ArgumentException("memberMatrix16 must have 16 elements, has " + memberMatrix16.Length);
            }
            if (localPositions == null) throw new ArgumentNullException("localPositions");
            if (localPositions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "localPositions must have a length that is a multiple of 3, has " + localPositions.Length);
            }

            double[] family = new double[localPositions.Length];
            for (int i = 0; i < localPositions.Length; i += 3)
            {
                double x = localPositions[i];
                double y = localPositions[i + 1];
                double z = localPositions[i + 2];

                double wx = memberMatrix16[0] * x + memberMatrix16[1] * y + memberMatrix16[2] * z + memberMatrix16[3] - _origin[0];
                double wy = memberMatrix16[4] * x + memberMatrix16[5] * y + memberMatrix16[6] * z + memberMatrix16[7] - _origin[1];
                double wz = memberMatrix16[8] * x + memberMatrix16[9] * y + memberMatrix16[10] * z + memberMatrix16[11] - _origin[2];

                family[i] = _cos * wx - _sin * wy;
                family[i + 1] = _sin * wx + _cos * wy;
                family[i + 2] = wz;
            }
            return family;
        }

        private double PlanAngle()
        {
            // Column 0 of the linear part: the image of the X axis.
            double xx = _m00;
            double xy = _m10;
            if (Math.Sqrt(xx * xx + xy * xy) >= DegenerateLength)
            {
                return Math.Atan2(xy, xx);
            }

            double yx = _m01;
            double yy = _m11;
            if (Math.Sqrt(yx * yx + yy * yy) >= DegenerateLength)
            {
                return Normalize(Math.Atan2(yy, yx) - Math.PI / 2.0);
            }

            return 0.0;
        }

        // Brings it back into (-pi, pi]: atan2 - pi/2 can go as low as
        // -3pi/2. It does not change the geometry, but an instance rotated
        // by -270 degrees instead of +90 reads badly in Revit's properties.
        private static double Normalize(double angle)
        {
            if (angle <= -Math.PI) { return angle + 2.0 * Math.PI; }
            if (angle > Math.PI) { return angle - 2.0 * Math.PI; }
            return angle;
        }
    }
}
