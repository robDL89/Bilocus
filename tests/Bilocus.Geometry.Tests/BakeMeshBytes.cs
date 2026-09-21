// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.IO;
using Bilocus.Geometry;

namespace Bilocus.Geometry.Tests
{
    // The golden vector of bake_mesh and a packer for its layout, shared by
    // the BakeMeshPayload and BakeFaceSet tests.
    //
    // The packer exists because every violated rule has to be built by hand,
    // byte by byte, and writing it in hex would make it unreadable which
    // field is wrong. That it produces the RIGHT layout is not taken for
    // granted: a test compares it against the golden vector, otherwise the
    // tests for the violated rules could pass for the wrong reason.
    internal static class BakeMeshBytes
    {
        // Copied literally from the Phase B plan, "Wire contract". The
        // Python suite asserts the same 144 bytes: if either side changes
        // the block order or the endianness, at least one of the two suites
        // turns red.
        public const string GoldenHex =
            "0000000000000000000000000000803f00000000000000000000803f0000803f"
            + "00000000000000000000803f000000000000003f0000003f0000803f04000000"
            + "0300000000000000010000000200000003000000000000000100000004000000"
            + "0000000001000000020000000000000002000000030000000000000001000000"
            + "04000000000000000000000001000000";

        public const int GoldenVertCount = 5;
        public const int GoldenFaceCount = 2;
        public const int GoldenLoopCount = 7;
        public const int GoldenTriCount = 3;

        // New arrays on every call: a test that raises one vertex must not
        // raise it for the next one too.
        public static float[] GoldenPositions()
        {
            return new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, 1f };
        }

        public static uint[] GoldenFaceSizes() { return new uint[] { 4, 3 }; }
        public static uint[] GoldenFaceVertices() { return new uint[] { 0, 1, 2, 3, 0, 1, 4 }; }
        public static uint[] GoldenTriVertices() { return new uint[] { 0, 1, 2, 0, 2, 3, 0, 1, 4 }; }
        public static uint[] GoldenTriFaces() { return new uint[] { 0, 0, 1 }; }

        public static byte[] Golden()
        {
            return FromHex(GoldenHex);
        }

        public static BakeMeshPayload ParseGolden()
        {
            return BakeMeshPayload.Parse(
                Golden(), GoldenVertCount, GoldenFaceCount, GoldenLoopCount, GoldenTriCount);
        }

        // Convert.FromHexString does not exist on net48.
        public static byte[] FromHex(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        // BinaryWriter always writes little-endian, regardless of the machine.
        public static byte[] Pack(
            float[] positions, uint[] faceSizes, uint[] faceVertices, uint[] triVertices, uint[] triFaces)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                foreach (float value in positions) { w.Write(value); }
                foreach (uint value in faceSizes) { w.Write(value); }
                foreach (uint value in faceVertices) { w.Write(value); }
                foreach (uint value in triVertices) { w.Write(value); }
                foreach (uint value in triFaces) { w.Write(value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        // The header counts derived from the array lengths, i.e. consistent
        // by construction: the violations lie only in the values.
        public static BakeMeshPayload Parse(
            float[] positions, uint[] faceSizes, uint[] faceVertices, uint[] triVertices, uint[] triFaces)
        {
            return BakeMeshPayload.Parse(
                Pack(positions, faceSizes, faceVertices, triVertices, triFaces),
                positions.Length / 3, faceSizes.Length, faceVertices.Length, triFaces.Length);
        }
    }
}
