// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    public class MeshPayloadTests
    {
        private static byte[] BuildPayload(float[] positions, float[] normals, int[] indices)
        {
            byte[] payload = new byte[(positions.Length + normals.Length) * 4 + indices.Length * 4];
            int offset = 0;
            foreach (float value in positions) { WriteFloat(payload, ref offset, value); }
            foreach (float value in normals) { WriteFloat(payload, ref offset, value); }
            foreach (int value in indices) { WriteUInt(payload, ref offset, (uint)value); }
            return payload;
        }

        private static void WriteFloat(byte[] buffer, ref int offset, float value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(raw);
            Array.Copy(raw, 0, buffer, offset, 4);
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

        [Fact]
        public void ParsesSingleTriangle()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1, 2 };

            MeshPayload parsed = MeshPayload.Parse(BuildPayload(positions, normals, indices), 3, 1);

            Assert.Equal(positions, parsed.Positions);
            Assert.Equal(normals, parsed.Normals);
            Assert.Equal(indices, parsed.Indices);
        }

        [Fact]
        public void PayloadShorterThanDeclared_Throws()
        {
            byte[] payload = new byte[10];
            Assert.Throws<ArgumentException>(() => MeshPayload.Parse(payload, 3, 1));
        }

        [Fact]
        public void PayloadLongerThanDeclared_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[9];
            int[] indices = new int[] { 0, 1, 2 };
            byte[] payload = BuildPayload(positions, normals, indices);
            byte[] longer = new byte[payload.Length + 4];
            Array.Copy(payload, longer, payload.Length);

            Assert.Throws<ArgumentException>(() => MeshPayload.Parse(longer, 3, 1));
        }

        [Fact]
        public void NegativeCounts_Throw()
        {
            Assert.Throws<ArgumentException>(() => MeshPayload.Parse(new byte[0], -1, 0));
            Assert.Throws<ArgumentException>(() => MeshPayload.Parse(new byte[0], 0, -1));
        }

        [Fact]
        public void ZeroCounts_ProduceEmptyArrays()
        {
            MeshPayload parsed = MeshPayload.Parse(new byte[0], 0, 0);
            Assert.Empty(parsed.Positions);
            Assert.Empty(parsed.Normals);
            Assert.Empty(parsed.Indices);
        }

        [Fact]
        public void IndexAboveIntMaxValue_Throws()
        {
            // A uint32 that does not fit in an int must be rejected, instead
            // of becoming a negative index from a cast wraparound.
            //
            // The payload has three REAL vertices on purpose. With
            // vert_count = 0 the test would pass anyway, but it would not be
            // possible to tell "the overflow check worked" apart from "any
            // range check would have caught the same value". With three
            // vertices present, the only possible reason for failure is the
            // overflow.
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };

            byte[] payload = new byte[(positions.Length + normals.Length) * 4 + 3 * 4];
            int offset = 0;
            foreach (float value in positions) { WriteFloat(payload, ref offset, value); }
            foreach (float value in normals) { WriteFloat(payload, ref offset, value); }
            WriteUInt(payload, ref offset, 0x80000000u);
            WriteUInt(payload, ref offset, 0u);
            WriteUInt(payload, ref offset, 0u);

            Assert.Throws<ArgumentException>(() => MeshPayload.Parse(payload, 3, 1));
        }
    }
}
