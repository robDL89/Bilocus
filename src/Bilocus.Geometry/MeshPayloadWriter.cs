// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // The write-side twin of MeshPayload.Parse.
    // Order: positions (vert*3 f32), normals (vert*3 f32), indices (tri*3 u32).
    // Little-endian, meters, Z-up. See DESIGN.md 5.2.
    //
    // The bytes are written by hand instead of with BitConverter for the
    // same reason as FrameCodec: the format stays little-endian by
    // definition and not by luck of the architecture it runs on.
    public static class MeshPayloadWriter
    {
        public static byte[] Write(float[] positions, float[] normals, int[] indices)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (normals == null) throw new ArgumentNullException("normals");
            if (indices == null) throw new ArgumentNullException("indices");

            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }
            if (normals.Length != positions.Length)
            {
                throw new ArgumentException(string.Format(
                    "normals is {0} long but positions is {1} long", normals.Length, positions.Length));
            }
            if (indices.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "indices must have a length that is a multiple of 3, has " + indices.Length);
            }

            int vertexCount = positions.Length / 3;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] < 0 || indices[i] >= vertexCount)
                {
                    throw new ArgumentException(string.Format(
                        "index {0} at position {1} is out of range 0..{2}",
                        indices[i], i, vertexCount - 1));
                }
            }

            byte[] payload = new byte[(positions.Length + normals.Length) * 4 + indices.Length * 4];
            int offset = 0;

            for (int i = 0; i < positions.Length; i++) { WriteFloat(payload, ref offset, positions[i]); }
            for (int i = 0; i < normals.Length; i++) { WriteFloat(payload, ref offset, normals[i]); }
            for (int i = 0; i < indices.Length; i++) { WriteUInt(payload, ref offset, (uint)indices[i]); }

            return payload;
        }

        private static void WriteFloat(byte[] buffer, ref int offset, float value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) { Array.Reverse(raw); }
            buffer[offset] = raw[0];
            buffer[offset + 1] = raw[1];
            buffer[offset + 2] = raw[2];
            buffer[offset + 3] = raw[3];
            offset += 4;
        }

        private static void WriteUInt(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
            offset += 4;
        }
    }
}
