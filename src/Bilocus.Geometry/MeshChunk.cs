// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

namespace Bilocus.Geometry
{
    // A piece of mesh that fits in a single Revit vertex buffer.
    // The indices are LOCAL to the chunk, so always under the 16-bit limit
    // of DirectContext3D's index buffers.
    public sealed class MeshChunk
    {
        public float[] Positions { get; private set; }
        public float[] Normals { get; private set; }
        public int[] Indices { get; private set; }

        public int VertexCount { get { return Positions.Length / 3; } }
        public int TriangleCount { get { return Indices.Length / 3; } }

        public MeshChunk(float[] positions, float[] normals, int[] indices)
        {
            Positions = positions;
            Normals = normals;
            Indices = indices;
        }
    }
}
