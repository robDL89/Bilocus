// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // Reads the binary payload of the geometry message.
    // Order: positions (vert*3 f32), normals (vert*3 f32), indices (tri*3 u32).
    // Little-endian, meters, Z-up. See DESIGN.md 5.2 and 5.3.
    //
    // The counts come from the header, the payload from the wire: if they
    // do not add up, either the sender has a bug or the frame is corrupt.
    // Either way the response is to reject, not to guess.
    public sealed class MeshPayload
    {
        public float[] Positions { get; private set; }
        public float[] Normals { get; private set; }
        public int[] Indices { get; private set; }

        private MeshPayload(float[] positions, float[] normals, int[] indices)
        {
            Positions = positions;
            Normals = normals;
            Indices = indices;
        }

        public static MeshPayload Parse(byte[] payload, int vertexCount, int triangleCount)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (vertexCount < 0) throw new ArgumentException("negative vert_count: " + vertexCount);
            if (triangleCount < 0) throw new ArgumentException("negative tri_count: " + triangleCount);

            long expected = (long)vertexCount * 3 * 4 * 2 + (long)triangleCount * 3 * 4;
            if (payload.Length != expected)
            {
                throw new ArgumentException(string.Format(
                    "payload is {0} bytes but the header declares {1} (vert {2}, tri {3})",
                    payload.Length, expected, vertexCount, triangleCount));
            }

            float[] positions = new float[vertexCount * 3];
            float[] normals = new float[vertexCount * 3];
            int[] indices = new int[triangleCount * 3];

            int offset = 0;
            for (int i = 0; i < positions.Length; i++) { positions[i] = ReadFloat(payload, ref offset); }
            for (int i = 0; i < normals.Length; i++) { normals[i] = ReadFloat(payload, ref offset); }
            for (int i = 0; i < indices.Length; i++)
            {
                uint value = ReadUInt(payload, ref offset);
                if (value > int.MaxValue)
                {
                    throw new ArgumentException(string.Format(
                        "index {0} at position {1} cannot be represented", value, i));
                }
                indices[i] = (int)value;
            }

            return new MeshPayload(positions, normals, indices);
        }

        private static float ReadFloat(byte[] buffer, ref int offset)
        {
            byte[] raw = new byte[4];
            Array.Copy(buffer, offset, raw, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(raw);
            offset += 4;
            return BitConverter.ToSingle(raw, 0);
        }

        private static uint ReadUInt(byte[] buffer, ref int offset)
        {
            uint value = (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
            offset += 4;
            return value;
        }
    }
}
