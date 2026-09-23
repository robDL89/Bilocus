// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;

namespace Bilocus.Geometry
{
    // Splits a mesh into pieces that fit in DirectContext3D's buffers.
    //
    // The limit was MEASURED, not assumed: Revit's index buffers use 16-bit
    // indices, so a buffer addresses at most 65536 vertices. Exceeding it
    // does NOT raise exceptions: Revit truncates silently and draws a wrong
    // shape. See DESIGN.md 8, Risks.
    // That is why validation is done here, by counting, and not left to a
    // try/catch around FlushBuffer.
    //
    // A split is not a cut of the vertex array: it is a repartitioning of
    // the TRIANGLES. A vertex shared between triangles that end up in
    // different chunks gets duplicated in both. The total number of
    // vertices produced can therefore exceed the starting one: that is the
    // correct price to pay.
    public static class MeshChunker
    {
        public const int MaxVerticesPerChunk = 65536;

        public static List<MeshChunk> Split(float[] positions, float[] normals, int[] indices)
        {
            Validate(positions, normals, indices);

            List<MeshChunk> chunks = new List<MeshChunk>();
            int triangleCount = indices.Length / 3;
            if (triangleCount == 0)
            {
                return chunks;
            }

            Dictionary<int, int> globalToLocal = new Dictionary<int, int>();
            List<float> chunkPositions = new List<float>();
            List<float> chunkNormals = new List<float>();
            List<int> chunkIndices = new List<int>();

            for (int t = 0; t < triangleCount; t++)
            {
                int a = indices[t * 3];
                int b = indices[t * 3 + 1];
                int c = indices[t * 3 + 2];

                int newVertices = 0;
                if (!globalToLocal.ContainsKey(a)) newVertices++;
                if (b != a && !globalToLocal.ContainsKey(b)) newVertices++;
                if (c != a && c != b && !globalToLocal.ContainsKey(c)) newVertices++;

                if (globalToLocal.Count + newVertices > MaxVerticesPerChunk)
                {
                    chunks.Add(new MeshChunk(
                        chunkPositions.ToArray(), chunkNormals.ToArray(), chunkIndices.ToArray()));
                    globalToLocal.Clear();
                    chunkPositions.Clear();
                    chunkNormals.Clear();
                    chunkIndices.Clear();
                }

                chunkIndices.Add(MapVertex(a, globalToLocal, positions, normals, chunkPositions, chunkNormals));
                chunkIndices.Add(MapVertex(b, globalToLocal, positions, normals, chunkPositions, chunkNormals));
                chunkIndices.Add(MapVertex(c, globalToLocal, positions, normals, chunkPositions, chunkNormals));
            }

            if (chunkIndices.Count > 0)
            {
                chunks.Add(new MeshChunk(
                    chunkPositions.ToArray(), chunkNormals.ToArray(), chunkIndices.ToArray()));
            }

            return chunks;
        }

        private static int MapVertex(
            int global,
            Dictionary<int, int> globalToLocal,
            float[] positions,
            float[] normals,
            List<float> chunkPositions,
            List<float> chunkNormals)
        {
            int local;
            if (globalToLocal.TryGetValue(global, out local))
            {
                return local;
            }

            local = chunkPositions.Count / 3;
            globalToLocal.Add(global, local);

            chunkPositions.Add(positions[global * 3]);
            chunkPositions.Add(positions[global * 3 + 1]);
            chunkPositions.Add(positions[global * 3 + 2]);

            chunkNormals.Add(normals[global * 3]);
            chunkNormals.Add(normals[global * 3 + 1]);
            chunkNormals.Add(normals[global * 3 + 2]);

            return local;
        }

        private static void Validate(float[] positions, float[] normals, int[] indices)
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

            // The range check is INTENTIONAL here, and is not a forgotten
            // duplicate elsewhere. MeshPayload.Parse deliberately does not do
            // it: that one operates on the wire and only verifies that the
            // bytes received match what the header declares. "The index
            // points to a vertex that exists" is instead semantic
            // consistency of the mesh, and it lives here, where the
            // invariant is actually exploited by the global-to-local map.
            // Also this class is public and usable without going through
            // MeshPayload: it cannot trust the caller.
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
        }
    }
}
