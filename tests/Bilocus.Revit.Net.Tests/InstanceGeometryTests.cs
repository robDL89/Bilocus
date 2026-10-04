// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Bilocus.Revit.Pull;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The pure half of instancing: the key that decides which instances
    // share a mesh in Blender, and the conversion of Revit's Transform into
    // the row-major matrix of the wire. A wrong key silently merges two
    // different families, a wrong matrix puts every window in the wrong
    // place: both are decided here, under test, and not in
    // ElementTessellator.
    public class InstanceGeometryTests
    {
        private static readonly float[] Triangle = new float[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f
        };

        [Fact]
        public void ComputeMeshKey_SameGeometry_SameKey()
        {
            float[] copy = (float[])Triangle.Clone();
            Assert.Equal(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(copy));
        }

        [Fact]
        public void ComputeMeshKey_Is32LowercaseHexChars()
        {
            string key = InstanceGeometry.ComputeMeshKey(Triangle);
            Assert.Equal(32, key.Length);
            foreach (char c in key)
            {
                Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), "not lowercase hex: " + key);
            }
        }

        // Float noise far below the quantum must not split identical
        // geometry into two meshes.
        [Fact]
        public void ComputeMeshKey_NoiseBelowQuantum_SameKey()
        {
            float[] noisy = (float[])Triangle.Clone();
            noisy[3] = 1.0000001f;
            Assert.Equal(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(noisy));
        }

        [Fact]
        public void ComputeMeshKey_DifferentGeometry_DifferentKey()
        {
            float[] other = (float[])Triangle.Clone();
            other[3] = 1.001f;
            Assert.NotEqual(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(other));
        }

        // Same vertices in another order are another tessellation: the key
        // is not a set hash, and does not need to be, because instances of
        // the same symbol come from the same GeometryElement.
        [Fact]
        public void ComputeMeshKey_DifferentVertexCount_DifferentKey()
        {
            float[] longer = new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f };
            Assert.NotEqual(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(longer));
        }

        [Fact]
        public void ComputeMeshKey_NonFinite_Throws()
        {
            float[] bad = (float[])Triangle.Clone();
            bad[4] = float.NaN;
            Assert.Throws<ArgumentException>(() => InstanceGeometry.ComputeMeshKey(bad));
        }

        [Fact]
        public void ComputeMeshKey_LengthNotMultipleOf3_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.ComputeMeshKey(new float[] { 1f, 2f }));
        }

        [Fact]
        public void RowMajorFromTransform_Identity_TranslationInMeters()
        {
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 10, 20, 30 });

            Assert.Equal(16, m.Length);
            Assert.Equal(1f, m[0]);
            Assert.Equal(1f, m[5]);
            Assert.Equal(1f, m[10]);
            Assert.Equal(1f, m[15]);
            Assert.Equal(3.048f, m[3], 5);
            Assert.Equal(6.096f, m[7], 5);
            Assert.Equal(9.144f, m[11], 5);
        }

        // Revit's basis vectors are the COLUMNS of the rotation: BasisX is
        // where the family's local X ends up. Row-major on the wire means
        // BasisX goes to m[0], m[4], m[8].
        [Fact]
        public void RowMajorFromTransform_RotatedBasis_GoesInColumns()
        {
            // 90 degrees around Z: local X -> world Y, local Y -> world -X
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { 0, 1, 0 }, new double[] { -1, 0, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 });

            double[] world = RowMajorMatrix.TransformPoints(m, new float[] { 1f, 0f, 0f });
            Assert.Equal(0.0, world[0], 6);
            Assert.Equal(1.0, world[1], 6);
            Assert.Equal(0.0, world[2], 6);
        }

        [Fact]
        public void RowMajorFromTransform_Mirrored_HasNegativeDeterminant()
        {
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { -1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 });

            Assert.True(RowMajorMatrix.Determinant3x3(m) < 0);
        }

        [Fact]
        public void RowMajorFromTransform_WrongLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 }));
        }

        [Fact]
        public void RowMajorFromTransform_NonFinite_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { double.NaN, 0, 0 }));
        }

        [Fact]
        public void InstancedMesh_NullArguments_Throw()
        {
            TessellatedMesh mesh = new TessellatedMesh(
                (float[])Triangle.Clone(), new float[9], new int[] { 0, 1, 2 }, new float[3]);
            float[] matrix = new float[16];

            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(null, "k", matrix));
            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(mesh, null, matrix));
            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(mesh, "k", null));
        }
    }
}
