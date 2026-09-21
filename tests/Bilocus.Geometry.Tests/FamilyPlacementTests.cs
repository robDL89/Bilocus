// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // The decomposition of an object's matrix for the family bake:
    // translation and plan rotation go to the instance, everything else
    // (tilts, scale, mirroring) ends up baked into the family geometry.
    //
    // An error here does not raise exceptions: it puts the instance in the
    // wrong place, or bakes skewed geometry into a straight family. That is
    // why every test asserts the same property, which is the very
    // definition of the decomposition: putting the rotation and translation
    // back onto the family points must reproduce, within 1e-9, the world
    // points from RowMajorMatrix.
    public class FamilyPlacementTests
    {
        private const double Tolerance = 1e-9;

        // Tolerance for comparisons against hand-written values: the matrix
        // travels in float32, and a sine of 30 degrees in float32 is not
        // exactly 0.5. The property itself is instead verified in tight
        // double precision.
        private const double FloatTolerance = 1e-6;

        // A non-trivial point cloud: the origin, the three axes, a generic
        // point with negative components.
        private static readonly float[] SamplePoints =
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f,
            0f, 0f, 1f,
            1.5f, -2.25f, 0.75f
        };

        private static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        private static float[] RotationZ(double degrees)
        {
            double a = degrees * Math.PI / 180.0;
            float c = (float)Math.Cos(a);
            float s = (float)Math.Sin(a);
            return new float[] { c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        // Rz(angle) * family points + origin == matrix applied to the local
        // points. This is the property that ties the instance and the
        // family together.
        private static void AssertRebuildsTheWorldPoints(float[] matrix, float[] local)
        {
            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);
            double[] family = placement.ToFamilyPoints(local);
            double[] world = RowMajorMatrix.TransformPoints(matrix, local);

            Assert.Equal(world.Length, family.Length);

            double c = Math.Cos(placement.AngleRadians);
            double s = Math.Sin(placement.AngleRadians);
            double[] origin = placement.OriginMeters;

            for (int i = 0; i < family.Length; i += 3)
            {
                double x = c * family[i] - s * family[i + 1] + origin[0];
                double y = s * family[i] + c * family[i + 1] + origin[1];
                double z = family[i + 2] + origin[2];

                AssertClose(world[i], x, Tolerance, "x of point " + i / 3);
                AssertClose(world[i + 1], y, Tolerance, "y of point " + i / 3);
                AssertClose(world[i + 2], z, Tolerance, "z of point " + i / 3);
            }
        }

        private static void AssertClose(double expected, double actual, double tolerance, string what)
        {
            Assert.True(Math.Abs(expected - actual) < tolerance,
                string.Format("{0}: expected {1}, got {2}", what, expected, actual));
        }

        private static void AssertPoints(double[] expected, double[] actual, double tolerance)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                AssertClose(expected[i], actual[i], tolerance, "component " + i);
            }
        }

        private static double[] ToDouble(float[] values)
        {
            double[] result = new double[values.Length];
            for (int i = 0; i < values.Length; i++) { result[i] = values[i]; }
            return result;
        }

        [Fact]
        public void Identity_IsAnUnrotatedInstanceAtTheOrigin()
        {
            float[] matrix = Identity();

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            Assert.Equal(new double[] { 0, 0, 0 }, placement.OriginMeters);
            Assert.Equal(0.0, placement.AngleRadians);
            Assert.False(placement.FlipWinding);
            AssertPoints(ToDouble(SamplePoints), placement.ToFamilyPoints(SamplePoints), Tolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // The translation goes ENTIRELY to the instance: the family points
        // stay the local ones, so the family origin is the Blender object's
        // origin.
        [Fact]
        public void TranslationOnly_GoesToTheOrigin_AndLeavesTheFamilyLocal()
        {
            float[] matrix = { 1, 0, 0, 10, 0, 1, 0, -20, 0, 0, 1, 1.25f, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            Assert.Equal(new double[] { 10, -20, 1.25 }, placement.OriginMeters);
            Assert.Equal(0.0, placement.AngleRadians);
            Assert.False(placement.FlipWinding);
            AssertPoints(ToDouble(SamplePoints), placement.ToFamilyPoints(SamplePoints), Tolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // A plan rotation belongs entirely to the instance: the geometry
        // inside the family stays straight, equal to the local one. It is
        // the case the user recognizes in Revit by rotating the instance by
        // hand.
        [Fact]
        public void RotationAboutZ_GoesToTheInstance_AndTheFamilyIsTheLocalGeometry()
        {
            float[] matrix = RotationZ(30);
            matrix[3] = 3f;
            matrix[7] = 4f;
            matrix[11] = 5f;

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            AssertClose(30.0 * Math.PI / 180.0, placement.AngleRadians, FloatTolerance, "angolo");
            Assert.Equal(new double[] { 3, 4, 5 }, placement.OriginMeters);
            Assert.False(placement.FlipWinding);
            AssertPoints(ToDouble(SamplePoints), placement.ToFamilyPoints(SamplePoints), FloatTolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // A tilt cannot be given to a level-hosted instance: it ends up
        // baked into the geometry, and the instance does not rotate.
        [Fact]
        public void RotationAboutX_IsBakedIntoTheFamily_WithAngleZero()
        {
            // Rx(90): local Y becomes Z, local Z becomes -Y.
            float[] matrix = { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            Assert.Equal(0.0, placement.AngleRadians);
            Assert.False(placement.FlipWinding);
            AssertPoints(new double[] { 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -1, 0, 1.5, -0.75, -2.25 },
                placement.ToFamilyPoints(SamplePoints), Tolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // Blender's matrix_world is T * R * S. With a non-uniform scale the
        // linear part is not a pure rotation, but the local X axis stays in
        // the direction rotated by 45 degrees: the instance rotates by 45,
        // the scale stays in the family.
        [Fact]
        public void RotationAboutZWithNonUniformScale_RotatesTheInstance_AndBakesTheScale()
        {
            double a = Math.PI / 4.0;
            float c = (float)Math.Cos(a);
            float s = (float)Math.Sin(a);
            // R * diag(2, 3, 0.5), then translation.
            float[] matrix = { 2 * c, -3 * s, 0, 7, 2 * s, 3 * c, 0, 8, 0, 0, 0.5f, 9, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            AssertClose(a, placement.AngleRadians, FloatTolerance, "angolo");
            Assert.False(placement.FlipWinding);
            AssertPoints(new double[] { 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0.5, 3, -6.75, 0.375 },
                placement.ToFamilyPoints(SamplePoints), FloatTolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // Scale -1 on X: the negative determinant tells BakeFaceSet to
        // reverse the loops, as in the DirectShape bake. The local X axis
        // points to world -X, so the angle is a half turn and the mirroring
        // stays on Y in the family: still a mirroring, so the flip is
        // needed.
        [Fact]
        public void NegativeScaleOnX_FlipsTheWinding()
        {
            float[] matrix = { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            Assert.True(placement.FlipWinding);
            AssertClose(Math.PI, Math.Abs(placement.AngleRadians), Tolerance, "angolo");
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // Vertical local X axis: its plan-view projection has no direction.
        // It falls back to the local Y axis, minus a quarter turn, so a
        // "standing" object rotated by 30 degrees in plan still gives an
        // instance rotated by 30.
        [Fact]
        public void VerticalXAxis_FallsBackOnTheYAxis()
        {
            // Rz(30) * Ry(-90): local X -> world Z, local Y -> Y rotated by
            // 30 degrees in plan, local Z -> -X rotated by 30 degrees.
            // Proper rotation, determinant +1.
            double a = 30.0 * Math.PI / 180.0;
            float c = (float)Math.Cos(a);
            float s = (float)Math.Sin(a);
            float[] matrix = { 0, -s, -c, 1, 0, c, -s, 2, 1, 0, 0, 3, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            AssertClose(a, placement.AngleRadians, FloatTolerance, "angolo");
            Assert.False(placement.FlipWinding);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // The projected Y axis is also null (degenerate matrix, e.g. zero
        // scale in plan): no direction to give the instance, angle 0 and
        // everything baked into the family.
        [Fact]
        public void DegenerateXAndYAxes_GiveAngleZero()
        {
            float[] matrix = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 1, 0, 0, 0, 0, 1 };

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            Assert.Equal(0.0, placement.AngleRadians);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        // A far-off translation stays in the instance and does not taint
        // the family points: the family stays small and close to its
        // origin even for an object kilometers away.
        [Fact]
        public void FarTranslation_DoesNotReachTheFamilyPoints()
        {
            float[] matrix = RotationZ(-60);
            matrix[3] = 12000f;
            matrix[7] = -3500f;

            FamilyPlacement placement = FamilyPlacement.Decompose(matrix);

            AssertClose(-60.0 * Math.PI / 180.0, placement.AngleRadians, FloatTolerance, "angolo");
            AssertPoints(ToDouble(SamplePoints), placement.ToFamilyPoints(SamplePoints), FloatTolerance);
            AssertRebuildsTheWorldPoints(matrix, SamplePoints);
        }

        [Fact]
        public void OriginMeters_IsACopy()
        {
            FamilyPlacement placement = FamilyPlacement.Decompose(Identity());

            placement.OriginMeters[0] = 99;

            Assert.Equal(0.0, placement.OriginMeters[0]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(15)]
        [InlineData(17)]
        public void MatrixOfWrongLength_IsRejected(int length)
        {
            Assert.Throws<ArgumentException>(delegate { FamilyPlacement.Decompose(new float[length]); });
        }

        [Fact]
        public void NonFiniteMatrix_IsRejected()
        {
            float[] matrix = Identity();
            matrix[5] = float.NaN;

            Assert.Throws<ArgumentException>(delegate { FamilyPlacement.Decompose(matrix); });
        }

        [Fact]
        public void NullArguments_AreRejected()
        {
            Assert.Throws<ArgumentNullException>(delegate { FamilyPlacement.Decompose(null); });
            Assert.Throws<ArgumentNullException>(delegate
            {
                FamilyPlacement.Decompose(Identity()).ToFamilyPoints(null);
            });
        }

        [Fact]
        public void PositionsNotMultipleOfThree_AreRejected()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                FamilyPlacement.Decompose(Identity()).ToFamilyPoints(new float[] { 0f, 0f });
            });
        }
    }
}
