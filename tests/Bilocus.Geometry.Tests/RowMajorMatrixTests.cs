// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // The bake's matrix arrives the same as the preview's: 16 row-major
    // floats, translation in 3, 7, 11 (DESIGN.md 5.2). The preview passes it
    // to the GPU, the bake applies it to the vertices before writing the
    // document: here the convention becomes the real position of a Revit
    // element, and a reading mistake would move the element without any
    // error.
    public class RowMajorMatrixTests
    {
        private const double Tolerance = 1e-9;

        private static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        private static void AssertPoints(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance,
                    string.Format("component {0}: expected {1}, got {2}", i, expected[i], actual[i]));
            }
        }

        [Fact]
        public void Identity_LeavesPointsWhereTheyAre()
        {
            double[] world = RowMajorMatrix.TransformPoints(Identity(), new float[] { 1f, 2f, 3f, -4f, 5f, 0.5f });
            AssertPoints(new double[] { 1, 2, 3, -4, 5, 0.5 }, world);
        }

        [Fact]
        public void Translation_IsReadFromElements3And7And11()
        {
            float[] matrix = { 1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 30, 0, 0, 0, 1 };

            double[] world = RowMajorMatrix.TransformPoints(matrix, new float[] { 1f, 2f, 3f, 0f, 0f, 0f });

            AssertPoints(new double[] { 11, 22, 33, 10, 20, 30 }, world);
        }

        // Pins down the convention in the other direction: a translation
        // written where a column-major matrix would put it must not move
        // anything. Whoever one day sends the transposed matrix would see
        // objects stay at the origin, instead of ending up in a plausible
        // but wrong place.
        [Fact]
        public void TranslationInTheColumnMajorSlots_IsNotRead()
        {
            float[] matrix = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 10, 20, 30, 1 };

            double[] world = RowMajorMatrix.TransformPoints(matrix, new float[] { 1f, 2f, 3f });

            AssertPoints(new double[] { 1, 2, 3 }, world);
        }

        [Fact]
        public void RotationOf90DegreesAboutZ_TurnsXIntoY()
        {
            float[] matrix = { 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

            double[] world = RowMajorMatrix.TransformPoints(matrix,
                new float[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f });

            AssertPoints(new double[] { 0, 1, 0, -1, 0, 0, 0, 0, 1 }, world);
            Assert.Equal(1.0, RowMajorMatrix.Determinant3x3(matrix), 9);
        }

        // Blender's matrix_world is T * R * S: scale 2, rotation of 90
        // degrees about Z, translation (5, 6, 7). Point (1, 1, 1) becomes
        // (2, 2, 2), then (-2, 2, 2), then (3, 8, 9).
        [Fact]
        public void ScaleRotationAndTranslation_Compose()
        {
            float[] matrix = { 0, -2, 0, 5, 2, 0, 0, 6, 0, 0, 2, 7, 0, 0, 0, 1 };

            double[] world = RowMajorMatrix.TransformPoints(matrix, new float[] { 1f, 1f, 1f });

            AssertPoints(new double[] { 3, 8, 9 }, world);
            Assert.Equal(8.0, RowMajorMatrix.Determinant3x3(matrix), 9);
        }

        // The result is double on purpose: at a hundred thousand meters
        // from the origin a float32 has steps of nearly a centimeter, and
        // the rounding would end up in the geometry written to the
        // document.
        [Fact]
        public void Result_IsComputedInDouble()
        {
            float[] matrix = { 1, 0, 0, 100000f, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

            double[] world = RowMajorMatrix.TransformPoints(matrix, new float[] { 0.001f, 0f, 0f });

            Assert.Equal(100000.0 + (double)0.001f, world[0]);
            Assert.NotEqual((double)(100000f + 0.001f), world[0]);
        }

        [Fact]
        public void Determinant_OfIdentity_IsOne()
        {
            Assert.Equal(1.0, RowMajorMatrix.Determinant3x3(Identity()), 12);
        }

        [Fact]
        public void Determinant_OfAScale_IsTheProductOfTheFactors()
        {
            float[] matrix = { 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 0, 0, 0, 1 };
            Assert.Equal(24.0, RowMajorMatrix.Determinant3x3(matrix), 12);
        }

        [Fact]
        public void Determinant_IgnoresTheTranslation()
        {
            float[] matrix = { 1, 0, 0, 50, 0, 1, 0, -70, 0, 0, 1, 9, 0, 0, 0, 1 };
            Assert.Equal(1.0, RowMajorMatrix.Determinant3x3(matrix), 12);
        }

        // A mirrored scale flips the normals: it is the negative determinant
        // that tells BakeFaceSet to reverse the order of the loops.
        [Fact]
        public void NegativeScaleOnX_HasANegativeDeterminant()
        {
            float[] matrix = { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

            Assert.True(RowMajorMatrix.Determinant3x3(matrix) < 0);
            Assert.Equal(-1.0, RowMajorMatrix.Determinant3x3(matrix), 12);
            AssertPoints(new double[] { -1, 2, 3 },
                RowMajorMatrix.TransformPoints(matrix, new float[] { 1f, 2f, 3f }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(15)]
        [InlineData(17)]
        public void MatrixOfWrongLength_IsRejected(int length)
        {
            float[] matrix = new float[length];

            Assert.Throws<ArgumentException>(delegate
            {
                RowMajorMatrix.TransformPoints(matrix, new float[] { 0f, 0f, 0f });
            });
            Assert.Throws<ArgumentException>(delegate { RowMajorMatrix.Determinant3x3(matrix); });
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void NonFiniteMatrix_IsRejected(float bad)
        {
            float[] matrix = Identity();
            matrix[7] = bad;

            Assert.Throws<ArgumentException>(delegate
            {
                RowMajorMatrix.TransformPoints(matrix, new float[] { 0f, 0f, 0f });
            });
            Assert.Throws<ArgumentException>(delegate { RowMajorMatrix.Determinant3x3(matrix); });
        }

        [Fact]
        public void NullArguments_AreRejected()
        {
            Assert.Throws<ArgumentNullException>(delegate
            {
                RowMajorMatrix.TransformPoints(null, new float[] { 0f, 0f, 0f });
            });
            Assert.Throws<ArgumentNullException>(delegate { RowMajorMatrix.TransformPoints(Identity(), null); });
            Assert.Throws<ArgumentNullException>(delegate { RowMajorMatrix.Determinant3x3(null); });
        }

        [Fact]
        public void PositionsNotMultipleOfThree_AreRejected()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                RowMajorMatrix.TransformPoints(Identity(), new float[] { 0f, 0f, 0f, 1f });
            });
        }
    }
}
