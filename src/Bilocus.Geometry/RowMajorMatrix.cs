// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // The matrix of a Blender object applied to its local vertices.
    //
    // 16 row-major floats like the rest of the protocol (DESIGN.md 5.2): the
    // translation lives in matrix[3], matrix[7], matrix[11]. In the preview
    // the same matrix goes to the GPU and a mistake shows up right away; in
    // the bake it becomes the position of a real document element, so the
    // convention lives here, under test, and is not copied by hand inside
    // the builder.
    //
    // The last row (12..15) is not read: a Blender object's matrix_world is
    // affine and always equals 0 0 0 1. Checking it would not protect
    // against anything real and would reject matrices the rest of the
    // bridge accepts.
    public static class RowMajorMatrix
    {
        public const int Length = 16;

        // World coordinates in meters, in double. The double is not
        // pedantry: at a hundred thousand meters from the origin a float32
        // has steps of nearly a centimeter, and the rounding would end up
        // in the geometry written to the document.
        public static double[] TransformPoints(float[] matrix16, float[] positions)
        {
            CheckMatrix(matrix16);
            if (positions == null) throw new ArgumentNullException("positions");
            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }

            double m00 = matrix16[0], m01 = matrix16[1], m02 = matrix16[2], m03 = matrix16[3];
            double m10 = matrix16[4], m11 = matrix16[5], m12 = matrix16[6], m13 = matrix16[7];
            double m20 = matrix16[8], m21 = matrix16[9], m22 = matrix16[10], m23 = matrix16[11];

            double[] world = new double[positions.Length];
            for (int i = 0; i < positions.Length; i += 3)
            {
                double x = positions[i];
                double y = positions[i + 1];
                double z = positions[i + 2];

                world[i] = m00 * x + m01 * y + m02 * z + m03;
                world[i + 1] = m10 * x + m11 * y + m12 * z + m13;
                world[i + 2] = m20 * x + m21 * y + m22 * z + m23;
            }
            return world;
        }

        // Determinant of the 3x3 part, rotation and scale without
        // translation.
        //
        // Used for its sign: negative means a mirrored scale, which flips
        // the orientation of the polygons. Without reversing the loops the
        // faces would arrive in Revit with the normals facing inward.
        public static double Determinant3x3(float[] matrix16)
        {
            CheckMatrix(matrix16);

            double a = matrix16[0], b = matrix16[1], c = matrix16[2];
            double d = matrix16[4], e = matrix16[5], f = matrix16[6];
            double g = matrix16[8], h = matrix16[9], i = matrix16[10];

            return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        }

        private static void CheckMatrix(float[] matrix16)
        {
            if (matrix16 == null) throw new ArgumentNullException("matrix16");
            if (matrix16.Length != Length)
            {
                throw new ArgumentException(string.Format(
                    "matrix has {0} numbers instead of {1}", matrix16.Length, Length));
            }
            for (int k = 0; k < matrix16.Length; k++)
            {
                if (float.IsNaN(matrix16[k]) || float.IsInfinity(matrix16[k]))
                {
                    throw new ArgumentException("matrix has a non-finite value at position " + k);
                }
            }
        }
    }
}
