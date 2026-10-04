// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Security.Cryptography;
using System.Text;
using Bilocus.Protocol;

namespace Bilocus.Revit.Pull
{
    // The pure half of instancing (spec 2026-10-04, section 2.2): which
    // instances share a mesh in Blender, and where each one goes.
    //
    // The key is a hash of the CONTENT of the symbol tessellation, not the
    // FamilySymbol id. Two instances of the same type can have different
    // geometry (instance parameters that drive the shape), and the id would
    // merge them silently; the content cannot. Positions are quantized to
    // KeyQuantum before hashing so that float noise does not split
    // identical geometry into two meshes.
    //
    // This file must NOT reference the Revit API: it is compiled into the
    // test project, where RevitAPI does not exist. ElementTessellator reads
    // the Transform's vectors into double arrays and hands them over.
    public static class InstanceGeometry
    {
        // 0.01 mm in meters: far below any modeling tolerance, far above
        // the float32 noise of a tessellation a few meters wide.
        public const double KeyQuantum = 1e-5;

        // 16 bytes of SHA-256 as hex: 128 bits are plenty against accidental
        // collisions on a selection, and the string stays short in the
        // custom properties Blender shows.
        private const int KeyBytes = 16;

        public static string ComputeMeshKey(float[] positions)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }

            byte[] bytes = new byte[positions.Length * 8];
            for (int i = 0; i < positions.Length; i++)
            {
                float value = positions[i];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new ArgumentException("non-finite position at index " + i);
                }

                long quantized = (long)Math.Round(value / KeyQuantum, MidpointRounding.AwayFromZero);

                // Little-endian written by hand: BitConverter would follow
                // the machine, and the key must not depend on it.
                for (int b = 0; b < 8; b++)
                {
                    bytes[i * 8 + b] = (byte)(quantized >> (8 * b));
                }
            }

            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(bytes);
            }

            StringBuilder text = new StringBuilder(KeyBytes * 2);
            for (int i = 0; i < KeyBytes; i++) { text.Append(digest[i].ToString("x2")); }
            return text.ToString();
        }

        // Revit's Transform as the wire's 16 row-major floats.
        //
        // The basis vectors are the COLUMNS of the rotation (BasisX is where
        // the family's local X ends up), so BasisX fills m[0], m[4], m[8].
        // They are unitless and stay as they are; only the origin goes from
        // feet to meters, because the payload's vertices are already in
        // meters. A mirrored instance has a basis with negative determinant
        // and arrives in Blender as a negative scale: nothing to do here.
        public static float[] RowMajorFromTransform(
            double[] basisX, double[] basisY, double[] basisZ, double[] originFeet)
        {
            CheckVector(basisX, "basisX");
            CheckVector(basisY, "basisY");
            CheckVector(basisZ, "basisZ");
            CheckVector(originFeet, "originFeet");

            double s = BridgeConstants.MetersPerFoot;
            return new float[]
            {
                (float)basisX[0], (float)basisY[0], (float)basisZ[0], (float)(originFeet[0] * s),
                (float)basisX[1], (float)basisY[1], (float)basisZ[1], (float)(originFeet[1] * s),
                (float)basisX[2], (float)basisY[2], (float)basisZ[2], (float)(originFeet[2] * s),
                0f, 0f, 0f, 1f
            };
        }

        private static void CheckVector(double[] vector, string name)
        {
            if (vector == null) throw new ArgumentNullException(name);
            if (vector.Length != 3)
            {
                throw new ArgumentException(name + " must have 3 components, has " + vector.Length);
            }
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(vector[i]) || double.IsInfinity(vector[i]))
                {
                    throw new ArgumentException(name + " has a non-finite component");
                }
            }
        }
    }

    // An instanceable element already tessellated: the symbol mesh in the
    // family's local space, its key, and where this instance puts it.
    public sealed class InstancedMesh
    {
        public TessellatedMesh Mesh { get; private set; }
        public string MeshKey { get; private set; }
        public float[] Matrix { get; private set; }

        public InstancedMesh(TessellatedMesh mesh, string meshKey, float[] matrix)
        {
            if (mesh == null) throw new ArgumentNullException("mesh");
            if (meshKey == null) throw new ArgumentNullException("meshKey");
            if (matrix == null) throw new ArgumentNullException("matrix");
            Mesh = mesh;
            MeshKey = meshKey;
            Matrix = matrix;
        }
    }
}
