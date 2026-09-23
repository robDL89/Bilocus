// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    public class MeshPayloadWriterTests
    {
        [Fact]
        public void WritesExpectedBytes_SingleTriangle()
        {
            // Expected bytes computed with a separate Python script
            // (struct.pack), not from memory.
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1, 2 };

            byte[] expected = new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x80, 0x3f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3f, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3f,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3f,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3f,
                0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00
            };

            byte[] actual = MeshPayloadWriter.Write(positions, normals, indices);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void RoundTrip_WriteThenParse_ProducesIdenticalData()
        {
            float[] positions = new float[]
            {
                0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0
            };
            float[] normals = new float[]
            {
                0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1
            };
            int[] indices = new int[] { 0, 1, 2, 1, 3, 2 };

            byte[] payload = MeshPayloadWriter.Write(positions, normals, indices);
            MeshPayload parsed = MeshPayload.Parse(payload, positions.Length / 3, indices.Length / 3);

            Assert.Equal(positions, parsed.Positions);
            Assert.Equal(normals, parsed.Normals);
            Assert.Equal(indices, parsed.Indices);
        }

        [Fact]
        public void MismatchedPositionsAndNormalsLengths_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1, 2 };

            Assert.Throws<ArgumentException>(() => MeshPayloadWriter.Write(positions, normals, indices));
        }

        [Fact]
        public void IndicesNotMultipleOfThree_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1 };

            Assert.Throws<ArgumentException>(() => MeshPayloadWriter.Write(positions, normals, indices));
        }

        [Fact]
        public void IndexOutOfRange_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1, 3 };

            Assert.Throws<ArgumentException>(() => MeshPayloadWriter.Write(positions, normals, indices));
        }

        [Fact]
        public void EmptyArrays_ProduceEmptyPayload()
        {
            byte[] payload = MeshPayloadWriter.Write(new float[0], new float[0], new int[0]);

            Assert.Empty(payload);
        }
    }
}
