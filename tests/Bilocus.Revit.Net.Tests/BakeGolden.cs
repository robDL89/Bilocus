// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Bake;

namespace Bilocus.Revit.Net.Tests
{
    // The golden bake_mesh vector from the router side: the same 144 bytes
    // that BakeMeshPayloadTests reads field by field, used here as "any
    // valid object" for the batch and dispatch tests.
    internal static class BakeGolden
    {
        // Copied verbatim from the Phase B plan, "Wire contract".
        public const string Hex =
            "0000000000000000000000000000803f00000000000000000000803f0000803f"
            + "00000000000000000000803f000000000000003f0000003f0000803f04000000"
            + "0300000000000000010000000200000003000000000000000100000004000000"
            + "0000000001000000020000000000000002000000030000000000000001000000"
            + "04000000000000000000000001000000";

        // Row-major with a translation of 2 on X: different from identity, so
        // a test that finds it again in the request proves it was really read.
        public const string MatrixJson = "[1, 0, 0, 2, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]";

        public static byte[] Payload()
        {
            byte[] bytes = new byte[Hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(Hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        public static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        public static BakeMeshRequest Request(string objectId, string name)
        {
            return BakeMeshRequest.Parse(objectId, name, BakeCategory.DefaultCategory, Identity(),
                5, 2, 7, 3, Payload());
        }

        // With the spacing of json.dumps, like the header example in the
        // plan: it is what really arrives from Blender.
        public static string MeshHeader(string objectId)
        {
            return MeshHeader(objectId, BakeCategory.DefaultCategory, MatrixJson);
        }

        public static string MeshHeader(string objectId, string category, string matrixJson)
        {
            return "{\"type\": \"bake_mesh\", \"obj_id\": \"" + objectId + "\", \"name\": \"Cube\", "
                + "\"category\": \"" + category + "\", \"matrix\": " + matrixJson + ", "
                + "\"vert_count\": 5, \"face_count\": 2, \"loop_count\": 7, \"tri_count\": 3}";
        }

        // The same header with fields appended, for example
        // "\"accept_open\": true" from Phase B2: the Blender side writes them
        // after tri_count.
        public static string MeshHeaderWith(string objectId, string extraFieldsJson)
        {
            string header = MeshHeader(objectId);
            return header.Substring(0, header.Length - 1) + ", " + extraFieldsJson + "}";
        }
    }
}
